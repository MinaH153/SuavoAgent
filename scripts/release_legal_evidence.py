#!/usr/bin/env python3
"""Exact locked-NuGet and pinned-upstream legal evidence helpers."""

from __future__ import annotations

import base64
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
from typing import Iterable
import xml.etree.ElementTree as ET
import zipfile


CATALOG_RELATIVE_PATH = "legal/package-license-evidence.json"
MAX_LEGAL_FILE_BYTES = 2 * 1024 * 1024
MAX_NUGET_ARTIFACT_BYTES = 256 * 1024 * 1024
MAX_NUGET_ENTRIES = 20_000
NUGET_SOURCE = "https://api.nuget.org/v3/index.json"
EXPECTED_SELF_CONTAINED_RUNTIME_PACKS = (
    {
        "name": "Microsoft.NETCore.App.Host.win-x64",
        "version": "8.0.28",
        "role": "apphost",
        "contentHash": "zdq0VNLgvPyhOw/kY03snvdSfB+g0Siw7FC1T7FkCsVvyJ8hA5KQ1SCzNyzXdrn50BeTUavqcr6zy9GPmAbDgg==",
        "artifactSha256": "ad5c07e2e48af85f524a2b404edd112795bfa3f524825014c819dee00f7c954a",
        "artifactSha512": "dfZD6+U82ONMWAZrVs1l4SgOFlA/aQM6RqSuoZbuiFLowJ5AW1r/2qQGjzThozADjsM0/79h28YsDNJohgI2Rg==",
        "nuspecSha256": "6c5428eca135be0fc67cd552de7869b9a54ae0ced74d6cfd36fee0e155dc4556",
        "repository": "https://github.com/dotnet/runtime",
        "repositoryCommit": "46295af5828b062bbbf93a9cef50fd8cb9fbcb09",
        "legal": {
            "LICENSE.TXT": "d7a68596ab69b06f51ca278a6545148e4269a9381c26d597c13df5d88e08cf5b",
            "THIRD-PARTY-NOTICES.TXT": "b60b2912da28eaa6518593c9e2efb5334ee062d3c42e80d8fdfa806b3dc52977",
        },
    },
    {
        "name": "Microsoft.NETCore.App.Runtime.win-x64",
        "version": "8.0.28",
        "role": "runtime",
        "contentHash": "G2SWebJKnBkixQcJlVkCV0EbqdoAhqAf6evVKDcY6CmFjPOFeC+gZeO6/dlm4v1fbKERjpjJzMg0mnKA1il1Zg==",
        "artifactSha256": "cd995f0e5a47962965dc3a37fda2dc8b3042a67bfe692c64ca9c146466b24614",
        "artifactSha512": "SRdQnoumUQkUjrO/IKeC9joscXGWFtxOxhImwiHfBCaRyiQCZSOX7G+NvrpY1HKaeDEdk3plYaXAM2U2Sa59zQ==",
        "nuspecSha256": "365187eba3a2ac9a943f9fe2549b98e7eda7f0e10d3d37a1be38735e85f4c026",
        "repository": "https://github.com/dotnet/runtime",
        "repositoryCommit": "46295af5828b062bbbf93a9cef50fd8cb9fbcb09",
        "legal": {
            "LICENSE.TXT": "d7a68596ab69b06f51ca278a6545148e4269a9381c26d597c13df5d88e08cf5b",
            "THIRD-PARTY-NOTICES.TXT": "b60b2912da28eaa6518593c9e2efb5334ee062d3c42e80d8fdfa806b3dc52977",
        },
    },
    {
        "name": "Microsoft.WindowsDesktop.App.Runtime.win-x64",
        "version": "8.0.28",
        "role": "runtime",
        "contentHash": "sKbAXRze+wBxtj2zexZfpR4vGe2yKZqRn79wFpwa6Ev7be81noXIewGpWvoy7s41y1bTPFzt2YDl3pGFFsXYmQ==",
        "artifactSha256": "9df882790f3fcf71d61465328e270f690e8439142a1d57d2fe98bd8649442e1c",
        "artifactSha512": "ooWLItKGmK5MGOfwWtrW69HFzra1rZndhoAVzN+Ir3H9AYYQWiCONs86GmJ02YjqwB+LLUfUrSZypgbzujeNqg==",
        "nuspecSha256": "0ca905fab89babe567e1d30694d7f367b691eac506efa7dec4d53d380544d864",
        "repository": "https://github.com/dotnet/windowsdesktop",
        "repositoryCommit": "432d0577ee8d6a36654d23a83182a0c7da27a69f",
        "legal": {
            "LICENSE": "a89886665765362eb77e0f8e26602c924520041d1711b2eedc136434fe4d01ab",
        },
    },
)


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def file_sha512_base64(path: Path) -> str:
    digest = hashlib.sha512()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return base64.b64encode(digest.digest()).decode("ascii")


