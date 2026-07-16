import importlib.util
import hashlib
import json
import os
import sys
import tempfile
import unittest
from decimal import Decimal
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "aggregate_coverage.py"
SPEC = importlib.util.spec_from_file_location("aggregate_coverage", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
coverage = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = coverage
SPEC.loader.exec_module(coverage)


class CoverageAggregatorTests(unittest.TestCase):
    def setUp(self):
        self.temporary_directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary_directory.cleanup)
        self.root = Path(self.temporary_directory.name)
        self.repo = self.root / "repo"
        self.source = self.repo / "src" / "Project.One" / "Foo.cs"
        self.source.parent.mkdir(parents=True)
        self.source.write_text("class Foo {}\n", encoding="utf-8")
        (self.source.parent / "Project.One.csproj").write_text(
            "<Project />\n",
            encoding="utf-8",
        )

    def write_report(
        self,
        name,
        *,
        filename="Project.One/Foo.cs",
        hits=1,
        branch="True",
        condition_coverage="50% (1/2)",
        source_roots=None,
        conditions='<conditions><condition number="1" type="jump" coverage="50%" /></conditions>',
    ):
        report = self.root / name / "coverage.cobertura.xml"
        report.parent.mkdir(parents=True)
        roots = [str(self.repo / "src")] if source_roots is None else source_roots
        source_xml = "".join(f"<source>{root}</source>" for root in roots)
        condition_attribute = (
            "" if condition_coverage is None else f' condition-coverage="{condition_coverage}"'
        )
        report.write_text(
            "<?xml version=\"1.0\"?>"
            "<coverage><sources>"
            f"{source_xml}"
            "</sources><packages><package name=\"Project.One\"><classes>"
            f'<class name="Foo" filename="{filename}"><lines>'
            f'<line number="1" hits="{hits}" branch="{branch}"{condition_attribute}>'
            f"{conditions if branch.lower() == 'true' else ''}"
            "</line></lines></class>"
            "</classes></package></packages></coverage>",
            encoding="utf-8",
        )
        return report

    def write_source_exclusions(self, entries):
        path = self.repo / coverage.SOURCE_EXCLUSIONS_FILE
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(
            json.dumps({"schema_version": 1, "sources": entries}),
            encoding="utf-8",
        )
        return path

    def exclusion(self, source, reason="Declaration-only source has no executable sequence points."):
        return {
            "path": source.relative_to(self.repo).as_posix(),
            "reason": reason,
            "sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
        }

    def test_duplicate_reports_merge_max_hits_and_do_not_double_count_branches(self):
        first = self.write_report("first", hits=0, condition_coverage="50% (1/2)")
        duplicate = self.write_report("duplicate", hits=4, condition_coverage="50% (1/2)")
        complete = self.write_report(
            "complete",
            hits=2,
            condition_coverage="100% (2/2)",
            conditions='<conditions><condition number="1" type="jump" coverage="100%" /></conditions>',
        )

        summary = coverage.aggregate_coverage([first, duplicate, complete], self.repo)

        self.assertEqual(summary.report_count, 3)
        self.assertEqual(summary.overall.line_covered, 1)
        self.assertEqual(summary.overall.line_total, 1)
        self.assertEqual(summary.overall.branch_covered, 2)
        self.assertEqual(summary.overall.branch_total, 2)
        merged_line = coverage._merge_line(
            coverage.LineCoverage(4, 1, 2),
            coverage.LineCoverage(2, 2, 2),
            self.source,
            1,
        )
        self.assertEqual(merged_line.hits, 4)

    def test_path_escape_is_rejected_even_when_target_does_not_exist(self):
        report = self.write_report("escape", filename="../../../outside.cs")

        with self.assertRaisesRegex(coverage.CoverageDataError, "parent traversal"):
            coverage.aggregate_coverage([report], self.repo)

    def test_ambiguous_existing_source_resolution_is_rejected(self):
        second = self.repo / "src" / "Project.Two" / "Foo.cs"
        second.parent.mkdir(parents=True)
        second.write_text("class FooTwo {}\n", encoding="utf-8")
        report = self.write_report(
            "ambiguous",
            filename="Foo.cs",
            source_roots=[str(self.source.parent), str(second.parent)],
        )

        with self.assertRaisesRegex(coverage.CoverageDataError, "ambiguous source resolution"):
            coverage.aggregate_coverage([report], self.repo)

    def test_platform_neutral_path_map_source_resolves(self):
        report = self.write_report(
            "path-map",
            filename="Foo.cs",
            source_roots=["/_/src/Project.One/"],
        )

        summary = coverage.aggregate_coverage([report], self.repo)

        self.assertEqual(summary.source_file_count, 1)
        self.assertEqual(summary.projects["Project.One"].line_covered, 1)

    def test_deterministic_report_without_source_roots_resolves(self):
        report = self.write_report(
            "deterministic-report",
            filename="/_/src/Project.One/Foo.cs",
            source_roots=[],
        )

        summary = coverage.aggregate_coverage([report], self.repo)

        self.assertEqual(summary.source_file_count, 1)

    def test_relative_filename_without_source_roots_is_rejected(self):
        report = self.write_report(
            "missing-source-root",
            filename="Project.One/Foo.cs",
            source_roots=[],
        )

        with self.assertRaisesRegex(coverage.CoverageDataError, "has no source roots"):
            coverage.aggregate_coverage([report], self.repo)

    @unittest.skipIf(os.name == "nt", "exercises Windows coverage on a POSIX merge host")
    def test_windows_drive_source_root_merges_on_posix(self):
        report = self.write_report(
            "windows-root",
            filename="Foo.cs",
            source_roots=[r"D:\a\SuavoAgent\SuavoAgent\src\Project.One"],
        )

        summary = coverage.aggregate_coverage([report], self.repo)

        self.assertEqual(summary.source_file_count, 1)
        self.assertEqual(summary.projects["Project.One"].line_total, 1)

    @unittest.skipIf(os.name == "nt", "exercises Windows coverage on a POSIX merge host")
    def test_windows_absolute_class_filename_merges_on_posix(self):
        report = self.write_report(
            "windows-filename",
            filename=r"D:\a\SuavoAgent\SuavoAgent\src\Project.One\Foo.cs",
            source_roots=[r"D:\a\SuavoAgent\SuavoAgent"],
        )

        summary = coverage.aggregate_coverage([report], self.repo)

        self.assertEqual(summary.source_file_count, 1)

    @unittest.skipIf(os.name == "nt", "exercises Windows coverage on a POSIX merge host")
    def test_ambiguous_windows_src_suffix_is_rejected(self):
        nested = self.repo / "src" / "Project.One" / "src" / "Project.Two" / "Foo.cs"
        nested.parent.mkdir(parents=True)
        nested.write_text("class NestedFoo {}\n", encoding="utf-8")
        second = self.repo / "src" / "Project.Two" / "Foo.cs"
        second.parent.mkdir(parents=True)
        second.write_text("class OtherFoo {}\n", encoding="utf-8")
        report = self.write_report(
            "windows-ambiguous",
            filename="Foo.cs",
            source_roots=[r"D:\a\src\Project.One\src\Project.Two"],
        )

        with self.assertRaisesRegex(coverage.CoverageDataError, "ambiguous source resolution"):
            coverage.aggregate_coverage([report], self.repo)

    @unittest.skipIf(os.name == "nt", "exercises Windows coverage on a POSIX merge host")
    def test_windows_source_root_parent_traversal_is_rejected(self):
        report = self.write_report(
            "windows-traversal",
            filename="Foo.cs",
            source_roots=[r"D:\a\..\src\Project.One"],
        )

        with self.assertRaisesRegex(coverage.CoverageDataError, "parent traversal"):
            coverage.aggregate_coverage([report], self.repo)

    def test_generated_vendored_and_nonexistent_sources_are_excluded(self):
        generated = self.write_report(
            "generated",
            filename=(
                "Project.One/System.Text.RegularExpressions.Generator/"
                "RegexGenerator/RegexGenerator.g.cs"
            ),
        )
        vendored_path = (
            self.repo
            / "src"
            / "SuavoAgent.Diagnostics"
            / "ThirdParty"
            / "Vendor.cs"
        )
        vendored_path.parent.mkdir(parents=True)
        vendored_path.write_text("class Vendor {}\n", encoding="utf-8")
        vendored = self.write_report(
            "vendored",
            filename="SuavoAgent.Diagnostics/ThirdParty/Vendor.cs",
        )
        included = self.write_report("included")

        summary = coverage.aggregate_coverage([generated, vendored, included], self.repo)

        self.assertEqual(summary.source_file_count, 1)
        self.assertEqual(summary.excluded_class_entries, 2)

    def test_malformed_branch_data_is_rejected(self):
        report = self.write_report("malformed", condition_coverage="not-coverage")

        with self.assertRaisesRegex(coverage.CoverageDataError, "malformed branch"):
            coverage.aggregate_coverage([report], self.repo)

    def test_thresholds_pass_at_actual_value_and_fail_above_it(self):
        report = self.write_report("threshold")
        summary = coverage.aggregate_coverage([report], self.repo)

        coverage.enforce_thresholds(summary, Decimal("100"), Decimal("50"))
        with self.assertRaisesRegex(coverage.CoverageThresholdError, "branch coverage"):
            coverage.enforce_thresholds(summary, Decimal("100"), Decimal("50.0001"))

    def test_expected_report_count_and_json_are_deterministic(self):
        report = self.write_report("json")
        summary = coverage.aggregate_coverage([report], self.repo, expect_reports=1)

        first = json.dumps(coverage.summary_as_json(summary), sort_keys=True)
        second = json.dumps(coverage.summary_as_json(summary), sort_keys=True)

        self.assertEqual(first, second)
        self.assertEqual(json.loads(first)["projects"]["Project.One"]["line"]["total"], 1)
        self.assertEqual(json.loads(first)["schema_version"], 2)
        self.assertEqual(json.loads(first)["authored_source_count"], 1)
        with self.assertRaisesRegex(coverage.CoverageDataError, "expected 2"):
            coverage.aggregate_coverage([report], self.repo, expect_reports=2)

    def test_required_production_project_missing_from_reports_is_rejected(self):
        second = self.repo / "src" / "Project.Two" / "Bar.cs"
        second.parent.mkdir(parents=True)
        second.write_text("class Bar {}\n", encoding="utf-8")
        (second.parent / "Project.Two.csproj").write_text(
            "<Project />\n",
            encoding="utf-8",
        )
        report = self.write_report("missing-project")

        with self.assertRaisesRegex(
            coverage.CoverageDataError,
            "production projects absent from coverage: Project.Two",
        ):
            coverage.aggregate_coverage(
                [report],
                self.repo,
                require_all_projects=True,
            )

    def test_required_source_missing_within_represented_project_is_rejected(self):
        missing = self.source.parent / "Missing.cs"
        missing.write_text("class Missing {}\n", encoding="utf-8")
        report = self.write_report("missing-source")

        with self.assertRaisesRegex(
            coverage.CoverageDataError,
            r"authored production sources absent from coverage: src/Project.One/Missing.cs",
        ):
            coverage.aggregate_coverage(
                [report],
                self.repo,
                require_all_projects=True,
            )

    def test_hash_pinned_declaration_only_source_can_be_excluded(self):
        declaration = self.source.parent / "IContract.cs"
        declaration.write_text("interface IContract {}\n", encoding="utf-8")
        self.write_source_exclusions([self.exclusion(declaration)])
        report = self.write_report("declaration-excluded")

        summary = coverage.aggregate_coverage(
            [report],
            self.repo,
            require_all_projects=True,
        )

        self.assertEqual(summary.authored_source_count, 2)
        self.assertEqual(summary.noninstrumentable_source_count, 1)

    def test_changed_excluded_source_requires_reaudit(self):
        declaration = self.source.parent / "IContract.cs"
        declaration.write_text("interface IContract {}\n", encoding="utf-8")
        exclusion = self.exclusion(declaration)
        self.write_source_exclusions([exclusion])
        declaration.write_text("interface IContract { void Run(); }\n", encoding="utf-8")
        report = self.write_report("changed-exclusion")

        with self.assertRaisesRegex(coverage.CoverageDataError, "hash changed and requires re-audit"):
            coverage.aggregate_coverage(
                [report],
                self.repo,
                require_all_projects=True,
            )

    def test_oversized_exclusion_catalog_is_rejected(self):
        path = self.repo / coverage.SOURCE_EXCLUSIONS_FILE
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(" " * (coverage.MAX_SOURCE_EXCLUSIONS_BYTES + 1), encoding="utf-8")
        report = self.write_report("oversized-exclusions")

        with self.assertRaisesRegex(coverage.CoverageDataError, "size is outside"):
            coverage.aggregate_coverage([report], self.repo)

    def test_exclusion_with_sequence_points_is_rejected(self):
        self.write_source_exclusions([self.exclusion(self.source)])
        report = self.write_report("represented-exclusion")

        with self.assertRaisesRegex(
            coverage.CoverageDataError,
            "exclusions unexpectedly contain sequence points",
        ):
            coverage.aggregate_coverage(
                [report],
                self.repo,
                require_all_projects=True,
            )


if __name__ == "__main__":
    unittest.main()
