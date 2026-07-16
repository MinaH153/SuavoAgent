#!/usr/bin/env python3
from __future__ import annotations

import argparse
import contextlib
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts"))

from release_legal_evidence import release_eligibility_blockers
import v1_bridge_run_metadata as RUN_METADATA

SBOM_SPEC = importlib.util.spec_from_file_location(
    "generate_release_sbom", ROOT / "scripts/generate-release-sbom.py"
)
assert SBOM_SPEC is not None and SBOM_SPEC.loader is not None
SBOM_MODULE = importlib.util.module_from_spec(SBOM_SPEC)
SBOM_SPEC.loader.exec_module(SBOM_MODULE)
TRUST_SPEC = importlib.util.spec_from_file_location(
    "verify_release_trust_inputs", ROOT / "scripts/verify-release-trust-inputs.py"
)
assert TRUST_SPEC is not None and TRUST_SPEC.loader is not None
TRUST_MODULE = importlib.util.module_from_spec(TRUST_SPEC)
TRUST_SPEC.loader.exec_module(TRUST_MODULE)


class ReleaseTrustToolsTests(unittest.TestCase):
    def test_publication_state_rechecks_numeric_release_assets_and_immutability(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            release_dir = root / "release"
            version = "v4.0.0"
            source_sha = "a" * 40
            relative_paths = (
                "SuavoAgent.Core.exe", "SuavoAgent.Broker.exe",
                "SuavoAgent.Helper.exe", "SuavoAgent.Watchdog.exe",
                "SuavoSetup.exe", f"SuavoAgent-{version}-win-x64.msi",
                "SuavoAgent-Setup.exe", "suavoagent.spdx.json",
                "field-release-receipt.json", f"update-manifest-{version}.txt",
                f"update-manifest-{version}.sig", "checksums.sha256",
                "checksums.sha256.sig", "legal/THIRD-PARTY-NOTICES.txt",
                "legal/THIRD-PARTY-PROVENANCE.json", "legal/external-assets.json",
                "legal/license-texts/Apache-2.0.txt", "legal/evidence/runtime.json",
            )
            for relative in relative_paths:
                path = release_dir / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes((relative + "\n").encode("ascii"))

            def asset_document(path: Path) -> None:
                assets = []
                for item in sorted(release_dir.rglob("*")):
                    if item.is_file():
                        raw = item.read_bytes()
                        assets.append({
                            "name": item.name,
                            "state": "uploaded",
                            "size": len(raw),
                            "digest": "sha256:" + hashlib.sha256(raw).hexdigest(),
                        })
                path.write_text(json.dumps([assets]), encoding="utf-8")

            pre_assets = root / "pre-assets.json"
            post_assets = root / "post-assets.json"
            asset_document(pre_assets)
            asset_document(post_assets)
            draft_release = root / "draft.json"
            published_release = root / "published.json"
            base = {
                "id": 777,
                "tag_name": version,
                "target_commitish": source_sha,
                "prerelease": False,
            }
            draft_release.write_text(json.dumps(base | {
                "draft": True, "immutable": False, "published_at": None,
            }), encoding="utf-8")
            published_release.write_text(json.dumps(base | {
                "draft": False, "immutable": True,
                "published_at": "2026-07-15T12:00:00Z",
            }), encoding="utf-8")

            def arguments(
                release: Path, assets: Path, draft: str, immutable: str,
                reference: Path | None = None,
            ) -> argparse.Namespace:
                return argparse.Namespace(
                    release=release, assets=assets, reference_assets=reference,
                    release_dir=release_dir, version=version, source_sha=source_sha,
                    expected_release_id=777, expected_draft=draft,
                    expected_immutable=immutable,
                )

            RUN_METADATA.validate_publication_state(
                arguments(draft_release, pre_assets, "true", "false")
            )
            RUN_METADATA.validate_publication_state(
                arguments(
                    published_release, post_assets, "false", "true", pre_assets
                )
            )

            published = json.loads(published_release.read_text())
            published["immutable"] = False
            published_release.write_text(json.dumps(published), encoding="utf-8")
            with self.assertRaises(Exception):
                RUN_METADATA.validate_publication_state(
                    arguments(
                        published_release, post_assets, "false", "true", pre_assets
                    )
                )

            published["immutable"] = True
            published_release.write_text(json.dumps(published), encoding="utf-8")
            target = release_dir / "SuavoAgent.Core.exe"
            target.write_bytes(b"changed after prepublication validation\n")
            asset_document(post_assets)
            with self.assertRaises(Exception):
                RUN_METADATA.validate_publication_state(
                    arguments(
                        published_release, post_assets, "false", "true", pre_assets
                    )
                )

    def test_trust_phase_keeps_bridge_source_pre_convergence_and_normal_source_complete(self) -> None:
        self.assertEqual(
            frozenset(), TRUST_MODULE.trust_phase_required("bridge-v1", set())
        )
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            TRUST_MODULE.trust_phase_required("normal-v2", set())
        self.assertEqual(
            TRUST_MODULE.CONVERGENCE_TRACKED,
            TRUST_MODULE.trust_phase_required(
                "normal-v2", set(TRUST_MODULE.CONVERGENCE_TRACKED)
            ),
        )
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            TRUST_MODULE.trust_phase_required(
                "bridge-v1", set(TRUST_MODULE.CONVERGENCE_TRACKED)
            )
        self.assertEqual(
            TRUST_MODULE.CONVERGENCE_INPUT_TRACKED,
            TRUST_MODULE.trust_phase_required(
                "convergence-v1", set(TRUST_MODULE.CONVERGENCE_INPUT_TRACKED)
            ),
        )
        for invalid in (
            set(),
            set(TRUST_MODULE.CONVERGENCE_INPUT_TRACKED)
            | set(TRUST_MODULE.CONVERGENCE_CLAIM_TRACKED),
        ):
            with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
                TRUST_MODULE.trust_phase_required("convergence-v1", invalid)

    def test_ci_auto_phase_accepts_only_three_exact_registry_artifact_states(self) -> None:
        inputs = set(TRUST_MODULE.CONVERGENCE_INPUT_TRACKED)
        all_artifacts = set(TRUST_MODULE.CONVERGENCE_TRACKED)
        self.assertEqual(
            "bridge-v1", TRUST_MODULE.resolve_trust_phase("auto", set(), "ota-update-v1")
        )
        self.assertEqual(
            "convergence-v1",
            TRUST_MODULE.resolve_trust_phase("auto", inputs, "ota-update-v1"),
        )
        self.assertEqual(
            "normal-v2",
            TRUST_MODULE.resolve_trust_phase("auto", all_artifacts, "ota-update-v2"),
        )
        for tracked, key_id in (
            (inputs, "ota-update-v2"),
            (all_artifacts, "ota-update-v1"),
            (set(), "ota-update-v2"),
        ):
            with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
                TRUST_MODULE.resolve_trust_phase("auto", tracked, key_id)
        ci = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
        stage = (ROOT / ".github/workflows/v1-bridge-stage.yml").read_text(encoding="utf-8")
        release = (ROOT / ".github/workflows/release.yml").read_text(encoding="utf-8")
        self.assertIn("--trust-phase auto", ci)
        self.assertIn("--trust-phase bridge-v1", stage)
        self.assertIn("--trust-phase normal-v2", release)

    def test_notice_bundle_retains_required_full_texts_and_exact_windows_closure(self) -> None:
        subprocess.run(
            [sys.executable, "scripts/generate-release-legal-bundle.py", "--check"],
            cwd=ROOT,
            check=True,
        )
        notices = (ROOT / "src/SuavoAgent.Setup/Legal/THIRD-PARTY-NOTICES.txt").read_text()
        for marker in (
            "Apache License",
            "MIT License",
            "Copyright (C) 2001-2020 Leptonica",
            "Copyright 2018 The ANGLE Project Authors",
            "# HarfBuzz",
            "Mozilla Public License Version 2.0",
            "Copyright 2010 the V8 project authors",
            "The author of this software is David M. Gay.",
            "Copyright (c) 2023 SciSharp STACK",
            "Copyright (c) 2023-2024 The ggml authors",
            "Copyright 2012-2020 Charles Weld",
            "MICROSOFT .NET RUNTIME 8.0.28",
        ):
            self.assertIn(marker, notices)
        provenance = json.loads((ROOT / "legal/THIRD-PARTY-PROVENANCE.json").read_text())
        names = {package["name"].casefold() for package in provenance["packages"]}
        self.assertFalse(any(
            token in name
            for name in names
            for token in ("nativeassets.linux", "nativeassets.macos", "nativeassets.webassembly")
        ))
        self.assertNotIn("avalonia.fonts.inter", names)
        self.assertNotIn("avalonia.diagnostics", names)
        self.assertIn("llamasharp", names)
        self.assertNotIn("jsoncanonicalizer", names)
        self.assertNotIn("es6numberserializer", names)
        self.assertEqual([], [
            f"{package['name']} {package['version']}"
            for package in provenance["packages"]
            if not any(
                legal.get("legalKind") in {"license", "copying"}
                for legal in package["retainedLegalFiles"]
            )
        ])
        self.assertEqual(
            {
                "Microsoft.NETCore.App.Host.win-x64",
                "Microsoft.NETCore.App.Runtime.win-x64",
                "Microsoft.WindowsDesktop.App.Runtime.win-x64",
            },
            {package["name"] for package in provenance["runtimePacks"]},
        )
        for package in provenance["runtimePacks"]:
            self.assertEqual("8.0.28", package["version"])
            self.assertTrue(any(
                legal["legalKind"] in {"license", "copying"}
                for legal in package["retainedLegalFiles"]
            ))
        for package in provenance["packages"]:
            self.assertRegex(package["packageArtifactSha256"], r"^[0-9a-f]{64}$")
            self.assertTrue(package["packageArtifactSha512"])
            self.assertTrue(package["packageContentHash"])
            self.assertTrue(package["packageDownloadUrl"].startswith("https://api.nuget.org/"))
            self.assertRegex(package["nuspecSha256"], r"^[0-9a-f]{64}$")
        self.assertEqual(
            hashlib.sha256(
                (ROOT / "legal/package-license-evidence.json").read_bytes()
            ).hexdigest(),
            provenance["packageLicenseEvidenceCatalogSha256"],
        )
        tesseract = next(
            package for package in provenance["packages"] if package["name"] == "Tesseract"
        )
        self.assertEqual("5.2.0", tesseract["version"])
        self.assertEqual("Copyright 2012-2020 Charles Weld", tesseract["copyright"])
        self.assertEqual(
            "cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30",
            tesseract["retainedLegalFiles"][0]["sha256"],
        )

        vendored = provenance["vendoredSources"]
        self.assertEqual(1, len(vendored))
        canonicalizer = vendored[0]
        self.assertEqual("cyberphone-json-canonicalization-dotnet", canonicalizer["id"])
        self.assertEqual(
            "19d51d7fe467d4706a3ff08adf8a748f29fc21e0",
            canonicalizer["upstream"]["revision"],
        )
        manifest = ROOT / canonicalizer["manifestPath"]
        self.assertEqual(
            hashlib.sha256(manifest.read_bytes()).hexdigest(),
            canonicalizer["manifestSha256"],
        )
        for source in canonicalizer["sourceFiles"]:
            for local in source["localFiles"]:
                self.assertEqual(
                    local["sha256"],
                    hashlib.sha256((ROOT / local["path"]).read_bytes()).hexdigest(),
                )

    def test_base_release_eligibility_accepts_official_model_and_native_runtime(self) -> None:
        result = subprocess.run(
            [
                sys.executable,
                "scripts/generate-release-legal-bundle.py",
                "--check",
                "--require-release-eligible",
            ],
            cwd=ROOT,
            text=True,
            capture_output=True,
        )
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertNotIn("unknown dependency licenses", result.stderr)
        self.assertNotIn("external asset provenance blocked", result.stderr)
        self.assertNotIn("missing exact dependency legal evidence", result.stderr)

    def test_release_gate_fails_closed_when_a_required_package_loses_legal_evidence(self) -> None:
        provenance = json.loads((ROOT / "legal/THIRD-PARTY-PROVENANCE.json").read_text())
        target = next(package for package in provenance["packages"] if package["name"] == "LLamaSharp")
        target["retainedLegalFiles"] = []
        blockers = release_eligibility_blockers(provenance, set())
        self.assertEqual(["LLamaSharp 0.24.0"], blockers["packages"])

    def test_pinned_package_license_catalog_is_complete_and_immutable(self) -> None:
        subprocess.run(
            [sys.executable, "scripts/sync-pinned-package-license-evidence.py"],
            cwd=ROOT,
            check=True,
        )
        catalog = json.loads((ROOT / "legal/package-license-evidence.json").read_text())
        identities = [
            (package["name"].casefold(), package["version"].casefold())
            for evidence in catalog["evidence"]
            for package in evidence["packages"]
        ]
        self.assertEqual(53, len(identities))
        self.assertEqual(len(identities), len(set(identities)))
        for evidence in catalog["evidence"]:
            self.assertIn(evidence["sourceRevision"], evidence["upstreamUrl"])
            retained = ROOT / evidence["retainedFile"]
            self.assertEqual(
                evidence["sha256"], hashlib.sha256(retained.read_bytes()).hexdigest()
            )

    def test_external_brain_licenses_are_exact_and_generic_mit_is_retired(self) -> None:
        self.assertFalse((ROOT / "legal/license-texts/MIT.txt").exists())
        assets = json.loads((ROOT / "legal/external-assets.json").read_text())["assets"]
        backend = next(
            asset for asset in assets if asset["id"] == "llamasharp-backend-cpu-0.24.0"
        )
        self.assertEqual(
            [
                "52e74038a69e948314106225360faee6159dae61f96a3a76fd0f3c2c3066c4f4",
                "e562a2ddfaf8280537795ac5ecd34e3012b6582a147ef69ba6a6a5c08c84757d",
            ],
            [entry["sha256"] for entry in backend["licenseFiles"]],
        )
        self.assertEqual(
            [
                "ce8eeb4c3d6937defc1dc38aaac4ad8bd282e8a5",
                "ceda28ef8e310a8dee60bf275077a3eedae8e36c",
            ],
            [entry["sourceRevision"] for entry in backend["licenseFiles"]],
        )

    def test_reviewed_tesseract_cohort_passes_independent_feature_gate(self) -> None:
        result = subprocess.run(
            [
                sys.executable,
                "scripts/generate-release-legal-bundle.py",
                "--check",
                "--require-release-eligible",
                "--require-feature-eligible",
                "tesseract-native-5.2.0-eng",
            ],
            cwd=ROOT,
            text=True,
            capture_output=True,
        )
        self.assertEqual(0, result.returncode, result.stderr)

    def test_spdx_contains_every_exact_release_file_and_no_phantom_file(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            release = Path(temporary) / "release"
            release.mkdir()
            expected = {"SuavoSetup.exe", "SuavoAgent.Core.exe", "legal/NOTICE.txt"}
            for relative in expected:
                path = release / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(relative.encode("ascii"))
            output = release / "suavoagent.spdx.json"
            subprocess.run(
                [
                    sys.executable,
                    "scripts/generate-release-sbom.py",
                    "--release-dir", str(release),
                    "--version", "v0.0.0-test",
                    "--source-commit", "a" * 40,
                    "--output", str(output),
                ],
                cwd=ROOT,
                check=True,
            )
            first = output.read_bytes()
            subprocess.run(
                [
                    sys.executable,
                    "scripts/generate-release-sbom.py",
                    "--release-dir", str(release),
                    "--version", "v0.0.0-test",
                    "--source-commit", "a" * 40,
                    "--output", str(output),
                ],
                cwd=ROOT,
                check=True,
            )
            self.assertEqual(first, output.read_bytes())
            sbom = json.loads(output.read_text())
            actual = {entry["fileName"].removeprefix("./") for entry in sbom["files"]}
            self.assertEqual(expected, actual)
            self.assertEqual("SPDX-2.3", sbom["spdxVersion"])
            for entry in sbom["files"]:
                self.assertEqual(
                    ["SHA1", "SHA256"],
                    [checksum["algorithm"] for checksum in entry["checksums"]],
                )
            root_package = next(
                package for package in sbom["packages"]
                if package["SPDXID"] == "SPDXRef-Package-SuavoAgent"
            )
            sha1s = sorted(
                next(
                    checksum["checksumValue"] for checksum in entry["checksums"]
                    if checksum["algorithm"] == "SHA1"
                )
                for entry in sbom["files"]
            )
            self.assertEqual(
                hashlib.sha1("".join(sha1s).encode("ascii")).hexdigest(),
                root_package["packageVerificationCode"]["packageVerificationCodeValue"],
            )
            self.assertEqual(
                ["./suavoagent.spdx.json"],
                root_package["packageVerificationCode"]["packageVerificationCodeExcludedFiles"],
            )
            canonicalizer = next(
                package for package in sbom["packages"]
                if package["name"] == "cyberphone-json-canonicalization-dotnet"
            )
            self.assertEqual(
                "Apache-2.0 AND BSD-3-Clause AND LicenseRef-Lucent-DToA AND MPL-2.0",
                canonicalizer["licenseDeclared"],
            )
            self.assertEqual(
                ["LicenseRef-Lucent-DToA"],
                [entry["licenseId"] for entry in sbom["hasExtractedLicensingInfos"]],
            )
            package_by_name = {package["name"]: package for package in sbom["packages"]}
            for runtime_name in (
                "Microsoft.NETCore.App.Host.win-x64",
                "Microsoft.NETCore.App.Runtime.win-x64",
                "Microsoft.WindowsDesktop.App.Runtime.win-x64",
            ):
                runtime = package_by_name[runtime_name]
                self.assertEqual("8.0.28", runtime["versionInfo"])
                self.assertIn(
                    {
                        "spdxElementId": "SPDXRef-Package-SuavoAgent",
                        "relationshipType": "DEPENDS_ON",
                        "relatedSpdxElement": runtime["SPDXID"],
                    },
                    sbom["relationships"],
                )
            self.assertEqual(
                "Copyright 2012-2020 Charles Weld",
                package_by_name["Tesseract"]["copyrightText"],
            )
            self.assertIn(
                "Managed Tesseract .NET wrapper only",
                package_by_name["Tesseract"]["comment"],
            )
            self.assertEqual(
                next(
                    package["packageArtifactSha256"]
                    for package in json.loads(
                        (ROOT / "legal/THIRD-PARTY-PROVENANCE.json").read_text()
                    )["packages"]
                    if package["name"] == "Tesseract"
                ),
                package_by_name["Tesseract"]["checksums"][0]["checksumValue"],
            )
            for name, expected_hash in (
                (
                    "qwen3-1.7b-q4-k-m",
                    "228fb5627f7510b8b3516cdb6435e4b0d2a2bf330fe5b0ab19284a3570a8bb1f",
                ),
                (
                    "llamasharp-backend-cpu-0.24.0",
                    "47120fed200482ab364b9d225271172ccbf2ac7713ad388e4e7fe7d89fdedb0a",
                ),
                (
                    "tesseract-native-5.2.0-eng",
                    "202d82fc7c7d8384df7da57206d5e1f456ccdabd648c46e67cdfaa3a911d4795",
                ),
                (
                    "tessdata-fast-eng",
                    "7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2",
                ),
            ):
                package = package_by_name[name]
                self.assertEqual(expected_hash, package["checksums"][0]["checksumValue"])
                self.assertIn(
                    {
                        "spdxElementId": "SPDXRef-Package-SuavoAgent",
                        "relationshipType": "DEPENDS_ON",
                        "relatedSpdxElement": package["SPDXID"],
                    },
                    sbom["relationships"],
                )
            self.assertIn(
                "Copyright (c) 2023-2024 The ggml authors",
                package_by_name["llamasharp-backend-cpu-0.24.0"]["copyrightText"],
            )
            self.assertEqual([], sbom["annotations"])

    def test_final_sbom_equals_publication_inputs_except_exact_cyclic_outputs(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            release = Path(temporary) / "release"
            release.mkdir()
            version = "v4.0.0"
            publication_inputs = {
                "SuavoAgent.Core.exe",
                f"SuavoAgent-{version}-win-x64.msi",
                "SuavoAgent-Setup.exe",
                f"update-manifest-{version}.txt",
                "field-release-receipt.json",
                "legal/THIRD-PARTY-NOTICES.txt",
            }
            for relative in publication_inputs:
                path = release / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes((relative + "\n").encode("ascii"))
            output = release / "suavoagent.spdx.json"
            output.write_text("provisional\n", encoding="ascii")
            subprocess.run(
                [
                    sys.executable,
                    "scripts/generate-release-sbom.py",
                    "--release-dir", str(release),
                    "--version", version,
                    "--source-commit", "a" * 40,
                    "--output", str(output),
                    "--exclude-finalization-outputs",
                ],
                cwd=ROOT,
                check=True,
            )
            document = json.loads(output.read_text(encoding="utf-8"))
            documented = {
                entry["fileName"].removeprefix("./")
                for entry in document["files"]
            }
            self.assertEqual(publication_inputs, documented)
            exclusions = frozenset(
                {"suavoagent.spdx.json"}
                | set(SBOM_MODULE.finalization_output_names(version))
            )
            root_package = next(
                package for package in document["packages"]
                if package["SPDXID"] == "SPDXRef-Package-SuavoAgent"
            )
            self.assertEqual(
                ["./" + name for name in sorted(exclusions)],
                root_package["packageVerificationCode"][
                    "packageVerificationCodeExcludedFiles"
                ],
            )

            for relative in SBOM_MODULE.finalization_output_names(version):
                (release / relative).write_bytes((relative + "\n").encode("ascii"))
            SBOM_MODULE.verify_exact_file_inventory(document, release, exclusions)

            private = release / "appsettings.json"
            private.write_text("must-not-publish\n", encoding="ascii")
            with self.assertRaisesRegex(SystemExit, "does not equal"):
                SBOM_MODULE.verify_exact_file_inventory(document, release, exclusions)
            private.unlink()

            installer = release / f"SuavoAgent-{version}-win-x64.msi"
            installer.write_bytes(b"replaced after SBOM generation\n")
            with self.assertRaisesRegex(SystemExit, "hash does not match"):
                SBOM_MODULE.verify_exact_file_inventory(document, release, exclusions)

    def test_spdx_ids_are_injective_for_paths_that_share_a_sanitized_form(self) -> None:
        self.assertNotEqual(
            SBOM_MODULE.spdx_id("File", "a_b.dll"),
            SBOM_MODULE.spdx_id("File", "a-b.dll"),
        )

    def test_each_release_workflow_must_retain_every_gate_independently(self) -> None:
        reusable = (ROOT / ".github/workflows/production-release-signing.yml").read_text()
        for workflow_name in ("release.yml", "hotfix.yml"):
            workflow = (ROOT / ".github/workflows" / workflow_name).read_text()
            TRUST_MODULE.validate_workflow_text(workflow_name, workflow, reusable)
            weakened = workflow.replace("--minimum-branch 80", "", 1)
            with contextlib.redirect_stderr(io.StringIO()):
                with self.assertRaises(SystemExit):
                    TRUST_MODULE.validate_workflow_text(workflow_name, weakened, reusable)

    def test_publication_proves_immutable_support_before_draft_and_publish(self) -> None:
        endpoint = '"repos/$GITHUB_REPOSITORY/immutable-releases"'
        for workflow_name in (
            "production-release-signing.yml",
            "v1-bridge-finalize.yml",
        ):
            workflow = (ROOT / ".github/workflows" / workflow_name).read_text()
            TRUST_MODULE.validate_immutable_publication_order(workflow_name, workflow)
            first = workflow.index(endpoint)
            weakened = workflow[:first] + workflow[first + len(endpoint):]
            with self.subTest(workflow=workflow_name), contextlib.redirect_stderr(
                io.StringIO()
            ):
                with self.assertRaises(SystemExit):
                    TRUST_MODULE.validate_immutable_publication_order(
                        workflow_name, weakened
                    )

    def test_hardened_signing_and_rfc3161_gates_reject_weakened_inputs(self) -> None:
        release = (ROOT / ".github/workflows/release.yml").read_text()
        TRUST_MODULE.validate_hardened_signing_workflow("release.yml", release, 3)
        weakened = release.replace("verify-signature: true", "verify-signature: false", 1)
        with contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit):
                TRUST_MODULE.validate_hardened_signing_workflow("release.yml", weakened, 3)

        signer = (ROOT / "scripts/esigner-codesign-hardened.sh").read_text()
        installer = (ROOT / "scripts/Test-InstallerAuthenticode.ps1").read_text()
        TRUST_MODULE.validate_hardened_signing_scripts(signer, installer)
        without_timestamp_requirement = installer.replace("verify /pa /all /tw", "verify /pa /all", 1)
        with contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit):
                TRUST_MODULE.validate_hardened_signing_scripts(
                    signer, without_timestamp_requirement
                )

    def test_bridge_signer_binds_each_privileged_phase_to_exact_authorizer(self) -> None:
        workflow = (ROOT / ".github/workflows/production-signing.yml").read_text()
        TRUST_MODULE.validate_bridge_signing_workflow(workflow)

        weakened = workflow.replace(
            TRUST_MODULE.EXPECTED_BRIDGE_AUTHORIZER_ASSERTION,
            '[[ "$GITHUB_WORKFLOW_REF" == *"v1-bridge-authorize.yml"* ]]',
            1,
        )
        with contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit):
                TRUST_MODULE.validate_bridge_signing_workflow(weakened)

        weakened = workflow.replace(
            TRUST_MODULE.EXPECTED_BRIDGE_AUTHORIZER_ASSERTION + "\n          mkdir descriptor",
            "mkdir descriptor\n          " + TRUST_MODULE.EXPECTED_BRIDGE_AUTHORIZER_ASSERTION,
            1,
        )
        with contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit):
                TRUST_MODULE.validate_bridge_signing_workflow(weakened)

    def test_production_signing_policy_is_exact_and_fails_closed_on_scope_expansion(self) -> None:
        template = json.loads(
            (
                ROOT
                / "infrastructure/aws/suavoagent-production-signing-v2.template.json"
            ).read_text()
        )
        TRUST_MODULE.validate_production_signing_template(template)

        weakened = json.loads(json.dumps(template))
        statements = weakened["Resources"]["GitHubProductionSigningRoleInlinePolicy"][
            "Properties"
        ]["PolicyDocument"]["Statement"]
        statements[0]["Action"] = ["kms:GetPublicKey", "kms:DescribeKey"]
        with contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit):
                TRUST_MODULE.validate_production_signing_template(weakened)

        weakened = json.loads(json.dumps(template))
        weakened["Resources"]["OtaSigningKey"] = {"Type": "AWS::KMS::Key"}
        with contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit):
                TRUST_MODULE.validate_production_signing_template(weakened)

        for retention_attribute in ("DeletionPolicy", "UpdateReplacePolicy"):
            weakened = json.loads(json.dumps(template))
            weakened["Resources"]["OtaSigningKey"].pop(retention_attribute)
            with self.subTest(retention=retention_attribute), contextlib.redirect_stderr(
                io.StringIO()
            ):
                with self.assertRaises(SystemExit):
                    TRUST_MODULE.validate_production_signing_template(weakened)

        weakened = json.loads(json.dumps(template))
        weakened["Resources"]["OtaSigningKeyAlias"]["Properties"]["TargetKeyId"] = {
            "Ref": "ReplacementKey"
        }
        with contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit):
                TRUST_MODULE.validate_production_signing_template(weakened)

        weakened = json.loads(json.dumps(template))
        weakened["Parameters"] = {"OtaSigningKeyArn": {"Type": "String"}}
        with contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit):
                TRUST_MODULE.validate_production_signing_template(weakened)

        self.assertEqual(
            {"Fn::GetAtt": ["OtaSigningKey", "Arn"]},
            template["Outputs"]["OtaKmsKeyId"]["Value"],
        )
        self.assertIn(
            TRUST_MODULE.EXPECTED_OTA_KMS_KEY_ARN,
            (ROOT / ".github/workflows/production-signing.yml").read_text(),
        )

        weakened = json.loads(json.dumps(template))
        weakened["Outputs"]["AWS_SIGNING_ROLE_ARN"] = weakened["Outputs"].pop(
            "AwsSigningRoleArn"
        )
        with contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit):
                TRUST_MODULE.validate_production_signing_template(weakened)

        weakened = json.loads(json.dumps(template))
        condition = weakened["Resources"]["GitHubProductionSigningRole"][
            "Properties"
        ]["AssumeRolePolicyDocument"]["Statement"][0]["Condition"]["StringEquals"]
        condition["token.actions.githubusercontent.com:sub"] = "repo:MinaH153/SuavoAgent:*"
        with contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit):
                TRUST_MODULE.validate_production_signing_template(weakened)


if __name__ == "__main__":
    unittest.main()