def require_sha256(value: object, label: str) -> str:
    if not isinstance(value, str) or not re.fullmatch(r"[0-9a-f]{64}", value):
        raise RuntimeError(f"{label} must be a lowercase SHA-256 digest")
    return value


def require_sha512_base64(value: object, label: str) -> str:
    if not isinstance(value, str):
        raise RuntimeError(f"{label} must be a base64 SHA-512 digest")
    try:
        decoded = base64.b64decode(value, validate=True)
    except (ValueError, TypeError) as error:
        raise RuntimeError(f"{label} must be a base64 SHA-512 digest") from error
    if len(decoded) != 64:
        raise RuntimeError(f"{label} must be a base64 SHA-512 digest")
    return value


def repository_file(root: Path, relative: object, label: str) -> Path:
    if not isinstance(relative, str) or not relative or "\\" in relative:
        raise RuntimeError(f"{label} must be a non-empty repository-relative POSIX path")
    relative_path = Path(relative)
    if relative_path.is_absolute() or ".." in relative_path.parts:
        raise RuntimeError(f"{label} escapes the repository: {relative}")
    resolved = (root / relative_path).resolve()
    try:
        resolved.relative_to(root.resolve())
    except ValueError as error:
        raise RuntimeError(f"{label} escapes the repository: {relative}") from error
    if not resolved.is_file():
        raise RuntimeError(f"{label} is missing: {relative}")
    return resolved


def package_key(name: str, version: str) -> str:
    return f"{name.casefold()}@{version.casefold()}"


def normalize_repository(value: str) -> str:
    normalized = value.strip().rstrip("/")
    if normalized.casefold().endswith(".git"):
        normalized = normalized[:-4]
    return normalized.casefold()


def _element_text(element: ET.Element, name: str) -> str:
    child = next(
        (node for node in element if node.tag.rsplit("}", 1)[-1] == name),
        None,
    )
    return (child.text or "").strip() if child is not None else ""


def _repository_metadata(metadata: ET.Element) -> tuple[str, str]:
    node = next(
        (child for child in metadata if child.tag.rsplit("}", 1)[-1] == "repository"),
        None,
    )
    if node is None:
        return "", ""
    return node.attrib.get("url", "").strip(), node.attrib.get("commit", "").strip()


def _safe_archive_path(name: str, label: str) -> PurePosixPath:
    if not name or "\\" in name or "\x00" in name:
        raise RuntimeError(f"unsafe {label} path: {name!r}")
    path = PurePosixPath(name)
    if path.is_absolute() or ".." in path.parts:
        raise RuntimeError(f"unsafe {label} path: {name!r}")
    return path


def _legal_kind(name: str) -> str:
    folded = Path(name).name.casefold()
    if "license" in folded or "licence" in folded:
        return "license"
    if "copying" in folded:
        return "copying"
    return "notice"


def _decode_legal_text(data: bytes, label: str) -> str:
    if not data or len(data) > MAX_LEGAL_FILE_BYTES:
        raise RuntimeError(f"legal file has invalid size: {label}")
    try:
        return data.decode("utf-8-sig", errors="strict")
    except UnicodeDecodeError as error:
        raise RuntimeError(f"legal file is not UTF-8 text: {label}") from error


