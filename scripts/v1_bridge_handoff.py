#!/usr/bin/env python3
"""Authenticated v2-to-v1 handoff for the one-time Release 1 bridge."""

from __future__ import annotations

import hashlib
import json
from pathlib import Path
import re
import stat
import subprocess
import sys
from typing import Any

from ota_update_trust_roots import (
    TrustRegistryError,
    load_registry_configuration,
    verify_signature_bytes,
)
from v1_bridge_file_io import SecureFileError, read_regular_once


CEREMONY = "ota-update-v1-bridge-release-1"
PURPOSE = "authorize-reviewed-v1-bridge-request"
REPOSITORY = "MinaH153/SuavoAgent"
REQUEST_PATH = "bridge-signing-request.json"
STAGE_WORKFLOW = ".github/workflows/v1-bridge-stage.yml"
AUTHORIZATION_WORKFLOW = ".github/workflows/v1-bridge-authorize.yml"
V2_KEY_ID = "ota-update-v2"
ROOT = Path(__file__).resolve().parents[1]
COMMIT_RE = re.compile(r"[0-9a-f]{40}")
DIGEST_RE = re.compile(r"[0-9a-f]{64}")
SAFE_ASSET_NAME_RE = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,199}")
DESCRIPTOR_KEYS = frozenset(
    {
        "artifactDigestSha256",
        "artifactName",
        "authenticodeSignerSha256",
        "authorizationRunAttempt",
        "authorizationRunId",
        "authorizationWorkflow",
        "ceremony",
        "purpose",
        "releaseFileCount",
        "repository",
        "requestFilesSha256",
        "requestPath",
        "requestSha256",
        "schemaVersion",
        "signingKeyId",
        "sourceSha",
        "stageRunAttempt",
        "stageRunId",
        "stageWorkflow",
        "version",
    }
)


class HandoffError(ValueError):
    pass


def canonical_json(document: object) -> bytes:
    return (
        json.dumps(document, ensure_ascii=True, separators=(",", ":"), sort_keys=True)
        + "\n"
    ).encode("utf-8")


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def regenerate_final_sbom(
    release_dir: Path, version: str, source_sha: str
) -> None:
    subprocess.run(
        (
            sys.executable,
            str(ROOT / "scripts/generate-release-sbom.py"),
            "--release-dir",
            str(release_dir),
            "--version",
            version,
            "--source-commit",
            source_sha,
            "--output",
            str(release_dir / "suavoagent.spdx.json"),
            "--exclude-finalization-outputs",
        ),
        cwd=ROOT,
        check=True,
    )


def _read_regular(path: Path, maximum: int, label: str) -> bytes:
    try:
        return read_regular_once(path, maximum, label)
    except SecureFileError as error:
        raise HandoffError(str(error)) from error


def _strict_json(path: Path, maximum: int, label: str) -> tuple[dict[str, Any], bytes]:
    raw = _read_regular(path, maximum, label)
    try:
        document = json.loads(raw)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise HandoffError(f"{label} is not valid JSON") from error
    if not isinstance(document, dict) or raw != canonical_json(document):
        raise HandoffError(f"{label} must be one canonical JSON object")
    return document, raw


