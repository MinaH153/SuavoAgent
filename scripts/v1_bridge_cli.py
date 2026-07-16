#!/usr/bin/env python3
"""CLI boundary for the one-time ota-update-v1 Release 1 bridge."""

from __future__ import annotations

import os
import sys


LOCAL_SIGN_BOOTSTRAP_ENV = "SUAVO_V1_BRIDGE_ISOLATED_BOOTSTRAP"
LOCAL_SIGN_BOOTSTRAP_VALUE = "v1-bridge-clean-isolated-v1"
SIGNING_PATH = "/usr/bin:/bin:/usr/sbin:/sbin"
AUTHORITY_URL = "https://github.com/MinaH153/SuavoAgent.git"
APPROVED_ORIGINS = {
    "https://github.com/MinaH153/SuavoAgent.git",
    "git@github.com:MinaH153/SuavoAgent.git",
    "ssh://git@github.com/MinaH153/SuavoAgent.git",
}


def _bootstrap_git_bytes(
    repository: str, *arguments: str, isolated_config: bool = False
) -> bytes | None:
    read_fd, write_fd = os.pipe()
    null_fd = os.open(os.devnull, os.O_WRONLY)
    environment = {
        "GIT_TERMINAL_PROMPT": "0",
        "GIT_OPTIONAL_LOCKS": "0",
        "GIT_NO_REPLACE_OBJECTS": "1",
        "GIT_CONFIG_GLOBAL": os.devnull,
        "GIT_CONFIG_NOSYSTEM": "1",
        "HOME": os.environ.get("HOME", "/nonexistent"),
        "LC_ALL": "C",
        "PATH": SIGNING_PATH,
    }
    if isolated_config:
        environment.update(
            {
                "GIT_CONFIG": os.devnull,
                "HOME": "/nonexistent",
            }
        )
    try:
        process = os.posix_spawn(
            "/usr/bin/git",
            ("/usr/bin/git", "-C", repository, "-c", "core.fsmonitor=false", *arguments),
            environment,
            file_actions=(
                (os.POSIX_SPAWN_DUP2, write_fd, 1),
                (os.POSIX_SPAWN_DUP2, null_fd, 2),
                (os.POSIX_SPAWN_CLOSE, read_fd),
                (os.POSIX_SPAWN_CLOSE, write_fd),
                (os.POSIX_SPAWN_CLOSE, null_fd),
            ),
        )
    except OSError:
        os.close(read_fd)
        os.close(write_fd)
        os.close(null_fd)
        return None
    os.close(write_fd)
    os.close(null_fd)
    chunks: list[bytes] = []
    while chunk := os.read(read_fd, 65536):
        chunks.append(chunk)
    os.close(read_fd)
    _, status = os.waitpid(process, 0)
    if os.waitstatus_to_exitcode(status) != 0:
        return None
    return b"".join(chunks)


def _bootstrap_git(repository: str, *arguments: str) -> str | None:
    raw = _bootstrap_git_bytes(repository, *arguments, isolated_config=True)
    try:
        return None if raw is None else raw.decode("utf-8").strip()
    except UnicodeDecodeError:
        return None


def _bootstrap_local_config(repository: str, *arguments: str) -> str | None:
    raw = _bootstrap_git_bytes(
        repository,
        "config",
        "--local",
        "--no-includes",
        *arguments,
    )
    try:
        return None if raw is None else raw.decode("utf-8").strip()
    except UnicodeDecodeError:
        return None


def _sys_path_is_valid(repository: str) -> bool:
    scripts = os.path.realpath(os.path.join(repository, "scripts"))
    base = os.path.realpath(sys.base_prefix)
    major, minor = sys.version_info[:2]
    expected = [
        os.path.join(base, "lib", f"python{major}{minor}.zip"),
        os.path.join(base, "lib", f"python{major}.{minor}"),
        os.path.join(base, "lib", f"python{major}.{minor}", "lib-dynload"),
        scripts,
    ]
    return [os.path.realpath(entry) if entry else "" for entry in sys.path] == expected


def _request_source_sha(repository: str) -> str | None:
    try:
        stage_index = sys.argv.index("--stage-dir") + 1
        source_index = sys.argv.index("--source-root") + 1
        stage = os.path.realpath(sys.argv[stage_index])
        source_root = os.path.realpath(sys.argv[source_index])
    except (ValueError, IndexError):
        return None
    if source_root != repository or stage == repository or stage.startswith(repository + os.sep):
        return None
    request = os.path.join(stage, "bridge-signing-request.json")
    if not os.path.isfile(request) or os.path.islink(request):
        return None
    try:
        with open(request, "rb") as source:
            raw = source.read(2 * 1024 * 1024 + 1)
        document = __import__("json").loads(raw)
    except (OSError, ValueError, UnicodeDecodeError):
        return None
    source_sha = document.get("sourceSha") if isinstance(document, dict) else None
    if (
        len(raw) > 2 * 1024 * 1024
        or not isinstance(source_sha, str)
        or len(source_sha) != 40
        or any(character not in "0123456789abcdef" for character in source_sha)
    ):
        return None
    return source_sha


