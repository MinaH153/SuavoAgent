from __future__ import annotations

import base64
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts"))
SPEC = importlib.util.spec_from_file_location(
    "ota_update_trust_roots",
    ROOT / "scripts/ota_update_trust_roots.py",
)
assert SPEC and SPEC.loader
TRUST = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(TRUST)


class OtaUpdateTrustRootsTests(unittest.TestCase):
    def test_production_registry_trusts_distinct_v1_and_v2_but_still_signs_with_v1(self) -> None:
        signing_key_id, roots = TRUST.load_registry_configuration()
        self.assertEqual("ota-update-v1", signing_key_id)
        self.assertEqual({"ota-update-v1", "ota-update-v2"}, set(roots))
        self.assertNotEqual(roots["ota-update-v1"], roots["ota-update-v2"])

    def test_pending_v2_marker_is_inert_in_a_bootstrap_registry(self) -> None:
        source = json.loads(TRUST.DEFAULT_REGISTRY.read_text())
        source["roots"][1]["publicKeyDerBase64"] = TRUST.PENDING_V2
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "roots.json"
            path.write_text(json.dumps(source))
            signing_key_id, roots = TRUST.load_registry_configuration(path)
        self.assertEqual("ota-update-v1", signing_key_id)
        self.assertEqual({"ota-update-v1"}, set(roots))

    def test_generated_v2_can_verify_der_and_p1363_without_private_key_in_registry(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            private_key = root / "private.pem"
            public_der = root / "public.der"
            payload = root / "payload"
            signature_der = root / "signature.der"
            signature_hex = root / "signature.hex"
            subprocess.run(
                ["openssl", "ecparam", "-name", "prime256v1", "-genkey", "-noout", "-out", private_key],
                check=True,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
            )
            subprocess.run(
                ["openssl", "pkey", "-in", private_key, "-pubout", "-outform", "DER", "-out", public_der],
                check=True,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
            )
            v1 = json.loads(TRUST.DEFAULT_REGISTRY.read_text())["roots"][0]
            registry_path = root / "roots.json"
            registry_path.write_text(json.dumps({
                "schemaVersion": 1,
                "signingKeyId": "ota-update-v2",
                "roots": [
                    v1,
                    {
                        "keyId": "ota-update-v2",
                        "publicKeyDerBase64": base64.b64encode(public_der.read_bytes()).decode("ascii"),
                    },
                ],
            }))
            expected = dict(TRUST.EXPECTED_SPKI_SHA256)
            TRUST.EXPECTED_SPKI_SHA256 = expected | {
                "ota-update-v2": hashlib.sha256(public_der.read_bytes()).hexdigest()
            }
            try:
                roots = TRUST.load_registry(registry_path)
            finally:
                TRUST.EXPECTED_SPKI_SHA256 = expected
            self.assertEqual({"ota-update-v1", "ota-update-v2"}, set(roots))

            payload.write_bytes(b"reviewed release bytes")
            subprocess.run(
                ["openssl", "dgst", "-sha256", "-sign", private_key, "-out", signature_der, payload],
                check=True,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
            )
            self.assertTrue(TRUST.verify_signature(roots, payload, signature_der, "der"))

            der = signature_der.read_bytes()
            p1363 = self._der_to_p1363(der)
            signature_hex.write_text(p1363.hex())
            self.assertTrue(TRUST.verify_signature(roots, payload, signature_hex, "p1363-hex"))

            payload.write_bytes(b"tampered")
            self.assertFalse(TRUST.verify_signature(roots, payload, signature_der, "der"))

    def test_duplicate_or_unknown_root_fails_closed(self) -> None:
        source = json.loads(TRUST.DEFAULT_REGISTRY.read_text())
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "roots.json"
            source["roots"][1] = dict(source["roots"][0])
            path.write_text(json.dumps(source))
            with self.assertRaises(TRUST.TrustRegistryError):
                TRUST.load_registry(path)

    def test_historic_v1_and_fixed_v2_roots_are_digest_pinned(self) -> None:
        self.assertEqual(
            "b3f5ddda0654713de31e6cbe3ae3b49ed53575d0938d4149779361c6d739e970",
            TRUST.EXPECTED_SPKI_SHA256["ota-update-v1"],
        )
        self.assertEqual(
            "6e4092980b1185627200476806d5063c43df77e5ac000b6b6ba72df89eb1406f",
            TRUST.EXPECTED_SPKI_SHA256["ota-update-v2"],
        )
        source = json.loads(TRUST.DEFAULT_REGISTRY.read_text())
        source["roots"][0]["publicKeyDerBase64"] = source["roots"][1]["publicKeyDerBase64"]
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "roots.json"
            path.write_text(json.dumps(source))
            with self.assertRaises(TRUST.TrustRegistryError):
                TRUST.load_registry(path)

    @staticmethod
    def _der_to_p1363(signature: bytes) -> bytes:
        if signature[0] != 0x30:
            raise AssertionError("not DER sequence")
        offset = 2
        values = []
        for _ in range(2):
            if signature[offset] != 0x02:
                raise AssertionError("not DER integer")
            length = signature[offset + 1]
            value = signature[offset + 2:offset + 2 + length]
            values.append(value.lstrip(b"\x00").rjust(32, b"\x00"))
            offset += 2 + length
        return b"".join(values)


if __name__ == "__main__":
    unittest.main()
