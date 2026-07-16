from __future__ import annotations

import base64
from contextlib import redirect_stderr
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
SCRIPTS = REPOSITORY_ROOT / "scripts"
RESOLVER_PATH = SCRIPTS / "resolve-release-rollback-evidence.py"
sys.path.insert(0, str(SCRIPTS))
SPEC = importlib.util.spec_from_file_location("rollback_evidence_resolver", RESOLVER_PATH)
assert SPEC is not None and SPEC.loader is not None
RESOLVER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(RESOLVER)


class RollbackEvidenceResolverTests(unittest.TestCase):
    LIVE_V3921_RECEIPT_B64 = (
        "ewogICJhcnRpZmFjdCI6ICJzdWF2b2FnZW50LXYzLjkyLjEtd2luLXg2NC56aXAiLAogICJhcnRp"
        "ZmFjdFNoYTI1NiI6ICJhMjViNzI3NDgxNGViNjExNjM0YzgxNmY0NzY0ZTM3ODMyNGJiNGQ4MDYy"
        "OGFhNWMyNThjMzlmMzMyZmJkYzJlIiwKICAiYXV0aGVudGljb2RlIjogInJlcXVpcmVkLXZhbGlk"
        "IiwKICAiY2hlY2tzdW1TaWduYXR1cmUiOiAiY2hlY2tzdW1zLnNoYTI1Ni5zaWciLAogICJtYW5p"
        "ZmVzdFNpZ25hdHVyZSI6ICJ1cGRhdGUtbWFuaWZlc3QtdjMuOTIuMS5zaWciLAogICJyZWxlYXNl"
        "VGFnIjogInYzLjkyLjEiLAogICJyb2xsYmFja0FydGlmYWN0IjogewogICAgImFydGlmYWN0Ijog"
        "InN1YXZvYWdlbnQtdjMuOTIuMC13aW4teDY0LnppcCIsCiAgICAiYXJ0aWZhY3RTaGEyNTYiOiAi"
        "NDY4MzJmOTUyNTMwNDNkNzYyODRkNmIxYjc5NDA1ZmM5NzI4NDlkNzMwZTcyNGE1MTNhNTk3MmIx"
        "NzFjYmYwOCIsCiAgICAicmVsZWFzZVRhZyI6ICJ2My45Mi4wIiwKICAgICJyZWxlYXNlVXJsIjog"
        "Imh0dHBzOi8vZ2l0aHViLmNvbS9NaW5hSDE1My9TdWF2b0FnZW50L3JlbGVhc2VzL2Rvd25sb2Fk"
        "L3YzLjkyLjAvc3Vhdm9hZ2VudC12My45Mi4wLXdpbi14NjQuemlwIgogIH0sCiAgInNvdXJjZUNv"
        "bW1pdCI6ICIyMWNiMTY1NDNiN2UwZWYyMDQ4MTgwOGRiZTA1NDM0ZmRlN2FiZTdjIiwKICAidHJh"
        "Y2syUXVlZW5WYWxpZGF0aW9uIjogImRvLW5vdC1ydW4tYWdhaW5zdC1vbGRlci10YWdzIiwKICAi"
        "dmVyc2lvbiI6ICIzLjkyLjEiCn0K"
    )
    LIVE_V3921_CHECKSUMS_B64 = (
        "OTZkZTEzZWI1MjFkNTkxYWNmYTM4ODIyNTQ5MGFkY2U3Mzk4OWEyNzAwZDVmN2Q3NTRlYWYzMzNm"
        "YmRjNzdjMyAgU3Vhdm9BZ2VudC5Db3JlLmV4ZQoyYzRiM2Y0YzI2NTBmMGRmMTI2Yjc3ZWY5YzFj"
        "NDE4ZjNlM2VlYzJkNmY5Mzk4OWMyYTNlMTc2M2RmZGEzMDBlICBTdWF2b0FnZW50LkJyb2tlci5l"
        "eGUKNmJlYjk2YWI2ZTQ3NTE0NWQ1ZDhlMmIwM2Q4ZDU2OGNkNTc4MWFmYWVhMjhkZTY3MTZiN2Vh"
        "NjQ0YjA4YmRmZSAgU3Vhdm9BZ2VudC5IZWxwZXIuZXhlCmIwNjJjZWVkZmE2ZDUxZTBjZGRiMDQ3"
        "MTE3M2Y0YmFhNWVmY2Y1YmJmZTk0ZjJjOTY3NjM4ZmQ1MzM4MTc3MjEgIFN1YXZvQWdlbnQuV2F0"
        "Y2hkb2cuZXhlCmU1Y2EwNWNiOTA1MzM5NWY1YjIwOGM2YmU3MDIyN2RjN2M3MmExZGE1NTZkODQ4"
        "NDJmN2M2ZjUzYTJkNjdkNGMgIFN1YXZvU2V0dXAuZXhlCmEyNWI3Mjc0ODE0ZWI2MTE2MzRjODE2"
        "ZjQ3NjRlMzc4MzI0YmI0ZDgwNjI4YWE1YzI1OGMzOWYzMzJmYmRjMmUgIHN1YXZvYWdlbnQtdjMu"
        "OTIuMS13aW4teDY0LnppcAo2MWFmZDlkYmUyMGE4OTAyYTRmYjQ2ZWE1NWMwM2JmZTlhM2Q4MGFm"
        "NDU0MGU3ZWU3Nzk5ODk1NmEyYjVlYmRmICBmaWVsZC1yZWxlYXNlLXJlY2VpcHQuanNvbgo="
    )
    LIVE_V3921_SIGNATURE_B64 = (
        "MEUCID0oWtA/fiuYXLOuykBCRHM56dcwbX5kTXEK75mMvSoTAiEA7VFSvnbhk8pMB9GuRf/Jr5dy"
        "bGLb80EeIu/hwe2pXrI="
    )

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
        ota_signing_key_id: str | None = "ota-update-v1",
        source_commit: str = "b" * 40,
        receipt_artifact_sha256: str | None = None,
        manifest_artifact_sha256: str | None = None,
    ) -> tuple[Path, Path, Path, str, str]:
        artifact_sha256 = receipt_artifact_sha256 or ("a" * 64)
        receipt = {
            "releaseTag": tag,
            "version": tag.removeprefix("v"),
            "sourceCommit": source_commit,
            "artifact": artifact,
            "artifactSha256": artifact_sha256,
            "authenticode": "required-valid",
            "checksumSignature": "checksums.sha256.sig",
            "manifestSignature": f"update-manifest-{tag}.sig",
        } | (
            {"otaSigningKeyId": ota_signing_key_id}
            if ota_signing_key_id is not None
            else {}
        )
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
        signature_path = self.root / "checksums.sha256.sig"
        signature_path.write_bytes(b"test-signature")
        return checksums_path, signature_path, receipt_path, artifact_sha256, receipt_sha256

    def resolve(
        self,
        tag: str,
        checksums: Path,
        signature: Path,
        receipt: Path,
        *,
        signed_by: str = "ota-update-v1",
        expected_key_id: str | None = "ota-update-v1",
        bridge_bindings: tuple[str, str, str] | None = None,
        allow_missing_legacy_key_id: bool = False,
    ) -> tuple[int, tuple[str, ...], str]:
        stderr = io.StringIO()
        bridge_tag, bridge_source, bridge_receipt = bridge_bindings or (None, None, None)
        try:
            with redirect_stderr(stderr):
                artifact, digest = RESOLVER.resolve(
                    tag,
                    checksums,
                    receipt,
                    signature_path=signature,
                    expected_ota_signing_key_id=(
                        None if bridge_bindings is not None else expected_key_id
                    ),
                    bridge_release_tag=bridge_tag,
                    bridge_source_sha=bridge_source,
                    bridge_receipt_sha256=bridge_receipt,
                    allow_missing_legacy_ota_signing_key_id=(
                        allow_missing_legacy_key_id
                    ),
                    signature_verifier=lambda key_id, _payload, _signature: (
                        key_id == signed_by
                    ),
                )
            return 0, (artifact, digest), stderr.getvalue()
        except SystemExit as error:
            return int(error.code), (), stderr.getvalue()

    def test_explicit_bridge_stage_accepts_live_v3921_legacy_receipt_only_under_v1(self):
        receipt = self.root / "field-release-receipt.json"
        checksums = self.root / "checksums.sha256"
        signature = self.root / "checksums.sha256.sig"
        receipt.write_bytes(base64.b64decode(self.LIVE_V3921_RECEIPT_B64))
        checksums.write_bytes(base64.b64decode(self.LIVE_V3921_CHECKSUMS_B64))
        signature.write_bytes(base64.b64decode(self.LIVE_V3921_SIGNATURE_B64))

        result = subprocess.run(
            [
                sys.executable,
                str(RESOLVER_PATH),
                "--tag", "v3.92.1",
                "--checksums", str(checksums),
                "--signature", str(signature),
                "--receipt", str(receipt),
                "--expected-ota-signing-key-id", "ota-update-v1",
                "--allow-missing-legacy-ota-signing-key-id",
            ],
            cwd=REPOSITORY_ROOT,
            text=True,
            capture_output=True,
            check=False,
        )

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(
            ["suavoagent-v3.92.1-win-x64.zip", "a25b7274814eb611634c816f4764e378324bb4d80628aa5c258c39f332fbdc2e"],
            result.stdout.splitlines(),
        )

    def test_accepts_declared_v2_native_bundle_signed_by_v2(self):
        tag = "v4.1.0"
        artifact = "SuavoAgent-Setup.exe"
        checksums, signature, receipt, digest, _ = self.write_evidence(
            tag,
            artifact,
            ota_signing_key_id="ota-update-v2",
        )
        bridge = ("v4.0.0", "c" * 40, "d" * 64)

        code, output, error = self.resolve(
            tag,
            checksums,
            signature,
            receipt,
            signed_by="ota-update-v2",
            bridge_bindings=bridge,
        )

        self.assertEqual(0, code, error)
        self.assertEqual((artifact, digest), output)

    def test_v1_is_allowed_only_for_exact_claim_bound_release_1(self):
        tag = "v4.0.0"
        source = "c" * 40
        checksums, signature, receipt, digest, receipt_sha = self.write_evidence(
            tag,
            "SuavoAgent-Setup.exe",
            source_commit=source,
        )

        accepted = self.resolve(
            tag,
            checksums,
            signature,
            receipt,
            bridge_bindings=(tag, source, receipt_sha),
        )
        rejected = self.resolve(
            tag,
            checksums,
            signature,
            receipt,
            bridge_bindings=(tag, source, "d" * 64),
        )

        self.assertEqual((0, ("SuavoAgent-Setup.exe", digest), ""), accepted)
        self.assertNotEqual(0, rejected[0])
        self.assertIn("policy requires ota-update-v2", rejected[2])

    def test_wrong_actual_root_is_rejected_even_when_receipt_declares_policy_root(self):
        tag = "v4.1.0"
        checksums, signature, receipt, _, _ = self.write_evidence(
            tag,
            "SuavoAgent-Setup.exe",
            ota_signing_key_id="ota-update-v2",
        )

        code, _, error = self.resolve(
            tag,
            checksums,
            signature,
            receipt,
            signed_by="ota-update-v1",
            bridge_bindings=("v4.0.0", "c" * 40, "d" * 64),
        )

        self.assertNotEqual(0, code)
        self.assertIn("not signed specifically by ota-update-v2", error)

    def test_conflicting_declared_root_is_rejected_in_explicit_bridge_stage_mode(self):
        tag = "v3.92.1"
        checksums, signature, receipt, _, _ = self.write_evidence(
            tag,
            f"suavoagent-{tag}-win-x64.zip",
            ota_signing_key_id="ota-update-v2",
        )

        code, _, error = self.resolve(tag, checksums, signature, receipt)

        self.assertNotEqual(0, code)
        self.assertIn("policy requires ota-update-v1", error)

    def test_missing_declared_root_is_rejected_outside_explicit_bridge_stage(self):
        tag = "v4.0.0"
        source = "c" * 40
        checksums, signature, receipt, _, receipt_sha = self.write_evidence(
            tag,
            "SuavoAgent-Setup.exe",
            ota_signing_key_id=None,
            source_commit=source,
        )

        code, _, error = self.resolve(
            tag,
            checksums,
            signature,
            receipt,
            bridge_bindings=(tag, source, receipt_sha),
        )

        self.assertNotEqual(0, code)
        self.assertIn("OTA signing key id is missing or invalid", error)

    def test_missing_legacy_root_requires_the_explicit_bridge_stage_allowance(self):
        tag = "v3.92.1"
        checksums, signature, receipt, _, _ = self.write_evidence(
            tag,
            f"suavoagent-{tag}-win-x64.zip",
            ota_signing_key_id=None,
        )

        rejected = self.resolve(tag, checksums, signature, receipt)
        accepted = self.resolve(
            tag,
            checksums,
            signature,
            receipt,
            allow_missing_legacy_key_id=True,
        )

        self.assertNotEqual(0, rejected[0])
        self.assertEqual(0, accepted[0], accepted[2])

    def test_rejects_receipt_not_bound_by_signed_checksums(self):
        tag = "v3.92.1"
        artifact = f"suavoagent-{tag}-win-x64.zip"
        checksums, signature, receipt, _, _ = self.write_evidence(tag, artifact)
        receipt.write_text(receipt.read_text(encoding="utf-8") + " ", encoding="utf-8")

        code, _, error = self.resolve(tag, checksums, signature, receipt)

        self.assertNotEqual(0, code)
        self.assertIn("not bound by the signed checksum manifest", error)

    def test_rejects_receipt_digest_that_disagrees_with_signed_artifact_digest(self):
        tag = "v3.92.1"
        artifact = f"suavoagent-{tag}-win-x64.zip"
        checksums, signature, receipt, _, _ = self.write_evidence(
            tag,
            artifact,
            receipt_artifact_sha256="a" * 64,
            manifest_artifact_sha256="c" * 64,
        )

        code, _, error = self.resolve(tag, checksums, signature, receipt)

        self.assertNotEqual(0, code)
        self.assertIn("artifact digest does not match", error)

    def test_rejects_signed_receipt_with_unapproved_artifact_name(self):
        tag = "v3.92.1"
        artifact = "../../unsigned.exe"
        checksums, signature, receipt, _, _ = self.write_evidence(tag, artifact)

        code, _, error = self.resolve(tag, checksums, signature, receipt)

        self.assertNotEqual(0, code)
        self.assertIn("invalid line", error)

    def test_rejects_versioned_native_bundle_name_after_canonical_transition(self):
        tag = "v3.93.0"
        artifact = f"SuavoAgent-Setup-{tag}-win-x64.exe"
        checksums, signature, receipt, _, _ = self.write_evidence(tag, artifact)

        code, _, error = self.resolve(tag, checksums, signature, receipt)

        self.assertNotEqual(0, code)
        self.assertIn("artifact name is outside the approved transition", error)


if __name__ == "__main__":
    unittest.main()