def locked_runtime_packages(root: Path, projects: Iterable[str]) -> list[dict[str, str]]:
    packages: dict[tuple[str, str], dict[str, str]] = {}
    for project in projects:
        lock_path = root / project / "packages.lock.json"
        assets_path = root / project / "obj/project.assets.json"
        if not lock_path.is_file():
            raise RuntimeError(f"missing production dependency lock: {lock_path.relative_to(root)}")
        if not assets_path.is_file():
            raise RuntimeError(f"restore assets missing: {assets_path.relative_to(root)}")
        lock = json.loads(lock_path.read_text(encoding="utf-8"))
        locked: dict[tuple[str, str], str] = {}
        for framework in lock["dependencies"].values():
            for name, detail in framework.items():
                version = detail.get("resolved")
                if not version:
                    continue
                content_hash = require_sha512_base64(
                    detail.get("contentHash"),
                    f"locked package content hash {name} {version}",
                )
                key = (name.casefold(), version.casefold())
                previous = locked.get(key)
                if previous is not None and previous != content_hash:
                    raise RuntimeError(f"inconsistent lock hash: {name} {version}")
                locked[key] = content_hash

        assets = json.loads(assets_path.read_text(encoding="utf-8"))
        targets = assets["targets"]
        target_name = next((name for name in targets if name.endswith("/win-x64")), None)
        if target_name is None:
            raise RuntimeError(
                f"RID-specific win-x64 restore is required: {assets_path.relative_to(root)}"
            )
        for identity, detail in targets[target_name].items():
            runtime_files = [
                path for path in detail.get("runtime", {}) if not path.endswith("/_._")
            ]
            native_files = list(detail.get("native", {}))
            content_files = list(detail.get("contentFiles", {}))
            rid_files = [
                path
                for path, metadata in detail.get("runtimeTargets", {}).items()
                if str(metadata.get("rid", "")).lower().startswith("win")
            ]
            if detail.get("type") != "package" or not (
                runtime_files or native_files or content_files or rid_files
            ):
                continue
            name, version = identity.rsplit("/", 1)
            key = (name.casefold(), version.casefold())
            content_hash = locked.get(key)
            if content_hash is None:
                raise RuntimeError(f"runtime package is not locked: {name} {version}")
            previous = packages.get(key)
            value = {"name": name, "version": version, "contentHash": content_hash}
            if previous is not None and previous != value:
                raise RuntimeError(f"runtime package lock drifted across projects: {name} {version}")
            packages[key] = value
    return sorted(
        packages.values(),
        key=lambda package: (package["name"].casefold(), package["version"]),
    )


def load_curated_evidence(root: Path) -> dict[str, dict[str, object]]:
    catalog_path = root / CATALOG_RELATIVE_PATH
    catalog = json.loads(catalog_path.read_text(encoding="utf-8"))
    if not isinstance(catalog, dict) or catalog.get("schemaVersion") != 1:
        raise RuntimeError("package license evidence catalog schema is not reviewed")
    groups = catalog.get("evidence")
    if not isinstance(groups, list) or not groups:
        raise RuntimeError("package license evidence catalog is empty")

    result: dict[str, dict[str, object]] = {}
    for group in groups:
        if not isinstance(group, dict):
            raise RuntimeError("package license evidence entry is invalid")
        repository = group.get("repository")
        revision = group.get("sourceRevision")
        upstream_path = group.get("upstreamPath")
        upstream_url = group.get("upstreamUrl")
        evidence_mode = group.get("packageRevisionEvidence")
        if (
            not isinstance(repository, str)
            or not repository.startswith("https://github.com/")
            or not isinstance(revision, str)
            or not re.fullmatch(r"[0-9a-f]{40}", revision)
            or not isinstance(upstream_path, str)
            or not upstream_path
            or not isinstance(upstream_url, str)
            or revision not in upstream_url
            or evidence_mode not in {
                "nuspec-repository-commit",
                "release-tag",
                "pinned-upstream-license",
            }
        ):
            raise RuntimeError("package license evidence source is not immutable")
        expected_upstream_url = (
            "https://raw.githubusercontent.com/"
            + repository.removeprefix("https://github.com/")
            + f"/{revision}/{upstream_path}"
        )
        if upstream_url != expected_upstream_url:
            raise RuntimeError("package license evidence URL does not match its repository")
        source_ref = group.get("sourceRef")
        if evidence_mode == "release-tag" and (
            not isinstance(source_ref, str)
            or not re.fullmatch(r"refs/tags/[A-Za-z0-9._/+^{}-]+", source_ref)
        ):
            raise RuntimeError("release-tag license evidence lacks an exact tag ref")
        if evidence_mode != "release-tag" and source_ref is not None:
            raise RuntimeError("non-tag package license evidence has an unexpected source ref")
        retained_path = group.get("retainedFile")
        retained = repository_file(root, retained_path, "curated package license")
        expected_hash = require_sha256(
            group.get("sha256"), f"curated package license {retained_path}"
        )
        if file_sha256(retained) != expected_hash:
            raise RuntimeError(f"curated package license bytes drifted: {retained_path}")
        text = _decode_legal_text(retained.read_bytes(), str(retained_path))
        package_entries = group.get("packages")
        if not isinstance(package_entries, list) or not package_entries:
            raise RuntimeError(f"curated package license has no packages: {retained_path}")
        for package in package_entries:
            if not isinstance(package, dict):
                raise RuntimeError(f"curated package identity is invalid: {retained_path}")
            name = package.get("name")
            version = package.get("version")
            license_expression = package.get("licenseExpression")
            if not all(isinstance(value, str) and value for value in (name, version, license_expression)):
                raise RuntimeError(f"curated package identity is incomplete: {retained_path}")
            key = package_key(name, version)
            if key in result:
                raise RuntimeError(f"curated package license is duplicated: {name} {version}")
            result[key] = {
                "name": Path(str(retained_path)).name,
                "sha256": expected_hash,
                "text": text,
                "legalKind": "copying" if "copying" in upstream_path.casefold() else "license",
                "source": "pinned-upstream",
                "repository": repository,
                "sourceRevision": revision,
                "upstreamPath": upstream_path,
                "upstreamUrl": upstream_url,
                "repositoryPath": retained_path,
                "packageRevisionEvidence": evidence_mode,
                "sourceRef": source_ref,
                "licenseExpression": license_expression,
            }
    return result


