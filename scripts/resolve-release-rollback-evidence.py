#!/usr/bin/env python3
"""Resolve rollback identity from a previously signed field release receipt."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import sys
from typing import Callable

from ota_update_trust_roots import (
    DEFAULT_REGISTRY,
    TrustRegistryError,
    load_registry_configuration,
    verify_signature_bytes,
)


MAX_CHECKSUM_BYTES = 128 * 1024
MAX_RECEIPT_BYTES = 64 * 1024
SHA256_PATTERN = re.compile(r"[a-f0-9]{64}")
STABLE_TAG_PATTERN = re.compile(r"v(\d+)\.(\d+)\.(\d+)")
CHECKSUM_LINE_PATTERN = re.compile(
    r"(?P<sha256>[a-f0-9]{64})  (?P<name>[A-Za-z0-9][A-Za-z0-9._-]{0,255})"
)
V1_KEY_ID = "ota-update-v1"
V2_KEY_ID = "ota-update-v2"
OTA_SIGNING_KEY_IDS = frozenset({V1_KEY_ID, V2_KEY_ID})


def fail(message: str) -> "NoReturn":
    print(message, file=sys.stderr)
    raise SystemExit(1)


def read_bounded(path: Path, maximum: int, label: str) -> bytes:
    if not path.is_file() or path.is_symlink():
        fail(f"{label} must be a regular non-link file")
    data = path.read_bytes()
    if not 0 < len(data) <= maximum:
        fail(f"{label} size is outside the allowed boundary")
    return data


def reject_duplicate_json_keys(pairs: list[tuple[str, object]]) -> dict[str, object]:
    result: dict[str, object] = {}
    for key, value in pairs:
        if key in result:
            fail(f"rollback receipt contains duplicate JSON key: {key}")
        result[key] = value
    return result


def parse_checksums(data: bytes) -> dict[str, str]:
    try:
        text = data.decode("utf-8")
    except UnicodeDecodeError:
        fail("rollback checksums are not UTF-8")
    result: dict[str, str] = {}
    for line in text.splitlines():
        match = CHECKSUM_LINE_PATTERN.fullmatch(line)
        if match is None:
            fail("rollback checksums contain an invalid line")
        name = match.group("name")
        if name in result:
            fail(f"rollback checksums contain duplicate artifact: {name}")
        result[name] = match.group("sha256")
    if not result:
        fail("rollback checksums are empty")
    return result


def allowed_artifacts(tag: str) -> set[str]:
    if STABLE_TAG_PATTERN.fullmatch(tag) is None:
        fail("rollback tag must be an exact stable vMAJOR.MINOR.PATCH")
    return {
        f"suavoagent-{tag}-win-x64.zip",
        "SuavoAgent-Setup.exe",
    }


def required_ota_signing_key_id(
    tag: str,
    receipt: dict[str, object],
    receipt_sha256: str,
    expected_ota_signing_key_id: str | None,
    bridge_release_tag: str | None,
    bridge_source_sha: str | None,
    bridge_receipt_sha256: str | None,
) -> str:
    bridge_values = (bridge_release_tag, bridge_source_sha, bridge_receipt_sha256)
    if expected_ota_signing_key_id is not None:
        if expected_ota_signing_key_id not in OTA_SIGNING_KEY_IDS:
            fail("expected OTA signing key id is invalid")
        if any(value is not None for value in bridge_values):
            fail("explicit OTA root and claim-bound Release 1 policy are mutually exclusive")
        return expected_ota_signing_key_id

    if any(value is None for value in bridge_values):
        fail("claim-bound Release 1 tag, source, and receipt digest are all required")
    if STABLE_TAG_PATTERN.fullmatch(bridge_release_tag or "") is None:
        fail("claim-bound Release 1 tag is invalid")
    if re.fullmatch(r"[a-f0-9]{40}", bridge_source_sha or "") is None:
        fail("claim-bound Release 1 source commit is invalid")
    if SHA256_PATTERN.fullmatch(bridge_receipt_sha256 or "") is None:
        fail("claim-bound Release 1 receipt digest is invalid")

    is_exact_release_1 = (
        tag == bridge_release_tag
        and receipt.get("sourceCommit") == bridge_source_sha
        and receipt_sha256 == bridge_receipt_sha256
    )
    return V1_KEY_ID if is_exact_release_1 else V2_KEY_ID


def resolve(
    tag: str,
    checksums_path: Path,
    receipt_path: Path,
    *,
    signature_path: Path,
    registry_path: Path = DEFAULT_REGISTRY,
    expected_ota_signing_key_id: str | None = None,
    bridge_release_tag: str | None = None,
    bridge_source_sha: str | None = None,
    bridge_receipt_sha256: str | None = None,
    allow_missing_legacy_ota_signing_key_id: bool = False,
    signature_verifier: Callable[[str, bytes, bytes], bool] | None = None,
) -> tuple[str, str]:
    checksums_data = read_bounded(checksums_path, MAX_CHECKSUM_BYTES, "rollback checksums")
    signature_data = read_bounded(signature_path, 512, "rollback checksum signature")
    receipt_data = read_bounded(receipt_path, MAX_RECEIPT_BYTES, "rollback receipt")
    checksums = parse_checksums(checksums_data)

    receipt_sha256 = hashlib.sha256(receipt_data).hexdigest()
    if checksums.get("field-release-receipt.json") != receipt_sha256:
        fail("rollback receipt is not bound by the signed checksum manifest")

    try:
        receipt = json.loads(
            receipt_data,
            object_pairs_hook=reject_duplicate_json_keys,
        )
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        fail(f"rollback receipt is not valid JSON: {error.__class__.__name__}")
    if not isinstance(receipt, dict):
        fail("rollback receipt root must be an object")

    required_key_id = required_ota_signing_key_id(
        tag,
        receipt,
        receipt_sha256,
        expected_ota_signing_key_id,
        bridge_release_tag,
        bridge_source_sha,
        bridge_receipt_sha256,
    )
    if allow_missing_legacy_ota_signing_key_id and (
        expected_ota_signing_key_id != V1_KEY_ID
        or any(value is not None for value in (
            bridge_release_tag,
            bridge_source_sha,
            bridge_receipt_sha256,
        ))
    ):
        fail("legacy missing-key allowance is restricted to explicit bridge-stage ota-update-v1")
    declared_key_id = receipt.get("otaSigningKeyId")
    allow_legacy_missing_v1 = (
        declared_key_id is None
        and allow_missing_legacy_ota_signing_key_id
        and expected_ota_signing_key_id == V1_KEY_ID
        and all(value is None for value in (
            bridge_release_tag,
            bridge_source_sha,
            bridge_receipt_sha256,
        ))
    )
    if declared_key_id is not None and declared_key_id not in OTA_SIGNING_KEY_IDS:
        fail("rollback receipt OTA signing key id is missing or invalid")
    if declared_key_id is not None and declared_key_id != required_key_id:
        fail(
            f"rollback receipt declares {declared_key_id} but policy requires {required_key_id}"
        )
    if declared_key_id is None and not allow_legacy_missing_v1:
        fail("rollback receipt OTA signing key id is missing or invalid")
    if signature_verifier is None:
        _, roots = load_registry_configuration(registry_path)
        verified = required_key_id in roots and verify_signature_bytes(
            {required_key_id: roots[required_key_id]},
            checksums_data,
            signature_data,
            "der",
        )
    else:
        verified = signature_verifier(required_key_id, checksums_data, signature_data)
    if not verified:
        fail(f"rollback checksums are not signed specifically by {required_key_id}")

    if receipt.get("releaseTag") != tag:
        fail("rollback receipt releaseTag does not match the selected release")
    expected_version = tag.removeprefix("v")
    if receipt.get("version") != expected_version:
        fail("rollback receipt version does not match the selected release")
    if receipt.get("authenticode") != "required-valid":
        fail("rollback receipt does not require valid Authenticode")
    if receipt.get("checksumSignature") != "checksums.sha256.sig":
        fail("rollback receipt checksum signature identity is invalid")
    if receipt.get("manifestSignature") != f"update-manifest-{tag}.sig":
        fail("rollback receipt manifest signature identity is invalid")
    source_commit = receipt.get("sourceCommit")
    if not isinstance(source_commit, str) or re.fullmatch(r"[a-f0-9]{40}", source_commit) is None:
        fail("rollback receipt source commit is invalid")

    artifact = receipt.get("artifact")
    artifact_sha256 = receipt.get("artifactSha256")
    if not isinstance(artifact, str) or artifact not in allowed_artifacts(tag):
        fail("rollback receipt artifact name is outside the approved transition")
    if not isinstance(artifact_sha256, str) or SHA256_PATTERN.fullmatch(artifact_sha256) is None:
        fail("rollback receipt artifact SHA-256 is invalid")
    if checksums.get(artifact) != artifact_sha256:
        fail("rollback receipt artifact digest does not match the signed checksum manifest")
    return artifact, artifact_sha256


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--tag", required=True)
    parser.add_argument("--checksums", required=True, type=Path)
    parser.add_argument("--signature", required=True, type=Path)
    parser.add_argument("--receipt", required=True, type=Path)
    parser.add_argument("--registry", type=Path, default=DEFAULT_REGISTRY)
    parser.add_argument(
        "--expected-ota-signing-key-id",
        choices=sorted(OTA_SIGNING_KEY_IDS),
    )
    parser.add_argument(
        "--allow-missing-legacy-ota-signing-key-id",
        action="store_true",
    )
    parser.add_argument("--bridge-release-tag")
    parser.add_argument("--bridge-source-sha")
    parser.add_argument("--bridge-receipt-sha256")
    args = parser.parse_args()
    try:
        artifact, artifact_sha256 = resolve(
            args.tag,
            args.checksums,
            args.receipt,
            signature_path=args.signature,
            registry_path=args.registry,
            expected_ota_signing_key_id=args.expected_ota_signing_key_id,
            bridge_release_tag=args.bridge_release_tag,
            bridge_source_sha=args.bridge_source_sha,
            bridge_receipt_sha256=args.bridge_receipt_sha256,
            allow_missing_legacy_ota_signing_key_id=(
                args.allow_missing_legacy_ota_signing_key_id
            ),
        )
    except TrustRegistryError as error:
        fail(str(error))
    print(artifact)
    print(artifact_sha256)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
