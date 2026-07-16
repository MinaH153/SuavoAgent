#!/usr/bin/env python3
"""Re-prove official external brain metadata before a production release."""

from __future__ import annotations

import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
import zipfile
from typing import Any
from urllib.request import Request, urlopen


ROOT = Path(__file__).resolve().parents[1]
QWEN_EVIDENCE = ROOT / "legal/evidence/qwen3-1.7b-q4-k-m.json"
NATIVE_EVIDENCE = ROOT / "legal/evidence/llamasharp-backend-cpu-0.24.0.json"
TESSERACT_EVIDENCE = ROOT / "legal/evidence/tesseract-native-5.2.0-eng.json"
USER_AGENT = "SuavoAgent-release-verifier/1.0 (+https://suavollc.com)"
MAX_METADATA_BYTES = 4 * 1024 * 1024
MAX_NATIVE_PACKAGE_BYTES = 64 * 1024 * 1024
MAX_LICENSE_BYTES = 128 * 1024


def require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


def load_json(path: Path) -> dict[str, Any]:
    value = json.loads(path.read_text(encoding="utf-8"))
    require(isinstance(value, dict), f"evidence is not an object: {path.name}")
    return value


def fetch_json(url: str) -> dict[str, Any]:
    request = Request(url, headers={"User-Agent": USER_AGENT, "Accept": "application/json"})
    with urlopen(request, timeout=30) as response:
        declared = response.headers.get("Content-Length")
        if declared is not None:
            require(0 < int(declared) <= MAX_METADATA_BYTES, "metadata response size is invalid")
        payload = response.read(MAX_METADATA_BYTES + 1)
    require(0 < len(payload) <= MAX_METADATA_BYTES, "metadata response exceeded its bound")
    value = json.loads(payload)
    require(isinstance(value, dict), "metadata response is not an object")
    return value


def fetch_bounded_bytes(url: str) -> bytes:
    request = Request(url, headers={"User-Agent": USER_AGENT})
    with urlopen(request, timeout=30) as response:
        declared = response.headers.get("Content-Length")
        if declared is not None:
            require(0 < int(declared) <= MAX_LICENSE_BYTES, "license response size is invalid")
        payload = response.read(MAX_LICENSE_BYTES + 1)
    require(0 < len(payload) <= MAX_LICENSE_BYTES, "license response exceeded its bound")
    return payload


def validate_qwen_metadata(metadata: dict[str, Any], evidence: dict[str, Any]) -> None:
    require(metadata.get("id") == "Qwen/Qwen3-1.7B-GGUF", "Qwen publisher identity drifted")
    require(metadata.get("sha") == evidence["sourceRevision"], "Qwen revision drifted")
    require(metadata.get("private") is False, "Qwen repository became private")
    require(metadata.get("gated") is False, "Qwen repository became gated")
    card = metadata.get("cardData")
    require(isinstance(card, dict), "Qwen card metadata is missing")
    require(str(card.get("license", "")).casefold() == "apache-2.0", "Qwen license drifted")
    matches = [
        item for item in metadata.get("siblings", [])
        if isinstance(item, dict) and item.get("rfilename") == evidence["artifactPath"]
    ]
    require(len(matches) == 1, "exact Qwen artifact is missing or ambiguous")
    artifact = matches[0]
    lfs = artifact.get("lfs")
    require(isinstance(lfs, dict), "Qwen LFS evidence is missing")
    require(artifact.get("size") == evidence["artifactSizeBytes"], "Qwen artifact size drifted")
    require(lfs.get("size") == evidence["artifactSizeBytes"], "Qwen LFS size drifted")
    require(lfs.get("sha256") == evidence["artifactSha256"], "Qwen LFS digest drifted")
    require(artifact.get("blobId") == evidence["blobId"], "Qwen Git blob identity drifted")


def download_exact(url: str, destination: Path, size: int, sha256: str) -> None:
    require(0 < size <= MAX_NATIVE_PACKAGE_BYTES, "native package expected size is invalid")
    request = Request(url, headers={"User-Agent": USER_AGENT})
    digest = hashlib.sha256()
    written = 0
    with urlopen(request, timeout=60) as response, destination.open("xb") as output:
        declared = response.headers.get("Content-Length")
        if declared is not None:
            require(int(declared) == size, "native package Content-Length drifted")
        while True:
            chunk = response.read(1024 * 1024)
            if not chunk:
                break
            require(written <= size - len(chunk), "native package exceeded its signed size")
            output.write(chunk)
            digest.update(chunk)
            written += len(chunk)
    require(written == size, "native package size drifted")
    require(digest.hexdigest() == sha256, "native package digest drifted")


