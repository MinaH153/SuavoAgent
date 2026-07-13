#!/usr/bin/env python3
"""Fail closed when source-controlled release trust inputs are incomplete."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys


ROOT = Path(__file__).resolve().parents[1]
REQUIRED_TRACKED = (
    ".github/workflows/release.yml",
    ".github/workflows/hotfix.yml",
    "global.json",
    "legal/external-assets.json",
    "legal/THIRD-PARTY-PROVENANCE.json",
    "legal/evidence/llamasharp-backend-cpu-0.24.0.json",
    "legal/evidence/qwen3-1.7b-q4-k-m.json",
    "legal/evidence/tesseract-native-5.2.0-eng.json",
    "legal/package-license-evidence.json",
    "scripts/Test-SuavoAgentReleaseProbe.ps1",
    "scripts/Test-SuavoAgentReleaseProbe.Legal.ps1",
    "tests/test_resolve_release_rollback_evidence.py",
    "scripts/aggregate_coverage.py",
    "scripts/coverage_model.py",
    "scripts/coverage-noninstrumentable-sources.json",
    "scripts/generate-release-legal-bundle.py",
    "scripts/release_legal_catalog.py",
    "scripts/release_legal_evidence.py",
    "scripts/resolve-release-rollback-evidence.py",
    "scripts/sync-pinned-package-license-evidence.py",
    "scripts/verify-external-release-assets.py",
    "legal/license-texts/Apache-2.0.txt",
    "legal/license-texts/Leptonica-BSD-2-Clause.txt",
    "legal/license-texts/llama.cpp-0.24.0-MIT.txt",
    "legal/license-texts/MPL-2.0.txt",
    "legal/license-texts/JsonCanonicalization-Apache-NOTICE.txt",
    "legal/license-texts/V8-DToA-BSD-3-Clause.txt",
    "legal/license-texts/NumberDToA-NOTICE.txt",
    "legal/vendored/json-canonicalization.json",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/JsonCanonicalizer.cs",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/NumberCachedPowers.cs",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/NumberDToA.Infrastructure.cs",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/NumberDToA.cs",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/NumberDToA.Formatting.cs",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/NumberDiyFp.cs",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/NumberDoubleHelper.cs",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/NumberFastDToA.cs",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/NumberFastDToABuilder.cs",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/NumberToJson.cs",
    "src/SuavoAgent.Helper/Assets/pharmacist-panda-v2.png",
    "src/SuavoAgent.Helper/Assets/README.md",
    "src/SuavoAgent.Setup/Legal/THIRD-PARTY-NOTICES.txt",
)
FORBIDDEN_WORKFLOW_MARKERS = (
    "SIGNING_KEY_PEM",
    "resolve/main",
    "dotnet-version: 8.0.x",
)
REQUIRED_WORKFLOW_MARKERS = (
    "environment: suavoagent-production-signing",
    "actions/attest-build-provenance@977bb373ede98d70efdf65b84cb5f73e068dcc2a",
    "AUTHENTICODE_SIGNER_SHA256",
    "OTA_KMS_KEY_ID",
    "--require-release-eligible",
    "verify-external-release-assets.py",
    "resolve-release-rollback-evidence.py",
    "legal/evidence",
    "--minimum-line 80",
    "--minimum-branch 80",
    "--require-all-projects",
)


def fail(message: str) -> None:
    print(message, file=sys.stderr)
    raise SystemExit(1)


def validate_workflow_text(workflow_name: str, workflow: str) -> None:
    for forbidden in FORBIDDEN_WORKFLOW_MARKERS:
        if forbidden in workflow:
            fail(
                f"forbidden mutable/exportable release input remains in "
                f"{workflow_name}: {forbidden}"
            )
    for required_text in REQUIRED_WORKFLOW_MARKERS:
        if required_text not in workflow:
            fail(f"release trust workflow gate missing in {workflow_name}: {required_text}")


def git(*arguments: str) -> str:
    return subprocess.check_output(
        ("git", *arguments), cwd=ROOT, text=True, stderr=subprocess.STDOUT
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--allow-dirty", action="store_true")
    args = parser.parse_args()
    if not args.allow_dirty and git("status", "--porcelain", "--untracked-files=all").strip():
        fail("release source checkout is not clean")

    tracked = set(git("ls-files").splitlines())
    required = set(REQUIRED_TRACKED)
    package_licenses = json.loads(
        (ROOT / "legal/package-license-evidence.json").read_text()
    )
    required.update(
        entry["retainedFile"] for entry in package_licenses["evidence"]
    )
    external_assets = json.loads((ROOT / "legal/external-assets.json").read_text())
    required.update(
        license_entry["path"]
        for asset in external_assets["assets"]
        for license_entry in asset.get("licenseFiles", [])
    )
    required.update(
        evidence["path"]
        for asset in external_assets["assets"]
        if isinstance((evidence := asset.get("provenanceEvidence")), dict)
    )
    solution = (ROOT / "SuavoAgent.sln").read_text()
    project_paths = {
        match.replace("\\", "/")
        for match in re.findall(r'"([^"\r\n]+\.csproj)"', solution)
    }
    required.update(
        str(Path(project).parent / "packages.lock.json") for project in project_paths
    )
    missing = sorted(
        name for name in required
        if name not in tracked and (not args.allow_dirty or not (ROOT / name).is_file())
    )
    if missing:
        fail("release inputs absent from clean clone: " + ", ".join(missing))

    global_json = json.loads((ROOT / "global.json").read_text())
    if global_json.get("sdk") != {
        "version": "8.0.128",
        "rollForward": "disable",
        "allowPrerelease": False,
    }:
        fail("global.json must pin the reviewed SDK without roll-forward")

    panda = ROOT / "src/SuavoAgent.Helper/Assets/pharmacist-panda-v2.png"
    panda_hash = hashlib.sha256(panda.read_bytes()).hexdigest()
    external = json.loads((ROOT / "legal/external-assets.json").read_text())
    panda_entry = next(asset for asset in external["assets"] if asset["id"] == "pharmacist-panda")
    if panda_hash != panda_entry["sha256"]:
        fail("pharmacist panda does not match its reviewed provenance digest")

    for workflow_name in (
        ".github/workflows/release.yml",
        ".github/workflows/hotfix.yml",
    ):
        workflow = (ROOT / workflow_name).read_text()
        validate_workflow_text(workflow_name, workflow)

    provenance = json.loads((ROOT / "legal/THIRD-PARTY-PROVENANCE.json").read_text())
    forbidden_canonicalizer_packages = {
        package["name"] for package in provenance["packages"]
        if package["name"].casefold() in {"jsoncanonicalizer", "es6numberserializer"}
    }
    if forbidden_canonicalizer_packages:
        fail(
            "replaced unknown-license canonicalizer packages remain in release closure: "
            + ", ".join(sorted(forbidden_canonicalizer_packages))
        )
    forbidden_native = {
        package["name"] for package in provenance["packages"]
        if any(token in package["name"].casefold()
               for token in ("nativeassets.linux", "nativeassets.macos", "nativeassets.webassembly"))
    }
    if forbidden_native:
        fail("non-Windows packages leaked into win-x64 closure: " + ", ".join(sorted(forbidden_native)))
    vendored = provenance.get("vendoredSources")
    if not isinstance(vendored, list) or len(vendored) != 1:
        fail("exact vendored JSON canonicalization provenance is missing")
    canonicalizer = vendored[0]
    manifest = ROOT / "legal/vendored/json-canonicalization.json"
    if (
        canonicalizer.get("id") != "cyberphone-json-canonicalization-dotnet"
        or canonicalizer.get("manifestSha256")
        != hashlib.sha256(manifest.read_bytes()).hexdigest()
    ):
        fail("vendored JSON canonicalization manifest digest is stale")
    package_license_catalog = ROOT / "legal/package-license-evidence.json"
    if provenance.get("packageLicenseEvidenceCatalogSha256") != hashlib.sha256(
        package_license_catalog.read_bytes()
    ).hexdigest():
        fail("package license evidence catalog digest is stale")

    notices = (ROOT / "src/SuavoAgent.Setup/Legal/THIRD-PARTY-NOTICES.txt").read_text()
    for required_text in (
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
        if required_text not in notices:
            fail(f"required retained license or copyright text is missing: {required_text}")

    subprocess.run(
        [
            sys.executable,
            "scripts/generate-release-legal-bundle.py",
            "--check",
            "--require-release-eligible",
        ],
        cwd=ROOT,
        check=True,
    )
    subprocess.run(
        [sys.executable, "scripts/sync-pinned-package-license-evidence.py"],
        cwd=ROOT,
        check=True,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
