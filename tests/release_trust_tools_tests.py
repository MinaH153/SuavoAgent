#!/usr/bin/env python3
from __future__ import annotations

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

    def test_spdx_ids_are_injective_for_paths_that_share_a_sanitized_form(self) -> None:
        self.assertNotEqual(
            SBOM_MODULE.spdx_id("File", "a_b.dll"),
            SBOM_MODULE.spdx_id("File", "a-b.dll"),
        )

    def test_each_release_workflow_must_retain_every_gate_independently(self) -> None:
        for workflow_name in ("release.yml", "hotfix.yml"):
            workflow = (ROOT / ".github/workflows" / workflow_name).read_text()
            TRUST_MODULE.validate_workflow_text(workflow_name, workflow)
            weakened = workflow.replace("--minimum-branch 80", "", 1)
            with contextlib.redirect_stderr(io.StringIO()):
                with self.assertRaises(SystemExit):
                    TRUST_MODULE.validate_workflow_text(workflow_name, weakened)


if __name__ == "__main__":
    unittest.main()