def validate_nuget_verification_output(output: str, evidence: dict[str, Any]) -> None:
    markers = (
        f"Verifying {evidence['packageId']}.{evidence['packageVersion']}",
        f"Signature type: {evidence['signatureType']}",
        f"Service index: {evidence['serviceIndex']}",
        "Owners: " + ",".join(evidence["owners"]),
        "SHA256 hash: " + evidence["repositoryCertificateSha256"].upper(),
        f"Successfully verified package '{evidence['packageId']}.{evidence['packageVersion']}'.",
    )
    for marker in markers:
        require(marker in output, f"NuGet repository-signature proof missing: {marker}")


def verify_nuget_signature(package: Path, evidence: dict[str, Any]) -> None:
    with zipfile.ZipFile(package) as archive:
        signatures = [name for name in archive.namelist() if name == ".signature.p7s"]
        require(len(signatures) == 1, "NuGet embedded repository signature is missing or ambiguous")
        embedded = archive.read(signatures[0])
    require(
        hashlib.sha256(embedded).hexdigest() == evidence["embeddedSignatureSha256"],
        "NuGet embedded repository-signature digest drifted",
    )
    result = subprocess.run(
        ["dotnet", "nuget", "verify", str(package), "--all", "--verbosity", "detailed"],
        cwd=ROOT,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        check=False,
    )
    require(result.returncode == 0, "NuGet repository-signature verification failed")
    validate_nuget_verification_output(result.stdout, evidence)


def validate_tesseract_catalog(
    metadata: dict[str, Any],
    evidence: dict[str, Any],
) -> None:
    require(metadata.get("id") == evidence["packageId"], "Tesseract package identity drifted")
    require(metadata.get("version") == evidence["packageVersion"], "Tesseract version drifted")
    require(metadata.get("licenseExpression") == "Apache-2.0", "Tesseract package license drifted")
    require(metadata.get("packageSize") == evidence["packageSizeBytes"], "Tesseract package size drifted")
    require(
        metadata.get("projectUrl") == "https://github.com/charlesw/tesseract/",
        "Tesseract publisher URL drifted",
    )
    entries = {
        entry.get("fullName"): entry.get("length")
        for entry in metadata.get("packageEntries", [])
        if isinstance(entry, dict)
    }
    require(
        entries.get("x64/leptonica-1.82.0.dll") == 4_168_192,
        "Tesseract Leptonica package entry drifted",
    )
    require(
        entries.get("x64/tesseract50.dll") == 2_788_352,
        "Tesseract native package entry drifted",
    )


def validate_tesseract_package(package: Path, evidence: dict[str, Any]) -> None:
    with zipfile.ZipFile(package) as archive:
        names = archive.namelist()
        require(len(names) == len(set(names)), "Tesseract package contains duplicate entries")
        for expected in evidence["nativeFiles"]:
            path = expected["path"]
            require(names.count(path) == 1, f"Tesseract runtime file missing: {path}")
            info = archive.getinfo(path)
            require(info.file_size == expected["sizeBytes"], f"Tesseract runtime size drifted: {path}")
            require(
                hashlib.sha256(archive.read(path)).hexdigest() == expected["sha256"],
                f"Tesseract runtime digest drifted: {path}",
            )


def validate_tesseract_licenses(
    evidence: dict[str, Any],
    fetcher: Any = fetch_bounded_bytes,
) -> None:
    bundle = hashlib.sha256()
    for entry in evidence["licenseFiles"]:
        retained = (ROOT / entry["path"]).read_bytes()
        require(hashlib.sha256(retained).hexdigest() == entry["sha256"], "OCR retained license drifted")
        upstream = fetcher(entry["source"])
        if "upstreamSha256" in entry:
            require(
                hashlib.sha256(upstream).hexdigest() == entry["upstreamSha256"],
                "OCR upstream license drifted",
            )
            require(
                retained.rstrip(b"\r\n") == upstream.rstrip(b"\r\n"),
                "OCR retained license differs beyond trailing blank lines",
            )
        else:
            require(upstream == retained, "OCR retained license bytes differ from upstream")
        bundle.update(retained)
    require(
        bundle.hexdigest() == evidence["licenseBundleSha256"],
        "OCR license bundle digest drifted",
    )


