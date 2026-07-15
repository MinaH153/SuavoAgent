import re
import unittest
from pathlib import Path


REPOSITORY_ROOT = Path(__file__).resolve().parents[1]


def workflow(name: str) -> str:
    return (REPOSITORY_ROOT / ".github" / "workflows" / name).read_text(
        encoding="utf-8"
    )


def job_block(document: str, job_name: str) -> str:
    match = re.search(
        rf"(?ms)^  {re.escape(job_name)}:\n(?P<body>.*?)(?=^  [a-z0-9][a-z0-9_-]*:\n|\Z)",
        document,
    )
    if match is None:
        raise AssertionError(f"workflow job is missing: {job_name}")
    return match.group(0)


class ReleaseWorkflowGateTests(unittest.TestCase):
    def test_release_and_hotfix_gate_signing_on_exact_sha_windows_suite(self):
        for workflow_name in ("release.yml", "hotfix.yml"):
            with self.subTest(workflow=workflow_name):
                document = workflow(workflow_name)
                windows_gate = job_block(document, "full-windows-suite")
                build = job_block(document, "build")
                coverage = job_block(document, "production-coverage")
                signing = job_block(document, "sign_windows")

                self.assertIn("runs-on: windows-latest", windows_gate)
                self.assertIn("ref: ${{ github.sha }}", windows_gate)
                self.assertIn("git rev-parse HEAD", windows_gate)
                self.assertIn("dotnet test SuavoAgent.sln", windows_gate)
                self.assertIn("LogFilePrefix=", windows_gate)
                self.assertNotIn("LogFileName=", windows_gate)
                self.assertIn('--collect "XPlat Code Coverage"', windows_gate)
                self.assertIn("DeterministicReport=true", windows_gate)
                self.assertIn("coverage.cobertura.xml", windows_gate)
                self.assertNotIn("continue-on-error", windows_gate)
                self.assertRegex(
                    build,
                    r"needs: \[[^\]]*full-windows-suite[^\]]*\]",
                )
                self.assertRegex(
                    signing,
                    r"needs: \[[^\]]*full-windows-suite[^\]]*\]",
                )
                self.assertRegex(
                    coverage,
                    r"needs: \[[^\]]*build[^\]]*full-windows-suite[^\]]*\]",
                )
                self.assertRegex(
                    signing,
                    r"needs: \[[^\]]*production-coverage[^\]]*\]",
                )
                self.assertEqual(
                    document.count("actions/checkout@"),
                    document.count("ref: ${{ github.sha }}"),
                    "every release-stage checkout must remain pinned to the tested SHA",
                )

    def test_release_and_hotfix_preserve_production_coverage_gate(self):
        for workflow_name in ("release.yml", "hotfix.yml"):
            with self.subTest(workflow=workflow_name):
                document = workflow(workflow_name)
                build = job_block(document, "build")
                coverage = job_block(document, "production-coverage")
                resolver_test = "tests/test_resolve_release_rollback_evidence.py"
                self.assertIn(resolver_test, build)
                self.assertIn(resolver_test, coverage)
                self.assertIn("DeterministicReport=true", build)
                self.assertIn("255", build)
                self.assertIn("65535", build)
                self.assertIn("--expect-reports 20", coverage)
                self.assertIn("--require-all-projects", coverage)
                self.assertIn("--minimum-line 80", coverage)
                self.assertIn("--minimum-branch 80", coverage)
                self.assertIn("combined/linux", coverage)
                self.assertIn("combined/windows", coverage)

    def test_release_and_hotfix_build_sign_and_smoke_native_installers(self):
        prerequisite_sha = (
            "cc0ff0eb1dc3f5188ae6300faef32bf5beeba4bdd6e8e445a9184072096b713b"
        )
        for workflow_name in ("release.yml", "hotfix.yml"):
            with self.subTest(workflow=workflow_name):
                document = workflow(workflow_name)
                preflight = job_block(document, "release-signing-preflight")
                build_msi = job_block(document, "build_msi")
                sign_msi = job_block(document, "sign_msi")
                build_bundle = job_block(document, "build_bundle")
                sign_bundle = job_block(document, "sign_bundle")
                smoke = job_block(document, "windows-release-smoke")
                publish = job_block(document, "release")

                self.assertIn("WIX_OSMF_EULA_ACCEPTED", preflight)
                self.assertIn("VC_REDIST_X64_URL", preflight)
                self.assertIn("SuavoAgent.Msi.wixproj", build_msi)
                self.assertIn("-p:AcceptEula=wix7", build_msi)
                self.assertIn("sslcom/actions-codesigner@", sign_msi)
                self.assertIn("-p:BuildProjectReferences=false", build_bundle)
                self.assertIn(prerequisite_sha, build_bundle)
                self.assertIn("sslcom/actions-codesigner@", sign_bundle)
                self.assertIn("Start-Process msiexec.exe", smoke)
                self.assertIn("Get-AuthenticodeSignature", smoke)
                self.assertIn("release/SuavoAgent-*-win-x64.msi", publish)
                self.assertIn("release/SuavoAgent-Setup.exe", publish)
                self.assertIn('ARTIFACT="SuavoAgent-Setup.exe"', publish)
                self.assertNotRegex(
                    document,
                    r"SuavoAgent-Setup-(?:\$\{\{|\$env:|\$\{VERSION\})",
                )
                self.assertNotIn("release/suavoagent-*-win-x64.zip", publish)

    def test_release_and_hotfix_resolve_rollback_from_signed_prior_receipt(self):
        for workflow_name in ("release.yml", "hotfix.yml"):
            with self.subTest(workflow=workflow_name):
                document = workflow(workflow_name)
                publish = job_block(document, "release")
                self.assertIn("--pattern field-release-receipt.json", publish)
                self.assertIn("resolve-release-rollback-evidence.py", publish)
                self.assertIn('ROLLBACK_ARTIFACT="${ROLLBACK_EVIDENCE[0]}"', publish)
                self.assertIn('ROLLBACK_ACTUAL_SHA="$(sha256sum', publish)
                self.assertIn('[[ "$ROLLBACK_ACTUAL_SHA" != "$ROLLBACK_SHA" ]]', publish)
                self.assertNotIn(
                    'ROLLBACK_ARTIFACT="SuavoAgent-Setup-${ROLLBACK_TAG}-win-x64.exe"',
                    publish,
                )
                self.assertLess(
                    publish.index("openssl dgst -sha256 -verify"),
                    publish.index("resolve-release-rollback-evidence.py"),
                )
                self.assertLess(
                    publish.index("resolve-release-rollback-evidence.py"),
                    publish.index("ROLLBACK_ACTUAL_SHA"),
                )

    def test_ci_collectors_are_deterministic_and_merge_both_platforms(self):
        document = workflow("ci.yml")
        linux = job_block(document, "build-and-test")
        windows = job_block(document, "windows-coverage")
        merger = job_block(document, "coverage-report")

        self.assertIn("DeterministicReport=true", linux)
        self.assertIn("DeterministicReport=true", windows)
        self.assertIn("needs: [build-and-test, windows-coverage]", merger)
        self.assertIn("--expect-reports 20", merger)
        self.assertIn("--require-all-projects", merger)

    def test_repository_does_not_break_coverlet_with_a_synthetic_path_map(self):
        props = (REPOSITORY_ROOT / "Directory.Build.props").read_text(encoding="utf-8")
        self.assertNotIn("<PathMap>", props)
        merger = (REPOSITORY_ROOT / "scripts" / "aggregate_coverage.py").read_text(
            encoding="utf-8"
        )
        self.assertIn("_remote_windows_candidates", merger)
        self.assertIn("_canonical_candidate", merger)

    def test_codeql_scans_every_repository_language_with_pinned_actions(self):
        document = workflow("codeql.yml")
        for language in ("csharp", "python", "actions"):
            with self.subTest(language=language):
                self.assertRegex(document, rf"(?m)^\s*- language: {language}$")
        self.assertIn("queries: security-and-quality", document)
        self.assertIn("security-events: write", document)
        action_uses = re.findall(r"uses: ([^\s]+)", document)
        self.assertGreaterEqual(len(action_uses), 3)
        for action in action_uses:
            with self.subTest(action=action):
                self.assertRegex(action, r"@[0-9a-f]{40}$")

    def test_every_workflow_action_is_commit_pinned(self):
        workflow_root = REPOSITORY_ROOT / ".github" / "workflows"
        for path in sorted(workflow_root.glob("*.yml")):
            for line_number, line in enumerate(
                path.read_text(encoding="utf-8").splitlines(), start=1
            ):
                match = re.search(r"\buses:\s+([^\s#]+)", line)
                if match is None:
                    continue
                with self.subTest(workflow=path.name, line=line_number):
                    self.assertRegex(match.group(1), r"@[0-9a-f]{40}$")

    def test_dependabot_covers_nuget_and_workflow_dependencies(self):
        document = (REPOSITORY_ROOT / ".github" / "dependabot.yml").read_text(
            encoding="utf-8"
        )
        self.assertIn("package-ecosystem: nuget", document)
        self.assertIn("package-ecosystem: github-actions", document)
        self.assertEqual(document.count("interval: weekly"), 2)


if __name__ == "__main__":
    unittest.main()
