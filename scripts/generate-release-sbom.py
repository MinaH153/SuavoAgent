#!/usr/bin/env python3
"""Create a deterministic SPDX 2.3 SBOM for the exact release directory."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re


ROOT = Path(__file__).resolve().parents[1]
RELEASE_EXTERNAL_ASSETS = {
    "qwen3-1.7b-q4-k-m",
    "llamasharp-backend-cpu-0.24.0",
    "tesseract-native-5.2.0-eng",
}
SELF_CONTAINED_RUNTIME_PACKS = {
    "Microsoft.NETCore.App.Host.win-x64",
    "Microsoft.NETCore.App.Runtime.win-x64",
    "Microsoft.WindowsDesktop.App.Runtime.win-x64",
}


def digest(path: Path, algorithm: str = "sha256") -> str:
    value = hashlib.new(algorithm)
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            value.update(chunk)
    return value.hexdigest()


def spdx_id(prefix: str, value: str) -> str:
    if not re.fullmatch(r"[A-Za-z0-9.-]+", prefix) or not value:
        raise ValueError("SPDX identifier input is invalid")
    return "SPDXRef-" + prefix + "-" + value.encode("utf-8").hex()


def finalization_output_names(version: str) -> tuple[str, ...]:
    """Outputs that cannot be hashed by the SBOM they authenticate."""
    if not re.fullmatch(r"v[0-9]+\.[0-9]+\.[0-9]+", version):
        raise SystemExit("final publication SBOM requires a canonical release version")
    return (
        "checksums.sha256",
        "checksums.sha256.sig",
        f"update-manifest-{version}.sig",
    )


def release_files(release: Path, excluded: frozenset[str]) -> tuple[Path, ...]:
    files: list[Path] = []
    for path in sorted(release.rglob("*")):
        if path.is_symlink():
            raise SystemExit(f"release directory contains a symlink: {path}")
        if not path.is_file():
            continue
        relative = path.relative_to(release).as_posix()
        if relative not in excluded:
            files.append(path)
    return tuple(files)


def verify_exact_file_inventory(
    document: dict[str, object],
    release: Path,
    excluded: frozenset[str],
) -> None:
    """Fail closed unless SPDX files and hashes equal the release minus exclusions."""
    expected = {
        path.relative_to(release).as_posix(): path
        for path in release_files(release, excluded)
    }
    entries = document.get("files")
    if not isinstance(entries, list):
        raise SystemExit("SBOM files inventory is malformed")
    observed: dict[str, dict[str, object]] = {}
    for entry in entries:
        if not isinstance(entry, dict):
            raise SystemExit("SBOM file entry is malformed")
        file_name = entry.get("fileName")
        if not isinstance(file_name, str) or not file_name.startswith("./"):
            raise SystemExit("SBOM file name is malformed")
        relative = file_name[2:]
        if relative in observed:
            raise SystemExit("SBOM file inventory contains a duplicate path")
        observed[relative] = entry
    if set(observed) != set(expected):
        raise SystemExit("SBOM file inventory does not equal the release cohort")

    for relative, path in expected.items():
        checksums = observed[relative].get("checksums")
        if not isinstance(checksums, list):
            raise SystemExit("SBOM file checksums are malformed")
        by_algorithm: dict[str, str] = {}
        for checksum in checksums:
            if not isinstance(checksum, dict):
                raise SystemExit("SBOM file checksum is malformed")
            algorithm = checksum.get("algorithm")
            value = checksum.get("checksumValue")
            if (
                algorithm not in ("SHA1", "SHA256")
                or not isinstance(value, str)
                or algorithm in by_algorithm
            ):
                raise SystemExit("SBOM file checksum set is not exact")
            by_algorithm[algorithm] = value
        if by_algorithm != {"SHA1": digest(path, "sha1"), "SHA256": digest(path)}:
            raise SystemExit("SBOM file hash does not match the release cohort")

    roots = [
        package
        for package in document.get("packages", [])
        if isinstance(package, dict)
        and package.get("SPDXID") == "SPDXRef-Package-SuavoAgent"
    ]
    if len(roots) != 1:
        raise SystemExit("SBOM root package is missing or duplicated")
    verification = roots[0].get("packageVerificationCode")
    declared = (
        verification.get("packageVerificationCodeExcludedFiles")
        if isinstance(verification, dict)
        else None
    )
    expected_exclusions = ["./" + name for name in sorted(excluded)]
    if declared != expected_exclusions:
        raise SystemExit("SBOM publication exclusions are not exact")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--release-dir", type=Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--source-commit", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--exclude-finalization-outputs", action="store_true")
    args = parser.parse_args()

    release = args.release_dir.resolve()
    if not release.is_dir() or not re.fullmatch(r"[0-9A-Fa-f]{40}", args.source_commit):
        raise SystemExit("release directory or source commit is invalid")
    if args.output.is_symlink():
        raise SystemExit("SBOM output must not be a symlink")
    output = args.output.resolve()
    try:
        output_relative = output.relative_to(release).as_posix()
    except ValueError:
        output_relative = ""
    excluded = {output_relative} if output_relative else set()
    if args.exclude_finalization_outputs:
        if output_relative != "suavoagent.spdx.json":
            raise SystemExit("final publication SBOM must be inside the release root")
        finalization_outputs = finalization_output_names(args.version)
        for relative in finalization_outputs:
            candidate = release / relative
            if candidate.exists() or candidate.is_symlink():
                raise SystemExit(
                    f"finalization output exists before the SBOM boundary: {relative}"
                )
        excluded.update(finalization_outputs)
    excluded_files = frozenset(excluded)
    provenance = json.loads((ROOT / "legal/THIRD-PARTY-PROVENANCE.json").read_text())
    files = release_files(release, excluded_files)
    if not files:
        raise SystemExit("release directory is empty")

    namespace_seed = f"{args.source_commit}|{args.version}|win-x64"
    document = {
        "spdxVersion": "SPDX-2.3",
        "dataLicense": "CC0-1.0",
        "SPDXID": "SPDXRef-DOCUMENT",
        "name": f"SuavoAgent-{args.version}-win-x64",
        "documentNamespace": "https://suavollc.com/spdx/suavoagent/" + hashlib.sha256(namespace_seed.encode()).hexdigest(),
        "creationInfo": {
            "created": "1970-01-01T00:00:00Z",
            "creators": ["Organization: MKM Technologies LLC", "Tool: SuavoAgent generate-release-sbom.py/1"],
        },
        "documentDescribes": ["SPDXRef-Package-SuavoAgent"],
        "packages": [{
            "name": "SuavoAgent",
            "SPDXID": "SPDXRef-Package-SuavoAgent",
            "versionInfo": args.version,
            "supplier": "Organization: MKM Technologies LLC",
            "downloadLocation": "NOASSERTION",
            "filesAnalyzed": True,
            "licenseConcluded": "NOASSERTION",
            "licenseDeclared": "NOASSERTION",
            "copyrightText": "Copyright 2026 MKM Technologies LLC",
            "externalRefs": [{
                "referenceCategory": "OTHER",
                "referenceType": "source-commit",
                "referenceLocator": args.source_commit.lower(),
            }],
        }],
        "files": [],
        "relationships": [],
        "hasExtractedLicensingInfos": [],
        "annotations": [],
    }
    file_sha1_values: list[str] = []
    for path in files:
        relative = path.relative_to(release).as_posix()
        identity = spdx_id("File", relative)
        sha1 = digest(path, "sha1")
        file_sha1_values.append(sha1)
        document["files"].append({
            "fileName": "./" + relative,
            "SPDXID": identity,
            "checksums": [
                {"algorithm": "SHA1", "checksumValue": sha1},
                {"algorithm": "SHA256", "checksumValue": digest(path)},
            ],
            "licenseConcluded": "NOASSERTION",
            "copyrightText": "NOASSERTION",
        })
        document["relationships"].append({
            "spdxElementId": "SPDXRef-Package-SuavoAgent",
            "relationshipType": "CONTAINS",
            "relatedSpdxElement": identity,
        })
    verification = {
        "packageVerificationCodeValue": hashlib.sha1(
            "".join(sorted(file_sha1_values)).encode("ascii")
        ).hexdigest(),
    }
    if excluded_files:
        verification["packageVerificationCodeExcludedFiles"] = [
            "./" + name for name in sorted(excluded_files)
        ]
    document["packages"][0]["packageVerificationCode"] = verification
    for package in provenance["packages"]:
        identity = spdx_id("NuGet", package["name"] + "-" + package["version"])
        declared = package["license"] if package["licenseType"] == "expression" else "NOASSERTION"
        document["packages"].append({
            "name": package["name"],
            "SPDXID": identity,
            "versionInfo": package["version"],
            "supplier": "NOASSERTION",
            "downloadLocation": package["packageDownloadUrl"],
            "filesAnalyzed": False,
            "checksums": [{
                "algorithm": "SHA256",
                "checksumValue": package["packageArtifactSha256"],
            }],
            "licenseConcluded": declared,
            "licenseDeclared": declared,
            "copyrightText": package.get("copyright") or "NOASSERTION",
            **({
                "comment": (
                    "Managed Tesseract .NET wrapper only. Its package build targets and bundled "
                    "x86/x64 native DLLs are excluded; the separately signed native OCR cohort "
                    "remains blocked and is documented by the SBOM annotation."
                )
            } if package["name"] == "Tesseract" else {}),
            "externalRefs": [{
                "referenceCategory": "PACKAGE-MANAGER",
                "referenceType": "purl",
                "referenceLocator": f"pkg:nuget/{package['name']}@{package['version']}",
            }],
        })
        document["relationships"].append({
            "spdxElementId": "SPDXRef-Package-SuavoAgent",
            "relationshipType": "DEPENDS_ON",
            "relatedSpdxElement": identity,
        })
    runtime_packs = provenance.get("runtimePacks", [])
    if {package.get("name") for package in runtime_packs} != SELF_CONTAINED_RUNTIME_PACKS:
        raise SystemExit("self-contained runtime pack SBOM cohort drifted")
    for package in sorted(runtime_packs, key=lambda value: value["name"]):
        identity = spdx_id("RuntimePack", package["name"] + "-" + package["version"])
        document["packages"].append({
            "name": package["name"],
            "SPDXID": identity,
            "versionInfo": package["version"],
            "supplier": "Organization: Microsoft Corporation",
            "downloadLocation": package["packageDownloadUrl"],
            "packageFileName": package["packageDownloadUrl"].rsplit("/", 1)[-1],
            "filesAnalyzed": False,
            "checksums": [{
                "algorithm": "SHA256",
                "checksumValue": package["packageArtifactSha256"],
            }],
            "licenseConcluded": package["license"],
            "licenseDeclared": package["license"],
            "copyrightText": package.get("copyright") or "NOASSERTION",
            "comment": f"Self-contained .NET {package['componentRole']} pack embedded in release executables.",
            "externalRefs": [{
                "referenceCategory": "PACKAGE-MANAGER",
                "referenceType": "purl",
                "referenceLocator": f"pkg:nuget/{package['name']}@{package['version']}",
            }],
        })
        document["relationships"].append({
            "spdxElementId": "SPDXRef-Package-SuavoAgent",
            "relationshipType": "DEPENDS_ON",
            "relatedSpdxElement": identity,
        })
    for component in provenance.get("vendoredSources", []):
        identity = spdx_id("Vendored", component["id"])
        licenses = sorted({
            license_id
            for source in component["sourceFiles"]
            for license_id in source["licenseExpression"].split(" AND ")
        })
        declared = " AND ".join(licenses)
        revision = component["upstream"]["revision"]
        repository = component["upstream"]["repository"]
        document["packages"].append({
            "name": component["id"],
            "SPDXID": identity,
            "versionInfo": revision,
            "supplier": "NOASSERTION",
            "downloadLocation": f"{repository}/tree/{revision}",
            "filesAnalyzed": False,
            "licenseConcluded": "NOASSERTION",
            "licenseDeclared": declared,
            "copyrightText": "NOASSERTION",
            "externalRefs": [
                {
                    "referenceCategory": "OTHER",
                    "referenceType": "source-commit",
                    "referenceLocator": revision,
                },
                {
                    "referenceCategory": "OTHER",
                    "referenceType": "vendored-manifest-sha256",
                    "referenceLocator": component["manifestSha256"],
                },
            ],
        })
        document["relationships"].append({
            "spdxElementId": "SPDXRef-Package-SuavoAgent",
            "relationshipType": "DEPENDS_ON",
            "relatedSpdxElement": identity,
        })
        if "LicenseRef-Lucent-DToA" in declared:
            notice = next(
                entry for entry in component["licenseFiles"]
                if entry["path"].endswith("NumberDToA-NOTICE.txt")
            )
            document["hasExtractedLicensingInfos"].append({
                "licenseId": "LicenseRef-Lucent-DToA",
                "name": "David M. Gay and Lucent DToA permissive notice",
                "extractedText": (ROOT / notice["path"]).read_text(
                    encoding="utf-8", errors="strict"
                ).rstrip(),
                "seeAlsos": [f"{repository}/blob/{revision}/dotnet/es6numberserializer/NumberDToA.cs"],
            })
    external_assets = provenance.get("externalAssets", [])
    selected_assets = {
        asset["id"]: asset
        for asset in external_assets
        if asset.get("releaseEligible") and asset.get("id") in RELEASE_EXTERNAL_ASSETS
    }
    if set(selected_assets) != RELEASE_EXTERNAL_ASSETS:
        missing = sorted(RELEASE_EXTERNAL_ASSETS - set(selected_assets))
        raise SystemExit("release external SBOM assets are missing: " + ", ".join(missing))
    for asset_id in sorted(selected_assets):
        asset = selected_assets[asset_id]
        identity = spdx_id("External", asset_id)
        external_refs = [
            {
                "referenceCategory": "OTHER",
                "referenceType": "source-commit",
                "referenceLocator": asset["sourceRevision"],
            }
        ]
        if asset_id == "llamasharp-backend-cpu-0.24.0":
            external_refs.extend((
                {
                    "referenceCategory": "PACKAGE-MANAGER",
                    "referenceType": "purl",
                    "referenceLocator": "pkg:nuget/LLamaSharp.Backend.Cpu@0.24.0",
                },
                {
                    "referenceCategory": "OTHER",
                    "referenceType": "llama-cpp-source-commit",
                    "referenceLocator": asset["llamaCppRevision"],
                },
            ))
        if asset_id == "tesseract-native-5.2.0-eng":
            external_refs.append({
                "referenceCategory": "PACKAGE-MANAGER",
                "referenceType": "purl",
                "referenceLocator": "pkg:nuget/Tesseract@5.2.0",
            })
        document["packages"].append({
            "name": asset_id,
            "SPDXID": identity,
            "versionInfo": asset["sourceRevision"],
            "supplier": "NOASSERTION",
            "downloadLocation": asset["artifactUrl"],
            "packageFileName": asset["artifactPath"],
            "filesAnalyzed": False,
            "checksums": [{
                "algorithm": "SHA256",
                "checksumValue": asset["artifactSha256"],
            }],
            "licenseConcluded": asset["license"],
            "licenseDeclared": asset["license"],
            "copyrightText": (
                "Copyright (c) 2023 SciSharp STACK\n"
                "Copyright (c) 2023-2024 The ggml authors"
                if asset_id == "llamasharp-backend-cpu-0.24.0"
                else "NOASSERTION"
            ),
            "externalRefs": external_refs,
        })
        document["relationships"].append({
            "spdxElementId": "SPDXRef-Package-SuavoAgent",
            "relationshipType": "DEPENDS_ON",
            "relatedSpdxElement": identity,
        })
        if asset_id == "tesseract-native-5.2.0-eng":
            trained_data = next(
                (
                    item for item in asset.get("runtimeFiles", [])
                    if item.get("path") == "tessdata/eng.traineddata"
                ),
                None,
            )
            if not isinstance(trained_data, dict):
                raise SystemExit("Tesseract English traineddata SBOM evidence is missing")
            data_identity = spdx_id("External", "tessdata-fast-eng")
            document["packages"].append({
                "name": "tessdata-fast-eng",
                "SPDXID": data_identity,
                "versionInfo": trained_data["sourceRevision"],
                "supplier": "Organization: tesseract-ocr",
                "downloadLocation": trained_data["sourceUrl"],
                "packageFileName": "eng.traineddata",
                "filesAnalyzed": False,
                "checksums": [{
                    "algorithm": "SHA256",
                    "checksumValue": trained_data["sha256"],
                }],
                "licenseConcluded": "Apache-2.0",
                "licenseDeclared": "Apache-2.0",
                "copyrightText": "NOASSERTION",
                "externalRefs": [{
                    "referenceCategory": "PACKAGE-MANAGER",
                    "referenceType": "purl",
                    "referenceLocator": (
                        "pkg:github/tesseract-ocr/tessdata_fast@"
                        + trained_data["sourceRevision"]
                    ),
                }],
            })
            document["relationships"].extend((
                {
                    "spdxElementId": "SPDXRef-Package-SuavoAgent",
                    "relationshipType": "DEPENDS_ON",
                    "relatedSpdxElement": data_identity,
                },
                {
                    "spdxElementId": identity,
                    "relationshipType": "DEPENDS_ON",
                    "relatedSpdxElement": data_identity,
                },
            ))
    for asset in sorted(external_assets, key=lambda value: value["id"]):
        if asset.get("releaseEligible") or asset.get("requiredForBaseRelease"):
            continue
        reason = asset.get("blockReason")
        if not isinstance(reason, str) or not reason.strip():
            raise SystemExit(f"blocked optional asset has no reason: {asset['id']}")
        document["annotations"].append({
            "annotationDate": "1970-01-01T00:00:00Z",
            "annotationType": "OTHER",
            "annotator": "Tool: SuavoAgent generate-release-sbom.py/1",
            "comment": (
                f"Optional external asset {asset['id']} is excluded from this release: "
                f"{reason}"
            ),
        })
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(document, indent=2, sort_keys=True) + "\n")
    verify_exact_file_inventory(document, release, excluded_files)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