def validate_external_license_evidence(
    catalog: dict[str, Any],
    native: dict[str, Any],
    fetcher: Any = fetch_bounded_bytes,
) -> None:
    assets = catalog.get("assets")
    require(isinstance(assets, list), "external asset catalog is missing")
    matches = [
        asset for asset in assets
        if isinstance(asset, dict)
        and asset.get("id") == "llamasharp-backend-cpu-0.24.0"
    ]
    require(len(matches) == 1, "LLamaSharp backend legal evidence is missing or ambiguous")
    licenses = matches[0].get("licenseFiles")
    require(isinstance(licenses, list) and len(licenses) == 2, "native brain license cohort drifted")
    require(
        [entry.get("sourceRevision") for entry in licenses] == [
            native["sourceRevision"],
            native["llamaCppRevision"],
        ],
        "native brain license revisions drifted",
    )
    bundle = hashlib.sha256()
    for entry in licenses:
        require(isinstance(entry, dict), "native brain license entry is invalid")
        revision = entry.get("sourceRevision")
        source = entry.get("source")
        expected = entry.get("sha256")
        relative = entry.get("path")
        require(
            isinstance(revision, str)
            and len(revision) == 40
            and isinstance(source, str)
            and source.startswith("https://raw.githubusercontent.com/")
            and revision in source,
            "native brain license source is not immutable",
        )
        require(
            isinstance(expected, str) and len(expected) == 64,
            "native brain license digest is invalid",
        )
        require(isinstance(relative, str), "native brain retained license path is invalid")
        path = (ROOT / relative).resolve()
        try:
            path.relative_to(ROOT.resolve())
        except ValueError as error:
            raise RuntimeError("native brain retained license path escapes the repository") from error
        require(path.is_file(), "native brain retained license is missing")
        local = path.read_bytes()
        require(hashlib.sha256(local).hexdigest() == expected, "retained license digest drifted")
        upstream = fetcher(source)
        require(hashlib.sha256(upstream).hexdigest() == expected, "upstream license digest drifted")
        require(upstream == local, "retained license bytes differ from pinned upstream")
        bundle.update(local)
    require(
        bundle.hexdigest() == matches[0].get("licenseBundleSha256"),
        "native brain license bundle digest drifted",
    )


def main() -> int:
    qwen = load_json(QWEN_EVIDENCE)
    native = load_json(NATIVE_EVIDENCE)
    tesseract = load_json(TESSERACT_EVIDENCE)
    catalog = load_json(ROOT / "legal/external-assets.json")
    validate_qwen_metadata(fetch_json(qwen["apiUrl"]), qwen)
    validate_external_license_evidence(catalog, native)
    validate_tesseract_catalog(fetch_json(tesseract["packageCatalogUrl"]), tesseract)
    validate_tesseract_licenses(tesseract)
    with tempfile.TemporaryDirectory(prefix="suavo-external-proof-") as temporary:
        package = Path(temporary) / "llamasharp.backend.cpu.0.24.0.nupkg"
        download_exact(
            native["artifactUrl"],
            package,
            native["artifactSizeBytes"],
            native["artifactSha256"],
        )
        verify_nuget_signature(package, native)
        tesseract_package = Path(temporary) / "tesseract.5.2.0.nupkg"
        download_exact(
            tesseract["packageUrl"],
            tesseract_package,
            tesseract["packageSizeBytes"],
            tesseract["packageSha256"],
        )
        verify_nuget_signature(tesseract_package, tesseract)
        validate_tesseract_package(tesseract_package, tesseract)
        trained_data = tesseract["trainedData"]
        download_exact(
            trained_data["artifactUrl"],
            Path(temporary) / trained_data["artifactPath"],
            trained_data["artifactSizeBytes"],
            trained_data["artifactSha256"],
        )
    print(
        "Official Qwen metadata, LLamaSharp native backend, and Tesseract OCR cohort verified."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
