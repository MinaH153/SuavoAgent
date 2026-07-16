#!/usr/bin/env python3
"""Fail closed unless GitHub exposes the exact stable-release tag ruleset."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys


MAX_INPUT_BYTES = 4 * 1024 * 1024
REQUIRED_INCLUDE = ["refs/tags/v*"]
REQUIRED_RULES = frozenset({"update", "deletion"})


class RulesetError(ValueError):
    pass


def _load(path: Path) -> list[dict[str, object]]:
    if path.is_symlink() or not path.is_file():
        raise RulesetError("ruleset input must be a regular non-link file")
    raw = path.read_bytes()
    if not raw or len(raw) > MAX_INPUT_BYTES:
        raise RulesetError("ruleset input size is invalid")
    try:
        document = json.loads(raw)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise RulesetError("ruleset input is not valid JSON") from error
    if not isinstance(document, list) or not all(
        isinstance(entry, dict) for entry in document
    ):
        raise RulesetError("ruleset input must be an array of objects")
    return document


def _is_exact_release_ruleset(
    ruleset: dict[str, object], repository: str
) -> bool:
    if (
        ruleset.get("source_type") != "Repository"
        or ruleset.get("source") != repository
        or ruleset.get("target") != "tag"
        or ruleset.get("enforcement") != "active"
        or ruleset.get("bypass_actors") != []
    ):
        return False

    conditions = ruleset.get("conditions")
    ref_name = conditions.get("ref_name") if isinstance(conditions, dict) else None
    if not isinstance(ref_name, dict):
        return False
    if ref_name.get("include") != REQUIRED_INCLUDE or ref_name.get("exclude") != []:
        return False

    rules = ruleset.get("rules")
    if not isinstance(rules, list) or not all(isinstance(rule, dict) for rule in rules):
        return False
    rule_types = {
        rule.get("type") for rule in rules if isinstance(rule.get("type"), str)
    }
    return REQUIRED_RULES.issubset(rule_types) and "creation" not in rule_types


def validate(path: Path, repository: str) -> dict[str, object]:
    if repository != "MinaH153/SuavoAgent":
        raise RulesetError("release repository identity is not approved")
    matches = [
        ruleset
        for ruleset in _load(path)
        if _is_exact_release_ruleset(ruleset, repository)
    ]
    if not matches:
        raise RulesetError(
            "expected an active repository v* tag ruleset with no bypass, "
            "update/deletion restrictions, and tag creation allowed"
        )
    return matches[0]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--repository", required=True)
    arguments = parser.parse_args()
    try:
        ruleset = validate(arguments.input, arguments.repository)
    except (OSError, RulesetError) as error:
        print(f"release tag ruleset validation failed: {error}", file=sys.stderr)
        return 1
    print(
        "validated release tag ruleset: "
        f"{ruleset.get('name', '<unnamed>')} ({ruleset.get('id', '<unknown>')})"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