def _positive_integer(value: object, label: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value <= 0:
        raise HandoffError(f"{label} must be a positive integer")
    return value


def _exact_string(value: object, pattern: re.Pattern[str], label: str) -> str:
    if not isinstance(value, str) or pattern.fullmatch(value) is None:
        raise HandoffError(f"{label} is malformed")
    return value


def _request_identity(request: dict[str, Any]) -> tuple[str, int, int, str, list[object]]:
    if (
        request.get("ceremony") != CEREMONY
        or request.get("repository") != REPOSITORY
        or request.get("stageWorkflow") != STAGE_WORKFLOW
    ):
        raise HandoffError("handoff request has the wrong ceremony identity")
    source_sha = _exact_string(request.get("sourceSha"), COMMIT_RE, "request source SHA")
    run_id = _positive_integer(request.get("stageRunId"), "request stage run id")
    run_attempt = _positive_integer(
        request.get("stageRunAttempt"), "request stage run attempt"
    )
    artifact_name = f"suavoagent-v1-bridge-request-{run_id}-{run_attempt}"
    if request.get("artifactName") != artifact_name:
        raise HandoffError("handoff request artifact identity is wrong")
    files = request.get("files")
    if not isinstance(files, list) or not files:
        raise HandoffError("handoff request contains no fixed release file bindings")
    return source_sha, run_id, run_attempt, artifact_name, files


def descriptor_document(
    request: dict[str, Any],
    request_raw: bytes,
    artifact_digest_sha256: str,
    authorization_run_id: int,
    authorization_run_attempt: int,
) -> dict[str, object]:
    artifact_digest = _exact_string(
        artifact_digest_sha256, DIGEST_RE, "GitHub request artifact digest"
    )
    source_sha, run_id, run_attempt, artifact_name, files = _request_identity(request)
    authenticode = request.get("authenticodeSignerSha256")
    version = request.get("version")
    if not isinstance(authenticode, str) or not isinstance(version, str):
        raise HandoffError("handoff request release identity is malformed")
    return {
        "artifactDigestSha256": artifact_digest,
        "artifactName": artifact_name,
        "authenticodeSignerSha256": authenticode,
        "authorizationRunAttempt": _positive_integer(
            authorization_run_attempt, "authorization run attempt"
        ),
        "authorizationRunId": _positive_integer(authorization_run_id, "authorization run id"),
        "authorizationWorkflow": AUTHORIZATION_WORKFLOW,
        "ceremony": CEREMONY,
        "purpose": PURPOSE,
        "releaseFileCount": len(files),
        "repository": REPOSITORY,
        "requestFilesSha256": sha256(canonical_json(files)),
        "requestPath": REQUEST_PATH,
        "requestSha256": sha256(request_raw),
        "schemaVersion": 1,
        "signingKeyId": V2_KEY_ID,
        "sourceSha": source_sha,
        "stageRunAttempt": run_attempt,
        "stageRunId": run_id,
        "stageWorkflow": STAGE_WORKFLOW,
        "version": version,
    }


def request_file_binding(request: dict[str, Any], relative_path: str) -> tuple[str, int]:
    matches = tuple(
        entry for entry in request.get("files", ())
        if isinstance(entry, dict) and entry.get("path") == f"release/{relative_path}"
    )
    if len(matches) != 1:
        raise HandoffError(f"authenticated request does not bind exactly one {relative_path}")
    digest = matches[0].get("sha256")
    size = matches[0].get("size")
    if not isinstance(digest, str) or DIGEST_RE.fullmatch(digest) is None:
        raise HandoffError(f"authenticated request has malformed {relative_path} digest")
    if isinstance(size, bool) or not isinstance(size, int) or size <= 0:
        raise HandoffError(f"authenticated request has malformed {relative_path} size")
    return digest, size


def checksums_from_authenticated_request(
    request: dict[str, Any], entries: tuple[tuple[str, str], ...],
    manifest_signature: bytes
) -> bytes:
    lines = [
        f"{request_file_binding(request, relative_path)[0]}  {asset_name}"
        for relative_path, asset_name in entries
    ]
    version = request.get("version")
    if not isinstance(version, str):
        raise HandoffError("authenticated request version is malformed")
    lines.append(f"{sha256(manifest_signature)}  update-manifest-{version}.sig")
    return ("\n".join(lines) + "\n").encode("ascii")


def publication_checksum_entries(
    release_dir: Path,
    files: tuple[Path, ...],
    version: str,
) -> tuple[tuple[str, str], ...]:
    excluded = {
        "checksums.sha256",
        "checksums.sha256.sig",
        f"update-manifest-{version}.sig",
    }
    entries = tuple(
        (relative_path, path.name)
        for path in files
        if (relative_path := path.relative_to(release_dir).as_posix())
        not in excluded
    )
    names = tuple(asset_name for _, asset_name in entries)
    if any(SAFE_ASSET_NAME_RE.fullmatch(name) is None for name in names):
        raise HandoffError("release checksum assets contain an unsafe GitHub basename")
    if len(names) != len(set(names)):
        raise HandoffError("release checksum assets contain duplicate GitHub basenames")
    return entries


def checksums_from_release_files(
    release_dir: Path,
    entries: tuple[tuple[str, str], ...],
    version: str,
    manifest_signature: bytes,
) -> bytes:
    lines = [
        f"{sha256(_read_regular(release_dir / relative_path, 2 * 1024 * 1024 * 1024, relative_path))}  {asset_name}"
        for relative_path, asset_name in entries
    ]
    lines.append(f"{sha256(manifest_signature)}  update-manifest-{version}.sig")
    return ("\n".join(lines) + "\n").encode("ascii")


def verify_authenticated_handoff(
    descriptor_path: Path,
    signature_path: Path,
    request: dict[str, Any],
    request_raw: bytes,
    registry_path: Path,
    expected_artifact_digest: str | None = None,
    expected_authorization_run_id: int | None = None,
    expected_authorization_run_attempt: int | None = None,
) -> dict[str, Any]:
    descriptor, descriptor_raw = _strict_json(
        descriptor_path, 64 * 1024, "v2 bridge handoff descriptor"
    )
    if set(descriptor) != DESCRIPTOR_KEYS:
        raise HandoffError("v2 bridge handoff descriptor has unknown or missing fields")
    try:
        _, roots = load_registry_configuration(registry_path)
    except TrustRegistryError as error:
        raise HandoffError(str(error)) from error
    signature_raw = _read_regular(signature_path, 512, "v2 bridge handoff signature")
    if V2_KEY_ID not in roots or not verify_signature_bytes(
        {V2_KEY_ID: roots[V2_KEY_ID]}, descriptor_raw, signature_raw, "der"
    ):
        raise HandoffError("bridge handoff was not signed specifically by reviewed ota-update-v2")
    artifact_digest = descriptor.get("artifactDigestSha256")
    if not isinstance(artifact_digest, str):
        raise HandoffError("v2 bridge handoff artifact digest is malformed")
    authorization_run_id = descriptor.get("authorizationRunId")
    authorization_run_attempt = descriptor.get("authorizationRunAttempt")
    expected = descriptor_document(
        request,
        request_raw,
        artifact_digest,
        authorization_run_id,
        authorization_run_attempt,
    )
    if descriptor != expected:
        raise HandoffError("v2 bridge handoff does not bind the exact reviewed request")
    if expected_artifact_digest is not None and artifact_digest != _exact_string(
        expected_artifact_digest, DIGEST_RE, "REST request artifact digest"
    ):
        raise HandoffError("v2 bridge handoff does not bind the REST request artifact digest")
    if (
        expected_authorization_run_id is not None
        and authorization_run_id != expected_authorization_run_id
    ) or (
        expected_authorization_run_attempt is not None
        and authorization_run_attempt != expected_authorization_run_attempt
    ):
        raise HandoffError("v2 bridge handoff does not bind the authorization run and attempt")
    return descriptor
