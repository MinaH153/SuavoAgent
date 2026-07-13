#!/usr/bin/env python3
"""Materialize exact license texts from immutable reviewed upstream URLs."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import sys
import tempfile
from urllib.request import Request, urlopen


ROOT = Path(__file__).resolve().parents[1]
CATALOG = ROOT / "legal/package-license-evidence.json"
MAX_LICENSE_BYTES = 2 * 1024 * 1024


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def entries() -> list[tuple[Path, str, str]]:
    catalog = json.loads(CATALOG.read_text(encoding="utf-8"))
    if not isinstance(catalog, dict) or catalog.get("schemaVersion") != 1:
        raise RuntimeError("package license evidence catalog schema is invalid")
    result: list[tuple[Path, str, str]] = []
    for entry in catalog.get("evidence", []):
        revision = entry.get("sourceRevision")
        url = entry.get("upstreamUrl")
        expected = entry.get("sha256")
        relative = entry.get("retainedFile")
        if (
            not isinstance(revision, str)
            or not re.fullmatch(r"[0-9a-f]{40}", revision)
            or not isinstance(url, str)
            or revision not in url
            or not url.startswith("https://raw.githubusercontent.com/")
            or not isinstance(expected, str)
            or not re.fullmatch(r"[0-9a-f]{64}", expected)
            or not isinstance(relative, str)
        ):
            raise RuntimeError("package license evidence source is not immutable")
        path = (ROOT / relative).resolve()
        path.relative_to(ROOT.resolve())
        result.append((path, url, expected))
    if len({path for path, _, _ in result}) != len(result):
        raise RuntimeError("package license evidence output path is duplicated")
    return result


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--sync", action="store_true")
    parser.add_argument("--verify-upstream", action="store_true")
    args = parser.parse_args()
    failed: list[str] = []
    for path, url, expected in entries():
        if args.sync or args.verify_upstream:
            request = Request(url, headers={"User-Agent": "SuavoAgent-legal-evidence"})
            with urlopen(request, timeout=30) as response:
                declared = response.headers.get("Content-Length")
                if declared is not None and not 0 < int(declared) <= MAX_LICENSE_BYTES:
                    raise RuntimeError(f"pinned upstream license size is invalid: {url}")
                upstream = response.read(MAX_LICENSE_BYTES + 1)
            if not 0 < len(upstream) <= MAX_LICENSE_BYTES:
                raise RuntimeError(f"pinned upstream license exceeded its bound: {url}")
            if digest(upstream) != expected:
                raise RuntimeError(f"pinned upstream license digest drifted: {url}")
            if args.sync:
                path.parent.mkdir(parents=True, exist_ok=True)
                with tempfile.NamedTemporaryFile(
                    dir=path.parent,
                    prefix=path.name + ".",
                    suffix=".tmp",
                    delete=False,
                ) as output:
                    output.write(upstream)
                    temporary = Path(output.name)
                temporary.replace(path)
        if not path.is_file() or digest(path.read_bytes()) != expected:
            failed.append(path.relative_to(ROOT).as_posix())
    if failed:
        print("missing or stale pinned package licenses: " + ", ".join(failed), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
