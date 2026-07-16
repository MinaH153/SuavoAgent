#!/usr/bin/env python3
"""Fail-closed tooling for the one-time ota-update-v1 Release 1 bridge."""

from __future__ import annotations

import argparse
import base64
import binascii
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
from typing import Any

from ecdsa_der_to_p1363 import der_to_p1363_hex
from ota_update_trust_roots import (
    TrustRegistryError,
    load_registry_configuration_bytes,
    verify_signature,
    verify_signature_bytes,
)
from v1_bridge_crypto import HistoricKeyError, open_historic_key
from v1_bridge_handoff import (
    HandoffError,
    checksums_from_authenticated_request,
    checksums_from_release_files,
    publication_checksum_entries,
    regenerate_final_sbom,
    request_file_binding,
    verify_authenticated_handoff,
)
from v1_bridge_file_io import SecureFileError, read_regular_once, release_file_entries_once
from v1_bridge_source_guard import validate_local_source
ROOT = Path(__file__).resolve().parents[1]
DEFAULT_REGISTRY = ROOT / "security/ota-update-trust-roots.json"
CEREMONY = "ota-update-v1-bridge-release-1"
REPOSITORY = "MinaH153/SuavoAgent"
STAGE_WORKFLOW = ".github/workflows/v1-bridge-stage.yml"
V1_KEY_ID = "ota-update-v1"
V2_KEY_ID = "ota-update-v2"
SCHEMA_VERSION = 1
BRIDGE_ROOT_SHA256 = {
    V1_KEY_ID: "b3f5ddda0654713de31e6cbe3ae3b49ed53575d0938d4149779361c6d739e970",
    V2_KEY_ID: "6e4092980b1185627200476806d5063c43df77e5ac000b6b6ba72df89eb1406f",
}
MAX_REQUEST_BYTES = 2 * 1024 * 1024
MAX_RESPONSE_BYTES = 32 * 1024
ChecksumEntries = tuple[tuple[str, str], ...]
SHA_RE = re.compile(r"[0-9a-f]{64}")
COMMIT_RE = re.compile(r"[0-9a-f]{40}")
VERSION_RE = re.compile(r"v([0-9]+)\.([0-9]+)\.([0-9]+)")
SAFE_NAME_RE = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,199}")
LEGAL_PATH_RE = re.compile(
    r"legal/(?:THIRD-PARTY-NOTICES\.txt|THIRD-PARTY-PROVENANCE\.json|"
    r"external-assets\.json|license-texts/[A-Za-z0-9._-]+\.txt|"
    r"evidence/[A-Za-z0-9._-]+\.json)"
)

REQUEST_KEYS = frozenset(
    {
        "schemaVersion",
        "ceremony",
        "repository",
        "sourceSha",
        "stageWorkflow",
        "stageRunId",
        "stageRunAttempt",
        "version",
        "registrySha256",
        "signingKeyId",
        "artifactName",
        "manifestPath",
        "checksumsPath",
        "authenticodeSignerSha256",
        "files",
    }
)
RESPONSE_KEYS = frozenset(
    {
        "schemaVersion",
        "ceremony",
        "repository",
        "sourceSha",
        "stageWorkflow",
        "stageRunId",
        "stageRunAttempt",
        "version",
        "artifactName",
        "requestSha256",
        "signingKeyId",
        "checksumsBase64",
        "manifestSignature",
        "checksumsSignature",
        "authenticodeSignerSha256",
    }
)
SIGNATURE_KEYS = frozenset(
    {"inputPath", "inputSha256", "outputPath", "format", "signatureBase64"}
)
FIELD_RELEASE_RECEIPT_KEYS = frozenset({
    "artifact", "artifactSha256", "authenticode", "checksumSignature",
    "manifestSignature", "otaSigningKeyId", "releaseTag", "rollbackArtifact",
    "sourceCommit", "track2QueenValidation", "version",
})
class BridgeError(ValueError):
    pass
def _canonical_json(document: object) -> bytes:
    return (
        json.dumps(document, ensure_ascii=True, separators=(",", ":"), sort_keys=True)
        + "\n"
    ).encode("utf-8")
def _sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()
def _read_regular(path: Path, maximum_bytes: int, label: str) -> bytes:
    try:
        return read_regular_once(path, maximum_bytes, label)
    except SecureFileError as error:
        raise BridgeError(str(error)) from error
