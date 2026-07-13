"""Immutable value types and policy constants for authored-code coverage."""

from dataclasses import dataclass
from pathlib import Path
from typing import Mapping, Optional


GENERATED_FILE_PATTERNS = (
    ".g.cs",
    ".generated.cs",
    ".designer.cs",
    ".assemblyinfo.cs",
    ".globalusings.g.cs",
)
SOURCE_EXCLUSIONS_FILE = Path("scripts/coverage-noninstrumentable-sources.json")


class CoverageDataError(RuntimeError):
    """The coverage corpus cannot be interpreted safely."""


class CoverageThresholdError(RuntimeError):
    """A requested coverage threshold was not met."""


@dataclass(frozen=True)
class LineCoverage:
    hits: int
    branch_covered: Optional[int] = None
    branch_total: Optional[int] = None


@dataclass(frozen=True)
class Totals:
    line_covered: int
    line_total: int
    branch_covered: int
    branch_total: int


@dataclass(frozen=True)
class CoverageSummary:
    report_count: int
    source_file_count: int
    authored_source_count: int
    noninstrumentable_source_count: int
    excluded_class_entries: int
    overall: Totals
    projects: Mapping[str, Totals]
