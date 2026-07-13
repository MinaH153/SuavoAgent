#!/usr/bin/python3
"""Deterministically merge Coverlet Cobertura coverage for repository sources.

The merger is deliberately conservative. A source line is counted once across
all reports, its hit count is the maximum observed hit count, and its branch
coverage is the maximum observed covered-condition count for an invariant
condition total. Cobertura does not identify individual branch outcomes across
separate reports, so summing them would overstate coverage.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
import xml.etree.ElementTree as ET
from decimal import Decimal, InvalidOperation
from pathlib import Path, PurePosixPath
from typing import Dict, Iterable, List, Mapping, MutableMapping, Optional, Sequence, Set, Tuple

try:
    from scripts.coverage_model import (
        CoverageDataError,
        CoverageSummary,
        CoverageThresholdError,
        GENERATED_FILE_PATTERNS,
        LineCoverage,
        SOURCE_EXCLUSIONS_FILE,
        Totals,
    )
except ModuleNotFoundError as error:
    if error.name != "scripts":
        raise
    from coverage_model import (  # type: ignore[no-redef]
        CoverageDataError,
        CoverageSummary,
        CoverageThresholdError,
        GENERATED_FILE_PATTERNS,
        LineCoverage,
        SOURCE_EXCLUSIONS_FILE,
        Totals,
    )


MAX_REPORT_BYTES = 64 * 1024 * 1024
MAX_SOURCE_EXCLUSIONS_BYTES = 256 * 1024
BRANCH_PATTERN = re.compile(
    r"^\s*(?P<percent>\d+(?:\.\d+)?)%\s*"
    r"\((?P<covered>\d+)\s*/\s*(?P<total>\d+)\)\s*$"
)
PERCENT_PATTERN = re.compile(r"^\s*(?P<percent>\d+(?:\.\d+)?)%\s*$")
WINDOWS_ABSOLUTE_PATTERN = re.compile(r"^[A-Za-z]:[/\\]")
def _is_within(path: Path, parent: Path) -> bool:
    try:
        path.relative_to(parent)
        return True
    except ValueError:
        return False


def _is_generated(relative_path: Path) -> bool:
    lower_name = relative_path.name.lower()
    if lower_name.endswith(GENERATED_FILE_PATTERNS):
        return True
    lower_parts = tuple(part.lower() for part in relative_path.parts)
    return "system.text.regularexpressions.generator" in lower_parts


def discover_authored_sources(repo_root: Path) -> Set[Path]:
    root = repo_root.resolve()
    src_root = (root / "src").resolve()
    if not src_root.is_dir() or not _is_within(src_root, root):
        raise CoverageDataError(f"repository has no safe src directory: {src_root}")

    third_party = (src_root / "SuavoAgent.Diagnostics" / "ThirdParty").resolve()
    authored: Set[Path] = set()
    for source in sorted(src_root.rglob("*.cs")):
        relative_lexical = source.relative_to(src_root)
        if any(part.lower() in {"bin", "obj"} for part in relative_lexical.parts):
            continue
        if _is_generated(relative_lexical):
            continue

        resolved = source.resolve()
        if not _is_within(resolved, src_root):
            continue
        if _is_within(resolved, third_party):
            continue
        if resolved.is_file():
            authored.add(resolved)

    if not authored:
        raise CoverageDataError("repository contains no authored production C# sources")
    return authored


def load_noninstrumentable_source_exclusions(
    repo_root: Path,
    authored_sources: Set[Path],
) -> Set[Path]:
    """Load the exact, hash-pinned set of authored files with no sequence points.

    Coverlet cannot represent declaration-only files such as interfaces, enums,
    and assembly attributes because portable PDBs contain no executable sequence
    points for them.  The exclusion list is deliberately exact and content
    addressed: changing an excluded file requires an explicit re-audit instead
    of silently removing it from the release denominator.
    """

    root = repo_root.resolve()
    exclusions_path = (root / SOURCE_EXCLUSIONS_FILE).resolve()
    if not _is_within(exclusions_path, root):
        raise CoverageDataError(
            f"coverage source exclusion file escapes repository: {exclusions_path}"
        )
    if not exclusions_path.exists():
        return set()
    if not exclusions_path.is_file():
        raise CoverageDataError(
            f"coverage source exclusion path is not a file: {exclusions_path}"
        )

    try:
        exclusions_payload = exclusions_path.read_bytes()
        exclusions_size = len(exclusions_payload)
        if exclusions_size <= 0 or exclusions_size > MAX_SOURCE_EXCLUSIONS_BYTES:
            raise CoverageDataError(
                "coverage source exclusions size is outside the accepted range: "
                f"{exclusions_size} bytes"
            )
        payload = json.loads(exclusions_payload.decode("utf-8"))
    except CoverageDataError:
        raise
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise CoverageDataError(
            f"cannot read coverage source exclusions {exclusions_path}: {exc}"
        ) from exc

    if not isinstance(payload, dict) or set(payload) != {"schema_version", "sources"}:
        raise CoverageDataError(
            "coverage source exclusions must contain only schema_version and sources"
        )
    if payload["schema_version"] != 1 or not isinstance(payload["sources"], list):
        raise CoverageDataError("coverage source exclusions schema is invalid")

    exclusions: Set[Path] = set()
    for index, entry in enumerate(payload["sources"]):
        label = f"coverage source exclusion #{index + 1}"
        if not isinstance(entry, dict) or set(entry) != {"path", "reason", "sha256"}:
            raise CoverageDataError(
                f"{label} must contain only path, reason, and sha256"
            )
        raw_path = entry["path"]
        reason = entry["reason"]
        expected_sha256 = entry["sha256"]
        if not isinstance(raw_path, str):
            raise CoverageDataError(f"{label} path must be a string")
        pure_path = _normalize_xml_path(raw_path, f"{label} path")
        if pure_path.is_absolute() or _is_windows_absolute(pure_path.as_posix()):
            raise CoverageDataError(f"{label} path must be repository-relative")
        if len(pure_path.parts) < 3 or pure_path.parts[0] != "src":
            raise CoverageDataError(f"{label} path must name an authored src file")

        source = _canonical_candidate(
            root / Path(*pure_path.parts),
            root,
            f"{label} path",
        )
        if source not in authored_sources:
            raise CoverageDataError(f"{label} is not an authored production source: {raw_path}")
        if source in exclusions:
            raise CoverageDataError(f"duplicate coverage source exclusion: {raw_path}")
        if (
            not isinstance(reason, str)
            or reason != reason.strip()
            or "\n" in reason
            or len(reason) < 12
            or len(reason) > 240
        ):
            raise CoverageDataError(f"{label} reason is invalid")
        if (
            not isinstance(expected_sha256, str)
            or re.fullmatch(r"[0-9a-f]{64}", expected_sha256) is None
        ):
            raise CoverageDataError(f"{label} sha256 is invalid")
        try:
            actual_sha256 = hashlib.sha256(source.read_bytes()).hexdigest()
        except OSError as exc:
            raise CoverageDataError(f"cannot hash {label} source {source}: {exc}") from exc
        if actual_sha256 != expected_sha256:
            raise CoverageDataError(
                f"{label} hash changed and requires re-audit: {raw_path}"
            )
        exclusions.add(source)

    return exclusions


def discover_reports(input_path: Path) -> List[Path]:
    requested = input_path.expanduser()
    if requested.is_file():
        reports = [requested.resolve()]
    elif requested.is_dir():
        input_root = requested.resolve()
        reports = []
        for candidate in requested.rglob("coverage.cobertura.xml"):
            resolved = candidate.resolve()
            if not _is_within(resolved, input_root):
                raise CoverageDataError(f"coverage report escapes input directory: {candidate}")
            if resolved.is_file():
                reports.append(resolved)
        reports = sorted(set(reports), key=lambda value: str(value))
    else:
        raise CoverageDataError(f"coverage input does not exist: {requested}")

    if not reports:
        raise CoverageDataError(f"no coverage.cobertura.xml reports found under {requested}")
    return reports


def _parse_report(report: Path) -> ET.Element:
    try:
        report_size = report.stat().st_size
        if report_size <= 0 or report_size > MAX_REPORT_BYTES:
            raise CoverageDataError(
                f"coverage report size is outside the accepted range: {report} ({report_size} bytes)"
            )
        payload = report.read_bytes()
    except OSError as exc:
        raise CoverageDataError(f"cannot read coverage report {report}: {exc}") from exc

    lowered = payload.lower()
    if b"<!doctype" in lowered or b"<!entity" in lowered:
        raise CoverageDataError(f"DTD/entity declarations are forbidden in coverage report: {report}")

    try:
        root = ET.fromstring(payload)
    except (ET.ParseError, ValueError) as exc:
        raise CoverageDataError(f"malformed coverage XML {report}: {exc}") from exc
    if root.tag != "coverage":
        raise CoverageDataError(f"unexpected coverage root element in {report}: {root.tag!r}")
    return root


def _normalize_xml_path(raw_path: str, label: str) -> PurePosixPath:
    if not raw_path or "\x00" in raw_path:
        raise CoverageDataError(f"{label} is empty or contains NUL")
    normalized = raw_path.strip().replace("\\", "/")
    if not normalized:
        raise CoverageDataError(f"{label} is empty")
    pure = PurePosixPath(normalized)
    if ".." in pure.parts:
        raise CoverageDataError(f"{label} contains parent traversal: {raw_path!r}")
    return pure


def _is_windows_absolute(path_text: str) -> bool:
    return path_text.startswith("//") or WINDOWS_ABSOLUTE_PATTERN.match(path_text) is not None


def _canonical_candidate(candidate: Path, repo_root: Path, label: str) -> Path:
    resolved = candidate.resolve(strict=False)
    if not _is_within(resolved, repo_root):
        raise CoverageDataError(f"{label} resolves outside repository: {resolved}")
    return resolved


def _parts_without_posix_root(path: PurePosixPath) -> Tuple[str, ...]:
    parts = path.parts
    if parts and parts[0] in {"/", "//"}:
        return tuple(parts[1:])
    return tuple(parts)


def _virtual_repo_candidate(
    path: PurePosixPath,
    repo_root: Path,
) -> Optional[Path]:
    """Resolve legacy compiler-PathMap namespace /_/src/... into this checkout."""

    parts = _parts_without_posix_root(path)
    if len(parts) < 3 or parts[0] != "_" or parts[1] != "src":
        return None
    return _canonical_candidate(
        repo_root / Path(*parts[1:]),
        repo_root,
        "virtual Coverlet source",
    )


def _remote_windows_candidates(
    path: PurePosixPath,
    repo_root: Path,
) -> Set[Path]:
    """Map a Windows CI path by an unambiguous authored ``src/...`` suffix.

    Cobertura reports produced on a Windows runner can be downloaded and merged
    by an Ubuntu job. A Windows drive cannot be resolved by the POSIX host, so
    only repository-relative suffixes beginning at a path segment named exactly
    ``src`` are considered. Parent traversal was already rejected by
    ``_normalize_xml_path`` and every candidate remains confined to the current
    checkout by ``_canonical_candidate``.
    """

    parts = _parts_without_posix_root(path)
    candidates: Set[Path] = set()
    for index, part in enumerate(parts):
        if part != "src" or len(parts) - index < 3:
            continue
        candidates.add(
            _canonical_candidate(
                repo_root / Path(*parts[index:]),
                repo_root,
                "Windows coverage source suffix",
            )
        )
    return candidates


def _absolute_coverage_candidates(
    path: PurePosixPath,
    repo_root: Path,
    label: str,
) -> Set[Path]:
    virtual = _virtual_repo_candidate(path, repo_root)
    if virtual is not None:
        return {virtual}

    path_text = path.as_posix()
    if _is_windows_absolute(path_text):
        if os.name == "nt":
            return {_canonical_candidate(Path(path_text), repo_root, label)}
        return _remote_windows_candidates(path, repo_root)
    if path.is_absolute():
        return {_canonical_candidate(Path(path_text), repo_root, label)}
    raise CoverageDataError(f"{label} is not absolute: {path_text!r}")


def _join_xml_paths(parent: PurePosixPath, child: PurePosixPath) -> PurePosixPath:
    parent_text = parent.as_posix().rstrip("/")
    child_text = child.as_posix().lstrip("/")
    return PurePosixPath(f"{parent_text}/{child_text}")


def _resolve_source_file(
    filename: str,
    source_roots: Sequence[str],
    repo_root: Path,
    authored_sources: Set[Path],
) -> Optional[Path]:
    pure_filename = _normalize_xml_path(filename, "class filename")
    normalized_filename = pure_filename.as_posix()
    filename_is_windows_absolute = _is_windows_absolute(normalized_filename)
    candidates: Set[Path] = set()

    virtual_filename = _virtual_repo_candidate(pure_filename, repo_root)
    if virtual_filename is not None:
        candidates.add(virtual_filename)
    elif pure_filename.is_absolute() or filename_is_windows_absolute:
        candidates.update(
            _absolute_coverage_candidates(
                pure_filename,
                repo_root,
                "absolute class filename",
            )
        )
    else:
        if not source_roots:
            raise CoverageDataError(
                f"relative class filename has no source roots: {filename!r}"
            )
        for raw_source_root in source_roots:
            source_pure = _normalize_xml_path(raw_source_root, "source root")
            source_text = source_pure.as_posix()
            source_is_windows_absolute = _is_windows_absolute(source_text)
            virtual_source = _virtual_repo_candidate(source_pure, repo_root)

            if (
                virtual_source is not None
                or source_pure.is_absolute()
                or source_is_windows_absolute
            ):
                combined = _join_xml_paths(source_pure, pure_filename)
                candidates.update(
                    _absolute_coverage_candidates(
                        combined,
                        repo_root,
                        "coverage source",
                    )
                )
            else:
                candidates.add(
                    _canonical_candidate(
                        repo_root / Path(*source_pure.parts) / Path(*pure_filename.parts),
                        repo_root,
                        "coverage source",
                    )
                )

    eligible = sorted(
        (candidate for candidate in candidates if candidate in authored_sources),
        key=lambda value: str(value),
    )
    if len(eligible) > 1:
        rendered = ", ".join(str(value) for value in eligible)
        raise CoverageDataError(f"ambiguous source resolution for {filename!r}: {rendered}")
    if eligible:
        return eligible[0]
    return None


def _parse_nonnegative_integer(raw: Optional[str], label: str) -> int:
    if raw is None or not re.fullmatch(r"\d+", raw):
        raise CoverageDataError(f"{label} must be a non-negative integer, got {raw!r}")
    return int(raw)


def _parse_line_coverage(line: ET.Element, report: Path) -> Tuple[int, LineCoverage]:
    number = _parse_nonnegative_integer(line.get("number"), f"line number in {report}")
    if number <= 0:
        raise CoverageDataError(f"line number must be positive in {report}, got {number}")
    hits = _parse_nonnegative_integer(line.get("hits"), f"line hits in {report}")

    raw_branch = line.get("branch")
    if raw_branch not in {"True", "False", "true", "false"}:
        raise CoverageDataError(f"line branch flag is malformed in {report}: {raw_branch!r}")
    is_branch = raw_branch.lower() == "true"
    raw_condition_coverage = line.get("condition-coverage")

    if not is_branch:
        if raw_condition_coverage is not None or line.find("conditions") is not None:
            raise CoverageDataError(f"non-branch line contains branch data in {report}")
        return number, LineCoverage(hits=hits)

    if raw_condition_coverage is None:
        raise CoverageDataError(f"branch line has no condition-coverage in {report}")
    match = BRANCH_PATTERN.fullmatch(raw_condition_coverage)
    if match is None:
        raise CoverageDataError(
            f"malformed branch condition-coverage in {report}: {raw_condition_coverage!r}"
        )

    percent = Decimal(match.group("percent"))
    covered = int(match.group("covered"))
    total = int(match.group("total"))
    if percent < 0 or percent > 100 or total <= 0 or covered > total:
        raise CoverageDataError(f"invalid branch totals in {report}: {raw_condition_coverage!r}")
    expected_percent = Decimal(covered) * Decimal(100) / Decimal(total)
    if abs(percent - expected_percent) > Decimal("0.011"):
        raise CoverageDataError(
            f"branch percentage disagrees with totals in {report}: {raw_condition_coverage!r}"
        )

    conditions = line.find("conditions")
    if conditions is not None:
        for condition in conditions.findall("condition"):
            condition_number = condition.get("number")
            condition_type = condition.get("type")
            condition_coverage = condition.get("coverage")
            if not condition_number or not condition_type or condition_coverage is None:
                raise CoverageDataError(f"malformed branch condition element in {report}")
            condition_match = PERCENT_PATTERN.fullmatch(condition_coverage)
            if condition_match is None:
                raise CoverageDataError(
                    f"malformed condition percentage in {report}: {condition_coverage!r}"
                )
            condition_percent = Decimal(condition_match.group("percent"))
            if condition_percent < 0 or condition_percent > 100:
                raise CoverageDataError(
                    f"condition percentage outside 0..100 in {report}: {condition_coverage!r}"
                )

    return number, LineCoverage(hits=hits, branch_covered=covered, branch_total=total)


def _merge_line(
    existing: Optional[LineCoverage], incoming: LineCoverage, source: Path, line_number: int
) -> LineCoverage:
    if existing is None:
        return incoming

    hits = max(existing.hits, incoming.hits)
    if existing.branch_total is None:
        return LineCoverage(hits, incoming.branch_covered, incoming.branch_total)
    if incoming.branch_total is None:
        return LineCoverage(hits, existing.branch_covered, existing.branch_total)
    if existing.branch_total != incoming.branch_total:
        raise CoverageDataError(
            "inconsistent branch-condition totals for "
            f"{source}:{line_number}: {existing.branch_total} vs {incoming.branch_total}"
        )

    assert existing.branch_covered is not None
    assert incoming.branch_covered is not None
    return LineCoverage(
        hits=hits,
        branch_covered=max(existing.branch_covered, incoming.branch_covered),
        branch_total=existing.branch_total,
    )


def _totals(lines: Iterable[LineCoverage]) -> Totals:
    line_values = tuple(lines)
    return Totals(
        line_covered=sum(1 for line in line_values if line.hits > 0),
        line_total=len(line_values),
        branch_covered=sum(line.branch_covered or 0 for line in line_values),
        branch_total=sum(line.branch_total or 0 for line in line_values),
    )


def aggregate_coverage(
    reports: Sequence[Path],
    repo_root: Path,
    expect_reports: Optional[int] = None,
    require_all_projects: bool = False,
) -> CoverageSummary:
    root = repo_root.resolve()
    unique_reports = sorted({Path(report).resolve() for report in reports}, key=lambda value: str(value))
    if not unique_reports:
        raise CoverageDataError("no coverage reports were supplied")
    if expect_reports is not None and len(unique_reports) != expect_reports:
        raise CoverageDataError(
            f"expected {expect_reports} coverage reports, found {len(unique_reports)}"
        )

    authored_sources = discover_authored_sources(root)
    noninstrumentable_sources = load_noninstrumentable_source_exclusions(
        root,
        authored_sources,
    )
    merged: MutableMapping[Tuple[Path, int], LineCoverage] = {}
    included_sources: Set[Path] = set()
    excluded_class_entries = 0

    for report in unique_reports:
        report_root = _parse_report(report)
        source_elements = report_root.findall("./sources/source")
        source_roots = [element.text or "" for element in source_elements]

        classes = report_root.findall("./packages/package/classes/class")
        if not classes:
            raise CoverageDataError(f"coverage report has no classes: {report}")
        for coverage_class in classes:
            filename = coverage_class.get("filename")
            if filename is None:
                raise CoverageDataError(f"coverage class has no filename: {report}")
            lines_element = coverage_class.find("lines")
            if lines_element is None or not lines_element.findall("line"):
                continue

            source_file = _resolve_source_file(filename, source_roots, root, authored_sources)
            if source_file is None:
                excluded_class_entries += 1
                continue
            included_sources.add(source_file)

            for line_element in lines_element.findall("line"):
                line_number, incoming = _parse_line_coverage(line_element, report)
                key = (source_file, line_number)
                merged[key] = _merge_line(merged.get(key), incoming, source_file, line_number)

    if not merged:
        raise CoverageDataError("coverage reports contain no eligible authored production source lines")

    project_lines: Dict[str, List[LineCoverage]] = {}
    for (source_file, _line_number), coverage in sorted(
        merged.items(), key=lambda item: (str(item[0][0]), item[0][1])
    ):
        relative = source_file.relative_to(root / "src")
        project = relative.parts[0] if len(relative.parts) > 1 else "(src-root)"
        project_lines.setdefault(project, []).append(coverage)

    projects = {
        project: _totals(project_lines[project])
        for project in sorted(project_lines)
    }
    if require_all_projects:
        expected_projects = {
            directory.name
            for directory in (root / "src").iterdir()
            if directory.is_dir() and any(directory.glob("*.csproj"))
        }
        missing_projects = sorted(expected_projects - set(projects))
        if missing_projects:
            raise CoverageDataError(
                "production projects absent from coverage: " + ", ".join(missing_projects)
            )
        represented_exclusions = sorted(
            noninstrumentable_sources & included_sources,
            key=lambda value: str(value),
        )
        if represented_exclusions:
            rendered = ", ".join(
                str(source.relative_to(root)) for source in represented_exclusions[:20]
            )
            raise CoverageDataError(
                "noninstrumentable source exclusions unexpectedly contain sequence points: "
                + rendered
            )
        expected_sources = authored_sources - noninstrumentable_sources
        missing_sources = sorted(expected_sources - included_sources, key=lambda value: str(value))
        if missing_sources:
            rendered = ", ".join(
                str(source.relative_to(root)) for source in missing_sources[:20]
            )
            suffix = (
                ""
                if len(missing_sources) <= 20
                else f" (and {len(missing_sources) - 20} more)"
            )
            raise CoverageDataError(
                "authored production sources absent from coverage: " + rendered + suffix
            )
    return CoverageSummary(
        report_count=len(unique_reports),
        source_file_count=len(included_sources),
        authored_source_count=len(authored_sources),
        noninstrumentable_source_count=len(noninstrumentable_sources),
        excluded_class_entries=excluded_class_entries,
        overall=_totals(merged.values()),
        projects=projects,
    )


def _percentage(covered: int, total: int) -> Optional[Decimal]:
    if total == 0:
        return None
    return Decimal(covered) * Decimal(100) / Decimal(total)


def _format_percentage(covered: int, total: int) -> str:
    value = _percentage(covered, total)
    return "n/a" if value is None else f"{value:.4f}%"


def print_summary(summary: CoverageSummary, stream: object = sys.stdout) -> None:
    print(
        f"Coverage reports: {summary.report_count}; authored source files represented: "
        f"{summary.source_file_count}/{summary.authored_source_count}; "
        f"hash-pinned noninstrumentable sources: {summary.noninstrumentable_source_count}; "
        f"excluded class entries: {summary.excluded_class_entries}",
        file=stream,
    )
    overall = summary.overall
    print(
        "Overall: "
        f"lines {overall.line_covered}/{overall.line_total} "
        f"({_format_percentage(overall.line_covered, overall.line_total)}); "
        f"branches {overall.branch_covered}/{overall.branch_total} "
        f"({_format_percentage(overall.branch_covered, overall.branch_total)})",
        file=stream,
    )
    for project, totals in summary.projects.items():
        print(
            f"{project}: lines {totals.line_covered}/{totals.line_total} "
            f"({_format_percentage(totals.line_covered, totals.line_total)}); "
            f"branches {totals.branch_covered}/{totals.branch_total} "
            f"({_format_percentage(totals.branch_covered, totals.branch_total)})",
            file=stream,
        )


def _metric_json(covered: int, total: int) -> Mapping[str, object]:
    percentage = _percentage(covered, total)
    return {
        "covered": covered,
        "total": total,
        "percent": None if percentage is None else float(round(percentage, 6)),
    }


def summary_as_json(summary: CoverageSummary) -> Mapping[str, object]:
    def render_totals(totals: Totals) -> Mapping[str, object]:
        return {
            "line": _metric_json(totals.line_covered, totals.line_total),
            "branch": _metric_json(totals.branch_covered, totals.branch_total),
        }

    return {
        "schema_version": 2,
        "report_count": summary.report_count,
        "source_file_count": summary.source_file_count,
        "authored_source_count": summary.authored_source_count,
        "noninstrumentable_source_count": summary.noninstrumentable_source_count,
        "excluded_class_entries": summary.excluded_class_entries,
        "overall": render_totals(summary.overall),
        "projects": {
            project: render_totals(summary.projects[project])
            for project in sorted(summary.projects)
        },
    }


def _threshold(raw: str) -> Decimal:
    try:
        value = Decimal(raw)
    except InvalidOperation as exc:
        raise argparse.ArgumentTypeError(f"invalid percentage: {raw!r}") from exc
    if not value.is_finite() or value < 0 or value > 100:
        raise argparse.ArgumentTypeError("coverage threshold must be between 0 and 100")
    return value


def enforce_thresholds(
    summary: CoverageSummary,
    minimum_line: Optional[Decimal],
    minimum_branch: Optional[Decimal],
) -> None:
    failures: List[str] = []
    line_percent = _percentage(summary.overall.line_covered, summary.overall.line_total)
    branch_percent = _percentage(summary.overall.branch_covered, summary.overall.branch_total)

    if minimum_line is not None and (line_percent is None or line_percent < minimum_line):
        rendered = "n/a" if line_percent is None else f"{line_percent:.8f}%"
        failures.append(f"line coverage {rendered} is below {minimum_line}%")
    if minimum_branch is not None and (
        branch_percent is None or branch_percent < minimum_branch
    ):
        rendered = "n/a" if branch_percent is None else f"{branch_percent:.8f}%"
        failures.append(f"branch coverage {rendered} is below {minimum_branch}%")
    if failures:
        raise CoverageThresholdError("; ".join(failures))


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Merge Coverlet Cobertura reports without double-counting source lines."
    )
    parser.add_argument("coverage_input", type=Path, help="report file or directory to scan")
    parser.add_argument(
        "--repo-root",
        type=Path,
        default=Path(__file__).resolve().parents[1],
        help="repository root (defaults to this script's repository)",
    )
    parser.add_argument("--minimum-line", type=_threshold, default=None)
    parser.add_argument("--minimum-branch", type=_threshold, default=None)
    parser.add_argument("--expect-reports", type=int, default=None)
    parser.add_argument(
        "--require-all-projects",
        action="store_true",
        help=(
            "fail if any src project or authored source has no represented sequence points; "
            "declaration-only exceptions must be hash-pinned in the audited exclusion file"
        ),
    )
    parser.add_argument(
        "--json",
        dest="json_output",
        metavar="PATH",
        help="also write deterministic JSON to PATH, or '-' for stdout",
    )
    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = _parser().parse_args(argv)
    if args.expect_reports is not None and args.expect_reports <= 0:
        print("coverage error: --expect-reports must be positive", file=sys.stderr)
        return 2

    try:
        reports = discover_reports(args.coverage_input)
        summary = aggregate_coverage(
            reports,
            args.repo_root,
            args.expect_reports,
            args.require_all_projects,
        )
        human_stream = sys.stderr if args.json_output == "-" else sys.stdout
        print_summary(summary, stream=human_stream)

        if args.json_output:
            rendered_json = json.dumps(summary_as_json(summary), indent=2, sort_keys=True) + "\n"
            if args.json_output == "-":
                sys.stdout.write(rendered_json)
            else:
                output_path = Path(args.json_output).expanduser()
                output_path.parent.mkdir(parents=True, exist_ok=True)
                output_path.write_text(rendered_json, encoding="utf-8")

        enforce_thresholds(summary, args.minimum_line, args.minimum_branch)
        return 0
    except CoverageThresholdError as exc:
        print(f"coverage gate failed: {exc}", file=sys.stderr)
        return 3
    except (CoverageDataError, OSError) as exc:
        print(f"coverage error: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
