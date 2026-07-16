from __future__ import annotations

import argparse
import base64
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts"))

import ota_update_trust_roots as trust_roots
import v1_bridge_convergence as convergence
import v1_bridge_release as bridge
from ecdsa_der_to_p1363 import der_to_p1363_hex


def openssl(*arguments: str, input_bytes: bytes | None = None) -> bytes:
    return subprocess.run(
        ("openssl", *arguments), input=input_bytes, check=True, capture_output=True
    ).stdout


class ConvergenceFixture:
    def __init__(self, root: Path) -> None:
        self.root = root
        self.v1_key = self.make_key("v1")
        self.v2_key = self.make_key("v2")
        self.device_key = self.make_key("device")
        self.maintenance_key = self.make_key("maintenance")
        self.other_device_key = self.make_key("other-device")
        self.other_maintenance_key = self.make_key("other-maintenance")
        self.public_keys = {
            bridge.V1_KEY_ID: self.public_der(self.v1_key),
            bridge.V2_KEY_ID: self.public_der(self.v2_key),
        }
        self.registry = root / "registry.json"
        self.inventory = root / "inventory.json"
        self.inventory_signature = root / "inventory.sig"
        self.evidence = root / "evidence.json"
        self.claim = root / "claim.json"
        self.claim_signature = root / "claim.sig"
        self.release_tag = "v4.0.0"
        self.source_sha = "a" * 40
        now = datetime.now(timezone.utc).replace(microsecond=0)
        self.published = (now - timedelta(minutes=10)).strftime("%Y-%m-%dT%H:%M:%SZ")
        self.cutoff = (now - timedelta(minutes=9)).strftime("%Y-%m-%dT%H:%M:%SZ")
        self.inventory_issued = (now - timedelta(minutes=8)).strftime("%Y-%m-%dT%H:%M:%SZ")
        self.install_completed = (now - timedelta(minutes=7)).strftime("%Y-%m-%dT%H:%M:%SZ")
        self.restart_observed = (now - timedelta(minutes=6)).strftime("%Y-%m-%dT%H:%M:%SZ")
        self.noop_observed = (now - timedelta(minutes=5)).strftime("%Y-%m-%dT%H:%M:%SZ")
        self.verified = (now - timedelta(minutes=4)).strftime("%Y-%m-%dT%H:%M:%SZ")
        self.inventory_expires = (now + timedelta(days=1)).strftime("%Y-%m-%dT%H:%M:%SZ")
        self.host_digest = "1" * 64
        self.release_bindings = {
            "burnArtifactName": "SuavoAgent-Setup.exe",
            "burnArtifactSha256": "2" * 64,
            "msiArtifactName": f"SuavoAgent-{self.release_tag}-win-x64.msi",
            "msiArtifactSha256": "3" * 64,
            "coreArtifactSha256": "4" * 64,
            "brokerArtifactSha256": "5" * 64,
            "helperArtifactSha256": "6" * 64,
            "watchdogArtifactSha256": "7" * 64,
            "maintenanceHostSha256": "8" * 64,
            "releaseReceiptSha256": "9" * 64,
            "checksumsSha256": "a" * 64,
            "checksumsSignatureSha256": "b" * 64,
            "updateManifestName": f"update-manifest-{self.release_tag}.txt",
            "updateManifestSha256": "0" * 64,
            "updateManifestSignatureSha256": "0" * 64,
        }
        self.refresh_manifest_bindings()
        self.write_registry(bridge.V1_KEY_ID)
        self.write_inventory()
        self.write_evidence()

    def make_key(self, name: str) -> Path:
        key = self.root / f"{name}.pem"
        subprocess.run(
            ("openssl", "ecparam", "-name", "prime256v1", "-genkey", "-noout", "-out", str(key)),
            check=True,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
        key.chmod(0o600)
        return key

    @staticmethod
    def public_der(key: Path) -> bytes:
        return openssl("pkey", "-in", str(key), "-pubout", "-outform", "DER")

    @staticmethod
    def sign_bytes(key: Path, payload: bytes) -> bytes:
        return openssl("dgst", "-sha256", "-sign", str(key), input_bytes=payload)

    def sign_p1363(self, key: Path, payload: bytes) -> bytes:
        return bytes.fromhex(der_to_p1363_hex(self.sign_bytes(key, payload)))

    def sign_p1363_base64url(self, key: Path, payload: bytes) -> str:
        return base64.urlsafe_b64encode(self.sign_p1363(key, payload)).decode("ascii").rstrip("=")

    def refresh_manifest_bindings(self) -> None:
        base = f"https://github.com/{bridge.REPOSITORY}/releases/download/{self.release_tag}"
        fields = (
            f"{base}/SuavoAgent.Core.exe", self.release_bindings["coreArtifactSha256"],
            f"{base}/SuavoAgent.Broker.exe", self.release_bindings["brokerArtifactSha256"],
            f"{base}/SuavoAgent.Helper.exe", self.release_bindings["helperArtifactSha256"],
            self.release_tag.removeprefix("v"), "net8.0", "win-x64",
            f"{base}/SuavoAgent.Watchdog.exe", self.release_bindings["watchdogArtifactSha256"],
        )
        self.manifest = "|".join(fields)
        self.manifest_signature_hex = der_to_p1363_hex(
            self.sign_bytes(self.v1_key, self.manifest.encode("ascii"))
        )
        self.release_bindings["updateManifestSha256"] = bridge._sha256(
            self.manifest.encode("ascii")
        )
        self.release_bindings["updateManifestSignatureSha256"] = bridge._sha256(
            self.manifest_signature_hex.encode("ascii")
        )

    def device_entry(
        self,
        host_digest: str,
        key: Path | None = None,
        maintenance_key: Path | None = None,
    ) -> dict[str, str]:
        public_key = self.public_der(key or self.device_key)
        maintenance_public_key = self.public_der(
            maintenance_key or self.maintenance_key
        )
        return {
            "hostDigest": host_digest,
            "attestationKeyId": bridge._sha256(public_key),
            "attestationPublicKeySpkiDerBase64": base64.b64encode(public_key).decode("ascii"),
            "maintenanceKeyId": bridge._sha256(maintenance_public_key),
            "maintenancePublicKeySpkiDerBase64": base64.b64encode(
                maintenance_public_key
            ).decode("ascii"),
        }

    def write_registry(self, selected: str) -> None:
        document = {
            "schemaVersion": 1,
            "signingKeyId": selected,
            "roots": [
                {
                    "keyId": key_id,
                    "publicKeyDerBase64": base64.b64encode(public_key).decode("ascii"),
                }
                for key_id, public_key in self.public_keys.items()
            ],
        }
        self.registry.write_bytes(bridge._canonical_json(document))

    def write_inventory(
        self,
        hosts: list[dict[str, str]] | None = None,
        overrides: dict[str, object] | None = None,
    ) -> None:
        registered = [self.device_entry(self.host_digest)] if hosts is None else hosts
        host_digests = [entry["hostDigest"] for entry in registered]
        document = {
            "schemaVersion": 3,
            "purpose": convergence.INVENTORY_PURPOSE,
            "repository": bridge.REPOSITORY,
            "bridgeReleaseTag": self.release_tag,
            "bridgeSourceSha": self.source_sha,
            "releasePublishedAtUtc": self.published,
            "snapshotCutoffUtc": self.cutoff,
            "fleetRegistryEpoch": 1,
            "enrollmentClosed": True,
            "registeredHostCount": len(registered),
            "registeredHostSetSha256": bridge._sha256(
                bridge._canonical_json(host_digests)
            ),
            "issuedAtUtc": self.inventory_issued,
            "expiresAtUtc": self.inventory_expires,
            "registeredHosts": registered,
            "releaseBindings": self.release_bindings,
        }
        document.update(overrides or {})
        raw = bridge._canonical_json(document)
        self.inventory.write_bytes(raw)
        self.inventory_signature.write_bytes(self.sign_bytes(self.v2_key, raw))

    def install_receipt(self, **overrides: object) -> dict[str, object]:
        entry = self.device_entry(self.host_digest)
        document: dict[str, object] = {
            "schemaVersion": 1,
            "purpose": convergence.INSTALL_RECEIPT_PURPOSE,
            "hostDigest": self.host_digest,
            "maintenanceKeyId": entry["maintenanceKeyId"],
            "installedReleaseTag": self.release_tag,
            "installedSourceSha": self.source_sha,
            "installerType": "msi",
            "installerArtifactSha256": self.release_bindings["msiArtifactSha256"],
            "releaseReceiptSha256": self.release_bindings["releaseReceiptSha256"],
            "checksumsSha256": self.release_bindings["checksumsSha256"],
            "checksumsSignatureSha256": self.release_bindings["checksumsSignatureSha256"],
            "installedCohort": {
                "SuavoAgent.Core.exe": self.release_bindings["coreArtifactSha256"],
                "SuavoAgent.Broker.exe": self.release_bindings["brokerArtifactSha256"],
                "SuavoAgent.Helper.exe": self.release_bindings["helperArtifactSha256"],
                "SuavoAgent.Watchdog.exe": self.release_bindings["watchdogArtifactSha256"],
                "SuavoSetup.exe": self.release_bindings["maintenanceHostSha256"],
            },
            "installTransactionId": "c" * 64,
            "installCompletedAtUtc": self.install_completed,
            "bootIdAtInstall": "d" * 64,
            "installMode": convergence.INSTALL_MODE,
        }
        return document | overrides

    def restart_receipt(
        self,
        install_receipt_sha256: str,
        **overrides: object,
    ) -> dict[str, object]:
        document: dict[str, object] = {
            "schemaVersion": 1,
            "purpose": convergence.RESTART_RECEIPT_PURPOSE,
            "hostDigest": self.host_digest,
            "installReceiptSha256": install_receipt_sha256,
            "bootIdBeforeRestart": "d" * 64,
            "bootIdAfterRestart": "e" * 64,
            "runningReleaseTag": self.release_tag,
            "runningSourceSha": self.source_sha,
            "outcome": convergence.RESTART_OUTCOME,
            "restartObservedAtUtc": self.restart_observed,
        }
        return document | overrides

    def noop_receipt(
        self,
        inventory_sha256: str,
        install_receipt_sha256: str,
        restart_receipt_sha256: str,
        **overrides: object,
    ) -> dict[str, object]:
        document: dict[str, object] = {
            "schemaVersion": 1,
            "purpose": convergence.V1_NOOP_RECEIPT_PURPOSE,
            "hostDigest": self.host_digest,
            "inventorySha256": inventory_sha256,
            "installReceiptSha256": install_receipt_sha256,
            "restartReceiptSha256": restart_receipt_sha256,
            "installedReleaseTag": self.release_tag,
            "installedSourceSha": self.source_sha,
            "otaSigningKeyId": bridge.V1_KEY_ID,
            "updateManifestName": self.release_bindings["updateManifestName"],
            "updateManifestCanonical": self.manifest,
            "updateManifestSignatureP1363Hex": self.manifest_signature_hex,
            "checksumsSha256": self.release_bindings["checksumsSha256"],
            "checksumsSignatureSha256": self.release_bindings["checksumsSignatureSha256"],
            "outcome": convergence.V1_NOOP_OUTCOME,
            "observedAtUtc": self.noop_observed,
        }
        return document | overrides

    def write_evidence(
        self,
        signer: Path | None = None,
        maintenance_signer: Path | None = None,
        machine_overrides: dict[str, object] | None = None,
        install_overrides: dict[str, object] | None = None,
        restart_overrides: dict[str, object] | None = None,
        noop_overrides: dict[str, object] | None = None,
    ) -> None:
        inventory_sha256 = bridge._sha256(self.inventory.read_bytes())
        install_receipt = self.install_receipt(**(install_overrides or {}))
        install_receipt_sha256 = bridge._sha256(bridge._canonical_json(install_receipt))
        restart_receipt = self.restart_receipt(
            install_receipt_sha256,
            **(restart_overrides or {}),
        )
        restart_receipt_sha256 = bridge._sha256(bridge._canonical_json(restart_receipt))
        noop_receipt = self.noop_receipt(
            inventory_sha256,
            install_receipt_sha256,
            restart_receipt_sha256,
            **(noop_overrides or {}),
        )
        noop_receipt_sha256 = bridge._sha256(bridge._canonical_json(noop_receipt))
        attestation: dict[str, object] = {
            "schemaVersion": 2,
            "purpose": convergence.ATTESTATION_PURPOSE,
            "attestationAuthority": convergence.ATTESTATION_AUTHORITY,
            "attestationKeyId": self.device_entry(self.host_digest)["attestationKeyId"],
            "hostDigest": self.host_digest,
            "inventorySha256": inventory_sha256,
            "installReceipt": install_receipt,
            "installReceiptSha256": install_receipt_sha256,
            "restartReceipt": restart_receipt,
            "restartReceiptSha256": restart_receipt_sha256,
            "v1NoopRehearsalReceipt": noop_receipt,
            "v1NoopRehearsalReceiptSha256": noop_receipt_sha256,
            "verifiedAtUtc": self.verified,
            "phiClassification": convergence.PHI_CLASSIFICATION,
        }
        attestation.update(machine_overrides or {})
        document = {
            "schemaVersion": 4,
            "ceremony": convergence.CEREMONY,
            "bridgeReleaseTag": self.release_tag,
            "bridgeSourceSha": self.source_sha,
            "machines": [{
                "attestation": attestation,
                "attestationSignatureBase64Url": self.sign_p1363_base64url(
                    signer or self.device_key,
                    bridge._canonical_json(attestation),
                ),
                "installReceiptSignatureBase64Url": self.sign_p1363_base64url(
                    maintenance_signer or self.maintenance_key,
                    bridge._canonical_json(install_receipt),
                ),
            }],
        }
        self.evidence.write_bytes(bridge._canonical_json(document))

    def sign_claim(self) -> None:
        convergence.sign_convergence_claim(
            argparse.Namespace(
                key=self.v1_key,
                evidence=self.evidence,
                inventory=self.inventory,
                inventory_signature=self.inventory_signature,
                claim=self.claim,
                signature=self.claim_signature,
                bridge_release_tag=self.release_tag,
                bridge_source_sha=self.source_sha,
                registry=self.registry,
            )
        )

    def resign_claim(self, key: Path) -> None:
        self.claim_signature.write_bytes(self.sign_bytes(key, self.claim.read_bytes()))

    def assert_normal(self, full_cohort: str = "true") -> None:
        bridge.assert_normal_release(
            self.registry,
            full_cohort,
            self.evidence,
            self.claim,
            self.claim_signature,
            self.inventory,
            self.inventory_signature,
        )


class V1BridgeConvergenceTests(unittest.TestCase):
    def test_install_receipt_matches_native_and_web_golden_vector(self) -> None:
        receipt = {
            "schemaVersion": 1,
            "purpose": convergence.INSTALL_RECEIPT_PURPOSE,
            "hostDigest": "a" * 64,
            "maintenanceKeyId": "9" * 64,
            "installedReleaseTag": "v4.0.0",
            "installedSourceSha": "d" * 40,
            "installerType": "msi",
            "installerArtifactSha256": "e" * 64,
            "releaseReceiptSha256": "f" * 64,
            "checksumsSha256": "c" * 64,
            "checksumsSignatureSha256": "8" * 64,
            "installedCohort": {
                "SuavoAgent.Core.exe": "1" * 64,
                "SuavoAgent.Broker.exe": "2" * 64,
                "SuavoAgent.Helper.exe": "3" * 64,
                "SuavoAgent.Watchdog.exe": "4" * 64,
                "SuavoSetup.exe": "5" * 64,
            },
            "installTransactionId": "7" * 64,
            "installCompletedAtUtc": "2026-07-15T12:00:00Z",
            "bootIdAtInstall": "b" * 64,
            "installMode": convergence.INSTALL_MODE,
        }

        canonical = bridge._canonical_json(receipt)

        self.assertTrue(canonical.endswith(b"\n"))
        self.assertFalse(canonical.endswith(b"\n\n"))
        self.assertEqual(
            bridge._sha256(canonical),
            "fe2988cb3f1ee9ebb1df7de3976207c5b8a2aa7b3e87ef8da9d5634dbb69d990",
        )

    def test_legacy_burn_receipt_cannot_satisfy_msi_install_proof(self) -> None:
        self.fixture.write_evidence(install_overrides={
            "installerType": "burn",
            "installerArtifactSha256": self.fixture.release_bindings[
                "burnArtifactSha256"
            ],
        })

        with self.assertRaisesRegex(bridge.BridgeError, "must prove.*MSI"):
            convergence.validate_evidence_bundle(
                self.fixture.evidence,
                self.fixture.inventory,
                self.fixture.inventory_signature,
                self.fixture.registry,
            )

    def setUp(self) -> None:
        self.original_registry_hashes = dict(trust_roots.EXPECTED_SPKI_SHA256)
        self.original_bridge_hashes = dict(bridge.BRIDGE_ROOT_SHA256)
        self.temporary = tempfile.TemporaryDirectory()
        self.fixture = ConvergenceFixture(Path(self.temporary.name))
        hashes = {
            key_id: bridge._sha256(public_key)
            for key_id, public_key in self.fixture.public_keys.items()
        }
        trust_roots.EXPECTED_SPKI_SHA256 = dict(hashes)
        bridge.BRIDGE_ROOT_SHA256 = dict(hashes)

    def tearDown(self) -> None:
        trust_roots.EXPECTED_SPKI_SHA256 = self.original_registry_hashes
        bridge.BRIDGE_ROOT_SHA256 = self.original_bridge_hashes
        self.temporary.cleanup()

    def ready_fixture(self) -> None:
        self.fixture.sign_claim()
        self.fixture.write_registry(bridge.V2_KEY_ID)

    def test_exact_v1_claim_v2_inventory_and_device_receipt_allow_normal_release(self) -> None:
        self.ready_fixture()
        self.fixture.assert_normal()

    def test_absent_claim_and_non_exact_activation_switch_reject(self) -> None:
        self.fixture.write_registry(bridge.V2_KEY_ID)
        with self.assertRaises(bridge.BridgeError):
            self.fixture.assert_normal()
        with self.assertRaises(bridge.BridgeError):
            self.fixture.assert_normal("TRUE")

    def test_central_v2_signature_cannot_impersonate_enrolled_device(self) -> None:
        self.fixture.write_evidence(signer=self.fixture.v2_key)
        with self.assertRaises(bridge.BridgeError):
            convergence.validate_evidence_bundle(
                self.fixture.evidence,
                self.fixture.inventory,
                self.fixture.inventory_signature,
                self.fixture.registry,
            )

    def test_attestation_schema_purpose_and_legacy_boolean_proofs_reject(self) -> None:
        for overrides in (
            {"purpose": "ota-update-v2"},
            {"schemaVersion": 1},
            {"schemaVersion": 2.0},
            {"restartObserved": False},
            {"v1NoopRehearsalSucceeded": False},
        ):
            with self.subTest(overrides=overrides):
                self.fixture.write_evidence(machine_overrides=overrides)
                with self.assertRaises(bridge.BridgeError):
                    convergence.validate_evidence_bundle(
                        self.fixture.evidence,
                        self.fixture.inventory,
                        self.fixture.inventory_signature,
                        self.fixture.registry,
                    )

    def test_nested_receipts_are_hash_bound_and_semantically_checked(self) -> None:
        cases = (
            {"install_overrides": {"installMode": "incremental"}},
            {
                "install_overrides": {
                    "installerType": None,
                    "installerArtifactSha256": None,
                }
            },
            {"install_overrides": {"schemaVersion": True}},
            {"restart_overrides": {"schemaVersion": 1.0}},
            {"noop_overrides": {"schemaVersion": True}},
            {"restart_overrides": {"bootIdAfterRestart": "d" * 64}},
            {"noop_overrides": {"outcome": "update-applied"}},
            {"machine_overrides": {"restartReceiptSha256": "f" * 64}},
            {
                "noop_overrides": {
                    "updateManifestSignatureP1363Hex": "8" * 128,
                }
            },
        )
        for arguments in cases:
            with self.subTest(arguments=arguments):
                self.fixture.write_evidence(**arguments)
                with self.assertRaises(bridge.BridgeError):
                    convergence.validate_evidence_bundle(
                        self.fixture.evidence,
                        self.fixture.inventory,
                        self.fixture.inventory_signature,
                        self.fixture.registry,
                    )

    def test_install_may_precede_inventory_but_restart_must_follow_closed_epoch(self) -> None:
        published = datetime.strptime(
            self.fixture.published,
            "%Y-%m-%dT%H:%M:%SZ",
        ).replace(tzinfo=timezone.utc)
        issued = datetime.strptime(
            self.fixture.inventory_issued,
            "%Y-%m-%dT%H:%M:%SZ",
        ).replace(tzinfo=timezone.utc)
        self.fixture.install_completed = (published + timedelta(seconds=30)).strftime(
            "%Y-%m-%dT%H:%M:%SZ"
        )
        self.fixture.write_evidence()
        convergence.validate_evidence_bundle(
            self.fixture.evidence,
            self.fixture.inventory,
            self.fixture.inventory_signature,
            self.fixture.registry,
        )

        before_epoch = (issued - timedelta(seconds=30)).strftime(
            "%Y-%m-%dT%H:%M:%SZ"
        )
        self.fixture.write_evidence(
            restart_overrides={"restartObservedAtUtc": before_epoch},
        )
        with self.assertRaises(bridge.BridgeError):
            convergence.validate_evidence_bundle(
                self.fixture.evidence,
                self.fixture.inventory,
                self.fixture.inventory_signature,
                self.fixture.registry,
            )

    def test_inventory_rejects_shared_device_or_maintenance_key_across_hosts(self) -> None:
        host_sets = (
            [
                self.fixture.device_entry("1" * 64),
                self.fixture.device_entry(
                    "9" * 64,
                    maintenance_key=self.fixture.other_maintenance_key,
                ),
            ],
            [
                self.fixture.device_entry("1" * 64),
                self.fixture.device_entry(
                    "9" * 64,
                    key=self.fixture.other_device_key,
                ),
            ],
        )
        for hosts in host_sets:
            with self.subTest(hosts=hosts):
                self.fixture.write_inventory(hosts)
                with self.assertRaises(bridge.BridgeError):
                    convergence.validate_inventory(
                        self.fixture.inventory,
                        self.fixture.inventory_signature,
                        self.fixture.registry,
                    )

    def test_inventory_requires_closed_epoch_count_digest_and_bounded_validity(self) -> None:
        expires_too_late = (
            datetime.strptime(self.fixture.inventory_issued, "%Y-%m-%dT%H:%M:%SZ")
            .replace(tzinfo=timezone.utc)
            + timedelta(days=8)
        ).strftime("%Y-%m-%dT%H:%M:%SZ")
        cases = (
            {"schemaVersion": 3.0},
            {"fleetRegistryEpoch": 0},
            {"enrollmentClosed": False},
            {"registeredHostCount": True},
            {"registeredHostCount": 2},
            {"registeredHostSetSha256": "f" * 64},
            {"expiresAtUtc": expires_too_late},
        )
        for overrides in cases:
            with self.subTest(overrides=overrides):
                self.fixture.write_inventory(overrides=overrides)
                with self.assertRaises(bridge.BridgeError):
                    convergence.validate_inventory(
                        self.fixture.inventory,
                        self.fixture.inventory_signature,
                        self.fixture.registry,
                    )

    def test_inventory_signature_and_device_key_substitution_reject(self) -> None:
        self.fixture.inventory_signature.write_bytes(
            self.fixture.sign_bytes(self.fixture.v1_key, self.fixture.inventory.read_bytes())
        )
        with self.assertRaises(bridge.BridgeError):
            convergence.validate_inventory(
                self.fixture.inventory,
                self.fixture.inventory_signature,
                self.fixture.registry,
            )
        self.fixture.write_inventory()
        self.fixture.write_evidence(
            machine_overrides={
                "attestationKeyId": self.fixture.device_entry(
                    self.fixture.host_digest, self.fixture.other_device_key
                )["attestationKeyId"]
            }
        )
        with self.assertRaises(bridge.BridgeError):
            convergence.validate_evidence_bundle(
                self.fixture.evidence,
                self.fixture.inventory,
                self.fixture.inventory_signature,
                self.fixture.registry,
            )

    def test_raw_key_ids_p1363_signatures_and_maintenance_authority_are_exact(self) -> None:
        entry = self.fixture.device_entry(self.fixture.host_digest)
        self.assertRegex(entry["attestationKeyId"], r"^[0-9a-f]{64}$")
        self.assertRegex(entry["maintenanceKeyId"], r"^[0-9a-f]{64}$")
        evidence = json.loads(self.fixture.evidence.read_text(encoding="utf-8"))
        machine = evidence["machines"][0]
        self.assertRegex(
            machine["attestationSignatureBase64Url"],
            r"^[A-Za-z0-9_-]{86}$",
        )
        self.assertRegex(
            machine["installReceiptSignatureBase64Url"],
            r"^[A-Za-z0-9_-]{86}$",
        )

        self.fixture.write_evidence(maintenance_signer=self.fixture.other_maintenance_key)
        with self.assertRaises(bridge.BridgeError):
            convergence.validate_evidence_bundle(
                self.fixture.evidence,
                self.fixture.inventory,
                self.fixture.inventory_signature,
                self.fixture.registry,
            )

    def test_prefixed_key_id_and_der_base64_device_signature_reject(self) -> None:
        host = self.fixture.device_entry(self.fixture.host_digest)
        host["attestationKeyId"] = "device-attestation:" + host["attestationKeyId"]
        self.fixture.write_inventory([host])
        with self.assertRaises(bridge.BridgeError):
            convergence.validate_inventory(
                self.fixture.inventory,
                self.fixture.inventory_signature,
                self.fixture.registry,
            )

        self.fixture.write_inventory()
        evidence = json.loads(self.fixture.evidence.read_text(encoding="utf-8"))
        attestation = evidence["machines"][0]["attestation"]
        der_signature = self.fixture.sign_bytes(
            self.fixture.device_key,
            bridge._canonical_json(attestation),
        )
        evidence["machines"][0]["attestationSignatureBase64Url"] = (
            base64.urlsafe_b64encode(der_signature).decode("ascii").rstrip("=")
        )
        self.fixture.evidence.write_bytes(bridge._canonical_json(evidence))
        with self.assertRaises(bridge.BridgeError):
            convergence.validate_evidence_bundle(
                self.fixture.evidence,
                self.fixture.inventory,
                self.fixture.inventory_signature,
                self.fixture.registry,
            )

    def test_noop_manifest_must_verify_under_historic_v1_not_v2(self) -> None:
        self.fixture.manifest_signature_hex = der_to_p1363_hex(
            self.fixture.sign_bytes(
                self.fixture.v2_key,
                self.fixture.manifest.encode("ascii"),
            )
        )
        self.fixture.release_bindings["updateManifestSignatureSha256"] = bridge._sha256(
            self.fixture.manifest_signature_hex.encode("ascii")
        )
        self.fixture.write_inventory()
        self.fixture.write_evidence()
        with self.assertRaises(bridge.BridgeError):
            convergence.validate_evidence_bundle(
                self.fixture.evidence,
                self.fixture.inventory,
                self.fixture.inventory_signature,
                self.fixture.registry,
            )

    def test_tampered_evidence_claim_count_and_wrong_claim_root_reject(self) -> None:
        self.ready_fixture()
        self.fixture.write_evidence(install_overrides={"installerArtifactSha256": "9" * 64})
        with self.assertRaises(bridge.BridgeError):
            self.fixture.assert_normal()
        self.fixture.write_evidence()
        claim = json.loads(self.fixture.claim.read_text(encoding="utf-8"))
        claim["expectedMachineCount"] = 2
        self.fixture.claim.write_bytes(bridge._canonical_json(claim))
        self.fixture.resign_claim(self.fixture.v1_key)
        with self.assertRaises(bridge.BridgeError):
            self.fixture.assert_normal()
        claim["expectedMachineCount"] = 1
        self.fixture.claim.write_bytes(bridge._canonical_json(claim))
        self.fixture.resign_claim(self.fixture.v2_key)
        with self.assertRaises(bridge.BridgeError):
            self.fixture.assert_normal()

    def test_claim_must_be_issued_during_inventory_validity(self) -> None:
        self.fixture.inventory_expires = (
            datetime.now(timezone.utc).replace(microsecond=0) - timedelta(minutes=1)
        ).strftime("%Y-%m-%dT%H:%M:%SZ")
        self.fixture.write_inventory()
        self.fixture.write_evidence()
        with self.assertRaises(bridge.BridgeError):
            self.fixture.sign_claim()

    def test_claim_rejects_json_numeric_aliases(self) -> None:
        self.ready_fixture()
        original = json.loads(self.fixture.claim.read_text(encoding="utf-8"))
        for field, value in (
            ("schemaVersion", 4.0),
            ("fleetRegistryEpoch", 1.0),
            ("expectedMachineCount", True),
            ("verifiedMachineCount", 1.0),
        ):
            with self.subTest(field=field, value=value):
                claim = original | {field: value}
                self.fixture.claim.write_bytes(bridge._canonical_json(claim))
                self.fixture.resign_claim(self.fixture.v1_key)
                with self.assertRaises(bridge.BridgeError):
                    self.fixture.assert_normal()

    def test_signed_claim_remains_durable_after_inventory_expiry(self) -> None:
        self.ready_fixture()
        real_datetime = datetime

        class FutureDateTime(real_datetime):
            @classmethod
            def now(cls, tz: timezone | None = None) -> datetime:
                return real_datetime.now(tz) + timedelta(days=2)

        with mock.patch.object(convergence, "datetime", FutureDateTime):
            self.fixture.assert_normal()

    def test_historic_key_has_only_clean_isolated_convergence_entrypoint(self) -> None:
        wrapper = (ROOT / "scripts/sign-ota-v1-convergence-local.sh").read_text(encoding="utf-8")
        ordinary_cli = (ROOT / "scripts/v1_bridge_cli.py").read_text(encoding="utf-8")
        for marker in (
            "hash-object --no-filters",
            "Git replacement refs are forbidden",
            "convergence outputs must be outside the source repository",
            "/opt/homebrew/bin/python3",
            "/usr/local/bin/python3",
            "-I -S -B",
            "runpy.run_path",
            "SUAVO_V1_CONVERGENCE_ISOLATED_BOOTSTRAP",
        ):
            self.assertIn(marker, wrapper)
        self.assertTrue(wrapper.startswith("#!/bin/bash -p\n"))
        self.assertNotIn("sign-convergence", ordinary_cli)

    def test_production_workflow_keeps_convergence_source_impossible_until_files_exist(self) -> None:
        reusable = (ROOT / ".github/workflows/production-release-signing.yml").read_text(
            encoding="utf-8"
        )
        self.assertIn("OTA_FULL_COHORT_MANIFEST", reusable)
        self.assertIn("v1_bridge_cli.py assert-normal-release", reusable)
        required = (ROOT / "scripts/verify-release-trust-inputs.py").read_text(encoding="utf-8")
        for path in (
            "security/ota-fleet-inventory-snapshot.json",
            "security/ota-fleet-inventory-snapshot.sig",
            "security/ota-v1-bridge-convergence-evidence.json",
        ):
            self.assertIn(path, required)
            self.assertFalse((ROOT / path).exists())


if __name__ == "__main__":
    unittest.main()