def _nupkg_paths(package: str, version: str, nuget_root: Path) -> tuple[Path, Path, Path]:
    root = nuget_root / package.lower() / version.lower()
    nupkg = root / f"{package.lower()}.{version.lower()}.nupkg"
    sidecar = root / f"{package.lower()}.{version.lower()}.nupkg.sha512"
    metadata = root / ".nupkg.metadata"
    if not nupkg.is_file() or not sidecar.is_file() or not metadata.is_file():
        raise RuntimeError(f"exact restored NuGet artifact is missing: {package} {version}")
    return nupkg, sidecar, metadata


def package_metadata(
    locked: dict[str, str],
    nuget_root: Path,
    curated: dict[str, dict[str, object]],
) -> tuple[dict[str, object], bool]:
    package = locked["name"]
    version = locked["version"]
    content_hash = require_sha512_base64(
        locked["contentHash"], f"locked package content hash {package} {version}"
    )
    nupkg, sidecar, metadata_path = _nupkg_paths(package, version, nuget_root)
    if not 0 < nupkg.stat().st_size <= MAX_NUGET_ARTIFACT_BYTES:
        raise RuntimeError(f"restored NuGet artifact size is invalid: {package} {version}")
    actual_artifact_sha512 = file_sha512_base64(nupkg)
    if sidecar.read_text(encoding="ascii").strip() != actual_artifact_sha512:
        raise RuntimeError(f"restored NuGet artifact digest drifted: {package} {version}")
    cache_metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
    if cache_metadata != {
        "version": 2,
        "contentHash": content_hash,
        "source": NUGET_SOURCE,
    }:
        raise RuntimeError(f"NuGet restore metadata drifted: {package} {version}")

    with zipfile.ZipFile(nupkg, "r") as archive:
        entries = [entry for entry in archive.infolist() if not entry.is_dir()]
        if not entries or len(entries) > MAX_NUGET_ENTRIES:
            raise RuntimeError(f"NuGet archive entry count is invalid: {package} {version}")
        seen: set[str] = set()
        for entry in entries:
            path = _safe_archive_path(entry.filename, "NuGet")
            folded = path.as_posix().casefold()
            if folded in seen:
                raise RuntimeError(f"duplicate NuGet archive path: {package} {version} {entry.filename}")
            seen.add(folded)
        nuspec_entries = [
            entry for entry in entries if PurePosixPath(entry.filename).suffix.casefold() == ".nuspec"
        ]
        if len(nuspec_entries) != 1:
            raise RuntimeError(f"NuGet package must contain exactly one nuspec: {package} {version}")
        if not 0 < nuspec_entries[0].file_size <= MAX_LEGAL_FILE_BYTES:
            raise RuntimeError(f"NuGet nuspec size is invalid: {package} {version}")
        nuspec_bytes = archive.read(nuspec_entries[0])
        metadata = next(
            node
            for node in ET.fromstring(nuspec_bytes).iter()
            if node.tag.rsplit("}", 1)[-1] == "metadata"
        )
        license_node = next(
            (node for node in metadata if node.tag.rsplit("}", 1)[-1] == "license"),
            None,
        )
        license_value = (license_node.text or "").strip() if license_node is not None else ""
        license_type = license_node.attrib.get("type", "") if license_node is not None else ""
        license_url = _element_text(metadata, "licenseUrl")
        legal_files: list[dict[str, object]] = []
        declared_file = license_value.casefold() if license_type == "file" else ""
        for entry in sorted(entries, key=lambda item: item.filename.casefold()):
            path = _safe_archive_path(entry.filename, "NuGet")
            basename = path.name.casefold()
            is_declared = declared_file and path.as_posix().casefold() == declared_file
            if not is_declared and not any(
                token in basename for token in ("license", "licence", "notice", "copying")
            ):
                continue
            if not 0 < entry.file_size <= MAX_LEGAL_FILE_BYTES:
                raise RuntimeError(
                    f"NuGet legal file size is invalid: {package} {version} {path}"
                )
            data = archive.read(entry)
            legal_files.append(
                {
                    "name": path.name,
                    "packagePath": path.as_posix(),
                    "sha256": hashlib.sha256(data).hexdigest(),
                    "text": _decode_legal_text(data, f"{package} {version} {path}"),
                    "legalKind": "license" if is_declared else _legal_kind(path.name),
                    "source": "locked-nupkg",
                }
            )

    license_unknown = not license_value and not license_url
    repository, repository_commit = _repository_metadata(metadata)
    key = package_key(package, version)
    needs_curated = not any(
        legal["legalKind"] in {"license", "copying"} for legal in legal_files
    )
    curated_entry = curated.get(key)
    if needs_curated:
        if curated_entry is None:
            raise RuntimeError(
                f"required runtime package lacks retained exact license evidence: {package} {version}"
            )
        if curated_entry["licenseExpression"] != (license_value or license_url):
            raise RuntimeError(f"curated package license expression drifted: {package} {version}")
        expected_repository = normalize_repository(str(curated_entry["repository"]))
        declared_repository = normalize_repository(repository)
        project_repository = normalize_repository(_element_text(metadata, "projectUrl"))
        if expected_repository not in {declared_repository, project_repository}:
            raise RuntimeError(f"curated package repository drifted: {package} {version}")
        if repository_commit and repository_commit != curated_entry["sourceRevision"]:
            raise RuntimeError(f"curated package source revision drifted: {package} {version}")
        if (
            curated_entry["packageRevisionEvidence"] == "nuspec-repository-commit"
            and repository_commit != curated_entry["sourceRevision"]
        ):
            raise RuntimeError(f"curated package lacks exact nuspec source revision: {package} {version}")
        legal_files.append(dict(curated_entry))
    elif curated_entry is not None:
        raise RuntimeError(f"stale curated package license evidence: {package} {version}")

    retained_license = ""
    if license_type == "file":
        match = next(
            (
                legal
                for legal in legal_files
                if str(legal.get("packagePath", "")).casefold() == license_value.casefold()
            ),
            None,
        )
        if match is None:
            raise RuntimeError(f"declared license file missing: {package} {version} {license_value}")
        retained_license = str(match["sha256"])
    return {
        "name": package,
        "version": version,
        "authors": _element_text(metadata, "authors"),
        "copyright": _element_text(metadata, "copyright"),
        "projectUrl": _element_text(metadata, "projectUrl"),
        "repository": repository,
        "repositoryCommit": repository_commit,
        "licenseType": "unknown" if license_unknown else (license_type or "url"),
        "license": license_value or license_url or "UNKNOWN - RELEASE BLOCKED",
        "licenseUrl": license_url,
        "retainedLicenseSha256": retained_license,
        "retainedLegalFiles": legal_files,
        "packageContentHash": content_hash,
        "packageArtifactSha256": file_sha256(nupkg),
        "packageArtifactSha512": actual_artifact_sha512,
        "packageDownloadUrl": (
            "https://api.nuget.org/v3-flatcontainer/"
            f"{package.lower()}/{version.lower()}/"
            f"{package.lower()}.{version.lower()}.nupkg"
        ),
        "nuspecSha256": hashlib.sha256(nuspec_bytes).hexdigest(),
    }, needs_curated