def _exclusive_write(path: Path, data: bytes, mode: int = 0o644) -> None:
    if path.exists() or path.is_symlink():
        raise BridgeError(f"output already exists: {path}")
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, mode)
    try:
        with os.fdopen(descriptor, "wb") as output:
            output.write(data)
            output.flush()
            os.fsync(output.fileno())
    except BaseException:
        path.unlink(missing_ok=True)
        raise
def _strict_json(path: Path, maximum_bytes: int, label: str) -> tuple[dict[str, Any], bytes]:
    raw = _read_regular(path, maximum_bytes, label)
    try:
        document = json.loads(raw)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise BridgeError(f"{label} is not valid JSON") from error
    if not isinstance(document, dict):
        raise BridgeError(f"{label} must be a JSON object")
    if raw != _canonical_json(document):
        raise BridgeError(f"{label} is not canonical JSON")
    return document, raw
def _strict_positive_integer(value: object, label: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value <= 0:
        raise BridgeError(f"{label} must be a positive integer")
    return value


def _strict_sha(value: object, pattern: re.Pattern[str], label: str) -> str:
    if not isinstance(value, str) or pattern.fullmatch(value) is None:
        raise BridgeError(f"{label} has an invalid digest")
    return value


def _version_parts(version: object) -> tuple[str, str]:
    if not isinstance(version, str) or (match := VERSION_RE.fullmatch(version)) is None:
        raise BridgeError("bridge version must be exact stable vMAJOR.MINOR.PATCH")
    if any(component != str(int(component, 10)) for component in match.groups()):
        raise BridgeError("bridge version must not contain leading-zero aliases")
    numbers = tuple(int(component, 10) for component in match.groups())
    if numbers[0] > 255 or numbers[1] > 255 or numbers[2] > 65535:
        raise BridgeError("bridge version is outside the Windows Installer boundary")
    return version, ".".join(str(number) for number in numbers)


def _safe_name(value: object, label: str) -> str:
    if not isinstance(value, str) or SAFE_NAME_RE.fullmatch(value) is None:
        raise BridgeError(f"{label} is not a safe file or tag name")
    return value


def _registry(registry_path: Path) -> tuple[str, dict[str, bytes], str]:
    raw = _read_regular(registry_path, 16 * 1024, "OTA trust registry")
    try:
        selected, roots = load_registry_configuration_bytes(raw)
    except TrustRegistryError as error:
        raise BridgeError(str(error)) from error
    return selected, roots, _sha256(raw)


def assert_bridge_source(registry_path: Path) -> None:
    selected, roots, _ = _registry(registry_path)
    if selected != V1_KEY_ID:
        raise BridgeError("Release 1 bridge source must select ota-update-v1")
    if set(roots) != {V1_KEY_ID, V2_KEY_ID} or roots[V1_KEY_ID] == roots[V2_KEY_ID]:
        raise BridgeError("Release 1 bridge source must contain two distinct reviewed roots")
    if any(_sha256(roots[key_id]) != digest for key_id, digest in BRIDGE_ROOT_SHA256.items()):
        raise BridgeError("Release 1 bridge roots do not match the pinned fleet and KMS authorities")


def assert_normal_release(
    registry_path: Path,
    full_cohort_manifest: str,
    evidence_path: Path | None = None,
    claim_path: Path | None = None,
    signature_path: Path | None = None,
    inventory_path: Path | None = None,
    inventory_signature_path: Path | None = None,
) -> None:
    selected, roots, _ = _registry(registry_path)
    if selected == V1_KEY_ID:
        raise BridgeError(
            "normal release/hotfix refuses signingKeyId=ota-update-v1; run "
            ".github/workflows/v1-bridge-stage.yml for Release 1"
        )
    if selected != V2_KEY_ID or V2_KEY_ID not in roots:
        raise BridgeError("normal release/hotfix requires reviewed ota-update-v2")
    if full_cohort_manifest != "true":
        raise BridgeError(
            "normal release/hotfix requires OTA_FULL_COHORT_MANIFEST exactly true"
        )
    from v1_bridge_convergence import (
        DEFAULT_CLAIM,
        DEFAULT_EVIDENCE,
        DEFAULT_INVENTORY,
        DEFAULT_INVENTORY_SIGNATURE,
        DEFAULT_SIGNATURE,
        verify_convergence_claim,
    )

    verify_convergence_claim(
        registry_path,
        evidence_path or DEFAULT_EVIDENCE,
        claim_path or DEFAULT_CLAIM,
        signature_path or DEFAULT_SIGNATURE,
        inventory_path or DEFAULT_INVENTORY,
        inventory_signature_path or DEFAULT_INVENTORY_SIGNATURE,
    )


def _authenticode_signers(value: object) -> str:
    if not isinstance(value, str) or re.fullmatch(
        r"[A-Fa-f0-9]{64}(?:,[A-Fa-f0-9]{64}){0,15}", value
    ) is None:
        raise BridgeError("Authenticode signer allowlist is invalid")
    signers = tuple(item.lower() for item in value.split(","))
    if len(signers) != len(set(signers)):
        raise BridgeError("Authenticode signer allowlist contains duplicates")
    return ",".join(signers)


def _walk_release_files(release_dir: Path) -> tuple[Path, ...]:
    if not release_dir.is_dir() or release_dir.is_symlink():
        raise BridgeError("release directory must be a regular non-link directory")
    discovered: list[Path] = []
    for directory, names, filenames in os.walk(release_dir, followlinks=False):
        directory_path = Path(directory)
        for name in names:
            candidate = directory_path / name
            if candidate.is_symlink():
                raise BridgeError(f"release directory contains a symlink: {candidate}")
        for name in filenames:
            candidate = directory_path / name
            _read_regular(candidate, 2 * 1024 * 1024 * 1024, "release file")
            discovered.append(candidate)
    return tuple(sorted(discovered, key=lambda path: path.relative_to(release_dir).as_posix()))


def _required_paths(version: str, finalized: bool) -> frozenset[str]:
    base = {
        "SuavoAgent.Core.exe",
        "SuavoAgent.Broker.exe",
        "SuavoAgent.Helper.exe",
        "SuavoAgent.Watchdog.exe",
        "SuavoSetup.exe",
        f"SuavoAgent-{version}-win-x64.msi",
        "SuavoAgent-Setup.exe",
        "suavoagent.spdx.json",
        "field-release-receipt.json",
        f"update-manifest-{version}.txt",
    }
    if finalized:
        base.update(
            {
                "checksums.sha256",
                f"update-manifest-{version}.sig",
                "checksums.sha256.sig",
            }
        )
    return frozenset(base)


def _validate_release_allowlist(release_dir: Path, version: str, finalized: bool) -> tuple[Path, ...]:
    files = _walk_release_files(release_dir)
    relative = frozenset(path.relative_to(release_dir).as_posix() for path in files)
    required = _required_paths(version, finalized)
    if not required.issubset(relative):
        raise BridgeError("release artifact is missing required files: " + ", ".join(sorted(required - relative)))
    unexpected = sorted(path for path in relative - required if LEGAL_PATH_RE.fullmatch(path) is None)
    if unexpected:
        raise BridgeError("release artifact contains files outside the allowlist: " + ", ".join(unexpected))
    for required_legal in (
        "legal/THIRD-PARTY-NOTICES.txt",
        "legal/THIRD-PARTY-PROVENANCE.json",
        "legal/external-assets.json",
    ):
        if required_legal not in relative:
            raise BridgeError(f"release artifact is missing {required_legal}")
    if not any(path.startswith("legal/license-texts/") for path in relative):
        raise BridgeError("release artifact has no retained license texts")
    if not any(path.startswith("legal/evidence/") for path in relative):
        raise BridgeError("release artifact has no retained legal evidence")
    return files


def _expected_manifest(release_dir: Path, repository: str, version: str, numeric: str) -> bytes:
    hashes = {
        name: _sha256(_read_regular(release_dir / name, 2 * 1024 * 1024 * 1024, name))
        for name in (
            "SuavoAgent.Core.exe",
            "SuavoAgent.Broker.exe",
            "SuavoAgent.Helper.exe",
            "SuavoAgent.Watchdog.exe",
        )
    }
    base = f"https://github.com/{repository}/releases/download/{version}"
    fields = (
        f"{base}/SuavoAgent.Core.exe", hashes["SuavoAgent.Core.exe"],
        f"{base}/SuavoAgent.Broker.exe", hashes["SuavoAgent.Broker.exe"],
        f"{base}/SuavoAgent.Helper.exe", hashes["SuavoAgent.Helper.exe"],
        numeric, "net8.0", "win-x64",
        f"{base}/SuavoAgent.Watchdog.exe", hashes["SuavoAgent.Watchdog.exe"],
    )
    return "|".join(fields).encode("ascii")


def _receipt(
    repository: str,
    source_sha: str,
    version: str,
    numeric: str,
    release_dir: Path,
    rollback_tag: str,
    rollback_artifact: str,
    rollback_sha: str,
) -> dict[str, object]:
    artifact = "SuavoAgent-Setup.exe"
    return {
        "artifact": artifact,
        "artifactSha256": _sha256(_read_regular(release_dir / artifact, 2 * 1024 * 1024 * 1024, artifact)),
        "authenticode": "required-valid",
        "checksumSignature": "checksums.sha256.sig",
        "manifestSignature": f"update-manifest-{version}.sig",
        "otaSigningKeyId": V1_KEY_ID,
        "releaseTag": version,
        "rollbackArtifact": {
            "artifact": rollback_artifact,
            "artifactSha256": rollback_sha,
            "releaseTag": rollback_tag,
            "releaseUrl": f"https://github.com/{repository}/releases/download/{rollback_tag}/{rollback_artifact}",
        },
        "sourceCommit": source_sha,
        "track2QueenValidation": "do-not-run-against-older-tags",
        "version": numeric,
    }


def _checksum_publication_entries(release_dir: Path, version: str, finalized: bool = False) -> ChecksumEntries:
    try:
        return publication_checksum_entries(
            release_dir,
            _validate_release_allowlist(release_dir, version, finalized=finalized),
            version,
        )
    except HandoffError as error:
        raise BridgeError(str(error)) from error


def _expected_checksums(release_dir: Path, version: str, manifest_signature: bytes, finalized: bool = False) -> bytes:
    entries = _checksum_publication_entries(release_dir, version, finalized=finalized)
    try:
        return checksums_from_release_files(
            release_dir, entries, version, manifest_signature
        )
    except HandoffError as error:
        raise BridgeError(str(error)) from error


def prepare_request(arguments: argparse.Namespace) -> None:
    repository = arguments.repository
    if repository != REPOSITORY:
        raise BridgeError("bridge repository identity is not approved")
    source_sha = _strict_sha(arguments.source_sha, COMMIT_RE, "source SHA")
    version, numeric = _version_parts(arguments.version)
    rollback_tag, _ = _version_parts(arguments.rollback_tag)
    rollback_artifact = _safe_name(arguments.rollback_artifact, "rollback artifact")
    rollback_sha = _strict_sha(arguments.rollback_sha, SHA_RE, "rollback SHA")
    run_id = _strict_positive_integer(arguments.stage_run_id, "stage run id")
    run_attempt = _strict_positive_integer(arguments.stage_run_attempt, "stage run attempt")
    authenticode_signers = _authenticode_signers(arguments.authenticode_signer_sha256)
    assert_bridge_source(arguments.registry)
    _, _, registry_sha = _registry(arguments.registry)
    release_dir = arguments.release_dir

    manifest_path = release_dir / f"update-manifest-{version}.txt"
    receipt_path = release_dir / "field-release-receipt.json"
    _exclusive_write(manifest_path, _expected_manifest(release_dir, repository, version, numeric))
    _exclusive_write(
        receipt_path,
        _canonical_json(
            _receipt(repository, source_sha, version, numeric, release_dir, rollback_tag, rollback_artifact, rollback_sha)
        ),
    )
    if getattr(arguments, "regenerate_final_sbom", False):
        regenerate_final_sbom(release_dir, version, source_sha)
    files = _validate_release_allowlist(release_dir, version, finalized=False)
    artifact_name = f"suavoagent-v1-bridge-request-{run_id}-{run_attempt}"
    entries = release_file_entries_once(files, release_dir, 2 * 1024 * 1024 * 1024)
    request = {
        "artifactName": artifact_name,
        "authenticodeSignerSha256": authenticode_signers,
        "ceremony": CEREMONY,
        "checksumsPath": "release/checksums.sha256",
        "files": entries,
        "manifestPath": f"release/update-manifest-{version}.txt",
        "registrySha256": registry_sha,
        "repository": repository,
        "schemaVersion": SCHEMA_VERSION,
        "signingKeyId": V1_KEY_ID,
        "sourceSha": source_sha,
        "stageRunAttempt": run_attempt,
        "stageRunId": run_id,
        "stageWorkflow": STAGE_WORKFLOW,
        "version": version,
    }
    _exclusive_write(arguments.output, _canonical_json(request))


def validate_request(
    request_path: Path,
    release_dir: Path,
    registry_path: Path,
    expected_repository: str | None = None,
    expected_sha: str | None = None,
    expected_run_id: int | None = None,
    expected_run_attempt: int | None = None,
    expected_artifact: str | None = None,
    finalized: bool = False,
) -> tuple[dict[str, Any], bytes]:
    request, raw = _strict_json(request_path, MAX_REQUEST_BYTES, "bridge signing request")
    if set(request) != REQUEST_KEYS:
        raise BridgeError("bridge signing request has unknown or missing fields")
    if request["schemaVersion"] != SCHEMA_VERSION or request["ceremony"] != CEREMONY:
        raise BridgeError("bridge signing request schema or ceremony is unsupported")
    if request["repository"] != REPOSITORY or request["stageWorkflow"] != STAGE_WORKFLOW:
        raise BridgeError("bridge signing request repository or workflow is wrong")
    source_sha = _strict_sha(request["sourceSha"], COMMIT_RE, "request source SHA")
    run_id = _strict_positive_integer(request["stageRunId"], "request run id")
    run_attempt = _strict_positive_integer(request["stageRunAttempt"], "request run attempt")
    version, numeric = _version_parts(request["version"])
    expected_name = f"suavoagent-v1-bridge-request-{run_id}-{run_attempt}"
    if request["artifactName"] != expected_name or request["signingKeyId"] != V1_KEY_ID:
        raise BridgeError("bridge signing request artifact or key identity is wrong")
    if request["authenticodeSignerSha256"] != _authenticode_signers(
        request["authenticodeSignerSha256"]
    ):
        raise BridgeError("bridge signing request signer allowlist is not canonical")
    if request["manifestPath"] != f"release/update-manifest-{version}.txt" or request["checksumsPath"] != "release/checksums.sha256":
        raise BridgeError("bridge signing request has an invalid signed path")
    assert_bridge_source(registry_path)
    _, _, registry_sha = _registry(registry_path)
    if request["registrySha256"] != registry_sha:
        raise BridgeError("bridge request registry digest does not match exact source")
    for actual, expected, label in (
        (request["repository"], expected_repository, "repository"),
        (source_sha, expected_sha, "source SHA"),
        (run_id, expected_run_id, "stage run id"),
        (run_attempt, expected_run_attempt, "stage run attempt"),
        (expected_name, expected_artifact, "artifact name"),
    ):
        if expected is not None and actual != expected:
            raise BridgeError(f"bridge request {label} does not match the finalization context")

    release_files = _validate_release_allowlist(release_dir, version, finalized=finalized)
    generated_names = {
        "checksums.sha256",
        f"update-manifest-{version}.sig",
        "checksums.sha256.sig",
    }
    files = tuple(
        path for path in release_files
        if path.relative_to(release_dir).as_posix() not in generated_names
    )
    entries = request["files"]
    if not isinstance(entries, list) or not entries:
        raise BridgeError("bridge signing request has no fixed file hashes")
    expected_entries = release_file_entries_once(
        files, release_dir, 2 * 1024 * 1024 * 1024
    )
    if tuple(entries) != expected_entries:
        raise BridgeError("bridge request fixed file hashes do not match artifact bytes")
    manifest = _read_regular(release_dir / f"update-manifest-{version}.txt", 128 * 1024, "bridge manifest")
    if manifest != _expected_manifest(release_dir, REPOSITORY, version, numeric):
        raise BridgeError("bridge manifest is not the exact Release 1 contract")
    receipt, _ = _strict_json(release_dir / "field-release-receipt.json", 128 * 1024, "field release receipt")
    if set(receipt) != FIELD_RELEASE_RECEIPT_KEYS:
        raise BridgeError("field release receipt has unknown or missing fields")
    if (receipt.get("sourceCommit") != source_sha or
        receipt.get("releaseTag") != version or
        receipt.get("otaSigningKeyId") != V1_KEY_ID):
        raise BridgeError(
            "field release receipt does not bind the request source, version, and ota-update-v1 root"
        )
    return request, raw


def _validate_local_source(source_root: Path, source_sha: str) -> None:
    error = validate_local_source(
        source_root,
        source_sha,
        frozenset(
            {
                "https://github.com/MinaH153/SuavoAgent.git",
                "git@github.com:MinaH153/SuavoAgent.git",
                "ssh://git@github.com/MinaH153/SuavoAgent.git",
            }
        ),
    )
    if error is not None:
        raise BridgeError(error)


def _private_key_public_der(key_path: Path) -> bytes:
    try:
        with open_historic_key(key_path) as key:
            return key.public_der()
    except HistoricKeyError as error:
        raise BridgeError(str(error)) from error


def _sign_der(key_path: Path, input_path: Path) -> bytes:
    payload = _read_regular(input_path, 128 * 1024, "historic signing payload")
    try:
        with open_historic_key(key_path) as key:
            return key.sign_der(payload)
    except HistoricKeyError as error:
        raise BridgeError(str(error)) from error


def local_sign(arguments: argparse.Namespace) -> None:
    request_path = arguments.stage_dir / "bridge-signing-request.json"
    release_dir = arguments.stage_dir / "release"
    request, request_raw = validate_request(request_path, release_dir, arguments.registry)
    _validate_local_source(arguments.source_root, request["sourceSha"])
    try:
        verify_authenticated_handoff(
            arguments.descriptor,
            arguments.descriptor_signature,
            request,
            request_raw,
            arguments.registry,
        )
    except HandoffError as error:
        raise BridgeError(str(error)) from error
    if arguments.response_json.exists() or arguments.response_json.is_symlink():
        raise BridgeError("response JSON output already exists")
    if arguments.response_b64.exists() or arguments.response_b64.is_symlink():
        raise BridgeError("response Base64 output already exists")

    version = request["version"]
    manifest_path = release_dir / f"update-manifest-{version}.txt"
    manifest_raw = _read_regular(manifest_path, 128 * 1024, "bridge manifest")
    try:
        manifest_digest, manifest_size = request_file_binding(
            request, f"update-manifest-{version}.txt"
        )
        if _sha256(manifest_raw) != manifest_digest or len(manifest_raw) != manifest_size:
            raise BridgeError("captured manifest no longer matches the authenticated request")
        checksum_entries = _checksum_publication_entries(release_dir, version)
        _, roots, _ = _registry(arguments.registry)
        with open_historic_key(arguments.key) as key:
            if key.public_der() != roots[V1_KEY_ID]:
                raise BridgeError("local private key does not match reviewed ota-update-v1")
            manifest_hex = der_to_p1363_hex(key.sign_der(manifest_raw)).encode("ascii")
            checksums_raw = checksums_from_authenticated_request(
                request, checksum_entries, manifest_hex
            )
            checksums_der = key.sign_der(checksums_raw)
        v1_only = {V1_KEY_ID: roots[V1_KEY_ID]}
        if not verify_signature_bytes(v1_only, manifest_raw, manifest_hex, "p1363-hex"):
            raise BridgeError("local manifest signature failed immediate v1 verification")
        if not verify_signature_bytes(v1_only, checksums_raw, checksums_der, "der"):
            raise BridgeError("local checksum signature failed immediate v1 verification")
    except (HandoffError, HistoricKeyError) as error:
        raise BridgeError(str(error)) from error
    response = {
        "artifactName": request["artifactName"],
        "authenticodeSignerSha256": request["authenticodeSignerSha256"],
        "ceremony": CEREMONY,
        "checksumsSignature": {
            "format": "der",
            "inputPath": request["checksumsPath"],
            "inputSha256": _sha256(checksums_raw),
            "outputPath": "release/checksums.sha256.sig",
            "signatureBase64": base64.b64encode(checksums_der).decode("ascii"),
        },
        "checksumsBase64": base64.b64encode(checksums_raw).decode("ascii"),
        "manifestSignature": {
            "format": "p1363-hex",
            "inputPath": request["manifestPath"],
            "inputSha256": _sha256(manifest_raw),
            "outputPath": f"release/update-manifest-{version}.sig",
            "signatureBase64": base64.b64encode(manifest_hex).decode("ascii"),
        },
        "repository": request["repository"],
        "requestSha256": _sha256(request_raw),
        "schemaVersion": SCHEMA_VERSION,
        "signingKeyId": V1_KEY_ID,
        "sourceSha": request["sourceSha"],
        "stageRunAttempt": request["stageRunAttempt"],
        "stageRunId": request["stageRunId"],
        "stageWorkflow": request["stageWorkflow"],
        "version": version,
    }
    response_raw = _canonical_json(response)
    response_b64 = base64.b64encode(response_raw) + b"\n"
    _exclusive_write(arguments.response_json, response_raw, 0o644)
    try:
        _exclusive_write(arguments.response_b64, response_b64, 0o644)
    except BaseException:
        arguments.response_json.unlink(missing_ok=True)
        raise


def _decode_canonical_base64(value: str, label: str, maximum_bytes: int) -> bytes:
    try:
        decoded = base64.b64decode(value, validate=True)
    except (binascii.Error, ValueError) as error:
        raise BridgeError(f"{label} is not canonical Base64") from error
    if not decoded or len(decoded) > maximum_bytes or base64.b64encode(decoded).decode("ascii") != value:
        raise BridgeError(f"{label} is not canonical Base64")
    return decoded


def _validate_signature_entry(
    entry: object,
    expected_input: str,
    expected_output: str,
    expected_format: str,
    input_raw: bytes,
) -> bytes:
    if not isinstance(entry, dict) or set(entry) != SIGNATURE_KEYS:
        raise BridgeError("bridge response signature has unknown or missing fields")
    if entry["inputPath"] != expected_input or entry["outputPath"] != expected_output or entry["format"] != expected_format:
        raise BridgeError("bridge response signature paths or format are wrong")
    if entry["inputSha256"] != _sha256(input_raw):
        raise BridgeError("bridge response signature input digest is wrong")
    if not isinstance(entry["signatureBase64"], str):
        raise BridgeError("bridge response signature must be Base64 text")
    return _decode_canonical_base64(entry["signatureBase64"], "bridge signature", 512)


def _validate_response(
    arguments: argparse.Namespace, response_raw: bytes, finalized: bool
) -> tuple[dict[str, Any], dict[str, Any], bytes, bytes, bytes]:
    request, request_raw = validate_request(
        arguments.request,
        arguments.release_dir,
        arguments.registry,
        expected_repository=arguments.expected_repository,
        expected_sha=arguments.expected_sha,
        expected_run_id=arguments.expected_run_id,
        expected_run_attempt=arguments.expected_run_attempt,
        expected_artifact=arguments.expected_artifact,
        finalized=finalized,
    )
    try:
        response = json.loads(response_raw)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise BridgeError("bridge response is not JSON") from error
    if not isinstance(response, dict) or response_raw != _canonical_json(response):
        raise BridgeError("bridge response is not canonical JSON")
    if set(response) != RESPONSE_KEYS:
        raise BridgeError("bridge response has unknown or missing fields")
    for key in (
        "schemaVersion", "ceremony", "repository", "sourceSha", "stageWorkflow",
        "stageRunId", "stageRunAttempt", "version", "artifactName", "signingKeyId",
        "authenticodeSignerSha256",
    ):
        if response[key] != request[key]:
            raise BridgeError(f"bridge response does not bind request field {key}")
    if response["requestSha256"] != _sha256(request_raw):
        raise BridgeError("bridge response request digest is wrong")

    version = request["version"]
    manifest_input = arguments.release_dir / f"update-manifest-{version}.txt"
    manifest_raw = _read_regular(manifest_input, 128 * 1024, "bridge manifest")
    manifest_signature = _validate_signature_entry(
        response["manifestSignature"], request["manifestPath"],
        f"release/update-manifest-{version}.sig", "p1363-hex", manifest_raw,
    )
    if not isinstance(response["checksumsBase64"], str):
        raise BridgeError("bridge response checksums must be Base64 text")
    checksums_raw = _decode_canonical_base64(
        response["checksumsBase64"], "bridge checksums", 128 * 1024
    )
    if checksums_raw != _expected_checksums(
        arguments.release_dir,
        version,
        manifest_signature,
        finalized=finalized,
    ):
        raise BridgeError("bridge response checksums are not deterministic or exact")
    checksums_signature = _validate_signature_entry(
        response["checksumsSignature"], request["checksumsPath"],
        "release/checksums.sha256.sig", "der", checksums_raw,
    )
    _, roots, _ = _registry(arguments.registry)
    with tempfile.TemporaryDirectory() as temporary:
        temporary_path = Path(temporary)
        checksums_input = temporary_path / "checksums.sha256"
        manifest_sig = temporary_path / "manifest.sig"
        checksums_sig = temporary_path / "checksums.sig"
        checksums_input.write_bytes(checksums_raw)
        manifest_sig.write_bytes(manifest_signature)
        checksums_sig.write_bytes(checksums_signature)
        v1_only = {V1_KEY_ID: roots[V1_KEY_ID]}
        if not verify_signature(v1_only, manifest_input, manifest_sig, "p1363-hex"):
            raise BridgeError("manifest signature did not verify specifically with ota-update-v1")
        if not verify_signature(v1_only, checksums_input, checksums_sig, "der"):
            raise BridgeError("checksum signature did not verify specifically with ota-update-v1")
    return request, response, manifest_signature, checksums_raw, checksums_signature


def finalize_response(arguments: argparse.Namespace) -> None:
    response_b64 = _read_regular(
        arguments.response_b64_file, MAX_RESPONSE_BYTES * 2, "bridge response Base64"
    )
    try:
        response_text = response_b64.decode("ascii").strip()
    except UnicodeDecodeError as error:
        raise BridgeError("bridge response Base64 is not ASCII") from error
    response_raw = _decode_canonical_base64(response_text, "bridge response", MAX_RESPONSE_BYTES)
    request, response, manifest_signature, checksums_raw, checksums_signature = _validate_response(
        arguments, response_raw, finalized=False
    )

    version = request["version"]
    outputs = (
        (arguments.release_dir / f"update-manifest-{version}.sig", manifest_signature),
        (arguments.release_dir / "checksums.sha256", checksums_raw),
        (arguments.release_dir / "checksums.sha256.sig", checksums_signature),
        (arguments.response_json, response_raw),
    )
    for path, _ in outputs:
        if path.exists() or path.is_symlink():
            raise BridgeError(f"output already exists: {path}")
    written: list[Path] = []
    try:
        for path, raw in outputs[:3]:
            _exclusive_write(path, raw)
            written.append(path)
        _validate_release_allowlist(arguments.release_dir, version, finalized=True)
        _exclusive_write(arguments.response_json, response_raw)
        written.append(arguments.response_json)
    except BaseException:
        for path in written:
            path.unlink(missing_ok=True)
        raise
    print(f"version={version}")
    print(f"source_sha={request['sourceSha']}")
    print(f"run_attempt={request['stageRunAttempt']}")
    print(f"artifact_name={request['artifactName']}")
    print(f"request_sha256={response['requestSha256']}")
    print(f"authenticode_signer_sha256={request['authenticodeSignerSha256']}")


def validate_final(arguments: argparse.Namespace) -> None:
    response, response_raw = _strict_json(
        arguments.stage_dir / "bridge-signing-response.json",
        MAX_RESPONSE_BYTES,
        "bridge signing response",
    )
    validation = argparse.Namespace(
        request=arguments.stage_dir / "bridge-signing-request.json",
        release_dir=arguments.stage_dir / "release",
        registry=arguments.registry,
        expected_repository=arguments.expected_repository,
        expected_sha=arguments.expected_sha,
        expected_run_id=arguments.expected_run_id,
        expected_run_attempt=arguments.expected_run_attempt,
        expected_artifact=arguments.expected_artifact,
    )
    request, checked, manifest_signature, checksums_raw, checksums_signature = _validate_response(
        validation, response_raw, finalized=True
    )
    if checked != response:
        raise BridgeError("final bridge response changed during validation")
    response_files = (
        (f"update-manifest-{request['version']}.sig", manifest_signature),
        ("checksums.sha256", checksums_raw),
        ("checksums.sha256.sig", checksums_signature),
    )
    for name, expected in response_files:
        if _read_regular(arguments.stage_dir / "release" / name, 128 * 1024, name) != expected:
            raise BridgeError(f"final response file does not match response: {name}")
    _validate_release_allowlist(arguments.stage_dir / "release", request["version"], finalized=True)
