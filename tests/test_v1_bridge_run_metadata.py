from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts"))

import ota_update_trust_roots as trust_roots
import v1_bridge_release as bridge
import v1_bridge_run_metadata as run_metadata
from tests.test_v1_bridge_convergence import ConvergenceFixture


class V1BridgeRunMetadataTests(unittest.TestCase):
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

    def test_live_release_revalidation_binds_every_convergence_asset(self) -> None:
        self.fixture.sign_claim()
        self.fixture.write_registry(bridge.V2_KEY_ID)
        release = self.fixture.root / "release.json"
        tag_ref = self.fixture.root / "tag-ref.json"
        assets = self.fixture.root / "assets.json"
        release.write_text(json.dumps({
            "tag_name": self.fixture.release_tag,
            "draft": False,
            "prerelease": False,
            "published_at": self.fixture.published,
        }), encoding="utf-8")
        tag_ref.write_text(json.dumps({
            "object": {"type": "commit", "sha": self.fixture.source_sha},
        }), encoding="utf-8")
        bindings = self.fixture.release_bindings
        expected = {
            "SuavoAgent.Core.exe": bindings["coreArtifactSha256"],
            "SuavoAgent.Broker.exe": bindings["brokerArtifactSha256"],
            "SuavoAgent.Helper.exe": bindings["helperArtifactSha256"],
            "SuavoAgent.Watchdog.exe": bindings["watchdogArtifactSha256"],
            bindings["burnArtifactName"]: bindings["burnArtifactSha256"],
            bindings["msiArtifactName"]: bindings["msiArtifactSha256"],
            "SuavoSetup.exe": bindings["maintenanceHostSha256"],
            "field-release-receipt.json": bindings["releaseReceiptSha256"],
            "checksums.sha256": bindings["checksumsSha256"],
            "checksums.sha256.sig": bindings["checksumsSignatureSha256"],
            bindings["updateManifestName"]: bindings["updateManifestSha256"],
            f"update-manifest-{self.fixture.release_tag}.sig": (
                bindings["updateManifestSignatureSha256"]
            ),
        }

        def write_assets(values: dict[str, str]) -> None:
            assets.write_text(json.dumps([[
                {
                    "name": name,
                    "state": "uploaded",
                    "size": 1,
                    "digest": f"sha256:{digest}",
                }
                for name, digest in values.items()
            ]]), encoding="utf-8")

        arguments = argparse.Namespace(
            registry=self.fixture.registry,
            evidence=self.fixture.evidence,
            claim=self.fixture.claim,
            claim_signature=self.fixture.claim_signature,
            inventory=self.fixture.inventory,
            inventory_signature=self.fixture.inventory_signature,
            release=release,
            tag_ref=tag_ref,
            assets=assets,
        )
        write_assets(expected)
        run_metadata.validate_live_bridge_release(arguments)
        write_assets(expected | {"SuavoAgent.Core.exe": "f" * 64})
        with self.assertRaises(bridge.BridgeError):
            run_metadata.validate_live_bridge_release(arguments)


if __name__ == "__main__":
    unittest.main()