def _authority_main_sha() -> str | None:
    raw = _bootstrap_git_bytes(
        "/",
        "ls-remote",
        AUTHORITY_URL,
        "refs/heads/main",
        isolated_config=True,
    )
    try:
        line = raw.decode("ascii").rstrip("\n") if raw is not None else ""
    except UnicodeDecodeError:
        return None
    parts = line.split("\t")
    if (
        len(parts) != 2
        or parts[1] != "refs/heads/main"
        or len(parts[0]) != 40
        or any(character not in "0123456789abcdef" for character in parts[0])
    ):
        return None
    return parts[0]


def _head_bytes_are_exact(repository: str, relative_paths: tuple[str, ...]) -> bool:
    for relative in relative_paths:
        path = os.path.join(repository, relative)
        if not os.path.isfile(path) or os.path.islink(path):
            return False
        if _bootstrap_git(repository, "ls-files", "-v", "--", relative) != f"H {relative}":
            return False
        expected = _bootstrap_git_bytes(
            repository, "show", f"HEAD:{relative}", isolated_config=True
        )
        try:
            with open(path, "rb") as source:
                actual = source.read()
        except OSError:
            return False
        if expected is None or actual != expected:
            return False
    return True


def _replacement_refs_are_absent(repository: str) -> bool:
    raw = _bootstrap_git_bytes(
        repository,
        "for-each-ref",
        "--format=%(refname)",
        "refs/replace/",
        isolated_config=True,
    )
    return raw == b""


def _tracked_worktree_is_exact(repository: str) -> bool:
    index = _bootstrap_git_bytes(
        repository, "ls-files", "-s", "-z", isolated_config=True
    )
    if not index:
        return False
    records = index.split(b"\0")
    if records[-1] != b"":
        return False
    for record in records[:-1]:
        try:
            metadata, encoded_path = record.split(b"\t", 1)
            mode, object_id, stage = metadata.split(b" ")
        except ValueError:
            return False
        if mode not in (b"100644", b"100755") or stage != b"0":
            return False
        relative = os.fsdecode(encoded_path)
        path = os.path.join(repository, relative)
        if not os.path.isfile(path) or os.path.islink(path):
            return False
        actual = _bootstrap_git_bytes(
            repository,
            "hash-object",
            "--no-filters",
            "--",
            relative,
            isolated_config=True,
        )
        if actual is None or actual.strip() != object_id:
            return False
    return True


def _local_sign_bootstrap_is_valid() -> bool:
    if (
        os.environ.get(LOCAL_SIGN_BOOTSTRAP_ENV) != LOCAL_SIGN_BOOTSTRAP_VALUE
        or os.environ.get("PATH") != SIGNING_PATH
        or not sys.flags.isolated
        or not sys.flags.no_site
    ):
        return False
    repository = os.path.realpath(os.path.join(os.path.dirname(__file__), ".."))
    if not _sys_path_is_valid(repository):
        return False
    if _bootstrap_git(repository, "rev-parse", "--show-toplevel") != repository:
        return False
    if _bootstrap_local_config(
        repository, "--get-all", "remote.origin.url"
    ) not in APPROVED_ORIGINS:
        return False
    rewrite_pattern = r"^(url\..*\.(insteadof|pushinsteadof)|remote\.origin\.(uploadpack|receivepack)|core\.sshcommand)$"
    rewrites = _bootstrap_local_config(repository, "--get-regexp", rewrite_pattern)
    if rewrites not in (None, ""):
        return False
    if not _replacement_refs_are_absent(repository):
        return False
    head_sha = _bootstrap_git(repository, "rev-parse", "HEAD")
    if head_sha != _request_source_sha(repository) or head_sha != _authority_main_sha():
        return False
    if _bootstrap_git(repository, "diff-index", "--cached", "--quiet", "HEAD", "--") != "":
        return False
    if _bootstrap_git(repository, "ls-files", "--others", "--exclude-standard", "--") != "":
        return False
    if _bootstrap_git(
        repository, "ls-files", "--others", "--ignored", "--exclude-standard", "--", "scripts"
    ) != "":
        return False
    return _head_bytes_are_exact(
        repository,
        (
            "scripts/sign-ota-v1-bridge-local.sh",
            "scripts/v1_bridge_cli.py",
            "scripts/v1_bridge_release.py",
            "scripts/v1_bridge_handoff.py",
            "scripts/v1_bridge_file_io.py",
            "scripts/v1_bridge_crypto.py",
            "scripts/v1_bridge_source_guard.py",
            "scripts/ota_update_trust_roots.py",
            "scripts/ecdsa_der_to_p1363.py",
            "security/ota-update-trust-roots.json",
        ),
    ) and _tracked_worktree_is_exact(repository)


