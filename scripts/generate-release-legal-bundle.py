#!/usr/bin/env python3
"""Generate deterministic shipped-runtime notices and provenance from lock files."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import sys
from urllib.parse import urlparse

from release_legal_evidence import (
    exact_self_contained_runtime_packs,
    load_curated_evidence,
    locked_runtime_packages,
    package_metadata,
    release_eligibility_blockers,
    validate_curated_coverage,
)
from release_legal_catalog import (
    EXPECTED_EXTERNAL_ASSETS,
    EXPECTED_VENDORED_LICENSE_FILES,
    EXPECTED_VENDORED_LOCAL_FILES,
    EXPECTED_VENDORED_LOCAL_SHA256,
    EXPECTED_VENDORED_SOURCE_LICENSES,
    EXPECTED_VENDORED_UPSTREAM_SHA256,
)


ROOT = Path(__file__).resolve().parents[1]
PRODUCTION_PROJECTS = (
    "src/SuavoAgent.Core",
    "src/SuavoAgent.Broker",
    "src/SuavoAgent.Helper",
    "src/SuavoAgent.Watchdog",
    "src/SuavoAgent.Setup",
)
RUNTIME_VERSION = "8.0.28"
NOTICE_OUTPUT = ROOT / "src/SuavoAgent.Setup/Legal/THIRD-PARTY-NOTICES.txt"
PROVENANCE_OUTPUT = ROOT / "legal/THIRD-PARTY-PROVENANCE.json"
VENDORED_MANIFEST = ROOT / "legal/vendored/json-canonicalization.json"
VENDORED_SOURCE_DIRECTORY = (
    ROOT / "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization"
)
def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def repository_file(relative: object, label: str) -> Path:
    if not isinstance(relative, str) or not relative or "\\" in relative:
        raise RuntimeError(f"{label} must be a non-empty repository-relative POSIX path")
    relative_path = Path(relative)
    if relative_path.is_absolute() or ".." in relative_path.parts:
        raise RuntimeError(f"{label} escapes the repository: {relative}")
    resolved = (ROOT / relative_path).resolve()
    try:
        resolved.relative_to(ROOT.resolve())
    except ValueError as error:
        raise RuntimeError(f"{label} escapes the repository: {relative}") from error
    if not resolved.is_file():
        raise RuntimeError(f"{label} is missing: {relative}")
    return resolved


def require_sha256(value: object, label: str) -> str:
    if not isinstance(value, str) or not re.fullmatch(r"[0-9a-f]{64}", value):
        raise RuntimeError(f"{label} must be a lowercase SHA-256 digest")
    return value


def require_https(value: object, label: str) -> str:
    if not isinstance(value, str):
        raise RuntimeError(f"{label} must be an HTTPS URL")
    parsed = urlparse(value)
    if parsed.scheme != "https" or not parsed.netloc or parsed.username or parsed.password:
        raise RuntimeError(f"{label} must be an HTTPS URL")
    return value


def reconstructed_number_dtoa_sha256() -> str:
    names = (
        "NumberDToA.Infrastructure.cs",
        "NumberDToA.cs",
        "NumberDToA.Formatting.cs",
    )
    headers: list[str] = []
    bodies: list[str] = []
    marker = "    partial class NumberDToA {\n"
    closing = "    }\n}\n"
    for name in names:
        source = (VENDORED_SOURCE_DIRECTORY / name).read_text(encoding="utf-8")
        if source.count(marker) != 1 or not source.endswith(closing):
            raise RuntimeError(f"vendored NumberDToA split shape drifted: {name}")
        header, body_and_closing = source.split(marker, 1)
        headers.append(header)
        bodies.append(body_and_closing[:-len(closing)])
    if len(set(headers)) != 1:
        raise RuntimeError("vendored NumberDToA split headers drifted")
    reconstructed = (
        headers[0]
        + "    class NumberDToA {\n\n"
        + bodies[0]
        + bodies[1]
        + "\n"
        + bodies[2]
        + closing
    )
    return hashlib.sha256(reconstructed.encode("utf-8")).hexdigest()


def reversed_reviewed_nullable_patch_sha256(name: str) -> str:
    replacements = {
        "JsonCanonicalizer.cs": (
            ("        void Serialize(object? o)", "        void Serialize(object o)"),
            (
                "            if (o is SortedDictionary<string, object?> objectDictionary)",
                "            if (o is SortedDictionary<string, object>)",
            ),
            (
                "                foreach (var keyValuePair in objectDictionary)",
                "                foreach (var keyValuePair in (SortedDictionary<string, object>)o)",
            ),
            (
                "            else if (o is List<object?> array)",
                "            else if (o is List<object>)",
            ),
            (
                "                foreach (object? value in array)",
                "                foreach (object value in (List<object>)o)",
            ),
            (
                "                buffer.Append((Boolean)o ? \"true\" : \"false\");",
                "                buffer.Append(o.ToString().ToLowerInvariant());",
            ),
            ("        object? ParseElement()", "        object ParseElement()"),
            (
                "            SortedDictionary<string, object?> dict =\n"
                "                new SortedDictionary<string, object?>(StringComparer.Ordinal);",
                "            SortedDictionary<string, object> dict =\n"
                "                new SortedDictionary<string, object>(StringComparer.Ordinal);",
            ),
            ("            var list = new List<object?>();", "            var list = new List<object>();"),
            ("        object? ParseSimpleType()", "        object ParseSimpleType()"),
        ),
        "NumberFastDToA.cs": ((
            "        public static String? NumberToString(double v)",
            "        public static String NumberToString(double v)",
        ),),
        "NumberToJson.cs": ((
            "            String? result = NumberFastDToA.NumberToString(value);",
            "            String result = NumberFastDToA.NumberToString(value);",
        ),),
    }
    source = (VENDORED_SOURCE_DIRECTORY / name).read_text(encoding="utf-8")
    for local, upstream in replacements[name]:
        if source.count(local) != 1:
            raise RuntimeError(f"reviewed nullable patch shape drifted: {name}")
        source = source.replace(local, upstream)
    if name == "NumberToJson.cs":
        source += "\n"
    return hashlib.sha256(source.encode("utf-8")).hexdigest()


def vendored_source_provenance() -> dict[str, object]:
    manifest = json.loads(VENDORED_MANIFEST.read_text(encoding="utf-8"))
    if not isinstance(manifest, dict) or manifest.get("schemaVersion") != 1:
        raise RuntimeError("vendored JSON canonicalization manifest schema is not reviewed")
    if manifest.get("id") != "cyberphone-json-canonicalization-dotnet":
        raise RuntimeError("vendored JSON canonicalization identity is not reviewed")

    upstream = manifest.get("upstream")
    if not isinstance(upstream, dict):
        raise RuntimeError("vendored JSON canonicalization upstream provenance is missing")
    if upstream.get("repository") != "https://github.com/cyberphone/json-canonicalization":
        raise RuntimeError("vendored JSON canonicalization repository is not official")
    revision = upstream.get("revision")
    if revision != "19d51d7fe467d4706a3ff08adf8a748f29fc21e0":
        raise RuntimeError("vendored JSON canonicalization revision is not the reviewed commit")
    if upstream.get("licensePath") != "LICENSE":
        raise RuntimeError("vendored JSON canonicalization upstream license path drifted")
    if upstream.get("licenseSha256") != "6821faaddedf2d78c95bb6d98b127e9e616097afd2f6bcc34389f000d13ab12d":
        raise RuntimeError("vendored JSON canonicalization upstream license digest drifted")

    if manifest.get("buildProvenance") != {
        "project": "src/SuavoAgent.Diagnostics/SuavoAgent.Diagnostics.csproj",
        "mode": "compiled-source",
        "generator": None,
    }:
        raise RuntimeError("vendored JSON canonicalization build provenance drifted")
    modifications = manifest.get("modifications")
    if (
        not isinstance(modifications, list)
        or not modifications
        or any(not isinstance(item, str) or not item.strip() for item in modifications)
    ):
        raise RuntimeError("vendored JSON canonicalization modifications are not declared")

    license_files = manifest.get("licenseFiles")
    if not isinstance(license_files, list):
        raise RuntimeError("vendored JSON canonicalization license files are missing")
    actual_license_paths: set[str] = set()
    for entry in license_files:
        if not isinstance(entry, dict):
            raise RuntimeError("vendored JSON canonicalization license entry is invalid")
        path_value = entry.get("path")
        path = repository_file(path_value, "vendored license")
        expected_hash = require_sha256(entry.get("sha256"), f"vendored license {path_value}")
        if EXPECTED_VENDORED_LICENSE_FILES.get(path_value) != expected_hash:
            raise RuntimeError(f"vendored license is not the reviewed text: {path_value}")
        if sha256(path) != expected_hash:
            raise RuntimeError(f"vendored license bytes drifted: {path_value}")
        if path_value in actual_license_paths:
            raise RuntimeError(f"vendored license is duplicated: {path_value}")
        actual_license_paths.add(path_value)
    if actual_license_paths != set(EXPECTED_VENDORED_LICENSE_FILES):
        raise RuntimeError("vendored JSON canonicalization license cohort drifted")

    source_files = manifest.get("sourceFiles")
    if not isinstance(source_files, list):
        raise RuntimeError("vendored JSON canonicalization source list is missing")
    actual_upstream_paths: set[str] = set()
    actual_local_paths: set[str] = set()
    for source in source_files:
        if not isinstance(source, dict):
            raise RuntimeError("vendored JSON canonicalization source entry is invalid")
        upstream_path = source.get("upstreamPath")
        expected_license = EXPECTED_VENDORED_SOURCE_LICENSES.get(upstream_path)
        if expected_license is None or source.get("licenseExpression") != expected_license:
            raise RuntimeError(f"vendored source license drifted: {upstream_path}")
        if upstream_path in actual_upstream_paths:
            raise RuntimeError(f"vendored upstream source is duplicated: {upstream_path}")
        actual_upstream_paths.add(upstream_path)
        upstream_hash = require_sha256(
            source.get("upstreamSha256"), f"vendored upstream source {upstream_path}"
        )
        if EXPECTED_VENDORED_UPSTREAM_SHA256.get(upstream_path) != upstream_hash:
            raise RuntimeError(f"vendored upstream source is not the reviewed file: {upstream_path}")
        local_files = source.get("localFiles")
        if not isinstance(local_files, list) or not local_files:
            raise RuntimeError(f"vendored local source mapping is missing: {upstream_path}")
        for local in local_files:
            if not isinstance(local, dict):
                raise RuntimeError(f"vendored local source mapping is invalid: {upstream_path}")
            local_path = local.get("path")
            path = repository_file(local_path, "vendored source")
            expected_hash = require_sha256(
                local.get("sha256"), f"vendored local source {local_path}"
            )
            if EXPECTED_VENDORED_LOCAL_SHA256.get(local_path) != expected_hash:
                raise RuntimeError(f"vendored source is not the reviewed local patch: {local_path}")
            if sha256(path) != expected_hash:
                raise RuntimeError(f"vendored source bytes drifted: {local_path}")
            if local_path in actual_local_paths:
                raise RuntimeError(f"vendored local source is duplicated: {local_path}")
            actual_local_paths.add(local_path)
    if actual_upstream_paths != set(EXPECTED_VENDORED_SOURCE_LICENSES):
        raise RuntimeError("vendored JSON canonicalization upstream source cohort drifted")
    if actual_local_paths != EXPECTED_VENDORED_LOCAL_FILES:
        raise RuntimeError("vendored JSON canonicalization local source cohort drifted")
    if reconstructed_number_dtoa_sha256() != EXPECTED_VENDORED_UPSTREAM_SHA256[
        "dotnet/es6numberserializer/NumberDToA.cs"
    ]:
        raise RuntimeError("vendored NumberDToA split does not reconstruct the reviewed upstream source")
    for name, upstream_path in (
        ("JsonCanonicalizer.cs", "dotnet/jsoncanonicalizer/JsonCanonicalizer.cs"),
        ("NumberFastDToA.cs", "dotnet/es6numberserializer/NumberFastDToA.cs"),
        ("NumberToJson.cs", "dotnet/es6numberserializer/NumberToJson.cs"),
    ):
        if reversed_reviewed_nullable_patch_sha256(name) != EXPECTED_VENDORED_UPSTREAM_SHA256[
            upstream_path
        ]:
            raise RuntimeError(f"reviewed nullable patch does not reverse to upstream: {name}")
    on_disk_sources = {
        path.relative_to(ROOT).as_posix()
        for path in VENDORED_SOURCE_DIRECTORY.glob("*.cs")
        if path.is_file()
    }
    if on_disk_sources != EXPECTED_VENDORED_LOCAL_FILES:
        raise RuntimeError("unattested source exists in the vendored canonicalization directory")

    return {
        "manifestPath": VENDORED_MANIFEST.relative_to(ROOT).as_posix(),
        "manifestSha256": sha256(VENDORED_MANIFEST),
        **manifest,
    }


def external_asset_provenance() -> tuple[Path, dict[str, object]]:
    external_path = ROOT / "legal/external-assets.json"
    external = json.loads(external_path.read_text(encoding="utf-8"))
    if not isinstance(external, dict) or external.get("schemaVersion") != 1:
        raise RuntimeError("external asset catalog schema is not reviewed")
    assets = external.get("assets")
    if not isinstance(assets, list):
        raise RuntimeError("external asset catalog is missing assets")
    actual = {asset.get("id") for asset in assets if isinstance(asset, dict)}
    if len(actual) != len(assets) or actual != set(EXPECTED_EXTERNAL_ASSETS):
        raise RuntimeError("external asset catalog must contain the exact reviewed cohort")

    for asset in assets:
        identity = asset["id"]
        expected_upstream, expected_license, expected_base_scope = EXPECTED_EXTERNAL_ASSETS[identity]
        if asset.get("license") != expected_license:
            raise RuntimeError(f"external asset license drifted: {identity}")
        if expected_upstream is not None:
            if require_https(asset.get("upstream"), f"external asset upstream {identity}") != expected_upstream:
                raise RuntimeError(f"external asset upstream is not the reviewed official URL: {identity}")
        for key in ("sourceRevision", "artifactRevision"):
            value = asset.get(key)
            if value is not None and (
                not isinstance(value, str) or not re.fullmatch(r"[0-9a-f]{40}", value)
            ):
                raise RuntimeError(f"external asset {key} is not an exact commit: {identity}")
        for key in ("sha256", "artifactSha256", "licenseBundleSha256"):
            value = asset.get(key)
            if value is not None:
                require_sha256(value, f"external asset {key} {identity}")
        if asset.get("artifactUpstream") is not None:
            require_https(asset["artifactUpstream"], f"external artifact upstream {identity}")
        size = asset.get("artifactSizeBytes")
        if size is not None and (not isinstance(size, int) or isinstance(size, bool) or size <= 0):
            raise RuntimeError(f"external asset size is invalid: {identity}")
        if not isinstance(asset.get("requiredForBaseRelease"), bool):
            raise RuntimeError(f"external asset release scope is not explicit: {identity}")
        if asset["requiredForBaseRelease"] is not expected_base_scope:
            raise RuntimeError(f"external asset release scope drifted: {identity}")
        if not isinstance(asset.get("releaseEligible"), bool):
            raise RuntimeError(f"external asset eligibility is not explicit: {identity}")
        if not asset["releaseEligible"]:
            if not isinstance(asset.get("blockReason"), str) or not asset["blockReason"].strip():
                raise RuntimeError(f"blocked external asset has no reason: {identity}")
        elif identity != "pharmacist-panda":
            artifact_url = require_https(
                asset.get("artifactUrl"),
                f"external artifact URL {identity}",
            )
            evidence_ref = asset.get("provenanceEvidence")
            if (
                asset.get("sourceRevision") is None
                or asset.get("artifactSha256") is None
                or asset.get("artifactSizeBytes") is None
                or asset.get("licenseBundleSha256") is None
                or not isinstance(evidence_ref, dict)
            ):
                raise RuntimeError(f"release-eligible external asset lacks deterministic evidence: {identity}")
            evidence_path = repository_file(
                evidence_ref.get("path"),
                f"external asset publisher evidence {identity}",
            )
            if sha256(evidence_path) != require_sha256(
                evidence_ref.get("sha256"),
                f"external asset publisher evidence digest {identity}",
            ):
                raise RuntimeError(f"external asset publisher evidence drifted: {identity}")
            evidence = json.loads(evidence_path.read_text(encoding="utf-8"))
            if not isinstance(evidence, dict) or evidence.get("schemaVersion") != 1:
                raise RuntimeError(f"external asset publisher evidence schema drifted: {identity}")
            for key, expected in (
                ("id", identity),
                ("sourceRevision", asset["sourceRevision"]),
                ("artifactUrl", asset.get("artifactUrl")),
                ("artifactSha256", asset["artifactSha256"]),
                ("artifactSizeBytes", asset["artifactSizeBytes"]),
                ("license", asset["license"]),
            ):
                if evidence.get(key) != expected:
                    raise RuntimeError(
                        f"external asset publisher evidence {key} drifted: {identity}"
                    )
            mode = asset.get("distributionMode")
            if mode == "official-upstream-lfs":
                expected_artifact_url = (
                    f"{asset['upstream']}/resolve/{asset['sourceRevision']}/"
                    f"{asset['artifactPath']}"
                )
                if (
                    identity != "qwen3-1.7b-q4-k-m"
                    or asset.get("artifactUpstream") != asset["upstream"]
                    or artifact_url != expected_artifact_url
                    or evidence.get("evidenceType") != "hugging-face-official-lfs"
                    or evidence.get("publisher") != "Qwen"
                    or evidence.get("repository") != asset["upstream"]
                    or evidence.get("private") is not False
                    or evidence.get("gated") is not False
                    or not re.fullmatch(r"[0-9a-f]{40}", str(evidence.get("blobId", "")))
                ):
                    raise RuntimeError(f"official model publisher evidence is invalid: {identity}")
            elif mode == "nuget-repository-signed":
                expected_artifact_url = (
                    "https://api.nuget.org/v3-flatcontainer/llamasharp.backend.cpu/"
                    "0.24.0/llamasharp.backend.cpu.0.24.0.nupkg"
                )
                if (
                    identity != "llamasharp-backend-cpu-0.24.0"
                    or asset.get("artifactUpstream") != expected_artifact_url
                    or artifact_url != expected_artifact_url
                    or evidence.get("evidenceType") != "nuget-repository-signed-package"
                    or evidence.get("packageId") != "LLamaSharp.Backend.Cpu"
                    or evidence.get("packageVersion") != "0.24.0"
                    or evidence.get("signatureType") != "Repository"
                    or evidence.get("serviceIndex") != "https://api.nuget.org/v3/index.json"
                    or evidence.get("owners") != ["Haiping-Chen"]
                    or evidence.get("repositoryCertificateSha256")
                    != "1f4b311d9acc115c8dc8018b5a49e00fce6da8e2855f9f014ca6f34570bc482d"
                    or evidence.get("llamaCppRevision") != asset.get("llamaCppRevision")
                ):
                    raise RuntimeError(f"NuGet repository-signature evidence is invalid: {identity}")
                require_sha256(
                    evidence.get("embeddedSignatureSha256"),
                    f"NuGet embedded signature {identity}",
                )
            elif mode == "nuget-repository-signed-plus-pinned-upstream-data":
                expected_artifact_url = (
                    "https://api.nuget.org/v3-flatcontainer/tesseract/5.2.0/"
                    "tesseract.5.2.0.nupkg"
                )
                trained_data = evidence.get("trainedData")
                runtime_files = asset.get("runtimeFiles")
                if (
                    identity != "tesseract-native-5.2.0-eng"
                    or asset.get("artifactUpstream") != expected_artifact_url
                    or artifact_url != expected_artifact_url
                    or evidence.get("evidenceType")
                    != "nuget-repository-signed-package-plus-pinned-upstream-data"
                    or evidence.get("packageId") != "Tesseract"
                    or evidence.get("packageVersion") != "5.2.0"
                    or evidence.get("signatureType") != "Repository"
                    or evidence.get("serviceIndex")
                    != "https://api.nuget.org/v3/index.json"
                    or evidence.get("owners") != ["charlesw"]
                    or evidence.get("repositoryCertificateSha256")
                    != "5a2901d6ada3d18260b9c6dfe2133c95d74b9eef6ae0e5dc334c8454d1477df4"
                    or not isinstance(trained_data, dict)
                    or trained_data.get("sourceRevision")
                    != "65727574dfcd264acbb0c3e07860e4e9e9b22185"
                    or trained_data.get("artifactSha256")
                    != "7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2"
                    or trained_data.get("artifactSizeBytes") != 4_113_088
                    or not isinstance(runtime_files, list)
                    or len(runtime_files) != 3
                ):
                    raise RuntimeError(
                        f"Tesseract native cohort evidence is invalid: {identity}"
                    )
                require_sha256(
                    evidence.get("embeddedSignatureSha256"),
                    f"NuGet embedded signature {identity}",
                )
            else:
                raise RuntimeError(f"external asset distribution mode is not reviewed: {identity}")
        license_entries = asset.get("licenseFiles", [])
        if not isinstance(license_entries, list):
            raise RuntimeError(f"external asset retained licenses are invalid: {identity}")
        license_paths: list[Path] = []
        for entry in license_entries:
            if not isinstance(entry, dict):
                raise RuntimeError(f"external asset retained license is invalid: {identity}")
            license_path = repository_file(
                entry.get("path"), "external asset license"
            )
            if sha256(license_path) != require_sha256(
                entry.get("sha256"), f"external asset retained license {identity}"
            ):
                raise RuntimeError(f"external asset retained license bytes drifted: {identity}")
            source = entry.get("source")
            if not isinstance(source, str) or not source.strip():
                raise RuntimeError(f"external asset retained license source is missing: {identity}")
            source_revision = entry.get("sourceRevision")
            if source_revision is not None and (
                not isinstance(source_revision, str)
                or not re.fullmatch(r"[0-9a-f]{40}", source_revision)
                or source_revision not in source
            ):
                raise RuntimeError(f"external asset retained license source is mutable: {identity}")
            license_paths.append(license_path)
        if asset["releaseEligible"] and identity != "pharmacist-panda":
            bundle = hashlib.sha256()
            for license_path in license_paths:
                bundle.update(license_path.read_bytes())
            if not license_paths or bundle.hexdigest() != asset["licenseBundleSha256"]:
                raise RuntimeError(f"external asset retained license digest drifted: {identity}")
        if identity == "qwen3-1.7b-q4-k-m" and [
            entry.get("path") for entry in license_entries
        ] != ["legal/license-texts/Apache-2.0.txt"]:
            raise RuntimeError("official model retained license cohort drifted")
        if identity == "llamasharp-backend-cpu-0.24.0":
            expected_licenses = [
                (
                    "legal/package-license-texts/SciSharp-LLamaSharp-0.24.0-LICENSE.txt",
                    "52e74038a69e948314106225360faee6159dae61f96a3a76fd0f3c2c3066c4f4",
                    "ce8eeb4c3d6937defc1dc38aaac4ad8bd282e8a5",
                ),
                (
                    "legal/license-texts/llama.cpp-0.24.0-MIT.txt",
                    "e562a2ddfaf8280537795ac5ecd34e3012b6582a147ef69ba6a6a5c08c84757d",
                    "ceda28ef8e310a8dee60bf275077a3eedae8e36c",
                ),
            ]
            actual_licenses = [
                (entry.get("path"), entry.get("sha256"), entry.get("sourceRevision"))
                for entry in license_entries
            ]
            if actual_licenses != expected_licenses:
                raise RuntimeError("official native brain retained license cohort drifted")
        if identity == "tesseract-native-5.2.0-eng":
            expected_licenses = [
                (
                    "legal/package-license-texts/Tesseract-5.2.0-LICENSE.txt",
                    "cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30",
                    "2c993543f7fa66576a8890a6c4ab053c4598aaed",
                ),
                (
                    "legal/license-texts/Leptonica-BSD-2-Clause.txt",
                    "4d3065116f182e29760af0c901d5dbb2e1e16c42765dfc24e69b26805e2acb1e",
                    "f4138265b390f1921b9891d6669674d3157887d8",
                ),
            ]
            actual_licenses = [
                (entry.get("path"), entry.get("sha256"), entry.get("sourceRevision"))
                for entry in license_entries
            ]
            if actual_licenses != expected_licenses:
                raise RuntimeError("official native OCR retained license cohort drifted")

    panda = next(asset for asset in assets if asset["id"] == "pharmacist-panda")
    panda_path = repository_file(panda.get("path"), "pharmacist panda")
    if sha256(panda_path) != require_sha256(panda.get("sha256"), "pharmacist panda"):
        raise RuntimeError("pharmacist panda bytes do not match reviewed provenance")
    repository_file(panda.get("provenance"), "pharmacist panda provenance")
    return external_path, external


def generate() -> tuple[str, str]:
    nuget_root = Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget/packages"))
    vendored = vendored_source_provenance()
    curated = load_curated_evidence(ROOT)
    packages = [
        package_metadata(locked, nuget_root, curated)[0]
        for locked in locked_runtime_packages(ROOT, PRODUCTION_PROJECTS)
    ]
    validate_curated_coverage(packages, curated)
    runtime_packs = exact_self_contained_runtime_packs(
        ROOT, nuget_root, PRODUCTION_PROJECTS
    )

    external_path, external = external_asset_provenance()

    lines = [
        "SUAVOAGENT THIRD-PARTY NOTICES",
        "Generated from checked-in production packages.lock.json files. Do not edit by hand.",
        "",
        "NUGET RUNTIME CLOSURE",
    ]
    for package in packages:
        lines.extend((
            "",
            f"{package['name']} {package['version']}",
            f"Authors: {package['authors'] or 'not declared'}",
            f"Copyright: {package['copyright'] or 'not declared'}",
            f"License: {package['license']}",
            f"Project: {package['projectUrl'] or 'not declared'}",
        ))
        if package["licenseUrl"]:
            lines.append(f"License URL: {package['licenseUrl']}")
        if package["retainedLicenseSha256"]:
            lines.append(f"Retained license SHA-256: {package['retainedLicenseSha256']}")
        for legal in package["retainedLegalFiles"]:
            source = legal.get("repositoryPath") or legal.get("packagePath")
            lines.append(
                f"Retained legal file: {legal['name']} "
                f"(SHA-256 {legal['sha256']}; source {legal['source']}:{source})"
            )
    lines.extend((
        "",
        "VENDORED SOURCE CLOSURE",
        "",
        f"{vendored['id']} @ {vendored['upstream']['revision']}",
        f"Official repository: {vendored['upstream']['repository']}",
        f"Manifest: {vendored['manifestPath']} (SHA-256 {vendored['manifestSha256']})",
        "Build mode: compiled source in src/SuavoAgent.Diagnostics/SuavoAgent.Diagnostics.csproj",
        "Declared modifications:",
    ))
    for modification in vendored["modifications"]:
        lines.append(f"- {modification}")
    for source in vendored["sourceFiles"]:
        lines.extend((
            "",
            f"Upstream source: {source['upstreamPath']}",
            f"Upstream SHA-256: {source['upstreamSha256']}",
            f"License: {source['licenseExpression']}",
        ))
        for local in source["localFiles"]:
            lines.append(f"Compiled local source: {local['path']} (SHA-256 {local['sha256']})")
    unique_legal = {}
    for package in packages:
        for legal in package["retainedLegalFiles"]:
            unique_legal.setdefault(legal["sha256"], legal)
    lines.extend(("", "RETAINED PACKAGE LICENSE AND NOTICE TEXTS"))
    for legal_hash, legal in sorted(unique_legal.items()):
        lines.extend((
            "",
            f"{legal['name']} (SHA-256 {legal_hash})",
            "",
            legal["text"].rstrip(),
        ))
    lines.extend(("", "VENDORED SOURCE LICENSE AND NOTICE TEXTS"))
    for license_entry in vendored["licenseFiles"]:
        license_path = ROOT / license_entry["path"]
        lines.extend((
            "",
            f"{license_entry['path']} (SHA-256 {license_entry['sha256']})",
            "",
            license_path.read_text(encoding="utf-8", errors="strict").rstrip(),
        ))
    lines.extend(("", "STANDARD LICENSE TEXTS USED BY DECLARED SPDX EXPRESSIONS"))
    for license_name in (
        "legal/license-texts/Apache-2.0.txt",
        "legal/license-texts/Leptonica-BSD-2-Clause.txt",
    ):
        license_path = ROOT / license_name
        lines.extend((
            "",
            f"{license_name} (SHA-256 {sha256(license_path)})",
            "",
            license_path.read_text(encoding="utf-8").rstrip(),
        ))
    lines.extend(("", f"MICROSOFT .NET RUNTIME {RUNTIME_VERSION}"))
    for package in runtime_packs:
        lines.extend((
            "",
            f"{package['name']} {package['version']} ({package['componentRole']})",
            f"Source commit: {package['repositoryCommit']}",
            f"Artifact SHA-256: {package['packageArtifactSha256']}",
            f"License: {package['license']}",
        ))
        for legal in package["retainedLegalFiles"]:
            lines.append(
                f"Retained legal file: {legal['packagePath']} (SHA-256 {legal['sha256']})"
            )
    runtime_legal = {
        legal["sha256"]: legal
        for package in runtime_packs
        for legal in package["retainedLegalFiles"]
    }
    lines.extend(("", "SELF-CONTAINED .NET RUNTIME LICENSE AND NOTICE TEXTS"))
    for legal_hash, legal in sorted(runtime_legal.items()):
        lines.extend((
            "",
            f"{legal['name']} (SHA-256 {legal_hash})",
            "",
            legal["text"].rstrip(),
        ))
    lines.extend(("", "EXTERNAL AND OPTIONAL ASSETS"))
    for asset in external["assets"]:
        lines.extend((
            "",
            asset["id"],
            f"Kind: {asset['kind']}",
            f"License: {asset['license']}",
            f"Upstream or provenance: {asset.get('upstream') or asset.get('provenance')}",
            f"Release eligible: {str(asset['releaseEligible']).lower()}",
        ))
        if asset.get("blockReason"):
            lines.append(f"Block reason: {asset['blockReason']}")
        for license_entry in asset.get("licenseFiles", []):
            license_name = license_entry["path"]
            license_path = ROOT / license_name
            lines.append(f"Retained license: {license_name} (SHA-256 {sha256(license_path)})")
    external_license_paths = {
        entry["path"]
        for asset in external["assets"]
        for entry in asset.get("licenseFiles", [])
    }
    lines.extend(("", "EXTERNAL ASSET LICENSE TEXTS"))
    for license_name in sorted(external_license_paths):
        license_path = ROOT / license_name
        lines.extend((
            "",
            f"{license_name} (SHA-256 {sha256(license_path)})",
            "",
            license_path.read_text(encoding="utf-8", errors="strict").rstrip(),
        ))
    notice = "\n".join(lines).rstrip() + "\n"

    provenance = {
        "schemaVersion": 1,
        "runtimeIdentifier": "win-x64",
        "dotnetRuntimeVersion": RUNTIME_VERSION,
        "packageLicenseEvidenceCatalogSha256": sha256(
            ROOT / "legal/package-license-evidence.json"
        ),
        "packages": [
            {
                **package,
                "retainedLegalFiles": [
                    {key: value for key, value in legal.items() if key != "text"}
                    for legal in package["retainedLegalFiles"]
                ],
            }
            for package in packages
        ],
        "runtimePacks": [
            {
                **package,
                "retainedLegalFiles": [
                    {key: value for key, value in legal.items() if key != "text"}
                    for legal in package["retainedLegalFiles"]
                ],
            }
            for package in runtime_packs
        ],
        "vendoredSources": [vendored],
        "externalAssetsCatalogSha256": sha256(external_path),
        "externalAssets": external["assets"],
    }
    return notice, json.dumps(provenance, indent=2, sort_keys=True) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true")
    parser.add_argument("--require-release-eligible", action="store_true")
    parser.add_argument("--require-feature-eligible", action="append", default=[])
    args = parser.parse_args()
    notice, provenance = generate()
    expected = ((NOTICE_OUTPUT, notice), (PROVENANCE_OUTPUT, provenance))
    if args.check:
        stale = [str(path.relative_to(ROOT)) for path, value in expected
                 if not path.is_file() or path.read_bytes() != value.encode("utf-8")]
        if stale:
            print("stale release legal bundle: " + ", ".join(stale), file=sys.stderr)
            return 1
    if args.require_release_eligible or args.require_feature_eligible:
        data = json.loads(provenance)
        requested_features = set(args.require_feature_eligible)
        blockers = release_eligibility_blockers(data, requested_features)
        if blockers["unknownFeatures"]:
            print(
                "unknown external feature assets: "
                + ", ".join(blockers["unknownFeatures"]),
                file=sys.stderr,
            )
            return 1
        if blockers["packages"] or blockers["blockedAssets"]:
            if blockers["packages"]:
                print(
                    "missing exact dependency legal evidence: "
                    + ", ".join(blockers["packages"]),
                    file=sys.stderr,
                )
            if blockers["blockedAssets"]:
                print(
                    "external asset provenance blocked: "
                    + ", ".join(blockers["blockedAssets"]),
                    file=sys.stderr,
                )
            return 1
    if args.check:
        return 0
    for path, value in expected:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(value.encode("utf-8"))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
