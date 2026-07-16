#!/usr/bin/env python3
"""One-shot, no-follow reads for historic-key ceremony inputs."""

from __future__ import annotations

import os
import hashlib
from pathlib import Path
import stat


class SecureFileError(ValueError):
    pass


def read_regular_once(path: Path, maximum_bytes: int, label: str) -> bytes:
    flags = os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0)
    try:
        descriptor = os.open(path, flags)
    except OSError as error:
        raise SecureFileError(f"{label} must be a regular non-link file") from error
    try:
        before = os.fstat(descriptor)
        if not stat.S_ISREG(before.st_mode) or before.st_size <= 0 or before.st_size > maximum_bytes:
            raise SecureFileError(f"{label} has an invalid size")
        chunks: list[bytes] = []
        remaining = before.st_size
        while remaining:
            chunk = os.read(descriptor, min(remaining, 1024 * 1024))
            if not chunk:
                raise SecureFileError(f"{label} changed while it was read")
            chunks.append(chunk)
            remaining -= len(chunk)
        if os.read(descriptor, 1):
            raise SecureFileError(f"{label} changed while it was read")
        after = os.fstat(descriptor)
        identity = lambda value: (
            value.st_dev, value.st_ino, value.st_size, value.st_mtime_ns, value.st_ctime_ns
        )
        if identity(before) != identity(after):
            raise SecureFileError(f"{label} changed while it was read")
        return b"".join(chunks)
    finally:
        os.close(descriptor)


def release_file_entries_once(
    paths: tuple[Path, ...], release_root: Path, maximum_bytes: int
) -> tuple[dict[str, object], ...]:
    entries: list[dict[str, object]] = []
    for path in paths:
        raw = read_regular_once(path, maximum_bytes, "release file")
        entries.append(
            {
                "path": "release/" + path.relative_to(release_root).as_posix(),
                "sha256": hashlib.sha256(raw).hexdigest(),
                "size": len(raw),
            }
        )
    return tuple(entries)