def exact_self_contained_runtime_packs(
    root: Path,
    nuget_root: Path,
    projects: Iterable[str],
) -> list[dict[str, object]]:
    versions = {str(expected["version"]) for expected in EXPECTED_SELF_CONTAINED_RUNTIME_PACKS}
    if len(versions) != 1:
        raise RuntimeError("self-contained runtime pack versions are inconsistent")
    runtime_version = next(iter(versions))
    props = ET.parse(root / "Directory.Build.props").getroot()
    configured_versions = [
        (node.text or "").strip()
        for node in props.iter()
        if node.tag.rsplit("}", 1)[-1] == "RuntimeFrameworkVersion"
    ]
    if configured_versions != [runtime_version]:
        raise RuntimeError("Directory.Build.props self-contained runtime version drifted")
    required_downloads = {
        "Microsoft.NETCore.App.Host.win-x64",
        "Microsoft.NETCore.App.Runtime.win-x64",
    }
    for project in projects:
        assets = json.loads((root / project / "obj/project.assets.json").read_text())
        frameworks = assets.get("project", {}).get("frameworks", {})
        downloads = {
            dependency["name"]: dependency["version"]
            for framework in frameworks.values()
            for dependency in framework.get("downloadDependencies", [])
        }
        for name in required_downloads:
            if downloads.get(name) != f"[{runtime_version}, {runtime_version}]":
                raise RuntimeError(f"self-contained runtime download drifted: {project} {name}")
        if project == "src/SuavoAgent.Helper" and downloads.get(
            "Microsoft.WindowsDesktop.App.Runtime.win-x64"
        ) != f"[{runtime_version}, {runtime_version}]":
            raise RuntimeError("Helper WindowsDesktop runtime download drifted")
    result: list[dict[str, object]] = []
    for expected in EXPECTED_SELF_CONTAINED_RUNTIME_PACKS:
        package, needs_curated = package_metadata(
            {
                "name": expected["name"],
                "version": expected["version"],
                "contentHash": expected["contentHash"],
            },
            nuget_root,
            {},
        )
        if needs_curated:
            raise RuntimeError(f"runtime pack license is not self-contained: {expected['name']}")
        for field, actual_field in (
            ("artifactSha256", "packageArtifactSha256"),
            ("artifactSha512", "packageArtifactSha512"),
            ("nuspecSha256", "nuspecSha256"),
            ("repository", "repository"),
            ("repositoryCommit", "repositoryCommit"),
        ):
            if package[actual_field] != expected[field]:
                raise RuntimeError(f"reviewed runtime pack {field} drifted: {expected['name']}")
        legal = {
            entry["packagePath"]: entry["sha256"]
            for entry in package["retainedLegalFiles"]
        }
        if legal != expected["legal"]:
            raise RuntimeError(f"reviewed runtime pack legal cohort drifted: {expected['name']}")
        package["componentRole"] = expected["role"]
        result.append(package)
    return result


