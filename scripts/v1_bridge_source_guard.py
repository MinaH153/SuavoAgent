#!/usr/bin/env python3
"""Sterile, filter-free source validation for the historic v1 ceremony."""

from __future__ import annotations

import os
from pathlib import Path
import subprocess


GIT = "/usr/bin/git"
SIGNING_PATH = "/usr/bin:/bin:/usr/sbin:/sbin"


def _environment(*, local_config: bool = False) -> dict[str, str]:
    environment = {
        "GIT_CONFIG_GLOBAL": os.devnull,
        "GIT_CONFIG_NOSYSTEM": "1",
        "GIT_NO_REPLACE_OBJECTS": "1",
        "GIT_OPTIONAL_LOCKS": "0",
        "GIT_TERMINAL_PROMPT": "0",
        "HOME": "/nonexistent",
        "LC_ALL": "C",
        "PATH": SIGNING_PATH,
    }
    if not local_config:
        environment["GIT_CONFIG"] = os.devnull
    return environment


def _git(
    source_root: Path,
    *arguments: str,
    local_config: bool = False,
) -> subprocess.CompletedProcess[bytes]:
    return subprocess.run(
        (GIT, "-c", "core.fsmonitor=false", *arguments),
        cwd=source_root,
        env=_environment(local_config=local_config),
        capture_output=True,
        check=False,
    )


def _text(
    source_root: Path,
    *arguments: str,
    local_config: bool = False,
) -> str | None:
    result = _git(source_root, *arguments, local_config=local_config)
    if result.returncode != 0:
        return None
    try:
        return result.stdout.decode("utf-8").strip()
    except UnicodeDecodeError:
        return None


def _tracked_worktree_is_exact(source_root: Path) -> bool:
    result = _git(source_root, "ls-files", "-s", "-z")
    if result.returncode != 0 or not result.stdout:
        return False
    records = result.stdout.split(b"\0")
    if records[-1] != b"":
        return False
    for record in records[:-1]:
        try:
            metadata, encoded_path = record.split(b"\t", 1)
            mode, expected, stage = metadata.split(b" ")
        except ValueError:
            return False
        if mode not in (b"100644", b"100755") or stage != b"0":
            return False
        relative = os.fsdecode(encoded_path)
        path = source_root / relative
        if not path.is_file() or path.is_symlink():
            return False
        if os.access(path, os.X_OK) != (mode == b"100755"):
            return False
        actual = _git(source_root, "hash-object", "--no-filters", "--", relative)
        if actual.returncode != 0 or actual.stdout.strip() != expected:
            return False
    return True


def validate_local_source(
    source_root: Path,
    source_sha: str,
    approved_origins: frozenset[str],
) -> str | None:
    if _text(source_root, "rev-parse", "HEAD") != source_sha:
        return "local signer is not running from the exact staged source SHA"
    replacements = _text(
        source_root, "for-each-ref", "--format=%(refname)", "refs/replace/"
    )
    if replacements is None or replacements:
        return "local signing source contains replacement refs"
    cached = _git(source_root, "diff-index", "--cached", "--quiet", "HEAD", "--")
    if cached.returncode != 0:
        return "local signing source index does not exactly equal HEAD"
    if _text(source_root, "ls-files", "--others", "--exclude-standard", "--") != "":
        return "local signing source has tracked or untracked modifications"
    if not _tracked_worktree_is_exact(source_root):
        return "local signing source has tracked or untracked modifications"
    origin = _text(
        source_root,
        "config",
        "--local",
        "--no-includes",
        "--get-all",
        "remote.origin.url",
        local_config=True,
    )
    if origin not in approved_origins:
        return "local signing source origin is not MinaH153/SuavoAgent"
    return None
