from __future__ import annotations

import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
RESOLVER = REPOSITORY_ROOT / "scripts" / "resolve-release-rollback-evidence.py"


class RollbackEvidenceResolverTests(unittest.TestCase):
    def setUp(self):
        self._temporary = tempfile.TemporaryDirectory()
        self.root = Path(self._temporary.name)

    def tearDown(self):
        self._temporary.cleanup()

    def write_evidence(
        self,
        tag: str,
        artifact: str,
        *,
        receipt_artifact_sha256: str | None = None,
        manifest_artifact_sha256: str | None = None,
    ) -> tuple[Path, Path, str]:
        artifact_sha256 = receipt_artifact_sha256 or ("a" * 64)
        receipt = {
            "releaseTag": tag,
            "version": tag.removeprefix("v"),
            "sourceCommit": "b" * 40,
            "artifact": artifact,
            "artifactSha256": artifact_sha256,
            "authenticode": "required-valid",
            "checksumSignature": "checksums.sha256.sig",
            "manifestSignature": f"update-manifest-{tag}.sig",
        }
        receipt_path = self.root / "field-release-receipt.json"
        receipt_path.write_text(
            json.dumps(receipt, indent=2, sort_keys=True) + "\n",
            encoding="utf-8",
        )
        receipt_sha256 = hashlib.sha256(receipt_path.read_bytes()).hexdigest()
        checksums_path = self.root / "checksums.sha256"
        checksums_path.write_text(
            f"{manifest_artifact_sha256 or artifact_sha256}  {artifact}\n"
            f"{receipt_sha256}  field-release-receipt.json\n",
            encoding="utf-8",
        )
        return checksums_path, receipt_path, artifact_sha256

    def run_resolver(
        self,
        tag: str,
        checksums_path: Path,
        receipt_path: Path,
    ) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [
                sys.executable,
                str(RESOLVER),
                "--tag",
                tag,
                "--checksums",
                str(checksums_path),
                "--receipt",
                str(receipt_path),
            ],
            cwd=REPOSITORY_ROOT,
            text=True,
            capture_output=True,
            check=False,
        )

    def test_accepts_v3921_legacy_zip_from_its_signed_receipt(self):
        tag = "v3.92.1"
        artifact = f"suavoagent-{tag}-win-x64.zip"
        checksums, receipt, artifact_sha256 = self.write_evidence(tag, artifact)

        result = self.run_resolver(tag, checksums, receipt)

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([artifact, artifact_sha256], result.stdout.splitlines())

    def test_accepts_future_native_burn_bundle_from_its_signed_receipt(self):
        tag = "v3.93.0"
        artifact = f"SuavoAgent-Setup-{tag}-win-x64.exe"
        checksums, receipt, artifact_sha256 = self.write_evidence(tag, artifact)

        result = self.run_resolver(tag, checksums, receipt)

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([artifact, artifact_sha256], result.stdout.splitlines())

    def test_rejects_receipt_not_bound_by_signed_checksums(self):
        tag = "v3.92.1"
        artifact = f"suavoagent-{tag}-win-x64.zip"
        checksums, receipt, _ = self.write_evidence(tag, artifact)
        receipt.write_text(receipt.read_text(encoding="utf-8") + " ", encoding="utf-8")

        result = self.run_resolver(tag, checksums, receipt)

        self.assertNotEqual(0, result.returncode)
        self.assertIn("not bound by the signed checksum manifest", result.stderr)

    def test_rejects_receipt_digest_that_disagrees_with_signed_artifact_digest(self):
        tag = "v3.92.1"
        artifact = f"suavoagent-{tag}-win-x64.zip"
        checksums, receipt, _ = self.write_evidence(
            tag,
            artifact,
            receipt_artifact_sha256="a" * 64,
            manifest_artifact_sha256="c" * 64,
        )

        result = self.run_resolver(tag, checksums, receipt)

        self.assertNotEqual(0, result.returncode)
        self.assertIn("artifact digest does not match", result.stderr)

    def test_rejects_signed_receipt_with_unapproved_artifact_name(self):
        tag = "v3.92.1"
        artifact = "../../unsigned.exe"
        checksums, receipt, _ = self.write_evidence(tag, artifact)
        # Keep the checksum grammar valid so rejection is specifically at the
        # approved legacy ZIP / native Burn transition boundary.
        checksums.write_text(
            checksums.read_text(encoding="utf-8").replace(artifact, "unsigned.exe"),
            encoding="utf-8",
        )
        receipt_sha256 = hashlib.sha256(receipt.read_bytes()).hexdigest()
        lines = checksums.read_text(encoding="utf-8").splitlines()
        lines[-1] = f"{receipt_sha256}  field-release-receipt.json"
        checksums.write_text("\n".join(lines) + "\n", encoding="utf-8")

        result = self.run_resolver(tag, checksums, receipt)

        self.assertNotEqual(0, result.returncode)
        self.assertIn("artifact name is outside the approved transition", result.stderr)


if __name__ == "__main__":
    unittest.main()
