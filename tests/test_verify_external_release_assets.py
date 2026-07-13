from __future__ import annotations

import importlib.util
from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location(
    "verify_external_release_assets",
    ROOT / "scripts/verify-external-release-assets.py",
)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class ExternalReleaseAssetVerifierTests(unittest.TestCase):
    def setUp(self) -> None:
        self.qwen = MODULE.load_json(MODULE.QWEN_EVIDENCE)
        self.native = MODULE.load_json(MODULE.NATIVE_EVIDENCE)
        self.tesseract = MODULE.load_json(MODULE.TESSERACT_EVIDENCE)
        self.catalog = MODULE.load_json(ROOT / "legal/external-assets.json")

    def qwen_metadata(self) -> dict[str, object]:
        return {
            "id": "Qwen/Qwen3-1.7B-GGUF",
            "sha": self.qwen["sourceRevision"],
            "private": False,
            "gated": False,
            "cardData": {"license": "apache-2.0"},
            "siblings": [{
                "rfilename": self.qwen["artifactPath"],
                "size": self.qwen["artifactSizeBytes"],
                "blobId": self.qwen["blobId"],
                "lfs": {
                    "sha256": self.qwen["artifactSha256"],
                    "size": self.qwen["artifactSizeBytes"],
                },
            }],
        }

    def test_exact_official_qwen_metadata_is_accepted(self) -> None:
        MODULE.validate_qwen_metadata(self.qwen_metadata(), self.qwen)

    def test_qwen_digest_drift_is_rejected(self) -> None:
        original = self.qwen_metadata()
        sibling = original["siblings"][0]
        metadata = {
            **original,
            "siblings": [{
                **sibling,
                "lfs": {**sibling["lfs"], "sha256": "0" * 64},
            }],
        }
        with self.assertRaisesRegex(RuntimeError, "digest drifted"):
            MODULE.validate_qwen_metadata(metadata, self.qwen)

    def test_exact_nuget_repository_signature_output_is_accepted(self) -> None:
        output = "\n".join((
            f"Verifying {self.native['packageId']}.{self.native['packageVersion']}",
            f"Signature type: {self.native['signatureType']}",
            f"Service index: {self.native['serviceIndex']}",
            "Owners: " + ",".join(self.native["owners"]),
            "SHA256 hash: " + self.native["repositoryCertificateSha256"].upper(),
            f"Successfully verified package '{self.native['packageId']}.{self.native['packageVersion']}'.",
        ))
        MODULE.validate_nuget_verification_output(output, self.native)

    def test_wrong_nuget_repository_certificate_is_rejected(self) -> None:
        output = "\n".join((
            f"Verifying {self.native['packageId']}.{self.native['packageVersion']}",
            f"Signature type: {self.native['signatureType']}",
            f"Service index: {self.native['serviceIndex']}",
            "Owners: " + ",".join(self.native["owners"]),
            "SHA256 hash: " + ("0" * 64),
            f"Successfully verified package '{self.native['packageId']}.{self.native['packageVersion']}'.",
        ))
        with self.assertRaisesRegex(RuntimeError, "repository-signature proof missing"):
            MODULE.validate_nuget_verification_output(output, self.native)

    def test_exact_pinned_native_license_bytes_are_accepted(self) -> None:
        by_source = {
            entry["source"]: (ROOT / entry["path"]).read_bytes()
            for asset in self.catalog["assets"]
            if asset["id"] == "llamasharp-backend-cpu-0.24.0"
            for entry in asset["licenseFiles"]
        }
        MODULE.validate_external_license_evidence(
            self.catalog,
            self.native,
            lambda source: by_source[source],
        )

    def test_upstream_native_license_drift_is_rejected(self) -> None:
        with self.assertRaisesRegex(RuntimeError, "upstream license digest drifted"):
            MODULE.validate_external_license_evidence(
                self.catalog,
                self.native,
                lambda _: b"tampered license",
            )

    def test_exact_tesseract_catalog_is_accepted(self) -> None:
        metadata = {
            "id": "Tesseract",
            "version": "5.2.0",
            "licenseExpression": "Apache-2.0",
            "packageSize": self.tesseract["packageSizeBytes"],
            "projectUrl": "https://github.com/charlesw/tesseract/",
            "packageEntries": [
                {"fullName": item["path"], "length": item["sizeBytes"]}
                for item in self.tesseract["nativeFiles"]
            ],
        }
        MODULE.validate_tesseract_catalog(metadata, self.tesseract)

    def test_tesseract_catalog_native_size_drift_is_rejected(self) -> None:
        metadata = {
            "id": "Tesseract",
            "version": "5.2.0",
            "licenseExpression": "Apache-2.0",
            "packageSize": self.tesseract["packageSizeBytes"],
            "projectUrl": "https://github.com/charlesw/tesseract/",
            "packageEntries": [
                {"fullName": "x64/leptonica-1.82.0.dll", "length": 1},
                {"fullName": "x64/tesseract50.dll", "length": 2_788_352},
            ],
        }
        with self.assertRaisesRegex(RuntimeError, "Leptonica package entry drifted"):
            MODULE.validate_tesseract_catalog(metadata, self.tesseract)

    def test_exact_tesseract_license_bytes_are_accepted(self) -> None:
        by_source = {}
        for entry in self.tesseract["licenseFiles"]:
            retained = (ROOT / entry["path"]).read_bytes()
            by_source[entry["source"]] = (
                retained + b"\n" if "upstreamSha256" in entry else retained
            )
        MODULE.validate_tesseract_licenses(
            self.tesseract,
            lambda source: by_source[source],
        )


if __name__ == "__main__":
    unittest.main()
