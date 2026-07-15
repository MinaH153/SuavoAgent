#!/usr/bin/env python3
"""Resolve rollback identity from a previously signed field release receipt."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import sys


MAX_CHECKSUM_BYTES = 128 * 1024
MAX_RECEIPT_BYTES = 64 * 1024
SHA256_PATTERN = re.compile(r"[a-f0-9]{64}")
STABLE_TAG_PATTERN = re.compile(r"v(\d+)\.(\d+)\.(\d+)")
CHECKSUM_LINE_PATTERN = re.compile(
    r"(?P<sha256>[a-f0-9]{64})  (?P<name>[A-Za-z0-9][A-Za-z0-9._-]{0,255})"
)


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


def resolve(tag: str, checksums_path: Path, receipt_path: Path) -> tuple[str, str]:
    checksums_data = read_bounded(checksums_path, MAX_CHECKSUM_BYTES, "rollback checksums")
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
    parser.add_argument("--receipt", required=True, type=Path)
    args = parser.parse_args()
    artifact, artifact_sha256 = resolve(args.tag, args.checksums, args.receipt)
    print(artifact)
    print(artifact_sha256)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
