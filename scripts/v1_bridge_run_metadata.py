#!/usr/bin/env python3
"""Validate GitHub run metadata around the v1 bridge handoff."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import re
import subprocess
import sys

from ota_update_trust_roots import TrustRegistryError, verify_signature
from v1_bridge_handoff import (
    HandoffError,
    descriptor_document,
    verify_authenticated_handoff,
)
from v1_bridge_release import (
    BridgeError,
    COMMIT_RE,
    DEFAULT_REGISTRY,
    REPOSITORY,
    STAGE_WORKFLOW,
    V1_KEY_ID,
    V2_KEY_ID,
    VERSION_RE,
    _canonical_json,
    _exclusive_write,
    _read_regular,
    _registry,
    _sha256,
    _strict_sha,
    _validate_release_allowlist,
    _version_parts,
    validate_request,
)


STAGE_WORKFLOW_NAME = "OTA v1 bridge - stage"
AUTHORIZATION_WORKFLOW = ".github/workflows/v1-bridge-authorize.yml"
AUTHORIZATION_WORKFLOW_NAME = "OTA v1 bridge - authorize"
SHA256_RE = re.compile(r"(?:sha256:)?([0-9a-f]{64})")


def _external_json(path: Path, maximum: int, label: str) -> object:
    try:
        return json.loads(_read_regular(path, maximum, label))
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise BridgeError(f"{label} is not valid JSON") from error


def _digest(value: object, label: str) -> str:
    if not isinstance(value, str) or (match := SHA256_RE.fullmatch(value)) is None:
        raise BridgeError(f"{label} is not an exact SHA-256 digest")
    return match.group(1)


def _artifact_entries(path: Path, label: str) -> tuple[dict[str, object], ...]:
    document = _external_json(path, 16 * 1024 * 1024, label)
    pages = document if isinstance(document, list) else [document]
    if not all(isinstance(page, dict) and isinstance(page.get("artifacts"), list) for page in pages):
        raise BridgeError(f"{label} is malformed")
    entries = tuple(entry for page in pages for entry in page["artifacts"])
    if not all(isinstance(entry, dict) for entry in entries):
        raise BridgeError(f"{label} contains a malformed artifact")
    return entries


def validate_stage_metadata(arguments: argparse.Namespace) -> None:
    metadata = _external_json(arguments.metadata, 1024 * 1024, "stage run metadata")
    if not isinstance(metadata, dict):
        raise BridgeError("stage run metadata must be an object")
    repository = metadata.get("repository")
    if not isinstance(repository, dict) or repository.get("full_name") != REPOSITORY:
        raise BridgeError("stage run belongs to the wrong repository")
    expected = {
        "id": arguments.expected_run_id,
        "workflow_id": arguments.expected_workflow_id,
        "run_attempt": arguments.expected_run_attempt,
        "event": "workflow_dispatch",
        "status": "completed",
        "conclusion": "success",
        "head_branch": "main",
        "head_sha": arguments.expected_sha,
        "path": STAGE_WORKFLOW,
        "name": STAGE_WORKFLOW_NAME,
    }
    for key, value in expected.items():
        if metadata.get(key) != value:
            raise BridgeError(f"stage run metadata has wrong {key}")


def validate_active_stage_metadata(arguments: argparse.Namespace) -> None:
    metadata = _external_json(arguments.metadata, 1024 * 1024, "active stage run metadata")
    if not isinstance(metadata, dict):
        raise BridgeError("active stage run metadata must be an object")
    repository = metadata.get("repository")
    if not isinstance(repository, dict) or repository.get("full_name") != REPOSITORY:
        raise BridgeError("active stage run belongs to the wrong repository")
    expected = {
        "id": arguments.expected_run_id,
        "workflow_id": arguments.expected_workflow_id,
        "run_attempt": arguments.expected_run_attempt,
        "event": "workflow_dispatch",
        "status": "in_progress",
        "conclusion": None,
        "head_branch": "main",
        "head_sha": arguments.expected_sha,
        "path": STAGE_WORKFLOW,
        "name": STAGE_WORKFLOW_NAME,
    }
    for key, value in expected.items():
        if metadata.get(key) != value:
            raise BridgeError(f"active stage run metadata has wrong {key}")


def validate_authorization_metadata(arguments: argparse.Namespace) -> None:
    metadata = _external_json(arguments.metadata, 1024 * 1024, "authorization run metadata")
    if not isinstance(metadata, dict):
        raise BridgeError("authorization run metadata must be an object")
    repository = metadata.get("repository")
    if not isinstance(repository, dict) or repository.get("full_name") != REPOSITORY:
        raise BridgeError("authorization run belongs to the wrong repository")
    expected = {
        "id": arguments.expected_run_id,
        "workflow_id": arguments.expected_workflow_id,
        "run_attempt": arguments.expected_run_attempt,
        "event": "workflow_dispatch",
        "status": "completed",
        "conclusion": "success",
        "head_branch": "main",
        "head_sha": arguments.expected_sha,
        "path": AUTHORIZATION_WORKFLOW,
        "name": AUTHORIZATION_WORKFLOW_NAME,
        "display_title": f"Authorize v1 bridge stage {arguments.expected_stage_run_id}",
    }
    for key, value in expected.items():
        if metadata.get(key) != value:
            raise BridgeError(f"authorization run metadata has wrong {key}")


def validate_artifacts(arguments: argparse.Namespace) -> None:
    artifacts = _artifact_entries(arguments.artifacts, "stage artifacts metadata")
    for expected_name in (arguments.expected_artifact, arguments.expected_descriptor):
        matches = tuple(
            entry for entry in artifacts
            if isinstance(entry, dict) and entry.get("name") == expected_name
        )
        if len(matches) != 1:
            raise BridgeError(f"stage run must expose exactly one artifact: {expected_name}")
        artifact = matches[0]
        if artifact.get("expired") is not False or not isinstance(artifact.get("id"), int):
            raise BridgeError("bridge handoff artifact is expired or malformed")
        _digest(artifact.get("digest"), "REST artifact digest")
        workflow_run = artifact.get("workflow_run")
        if (
            not isinstance(workflow_run, dict)
            or workflow_run.get("id") != arguments.expected_run_id
            or workflow_run.get("head_sha") != arguments.expected_sha
        ):
            raise BridgeError("bridge artifact is not bound to the expected stage run and SHA")


def validate_descriptor_artifact(arguments: argparse.Namespace) -> None:
    artifacts = _artifact_entries(arguments.artifacts, "authorization artifacts metadata")
    matches = tuple(
        entry for entry in artifacts
        if isinstance(entry, dict) and entry.get("name") == arguments.expected_descriptor
    )
    if len(matches) != 1:
        raise BridgeError("authorization run must expose exactly one descriptor artifact")
    artifact = matches[0]
    workflow_run = artifact.get("workflow_run")
    if (
        artifact.get("expired") is not False
        or not isinstance(artifact.get("id"), int)
        or not isinstance(workflow_run, dict)
        or workflow_run.get("id") != arguments.expected_run_id
        or workflow_run.get("head_sha") != arguments.expected_sha
    ):
        raise BridgeError("descriptor artifact is expired, malformed, or bound to another run")
    _digest(artifact.get("digest"), "REST descriptor artifact digest")


def assert_not_replayed(arguments: argparse.Namespace) -> None:
    document = _external_json(arguments.runs, 16 * 1024 * 1024, "finalization runs metadata")
    pages = document if isinstance(document, list) else [document]
    if not all(isinstance(page, dict) and isinstance(page.get("workflow_runs"), list) for page in pages):
        raise BridgeError("finalization run history is malformed")
    runs = tuple(run for page in pages for run in page["workflow_runs"])
    title = f"Finalize v1 bridge stage {arguments.stage_run_id}"
    replays = tuple(
        run for run in runs
        if isinstance(run, dict)
        and run.get("id") != arguments.current_run_id
        and run.get("display_title") == title
        and run.get("conclusion") == "success"
    )
    if replays:
        raise BridgeError("this bridge stage run was already finalized successfully")


def publication_paths(arguments: argparse.Namespace) -> None:
    version, _ = _version_parts(arguments.version)
    for path in _validate_release_allowlist(arguments.release_dir, version, finalized=True):
        print(path)


def verify_v1(arguments: argparse.Namespace) -> None:
    _, roots, _ = _registry(arguments.registry)
    if not verify_signature(
        {V1_KEY_ID: roots[V1_KEY_ID]}, arguments.input, arguments.signature, arguments.format
    ):
        raise BridgeError("signature did not verify specifically with ota-update-v1")


def verify_v2(arguments: argparse.Namespace) -> None:
    _, roots, _ = _registry(arguments.registry)
    if V2_KEY_ID not in roots or not verify_signature(
        {V2_KEY_ID: roots[V2_KEY_ID]}, arguments.input, arguments.signature, arguments.format
    ):
        raise BridgeError("signature did not verify specifically with ota-update-v2")


def _request_artifact_digest(
    path: Path, expected_name: str, expected_run_id: int, expected_sha: str
) -> str:
    artifacts = _artifact_entries(path, "stage artifacts metadata")
    matches = tuple(
        artifact for artifact in artifacts
        if isinstance(artifact, dict) and artifact.get("name") == expected_name
    )
    if len(matches) != 1:
        raise BridgeError("stage run must expose exactly one request artifact")
    artifact = matches[0]
    workflow_run = artifact.get("workflow_run")
    if (
        artifact.get("expired") is not False
        or not isinstance(artifact.get("id"), int)
        or not isinstance(workflow_run, dict)
        or workflow_run.get("id") != expected_run_id
        or workflow_run.get("head_sha") != expected_sha
    ):
        raise BridgeError("request artifact is expired, malformed, or bound to another run")
    return _digest(artifact.get("digest"), "REST request artifact digest")


def validate_request_artifact(arguments: argparse.Namespace) -> None:
    _request_artifact_digest(
        arguments.artifacts,
        arguments.expected_artifact,
        arguments.expected_run_id,
        arguments.expected_sha,
    )


def write_descriptor(arguments: argparse.Namespace) -> None:
    digest = _digest(arguments.artifact_digest, "upload artifact digest")
    if arguments.repository != REPOSITORY or arguments.stage_workflow != STAGE_WORKFLOW:
        raise BridgeError("bridge descriptor repository or workflow is wrong")
    stage_dir = arguments.request.parent
    if (
        arguments.request.name != "bridge-signing-request.json"
        or arguments.release_dir != stage_dir / "release"
        or not stage_dir.is_dir()
        or stage_dir.is_symlink()
        or {entry.name for entry in stage_dir.iterdir()}
        != {"bridge-signing-request.json", "release"}
        or arguments.release_dir.is_symlink()
    ):
        raise BridgeError("downloaded request artifact has extra, missing, or linked top-level entries")
    rest_digest = _request_artifact_digest(
        arguments.artifacts,
        arguments.artifact_name,
        arguments.stage_run_id,
        arguments.source_sha,
    )
    if digest != rest_digest:
        raise BridgeError("upload output and REST request artifact digests disagree")
    request, request_raw = validate_request(
        arguments.request,
        arguments.release_dir,
        arguments.registry,
        expected_repository=arguments.repository,
        expected_sha=arguments.source_sha,
        expected_run_id=arguments.stage_run_id,
        expected_run_attempt=arguments.stage_run_attempt,
        expected_artifact=arguments.artifact_name,
    )
    descriptor = descriptor_document(
        request,
        request_raw,
        digest,
        arguments.authorization_run_id,
        arguments.authorization_run_attempt,
    )
    _exclusive_write(arguments.output, _canonical_json(descriptor))


def validate_descriptor(arguments: argparse.Namespace) -> None:
    request, request_raw = validate_request(
        arguments.request,
        arguments.release_dir,
        arguments.registry,
        expected_repository=REPOSITORY,
        expected_sha=arguments.expected_sha,
        expected_run_id=arguments.expected_run_id,
        expected_run_attempt=arguments.expected_run_attempt,
        expected_artifact=arguments.expected_artifact,
    )
    digest = _request_artifact_digest(
        arguments.artifacts,
        arguments.expected_artifact,
        arguments.expected_run_id,
        arguments.expected_sha,
    )
    verify_authenticated_handoff(
        arguments.descriptor,
        arguments.signature,
        request,
        request_raw,
        arguments.registry,
        expected_artifact_digest=digest,
        expected_authorization_run_id=arguments.expected_authorization_run_id,
        expected_authorization_run_attempt=arguments.expected_authorization_run_attempt,
    )


def _release_asset_bindings(path: Path, label: str) -> dict[str, tuple[str, int]]:
    pages = _external_json(path, 16 * 1024 * 1024, label)
    pages = pages if isinstance(pages, list) and all(isinstance(page, list) for page in pages) else [pages]
    flattened = tuple(asset for page in pages for asset in page)
    if not all(isinstance(asset, dict) for asset in flattened):
        raise BridgeError(f"{label} contains a malformed asset")
    assets = flattened
    actual_names = tuple(asset.get("name") for asset in assets)
    if not all(isinstance(name, str) for name in actual_names):
        raise BridgeError(f"{label} contains an invalid asset name")
    if len(actual_names) != len(set(actual_names)):
        raise BridgeError(f"{label} contains duplicate asset names")
    if any(asset.get("state") != "uploaded" for asset in assets):
        raise BridgeError(f"{label} contains an asset outside uploaded state")
    bindings: dict[str, tuple[str, int]] = {}
    for asset in assets:
        size = asset.get("size")
        if not isinstance(size, int) or isinstance(size, bool) or size < 0:
            raise BridgeError(f"{label} contains an invalid asset size")
        bindings[asset["name"]] = (
            _digest(asset.get("digest"), f"{label} asset digest"), size
        )
    return bindings


def validate_release_assets(arguments: argparse.Namespace) -> dict[str, tuple[str, int]]:
    actual = _release_asset_bindings(arguments.assets, "release assets metadata")
    version, _ = _version_parts(arguments.version)
    release_files = _validate_release_allowlist(arguments.release_dir, version, finalized=True)
    names = tuple(path.name for path in release_files)
    if len(names) != len(set(names)):
        raise BridgeError("release allowlist contains duplicate asset basenames")
    expected: dict[str, tuple[str, int]] = {}
    for path in release_files:
        raw = _read_regular(path, 2 * 1024 * 1024 * 1024, "release asset")
        expected[path.name] = (_sha256(raw), len(raw))
    if actual != expected:
        raise BridgeError("draft release asset names, sizes, or SHA-256 digests are not exact")
    return actual


def validate_publication_state(arguments: argparse.Namespace) -> None:
    release = _external_json(arguments.release, 1024 * 1024, "release metadata")
    if not isinstance(release, dict):
        raise BridgeError("release metadata must be an object")
    version, _ = _version_parts(arguments.version)
    source_sha = _strict_sha(arguments.source_sha, COMMIT_RE, "publication source SHA")
    draft = arguments.expected_draft == "true"
    immutable = arguments.expected_immutable == "true"
    if arguments.expected_release_id < 1:
        raise BridgeError("publication release ID must be positive")
    expected = {
        "id": arguments.expected_release_id,
        "tag_name": version,
        "target_commitish": source_sha,
        "draft": draft,
        "prerelease": False,
        "immutable": immutable,
    }
    for key, value in expected.items():
        if release.get(key) != value:
            raise BridgeError(f"release publication metadata has wrong {key}")
    published_at = release.get("published_at")
    if (draft and published_at is not None) or (
        not draft and (not isinstance(published_at, str) or not published_at)
    ):
        raise BridgeError("release publication timestamp does not match draft state")
    current = validate_release_assets(arguments)
    if arguments.reference_assets is not None:
        reference = _release_asset_bindings(
            arguments.reference_assets, "reference release assets metadata"
        )
        if current != reference:
            raise BridgeError("published release assets changed after pre-publication validation")


def validate_live_bridge_release(arguments: argparse.Namespace) -> None:
    from v1_bridge_convergence import validate_inventory, verify_convergence_claim

    claim = verify_convergence_claim(
        arguments.registry,
        arguments.evidence,
        arguments.claim,
        arguments.claim_signature,
        arguments.inventory,
        arguments.inventory_signature,
    )
    inventory, _ = validate_inventory(
        arguments.inventory, arguments.inventory_signature, arguments.registry
    )
    if (
        claim.get("bridgeReleaseTag") != inventory.get("bridgeReleaseTag")
        or claim.get("bridgeSourceSha") != inventory.get("bridgeSourceSha")
    ):
        raise BridgeError("convergence claim and authoritative inventory disagree")
    release = _external_json(arguments.release, 2 * 1024 * 1024, "live bridge release")
    if not isinstance(release, dict):
        raise BridgeError("live bridge release is malformed")
    if (
        release.get("tag_name") != claim["bridgeReleaseTag"]
        or release.get("draft") is not False
        or release.get("prerelease") is not False
        or release.get("published_at") != inventory["releasePublishedAtUtc"]
    ):
        raise BridgeError("live bridge release metadata no longer matches convergence authority")
    tag_ref = _external_json(arguments.tag_ref, 128 * 1024, "live bridge tag ref")
    tag_object = tag_ref.get("object") if isinstance(tag_ref, dict) else None
    if (
        not isinstance(tag_object, dict)
        or tag_object.get("type") != "commit"
        or tag_object.get("sha") != claim["bridgeSourceSha"]
    ):
        raise BridgeError("live bridge tag is not the exact immutable source commit")
    assets_document = _external_json(arguments.assets, 16 * 1024 * 1024, "live bridge assets")
    pages = assets_document if isinstance(assets_document, list) else [assets_document]
    if not all(isinstance(page, list) for page in pages):
        raise BridgeError("live bridge asset pages are malformed")
    flattened = tuple(asset for page in pages for asset in page)
    if not all(isinstance(asset, dict) for asset in flattened):
        raise BridgeError("live bridge assets contain a malformed asset")
    assets = flattened
    bindings = inventory["releaseBindings"]
    manifest_signature_name = bindings["updateManifestName"].removesuffix(".txt") + ".sig"
    expected = {
        "SuavoAgent.Core.exe": bindings["coreArtifactSha256"],
        "SuavoAgent.Broker.exe": bindings["brokerArtifactSha256"],
        "SuavoAgent.Helper.exe": bindings["helperArtifactSha256"],
        "SuavoAgent.Watchdog.exe": bindings["watchdogArtifactSha256"],
        bindings["burnArtifactName"]: bindings["burnArtifactSha256"],
        bindings["msiArtifactName"]: bindings["msiArtifactSha256"],
        "SuavoSetup.exe": bindings["maintenanceHostSha256"],
        "field-release-receipt.json": bindings["releaseReceiptSha256"],
        "checksums.sha256": bindings["checksumsSha256"],
        "checksums.sha256.sig": bindings["checksumsSignatureSha256"],
        bindings["updateManifestName"]: bindings["updateManifestSha256"],
        manifest_signature_name: bindings["updateManifestSignatureSha256"],
    }
    for name, digest in expected.items():
        matches = tuple(asset for asset in assets if asset.get("name") == name)
        if (
            len(matches) != 1
            or matches[0].get("state") != "uploaded"
            or not isinstance(matches[0].get("size"), int)
            or matches[0]["size"] <= 0
            or _digest(matches[0].get("digest"), f"live {name} digest") != digest
        ):
            raise BridgeError(f"live bridge asset no longer matches convergence authority: {name}")


def assert_version_newest(arguments: argparse.Namespace) -> None:
    document = _external_json(arguments.releases, 16 * 1024 * 1024, "release history")
    pages = document if isinstance(document, list) and all(isinstance(page, list) for page in document) else [document]
    releases = tuple(release for page in pages for release in page if isinstance(release, dict))
    target, _ = _version_parts(arguments.version)
    target_tuple = tuple(int(part) for part in target[1:].split("."))
    stable = tuple(
        release.get("tag_name", release.get("tagName")) for release in releases
        if release.get("draft", release.get("isDraft")) is False
        and release.get("prerelease", release.get("isPrerelease")) is False
    )
    parsed = tuple(
        tuple(int(part) for part in tag[1:].split("."))
        for tag in stable
        if isinstance(tag, str) and VERSION_RE.fullmatch(tag)
        and all(part == str(int(part)) for part in tag[1:].split("."))
    )
    if parsed and target_tuple <= max(parsed):
        raise BridgeError("bridge version must be strictly greater than greatest published stable release")


def assert_release_absent(arguments: argparse.Namespace) -> None:
    document = _external_json(arguments.releases, 16 * 1024 * 1024, "release history")
    pages = document if isinstance(document, list) and all(isinstance(page, list) for page in document) else [document]
    if not all(isinstance(page, list) for page in pages):
        raise BridgeError("release history is malformed")
    target, _ = _version_parts(arguments.version)
    for release in (entry for page in pages for entry in page):
        if not isinstance(release, dict):
            raise BridgeError("release history contains a malformed release")
        if release.get("tag_name", release.get("tagName")) == target:
            raise BridgeError("bridge release tag already exists, including as a draft")


def parser() -> argparse.ArgumentParser:
    result = argparse.ArgumentParser()
    commands = result.add_subparsers(dest="command", required=True)
    stage = commands.add_parser("validate-stage-metadata")
    stage.add_argument("--metadata", type=Path, required=True)
    stage.add_argument("--expected-sha", required=True)
    stage.add_argument("--expected-run-id", type=int, required=True)
    stage.add_argument("--expected-run-attempt", type=int, required=True)
    stage.add_argument("--expected-workflow-id", type=int, required=True)
    active = commands.add_parser("validate-active-stage-metadata")
    active.add_argument("--metadata", type=Path, required=True)
    active.add_argument("--expected-sha", required=True)
    active.add_argument("--expected-run-id", type=int, required=True)
    active.add_argument("--expected-run-attempt", type=int, required=True)
    active.add_argument("--expected-workflow-id", type=int, required=True)
    authorization = commands.add_parser("validate-authorization-metadata")
    authorization.add_argument("--metadata", type=Path, required=True)
    authorization.add_argument("--expected-sha", required=True)
    authorization.add_argument("--expected-run-id", type=int, required=True)
    authorization.add_argument("--expected-stage-run-id", type=int, required=True)
    authorization.add_argument("--expected-workflow-id", type=int, required=True)
    authorization.add_argument("--expected-run-attempt", type=int, required=True)
    artifacts = commands.add_parser("validate-artifacts")
    artifacts.add_argument("--artifacts", type=Path, required=True)
    artifacts.add_argument("--expected-artifact", required=True)
    artifacts.add_argument("--expected-descriptor", required=True)
    artifacts.add_argument("--expected-run-id", type=int, required=True)
    artifacts.add_argument("--expected-sha", required=True)
    descriptor_artifact = commands.add_parser("validate-descriptor-artifact")
    descriptor_artifact.add_argument("--artifacts", type=Path, required=True)
    descriptor_artifact.add_argument("--expected-descriptor", required=True)
    descriptor_artifact.add_argument("--expected-run-id", type=int, required=True)
    descriptor_artifact.add_argument("--expected-sha", required=True)
    request_artifact = commands.add_parser("validate-request-artifact")
    request_artifact.add_argument("--artifacts", type=Path, required=True)
    request_artifact.add_argument("--expected-artifact", required=True)
    request_artifact.add_argument("--expected-run-id", type=int, required=True)
    request_artifact.add_argument("--expected-sha", required=True)
    replay = commands.add_parser("assert-not-replayed")
    replay.add_argument("--runs", type=Path, required=True)
    replay.add_argument("--stage-run-id", type=int, required=True)
    replay.add_argument("--current-run-id", type=int, required=True)
    publication = commands.add_parser("publication-paths")
    publication.add_argument("--release-dir", type=Path, required=True)
    publication.add_argument("--version", required=True)
    verify = commands.add_parser("verify-v1")
    verify.add_argument("--input", type=Path, required=True)
    verify.add_argument("--signature", type=Path, required=True)
    verify.add_argument("--format", choices=("der", "p1363-hex"), required=True)
    verify.add_argument("--registry", type=Path, default=DEFAULT_REGISTRY)
    verify_v2_parser = commands.add_parser("verify-v2")
    verify_v2_parser.add_argument("--input", type=Path, required=True)
    verify_v2_parser.add_argument("--signature", type=Path, required=True)
    verify_v2_parser.add_argument("--format", choices=("der", "p1363-hex"), required=True)
    verify_v2_parser.add_argument("--registry", type=Path, default=DEFAULT_REGISTRY)
    descriptor = commands.add_parser("write-descriptor")
    descriptor.add_argument("--output", type=Path, required=True)
    descriptor.add_argument("--repository", required=True)
    descriptor.add_argument("--source-sha", required=True)
    descriptor.add_argument("--stage-workflow", required=True)
    descriptor.add_argument("--stage-run-id", type=int, required=True)
    descriptor.add_argument("--stage-run-attempt", type=int, required=True)
    descriptor.add_argument("--artifact-name", required=True)
    descriptor.add_argument("--artifact-digest", required=True)
    descriptor.add_argument("--authorization-run-id", type=int, required=True)
    descriptor.add_argument("--authorization-run-attempt", type=int, required=True)
    descriptor.add_argument("--artifacts", type=Path, required=True)
    descriptor.add_argument("--request", type=Path, required=True)
    descriptor.add_argument("--release-dir", type=Path, required=True)
    descriptor.add_argument("--registry", type=Path, default=DEFAULT_REGISTRY)
    validate = commands.add_parser("validate-descriptor")
    validate.add_argument("--descriptor", type=Path, required=True)
    validate.add_argument("--signature", type=Path, required=True)
    validate.add_argument("--request", type=Path, required=True)
    validate.add_argument("--release-dir", type=Path, required=True)
    validate.add_argument("--artifacts", type=Path, required=True)
    validate.add_argument("--expected-sha", required=True)
    validate.add_argument("--expected-run-id", type=int, required=True)
    validate.add_argument("--expected-run-attempt", type=int, required=True)
    validate.add_argument("--expected-artifact", required=True)
    validate.add_argument("--expected-authorization-run-id", type=int, required=True)
    validate.add_argument("--expected-authorization-run-attempt", type=int, required=True)
    validate.add_argument("--registry", type=Path, default=DEFAULT_REGISTRY)
    assets = commands.add_parser("validate-release-assets")
    assets.add_argument("--assets", type=Path, required=True)
    assets.add_argument("--release-dir", type=Path, required=True)
    assets.add_argument("--version", required=True)
    publication_state = commands.add_parser("validate-publication-state")
    publication_state.add_argument("--release", type=Path, required=True)
    publication_state.add_argument("--assets", type=Path, required=True)
    publication_state.add_argument("--reference-assets", type=Path)
    publication_state.add_argument("--release-dir", type=Path, required=True)
    publication_state.add_argument("--version", required=True)
    publication_state.add_argument("--source-sha", required=True)
    publication_state.add_argument("--expected-release-id", type=int, required=True)
    publication_state.add_argument(
        "--expected-draft", choices=("true", "false"), required=True
    )
    publication_state.add_argument(
        "--expected-immutable", choices=("true", "false"), required=True
    )
    live = commands.add_parser("validate-live-bridge-release")
    for name in (
        "release", "assets", "tag-ref", "inventory", "inventory-signature",
        "evidence", "claim", "claim-signature", "registry",
    ):
        live.add_argument(f"--{name}", type=Path, required=True)
    newest = commands.add_parser("assert-version-newest")
    newest.add_argument("--releases", type=Path, required=True)
    newest.add_argument("--version", required=True)
    absent = commands.add_parser("assert-release-absent")
    absent.add_argument("--releases", type=Path, required=True)
    absent.add_argument("--version", required=True)
    return result


def main() -> int:
    arguments = parser().parse_args()
    try:
        {
            "validate-stage-metadata": validate_stage_metadata,
            "validate-active-stage-metadata": validate_active_stage_metadata,
            "validate-authorization-metadata": validate_authorization_metadata,
            "validate-artifacts": validate_artifacts,
            "validate-descriptor-artifact": validate_descriptor_artifact,
            "validate-request-artifact": validate_request_artifact,
            "assert-not-replayed": assert_not_replayed,
            "publication-paths": publication_paths,
            "verify-v1": verify_v1,
            "verify-v2": verify_v2,
            "write-descriptor": write_descriptor,
            "validate-descriptor": validate_descriptor,
            "validate-release-assets": validate_release_assets,
            "validate-publication-state": validate_publication_state,
            "validate-live-bridge-release": validate_live_bridge_release,
            "assert-version-newest": assert_version_newest,
            "assert-release-absent": assert_release_absent,
        }[arguments.command](arguments)
        return 0
    except (BridgeError, HandoffError, OSError, subprocess.SubprocessError, TrustRegistryError) as error:
        print(str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
