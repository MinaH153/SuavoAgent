#!/usr/bin/env python3
"""Select the greatest published stable semver below a stable or RC target."""

import json
import re
import sys


def stable(value: str):
    match = re.fullmatch(r"v?(\d+)\.(\d+)\.(\d+)", value or "")
    return tuple(map(int, match.groups())) if match else None


def release_core(value: str):
    match = re.fullmatch(
        r"v?(\d+)\.(\d+)\.(\d+)(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?",
        value or "",
    )
    return tuple(map(int, match.groups())) if match else None


def main() -> int:
    if len(sys.argv) != 2:
        return 64
    current = release_core(sys.argv[1])
    if current is None:
        return 65
    rows = json.load(sys.stdin)
    candidates = [
        (parsed, row["tagName"])
        for row in rows
        if not row.get("isDraft", False)
        and not row.get("isPrerelease", False)
        and (parsed := stable(row.get("tagName", ""))) is not None
        and parsed < current
    ]
    if candidates:
        print(max(candidates)[1])
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
