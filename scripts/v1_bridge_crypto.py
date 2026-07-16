#!/usr/bin/env python3
"""Stable-file-descriptor P-256 operations for the historic local key."""

from __future__ import annotations

from contextlib import contextmanager
import os
from pathlib import Path
import stat
import subprocess
from typing import Iterator


class HistoricKeyError(ValueError):
    pass


def _stat_identity(value: os.stat_result) -> tuple[int, int, int, int, int]:
    return value.st_dev, value.st_ino, value.st_size, value.st_mtime_ns, value.st_ctime_ns


class HistoricKey:
    def __init__(self, descriptor: int) -> None:
        self._descriptor = descriptor

    def _rewind(self) -> str:
        os.lseek(self._descriptor, 0, os.SEEK_SET)
        return f"/dev/fd/{self._descriptor}"

    def public_der(self) -> bytes:
        result = subprocess.run(
            ("/usr/bin/openssl", "pkey", "-in", self._rewind(), "-pubout", "-outform", "DER"),
            capture_output=True,
            pass_fds=(self._descriptor,),
            check=False,
        )
        if result.returncode != 0 or len(result.stdout) != 91:
            raise HistoricKeyError("v1 bridge key is not a readable P-256 private key")
        return result.stdout

    def sign_der(self, payload: bytes) -> bytes:
        if not payload or len(payload) > 128 * 1024:
            raise HistoricKeyError("historic signing payload has an invalid size")
        result = subprocess.run(
            ("/usr/bin/openssl", "dgst", "-sha256", "-sign", self._rewind()),
            input=payload,
            capture_output=True,
            pass_fds=(self._descriptor,),
            check=False,
        )
        if result.returncode != 0 or not (68 <= len(result.stdout) <= 72):
            raise HistoricKeyError("OpenSSL did not return an exact P-256 DER signature")
        return result.stdout


@contextmanager
def open_historic_key(path: Path) -> Iterator[HistoricKey]:
    flags = os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0)
    try:
        descriptor = os.open(path, flags)
    except OSError as error:
        raise HistoricKeyError("v1 bridge key must be a regular non-link file") from error
    try:
        before = os.fstat(descriptor)
        if (
            not stat.S_ISREG(before.st_mode)
            or stat.S_IMODE(before.st_mode) != 0o600
            or before.st_uid != os.getuid()
            or before.st_size <= 0
            or before.st_size > 64 * 1024
        ):
            raise HistoricKeyError("v1 bridge key must be owner-only mode 0600")
        try:
            yield HistoricKey(descriptor)
        finally:
            after = os.fstat(descriptor)
            if _stat_identity(before) != _stat_identity(after):
                raise HistoricKeyError("v1 bridge key changed during the signing ceremony")
    finally:
        os.close(descriptor)
