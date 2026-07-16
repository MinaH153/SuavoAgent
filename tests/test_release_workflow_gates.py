import json
import re
import subprocess
import sys
import tempfile
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


def run_ruleset_validator(document: object, repository: str = "MinaH153/SuavoAgent"):
    with tempfile.TemporaryDirectory() as temporary:
        path = Path(temporary) / "rulesets.json"
        path.write_text(json.dumps(document), encoding="utf-8")
        return subprocess.run(
            (
                sys.executable,
                str(REPOSITORY_ROOT / "scripts" / "validate-release-tag-ruleset.py"),
                "--input",
                str(path),
                "--repository",
                repository,
            ),
            capture_output=True,
            text=True,
            check=False,
        )


class ReleaseWorkflowGateTests(unittest.TestCase):
    @staticmethod
    def exact_release_tag_ruleset() -> dict[str, object]:
        return {
            "id": 42,
            "name": "Immutable stable release tags",
            "source_type": "Repository",
            "source": "MinaH153/SuavoAgent",
            "target": "tag",
            "enforcement": "active",
            "bypass_actors": [],
            "conditions": {
                "ref_name": {"include": ["refs/tags/v*"], "exclude": []}
            },
            "rules": [{"type": "update"}, {"type": "deletion"}],
        }

    def test_release_tag_ruleset_validator_accepts_only_closed_stable_tag_policy(self):
        valid = self.exact_release_tag_ruleset()
        self.assertEqual(0, run_ruleset_validator([valid]).returncode)
        self.assertEqual(0, run_ruleset_validator([valid, valid | {"id": 43}]).returncode)

        invalid_documents = {
            "missing": [],
            "inactive": [valid | {"enforcement": "evaluate"}],
            "organization-owned": [valid | {"source_type": "Organization"}],
            "wrong-pattern": [
                valid
                | {
                    "conditions": {
                        "ref_name": {"include": ["refs/tags/release-*"], "exclude": []}
                    }
                }
            ],
            "excluded-stable-tags": [
                valid
                | {
                    "conditions": {
                        "ref_name": {
                            "include": ["refs/tags/v*"],
                            "exclude": ["refs/tags/v4.*"],
                        }
                    }
                }
            ],
            "bypass": [valid | {"bypass_actors": [{"actor_type": "RepositoryRole"}]}],
            "updates-allowed": [valid | {"rules": [{"type": "deletion"}]}],
            "deletions-allowed": [valid | {"rules": [{"type": "update"}]}],
            "creation-blocked": [
                valid
                | {
                    "rules": [
                        {"type": "update"},
                        {"type": "deletion"},
                        {"type": "creation"},
                    ]
                }
            ],
        }
        for label, document in invalid_documents.items():
            with self.subTest(label=label):
                result = run_ruleset_validator(document)
                self.assertNotEqual(0, result.returncode)
                self.assertIn("release tag ruleset validation failed", result.stderr)

    def test_publishers_revalidate_exact_tag_ruleset_before_draft_and_publish(self):
        publishers = (
            job_block(
                workflow("production-release-signing.yml"),
                "sign-and-publish-reviewed-release",
            ),
            job_block(workflow("v1-bridge-finalize.yml"), "attest-and-release"),
        )
        for publisher in publishers:
            with self.subTest(publisher=publisher.splitlines()[0].strip()):
                self.assertIn("validate-release-tag-ruleset.py", publisher)
                self.assertEqual(1, publisher.count("validate_release_tag_ruleset before-draft"))
                self.assertEqual(1, publisher.count("validate_release_tag_ruleset before-publish"))
                draft_gate = publisher.index("validate_release_tag_ruleset before-draft")
                draft_create = publisher.index("gh release create")
                publish_gate = publisher.index("validate_release_tag_ruleset before-publish")
                publish = publisher.index("--method PATCH")
                self.assertLess(draft_gate, draft_create)
                self.assertLess(draft_create, publish_gate)
                self.assertLess(publish_gate, publish)

    def test_every_esigner_call_revalidates_exact_protected_main(self):
        for workflow_name in ("release.yml", "hotfix.yml"):
            document = workflow(workflow_name)
            trusted = job_block(document, "trusted-main-source")
            self.assertIn('[[ "$GITHUB_REF_PROTECTED" == "true" ]]', trusted)
            for job_name in ("sign_windows", "sign_msi", "sign_bundle"):
                with self.subTest(workflow=workflow_name, job=job_name):
                    signing = job_block(document, job_name)
                    signer = signing.index("scripts/esigner-codesign-hardened.sh")
                    for marker in (
                        '[[ "$GITHUB_REF" == "refs/heads/main" ]]',
                        '[[ "$GITHUB_REF_PROTECTED" == "true" ]]',
                        '[[ "$(git rev-parse origin/main)" == "$GITHUB_SHA" ]]',
                    ):
                        self.assertIn(marker, signing[:signer])

        stage = workflow("v1-bridge-stage.yml")
        source_gate = job_block(stage, "source-gate")
        self.assertIn('[[ "$GITHUB_REF_PROTECTED" == "true" ]]', source_gate)
        for job_name in ("sign-windows", "sign-msi", "sign-bundle"):
            with self.subTest(workflow="v1-bridge-stage.yml", job=job_name):
                signing = job_block(stage, job_name)
                signer = signing.index("scripts/esigner-codesign-hardened.sh")
                for marker in (
                    '[[ "$GITHUB_REF" == "refs/heads/main" ]]',
                    '[[ "$GITHUB_REF_PROTECTED" == "true" ]]',
                    '[[ "$(git rev-parse origin/main)" == "${{ inputs.source_sha }}" ]]',
                ):
                    self.assertIn(marker, signing[:signer])

    def test_hotfix_outputs_match_reusable_publisher_artifact_names(self):
        hotfix = workflow("hotfix.yml")
        reusable = workflow("production-release-signing.yml")
        for artifact in (
            "suavoagent-final-${{ inputs.version }}",
            "suavoagent-msi-${{ inputs.version }}",
            "suavoagent-bundle-${{ inputs.version }}",
        ):
            with self.subTest(artifact=artifact):
                self.assertIn(f"name: {artifact}", hotfix)
                self.assertIn(f"name: {artifact}", reusable)
        self.assertNotRegex(
            hotfix,
            r"name: suavoagent-hotfix-(?:final|msi|bundle)-\$\{\{ inputs\.version \}\}",
        )

    def test_release_and_hotfix_use_protected_main_draft_first_publication(self):
        release_document = workflow("release.yml")
        reusable_publish = job_block(
            workflow("production-release-signing.yml"), "sign-and-publish-reviewed-release"
        )
        self.assertIn("workflow_dispatch:", release_document)
        self.assertNotRegex(release_document, r"(?m)^\s+tags:\s*$")
        for workflow_name in ("release.yml", "hotfix.yml"):
            with self.subTest(workflow=workflow_name):
                document = workflow(workflow_name)
                trusted = job_block(document, "trusted-main-source")
                preflight = job_block(document, "release-signing-preflight")
                build = job_block(document, "build")
                publish = job_block(document, "release")
                self.assertIn('[[ "$GITHUB_REF" == "refs/heads/main" ]]', trusted)
                self.assertIn('[[ "$(git rev-parse origin/main)" == "$GITHUB_SHA" ]]', trusted)
                self.assertIn("needs: trusted-main-source", preflight)
                self.assertIn("no leading zeros", build)
                self.assertIn("assert-version-newest", build)
                self.assertIn("assert-release-absent", build)
                self.assertNotIn("softprops/action-gh-release", document)
                self.assertIn("uses: ./.github/workflows/production-release-signing.yml", publish)
                self.assertIn("gh release create", reusable_publish)
                self.assertIn("--draft", reusable_publish)
                self.assertIn("validate-release-assets", reusable_publish)
                self.assertNotIn("gh release edit", reusable_publish)
                self.assertIn("--method PATCH", reusable_publish)
                self.assertIn(
                    '"repos/$GITHUB_REPOSITORY/releases/$RELEASE_ID"',
                    reusable_publish,
                )
                self.assertEqual(2, reusable_publish.count("validate-publication-state"))
                self.assertIn("--expected-immutable false", reusable_publish)
                self.assertIn("--expected-immutable true", reusable_publish)
                self.assertIn("--reference-assets prepublish-assets.json", reusable_publish)
                self.assertIn("immutable-releases", reusable_publish)
                immutable_checks = [
                    match.start()
                    for match in re.finditer(
                        '"repos/\\$GITHUB_REPOSITORY/immutable-releases"',
                        reusable_publish,
                    )
                ]
                create_index = reusable_publish.index("gh release create")
                patch_index = reusable_publish.index("--method PATCH")
                self.assertEqual(2, len(immutable_checks))
                self.assertLess(immutable_checks[0], create_index)
                self.assertLess(create_index, immutable_checks[1])
                self.assertLess(immutable_checks[1], patch_index)
                self.assertGreaterEqual(reusable_publish.count("git/ref/tags/$VERSION"), 3)
                self.assertLess(
                    reusable_publish.index("validate-release-assets"),
                    reusable_publish.index("--method PATCH"),
                )
                self.assertLess(
                    reusable_publish.index("--expected-immutable false"),
                    reusable_publish.index("--method PATCH"),
                )
                self.assertGreater(
                    reusable_publish.index("--expected-immutable true"),
                    reusable_publish.index("--method PATCH"),
                )

    def test_release_and_hotfix_rehearse_exact_signed_msi_and_burn_bundle(self):
        for workflow_name in ("release.yml", "hotfix.yml"):
            with self.subTest(workflow=workflow_name):
                smoke = job_block(workflow(workflow_name), "windows-release-smoke")
                self.assertEqual(
                    2, smoke.count("Invoke-SuavoAgentInstallerRehearsal.ps1")
                )
                self.assertIn("-InstallerKind Msi", smoke)
                self.assertIn("-InstallerKind Bundle", smoke)
                self.assertEqual(2, smoke.count("-MsiPath $msi[0].FullName"))
                self.assertIn(
                    "-EvidenceDirectory 'installer-rehearsal-evidence/msi'", smoke
                )
                self.assertIn(
                    "-EvidenceDirectory 'installer-rehearsal-evidence/bundle'", smoke
                )
                self.assertIn("Upload installer rehearsal evidence", smoke)
                self.assertIn("if: always()", smoke)
                self.assertIn("path: installer-rehearsal-evidence/", smoke)

    def test_v1_bridge_rehearses_exact_signed_msi_and_burn_bundle(self):
        smoke = job_block(workflow("v1-bridge-stage.yml"), "windows-smoke")
        self.assertEqual(2, smoke.count("Invoke-SuavoAgentInstallerRehearsal.ps1"))
        self.assertIn("-InstallerKind Msi", smoke)
        self.assertIn("-InstallerKind Bundle", smoke)
        self.assertEqual(2, smoke.count("-MsiPath $msi[0].FullName"))
        self.assertIn("-ExpectedReleaseTag '${{ inputs.version }}'", smoke)
        self.assertIn("-AllowedSignerSha256 '${{ needs.source-gate.outputs.authenticode_signer_sha256 }}'", smoke)
        self.assertIn("-EvidenceDirectory 'installer-rehearsal-evidence/msi'", smoke)
        self.assertIn("-EvidenceDirectory 'installer-rehearsal-evidence/bundle'", smoke)
        self.assertIn("Upload installer rehearsal evidence", smoke)
        self.assertIn("if: always()", smoke)
        self.assertIn("path: installer-rehearsal-evidence/", smoke)

    def test_v1_bridge_regenerates_final_sbom_after_private_config_removal(self):
        stage = job_block(workflow("v1-bridge-stage.yml"), "prepare-request")
        self.assertIn("name: v1-bridge-msi-${{ inputs.version }}", stage)
        self.assertIn("name: v1-bridge-bundle-${{ inputs.version }}", stage)
        self.assertEqual(1, stage.count("--regenerate-final-sbom"))
        self.assertLess(
            stage.index("rm stage/release/appsettings.json"),
            stage.index("--regenerate-final-sbom"),
        )
        source = (REPOSITORY_ROOT / "scripts/v1_bridge_release.py").read_text(
            encoding="utf-8"
        )
        prepare = source[source.index("def prepare_request(") : source.index(
            "def validate_request(", source.index("def prepare_request(")
        )]
        self.assertLess(
            prepare.index("_exclusive_write(\n        receipt_path"),
            prepare.index("regenerate_final_sbom"),
        )
        self.assertLess(
            prepare.index("regenerate_final_sbom"),
            prepare.index("_validate_release_allowlist"),
        )
        sign_windows = job_block(workflow("v1-bridge-stage.yml"), "sign-windows")
        self.assertNotIn("generate-release-sbom.py", sign_windows)

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
                self.assertNotIn("--expect-reports", coverage)
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
                reusable_publish = job_block(
                    workflow("production-release-signing.yml"),
                    "sign-and-publish-reviewed-release",
                )

                self.assertIn("WIX_OSMF_EULA_ACCEPTED", preflight)
                self.assertIn("VC_REDIST_X64_URL", preflight)
                self.assertIn(
                    "ota_update_trust_roots.py validate --require-key-id ota-update-v2",
                    preflight,
                )
                self.assertIn("assert-signing-public-key", preflight)
                self.assertIn("SuavoAgent.Msi.wixproj", build_msi)
                self.assertIn("-p:AcceptEula=wix7", build_msi)
                self.assertIn("scripts/esigner-codesign-hardened.sh", sign_msi)
                self.assertIn("verify-signature: true", sign_msi)
                self.assertIn("-p:BuildProjectReferences=false", build_bundle)
                self.assertIn(prerequisite_sha, build_bundle)
                self.assertIn("scripts/esigner-codesign-hardened.sh", sign_bundle)
                self.assertIn("verify-signature: true", sign_bundle)
                self.assertEqual(
                    2, smoke.count("Invoke-SuavoAgentInstallerRehearsal.ps1")
                )
                self.assertIn("-InstallerKind Msi", smoke)
                self.assertIn("-InstallerKind Bundle", smoke)
                self.assertEqual(2, smoke.count("-MsiPath $msi[0].FullName"))
                self.assertEqual(3, smoke.count("MpCmdRun.exe"))
                self.assertIn("uses: ./.github/workflows/production-release-signing.yml", publish)
                self.assertIn("publication-paths", reusable_publish)
                self.assertIn("--release-dir release", reusable_publish)
                self.assertIn("validate-release-assets", reusable_publish)
                self.assertIn('ARTIFACT="SuavoAgent-Setup.exe"', reusable_publish)
                self.assertNotRegex(
                    document,
                    r"SuavoAgent-Setup-(?:\$\{\{|\$env:|\$\{VERSION\})",
                )
                self.assertNotIn("release/suavoagent-*-win-x64.zip", reusable_publish)

    def test_protected_environment_values_are_not_read_by_unprotected_jobs(self):
        for workflow_name in (
            "release.yml",
            "hotfix.yml",
            "v1-bridge-stage.yml",
            "v1-bridge-authorize.yml",
            "v1-bridge-finalize.yml",
            "production-signing.yml",
            "production-release-signing.yml",
        ):
            document = workflow(workflow_name)
            names = re.findall(r"(?m)^  ([a-z0-9][a-z0-9_-]*):$", document)
            for name in names:
                block = job_block(document, name)
                if "${{ vars." in block or "${{ secrets." in block:
                    with self.subTest(workflow=workflow_name, job=name):
                        self.assertIn(
                            "environment: suavoagent-production-signing", block
                        )

        for workflow_name in ("release.yml", "hotfix.yml"):
            document = workflow(workflow_name)
            preflight = job_block(document, "release-signing-preflight")
            bundle = job_block(document, "build_bundle")
            self.assertIn(
                "vc_redist_x64_url: ${{ steps.signing-preflight.outputs.vc_redist_x64_url }}",
                preflight,
            )
            self.assertIn(
                "VC_REDIST_X64_URL: ${{ needs.release-signing-preflight.outputs.vc_redist_x64_url }}",
                bundle,
            )
            self.assertNotIn("${{ vars.VC_REDIST_X64_URL }}", bundle)

        stage = workflow("v1-bridge-stage.yml")
        source_gate = job_block(stage, "source-gate")
        bundle = job_block(stage, "build-bundle")
        self.assertIn(
            "vc_redist_x64_url: ${{ steps.gate.outputs.vc_redist_x64_url }}",
            source_gate,
        )
        self.assertIn(
            "VC_REDIST_X64_URL: ${{ needs.source-gate.outputs.vc_redist_x64_url }}",
            bundle,
        )
        self.assertNotIn("${{ vars.VC_REDIST_X64_URL }}", bundle)

    def test_esigner_and_installer_timestamp_boundaries_fail_closed(self):
        setup_java = "actions/setup-java@0f481fcb613427c0f801b606911222b5b6f3083a"
        for workflow_name in ("release.yml", "hotfix.yml", "v1-bridge-stage.yml"):
            with self.subTest(workflow=workflow_name):
                document = workflow(workflow_name)
                self.assertNotIn("sslcom/actions-codesigner@", document)
                self.assertNotIn("bash -c", document)
                self.assertNotRegex(document, r"\beval\b")
                self.assertEqual(3, document.count(setup_java))
                self.assertEqual(3, document.count("java-version: '11.0.31+11'"))
                self.assertEqual(3, document.count("verify-signature: true"))
                self.assertEqual(3, document.count("scripts/esigner-codesign-hardened.sh"))

        signer = (REPOSITORY_ROOT / "scripts/esigner-codesign-hardened.sh").read_text()
        self.assertIn(
            "https://github.com/SSLcom/CodeSignTool/releases/download/v1.3.0/CodeSignTool-v1.3.0.zip",
            signer,
        )
        self.assertIn(
            "359782cee5c709b172610e2abd8cb49445bfadd26f44073ca18600c585b91b8d",
            signer,
        )
        self.assertIn('java_vendor" == "Eclipse Adoptium"', signer)
        self.assertIn('java_runtime" == "11.0.31+11"', signer)
        self.assertIn("args=(", signer)
        self.assertIn("-malware_block=true", signer)
        self.assertNotIn("bash -c", signer)
        self.assertNotRegex(signer, r"\beval\b")

        timestamp = (REPOSITORY_ROOT / "scripts/Test-InstallerAuthenticode.ps1").read_text()
        for marker in (
            "TimeStamperCertificate",
            "1.3.6.1.5.5.7.3.8",
            "signtool.exe",
            "verify /pa /all /tw",
        ):
            self.assertIn(marker, timestamp)
        for workflow_name, count in (
            ("release.yml", 1),
            ("hotfix.yml", 1),
            ("v1-bridge-stage.yml", 1),
            ("v1-bridge-finalize.yml", 2),
        ):
            self.assertEqual(count, workflow(workflow_name).count("Test-InstallerAuthenticode.ps1"))

    def test_release_and_hotfix_resolve_rollback_from_signed_prior_receipt(self):
        publish = job_block(
            workflow("production-release-signing.yml"), "sign-and-publish-reviewed-release"
        )
        self.assertIn("--pattern field-release-receipt.json", publish)
        self.assertIn("resolve-release-rollback-evidence.py", publish)
        self.assertIn('--signature "$rollback/checksums.sha256.sig"', publish)
        self.assertIn('--bridge-release-tag "$BRIDGE_TAG"', publish)
        self.assertIn('--bridge-source-sha "$BRIDGE_SOURCE_SHA"', publish)
        self.assertIn('--bridge-receipt-sha256 "$BRIDGE_RECEIPT_SHA"', publish)
        self.assertIn('"otaSigningKeyId": "ota-update-v2"', publish)
        self.assertIn('ROLLBACK_ARTIFACT="${evidence[0]}"', publish)
        self.assertIn('[[ "$(sha256sum "$rollback/$ROLLBACK_ARTIFACT"', publish)
        self.assertNotIn(
            'ROLLBACK_ARTIFACT="SuavoAgent-Setup-${ROLLBACK_TAG}-win-x64.exe"',
            publish,
        )
        self.assertNotIn("ota_update_trust_roots.py verify", publish)
        for workflow_name in ("release.yml", "hotfix.yml"):
            with self.subTest(workflow=workflow_name):
                document = workflow(workflow_name)
                caller = job_block(document, "release")
                self.assertIn("uses: ./.github/workflows/production-release-signing.yml", caller)

    def test_ci_collectors_are_deterministic_and_merge_both_platforms(self):
        document = workflow("ci.yml")
        linux = job_block(document, "build-and-test")
        windows = job_block(document, "windows-coverage")
        merger = job_block(document, "coverage-report")

        self.assertIn("DeterministicReport=true", linux)
        self.assertIn("DeterministicReport=true", windows)
        self.assertIn("LogFilePrefix=test-results", linux)
        self.assertIn("LogFilePrefix=test-results", windows)
        self.assertNotIn("LogFileName=test-results.trx", linux)
        self.assertNotIn("LogFileName=test-results.trx", windows)
        self.assertIn("needs: [build-and-test, windows-coverage]", merger)
        self.assertNotIn("--expect-reports", linux)
        self.assertNotIn("--expect-reports", merger)
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
                    action = match.group(1)
                    if action.startswith("./.github/workflows/"):
                        self.assertRegex(action, r"^\./\.github/workflows/[a-z0-9-]+\.yml$")
                    else:
                        self.assertRegex(action, r"@[0-9a-f]{40}$")

    def test_dependabot_covers_nuget_and_workflow_dependencies(self):
        document = (REPOSITORY_ROOT / ".github" / "dependabot.yml").read_text(
            encoding="utf-8"
        )
        self.assertIn("package-ecosystem: nuget", document)
        self.assertIn("package-ecosystem: github-actions", document)
        self.assertEqual(document.count("interval: weekly"), 2)


if __name__ == "__main__":
    unittest.main()