if len(sys.argv) > 1 and sys.argv[1] == "local-sign":
    if not _local_sign_bootstrap_is_valid():
        print(
            "refusing historic v1 key access outside the hardened bridge ceremony",
            file=sys.stderr,
        )
        raise SystemExit(1)
    os.environ.pop(LOCAL_SIGN_BOOTSTRAP_ENV, None)

import argparse
from pathlib import Path
import subprocess

from ota_update_trust_roots import TrustRegistryError
from v1_bridge_release import (
    BridgeError,
    DEFAULT_REGISTRY,
    ROOT,
    assert_bridge_source,
    assert_normal_release,
    finalize_response,
    local_sign,
    prepare_request,
    validate_final,
)


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    commands = parser.add_subparsers(dest="command", required=True)
    for name in ("assert-bridge-source", "assert-normal-release"):
        command = commands.add_parser(name)
        command.add_argument("--registry", type=Path, default=DEFAULT_REGISTRY)
        if name == "assert-normal-release":
            command.add_argument("--full-cohort-manifest", required=True)
            command.add_argument("--evidence", type=Path)
            command.add_argument("--claim", type=Path)
            command.add_argument("--signature", type=Path)
            command.add_argument("--inventory", type=Path)
            command.add_argument("--inventory-signature", type=Path)

    prepare = commands.add_parser("prepare-request")
    prepare.add_argument("--release-dir", type=Path, required=True)
    prepare.add_argument("--output", type=Path, required=True)
    prepare.add_argument("--repository", required=True)
    prepare.add_argument("--source-sha", required=True)
    prepare.add_argument("--stage-run-id", type=int, required=True)
    prepare.add_argument("--stage-run-attempt", type=int, required=True)
    prepare.add_argument("--version", required=True)
    prepare.add_argument("--rollback-tag", required=True)
    prepare.add_argument("--rollback-artifact", required=True)
    prepare.add_argument("--rollback-sha", required=True)
    prepare.add_argument("--authenticode-signer-sha256", required=True)
    prepare.add_argument("--regenerate-final-sbom", action="store_true")
    prepare.add_argument("--registry", type=Path, default=DEFAULT_REGISTRY)

    local = commands.add_parser("local-sign")
    local.add_argument("--key", type=Path, required=True)
    local.add_argument("--stage-dir", type=Path, required=True)
    local.add_argument("--descriptor", type=Path, required=True)
    local.add_argument("--descriptor-signature", type=Path, required=True)
    local.add_argument("--source-root", type=Path, default=ROOT)
    local.add_argument("--response-json", type=Path, required=True)
    local.add_argument("--response-b64", type=Path, required=True)
    local.add_argument("--registry", type=Path, default=DEFAULT_REGISTRY)

    finalize = commands.add_parser("finalize")
    finalize.add_argument("--request", type=Path, required=True)
    finalize.add_argument("--release-dir", type=Path, required=True)
    finalize.add_argument("--response-b64-file", type=Path, required=True)
    finalize.add_argument("--response-json", type=Path, required=True)
    finalize.add_argument("--expected-repository", required=True)
    finalize.add_argument("--expected-sha", required=True)
    finalize.add_argument("--expected-run-id", type=int, required=True)
    finalize.add_argument("--expected-run-attempt", type=int, required=True)
    finalize.add_argument("--expected-artifact", required=True)
    finalize.add_argument("--registry", type=Path, default=DEFAULT_REGISTRY)

    final = commands.add_parser("validate-final")
    final.add_argument("--stage-dir", type=Path, required=True)
    final.add_argument("--expected-repository", required=True)
    final.add_argument("--expected-sha", required=True)
    final.add_argument("--expected-run-id", type=int, required=True)
    final.add_argument("--expected-run-attempt", type=int, required=True)
    final.add_argument("--expected-artifact", required=True)
    final.add_argument("--registry", type=Path, default=DEFAULT_REGISTRY)

    return parser


def main() -> int:
    arguments = _parser().parse_args()
    try:
        if arguments.command == "assert-bridge-source":
            assert_bridge_source(arguments.registry)
        elif arguments.command == "assert-normal-release":
            assert_normal_release(
                arguments.registry,
                arguments.full_cohort_manifest,
                arguments.evidence,
                arguments.claim,
                arguments.signature,
                arguments.inventory,
                arguments.inventory_signature,
            )
        elif arguments.command == "prepare-request":
            prepare_request(arguments)
        elif arguments.command == "local-sign":
            local_sign(arguments)
        elif arguments.command == "finalize":
            finalize_response(arguments)
        elif arguments.command == "validate-final":
            validate_final(arguments)
        return 0
    except (BridgeError, OSError, subprocess.SubprocessError, TrustRegistryError) as error:
        print(str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