def validate_curated_coverage(
    packages: list[dict[str, object]],
    curated: dict[str, dict[str, object]],
) -> None:
    used = {
        package_key(str(package["name"]), str(package["version"]))
        for package in packages
        if any(
            legal.get("source") == "pinned-upstream"
            for legal in package["retainedLegalFiles"]
        )
    }
    if used != set(curated):
        missing = sorted(set(curated) - used)
        unexpected = sorted(used - set(curated))
        raise RuntimeError(
            "curated package license cohort drifted"
            + (f"; unused: {', '.join(missing)}" if missing else "")
            + (f"; undeclared: {', '.join(unexpected)}" if unexpected else "")
        )


def release_eligibility_blockers(
    provenance: dict[str, object], requested_features: set[str]
) -> dict[str, list[str]]:
    package_blockers: list[str] = []
    for package in [*provenance["packages"], *provenance.get("runtimePacks", [])]:
        retained = package.get("retainedLegalFiles", [])
        has_license = any(
            legal.get("legalKind") in {"license", "copying"}
            and isinstance(legal.get("sha256"), str)
            and re.fullmatch(r"[0-9a-f]{64}", legal["sha256"])
            and legal.get("source") in {"locked-nupkg", "pinned-upstream"}
            for legal in retained
            if isinstance(legal, dict)
        )
        if package.get("licenseType") == "unknown" or not has_license:
            package_blockers.append(f"{package['name']} {package['version']}")
    assets = provenance["externalAssets"]
    known_assets = {asset["id"] for asset in assets}
    unknown_features = sorted(requested_features - known_assets)
    blocked_assets = [
        asset["id"]
        for asset in assets
        if not asset["releaseEligible"]
        and (asset["requiredForBaseRelease"] or asset["id"] in requested_features)
    ]
    return {
        "packages": sorted(package_blockers, key=str.casefold),
        "unknownFeatures": unknown_features,
        "blockedAssets": sorted(blocked_assets),
    }
