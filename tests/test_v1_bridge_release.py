from __future__ import annotations

import argparse
import base64
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts"))

import v1_bridge_release as bridge
import v1_bridge_run_metadata as run_metadata
import v1_bridge_handoff as handoff
import ota_update_trust_roots as trust_roots
from ecdsa_der_to_p1363 import der_to_p1363_hex


def command(*arguments: str, cwd: Path | None = None) -> str:
    return subprocess.check_output(arguments, cwd=cwd, text=True).strip()


def job_block(document: str, job_name: str) -> str:
    match = re.search(
        rf"(?ms)^  {re.escape(job_name)}:\n(?P<body>.*?)(?=^  [a-z0-9][a-z0-9_-]*:\n|\Z)",
        document,
    )
    if match is None:
        raise AssertionError(f"workflow job is missing: {job_name}")
    return match.group(0)


class BridgeFixture:
    def __init__(self, root: Path) -> None:
        self.root = root
        self.source = root / "source"
        self.stage = root / "stage"
        self.release = self.stage / "release"
        self.source.mkdir()
        self.release.mkdir(parents=True)
        self.v1_key = root / "v1.pem"
        self.v2_key = root / "v2.pem"
        for key in (self.v1_key, self.v2_key):
            subprocess.run(
                ("openssl", "ecparam", "-name", "prime256v1", "-genkey", "-noout", "-out", str(key)),
                check=True,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
            )
            key.chmod(0o600)
        registry_dir = self.source / "security"
        registry_dir.mkdir()
        self.registry = registry_dir / "ota-update-trust-roots.json"
        self.registry.write_text(
            json.dumps(
                {
                    "schemaVersion": 1,
                    "signingKeyId": "ota-update-v1",
                    "roots": [
                        {"keyId": "ota-update-v1", "publicKeyDerBase64": self.public_key(self.v1_key)},
                        {"keyId": "ota-update-v2", "publicKeyDerBase64": self.public_key(self.v2_key)},
                    ],
                },
                indent=2,
            )
            + "\n",
            encoding="utf-8",
        )
        roots = json.loads(self.registry.read_text(encoding="utf-8"))["roots"]
        self.root_hashes = {
            entry["keyId"]: bridge._sha256(base64.b64decode(entry["publicKeyDerBase64"], validate=True))
            for entry in roots
        }
        bridge.BRIDGE_ROOT_SHA256 = dict(self.root_hashes)
        trust_roots.EXPECTED_SPKI_SHA256 = dict(self.root_hashes)
        command("git", "init", "-q", cwd=self.source)
        command("git", "config", "user.email", "bridge-test@example.invalid", cwd=self.source)
        command("git", "config", "user.name", "Bridge Test", cwd=self.source)
        command("git", "remote", "add", "origin", "https://github.com/MinaH153/SuavoAgent.git", cwd=self.source)
        command("git", "add", "security/ota-update-trust-roots.json", cwd=self.source)
        command("git", "commit", "-qm", "bridge fixture", cwd=self.source)
        self.source_sha = command("git", "rev-parse", "HEAD", cwd=self.source)
        self.version = "v4.0.0"
        fixed = (
            "SuavoAgent.Core.exe",
            "SuavoAgent.Broker.exe",
            "SuavoAgent.Helper.exe",
            "SuavoAgent.Watchdog.exe",
            "SuavoSetup.exe",
            f"SuavoAgent-{self.version}-win-x64.msi",
            "SuavoAgent-Setup.exe",
            "suavoagent.spdx.json",
            "legal/THIRD-PARTY-NOTICES.txt",
            "legal/THIRD-PARTY-PROVENANCE.json",
            "legal/external-assets.json",
            "legal/license-texts/Apache-2.0.txt",
            "legal/evidence/runtime.json",
        )
        for relative in fixed:
            path = self.release / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes((relative + "\n").encode("ascii"))
        bridge.prepare_request(
            argparse.Namespace(
                repository=bridge.REPOSITORY,
                source_sha=self.source_sha,
                version=self.version,
                rollback_tag="v3.92.1",
                rollback_artifact="SuavoAgent-Setup.exe",
                rollback_sha="a" * 64,
                authenticode_signer_sha256="c" * 64,
                stage_run_id=12345,
                stage_run_attempt=2,
                registry=self.registry,
                release_dir=self.release,
                output=self.stage / "bridge-signing-request.json",
                regenerate_final_sbom=True,
            )
        )
        self.descriptor = root / "bridge-handoff-descriptor.json"
        self.descriptor_signature = root / "bridge-handoff-descriptor.sig"
        request, request_raw = bridge.validate_request(
            self.stage / "bridge-signing-request.json",
            self.release,
            self.registry,
            expected_repository=bridge.REPOSITORY,
            expected_sha=self.source_sha,
            expected_run_id=12345,
            expected_run_attempt=2,
            expected_artifact="suavoagent-v1-bridge-request-12345-2",
        )
        self.descriptor.write_bytes(
            bridge._canonical_json(
                handoff.descriptor_document(request, request_raw, "c" * 64, 67890, 3)
            )
        )
        self.sign_file(self.v2_key, self.descriptor, self.descriptor_signature)

    @staticmethod
    def public_key(key: Path) -> str:
        result = subprocess.run(
            ("openssl", "pkey", "-in", str(key), "-pubout", "-outform", "DER"),
            check=True,
            capture_output=True,
        )
        return base64.b64encode(result.stdout).decode("ascii")

    @staticmethod
    def sign_file(key: Path, payload: Path, output: Path) -> None:
        subprocess.run(
            ("openssl", "dgst", "-sha256", "-sign", str(key), "-out", str(output), str(payload)),
            check=True,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )

    def local_arguments(self, response_json: Path | None = None, response_b64: Path | None = None) -> argparse.Namespace:
        return argparse.Namespace(
            key=self.v1_key,
            stage_dir=self.stage,
            source_root=self.source,
            descriptor=self.descriptor,
            descriptor_signature=self.descriptor_signature,
            response_json=response_json or self.root / "response.json",
            response_b64=response_b64 or self.root / "response.b64",
            registry=self.registry,
        )

    def finalize_arguments(self, response_b64: Path, response_json: Path | None = None) -> argparse.Namespace:
        return argparse.Namespace(
            request=self.stage / "bridge-signing-request.json",
            release_dir=self.release,
            response_b64_file=response_b64,
            response_json=response_json or self.stage / "bridge-signing-response.json",
            expected_repository=bridge.REPOSITORY,
            expected_sha=self.source_sha,
            expected_run_id=12345,
            expected_run_attempt=2,
            expected_artifact="suavoagent-v1-bridge-request-12345-2",
            registry=self.registry,
        )


class V1BridgeReleaseTests(unittest.TestCase):
    def setUp(self) -> None:
        self.production_bridge_roots = dict(bridge.BRIDGE_ROOT_SHA256)
        self.production_registry_roots = dict(trust_roots.EXPECTED_SPKI_SHA256)
        self.temporary = tempfile.TemporaryDirectory()
        self.fixture = BridgeFixture(Path(self.temporary.name))

    def tearDown(self) -> None:
        bridge.BRIDGE_ROOT_SHA256 = self.production_bridge_roots
        trust_roots.EXPECTED_SPKI_SHA256 = self.production_registry_roots
        self.temporary.cleanup()

    def test_local_sign_and_finalize_accept_only_exact_request(self) -> None:
        local = self.fixture.local_arguments()
        bridge.local_sign(local)
        request = json.loads(
            (self.fixture.stage / "bridge-signing-request.json").read_text(encoding="utf-8")
        )
        response = json.loads(local.response_json.read_text(encoding="utf-8"))
        receipt = json.loads(
            (self.fixture.release / "field-release-receipt.json").read_text(
                encoding="utf-8"
            )
        )
        self.assertEqual("c" * 64, request["authenticodeSignerSha256"])
        self.assertEqual(request["authenticodeSignerSha256"], response["authenticodeSignerSha256"])
        self.assertEqual(bridge.V1_KEY_ID, receipt["otaSigningKeyId"])
        bridge.finalize_response(self.fixture.finalize_arguments(local.response_b64))
        self.assertEqual(128, (self.fixture.release / f"update-manifest-{self.fixture.version}.sig").stat().st_size)
        self.assertGreater((self.fixture.release / "checksums.sha256.sig").stat().st_size, 64)
        manifest_signature = self.fixture.release / f"update-manifest-{self.fixture.version}.sig"
        expected_line = (
            f"{bridge._sha256(manifest_signature.read_bytes())}  "
            f"update-manifest-{self.fixture.version}.sig\n"
        )
        checksums_text = (self.fixture.release / "checksums.sha256").read_text(
            encoding="ascii"
        )
        self.assertIn(expected_line, checksums_text)
        checksum_names = tuple(
            line.split("  ", 1)[1] for line in checksums_text.splitlines()
        )
        request_names = tuple(
            Path(entry["path"]).name for entry in request["files"]
        )
        self.assertEqual(
            request_names + (f"update-manifest-{self.fixture.version}.sig",),
            checksum_names,
        )
        final_publication_names = {
            path.name
            for path in bridge._validate_release_allowlist(
                self.fixture.release, self.fixture.version, finalized=True
            )
        }
        self.assertEqual(
            final_publication_names
            - {"checksums.sha256", "checksums.sha256.sig"},
            set(checksum_names),
        )
        for required_name in (
            "suavoagent.spdx.json",
            "THIRD-PARTY-NOTICES.txt",
            "THIRD-PARTY-PROVENANCE.json",
            "external-assets.json",
            "Apache-2.0.txt",
            "runtime.json",
        ):
            self.assertIn(required_name, checksum_names)
        self.assertTrue((self.fixture.stage / "bridge-signing-response.json").is_file())

    def test_request_regenerates_sbom_over_exact_final_unsigned_cohort(self) -> None:
        sbom = json.loads(
            (self.fixture.release / "suavoagent.spdx.json").read_text(
                encoding="utf-8"
            )
        )
        documented = {
            entry["fileName"].removeprefix("./") for entry in sbom["files"]
        }
        release_inputs = {
            path.relative_to(self.fixture.release).as_posix()
            for path in self.fixture.release.rglob("*")
            if path.is_file() and path.name != "suavoagent.spdx.json"
        }
        self.assertEqual(release_inputs, documented)
        self.assertIn(
            f"SuavoAgent-{self.fixture.version}-win-x64.msi", documented
        )
        self.assertIn("SuavoAgent-Setup.exe", documented)
        self.assertIn(f"update-manifest-{self.fixture.version}.txt", documented)
        self.assertIn("field-release-receipt.json", documented)
        self.assertNotIn("appsettings.json", documented)
        root_package = next(
            package for package in sbom["packages"]
            if package["SPDXID"] == "SPDXRef-Package-SuavoAgent"
        )
        self.assertEqual(
            [
                "./checksums.sha256",
                "./checksums.sha256.sig",
                "./suavoagent.spdx.json",
                f"./update-manifest-{self.fixture.version}.sig",
            ],
            root_package["packageVerificationCode"][
                "packageVerificationCodeExcludedFiles"
            ],
        )

    def test_publication_rejects_duplicate_github_asset_basenames(self) -> None:
        collision = self.fixture.release / "legal/evidence/external-assets.json"
        collision.write_text("{}\n", encoding="ascii")

        with self.assertRaisesRegex(
            bridge.BridgeError, "duplicate GitHub basenames"
        ):
            bridge._checksum_publication_entries(
                self.fixture.release, self.fixture.version
            )

    def test_wrong_stage_run_attempt_and_sha_fail_closed(self) -> None:
        request = self.fixture.stage / "bridge-signing-request.json"
        for overrides in (
            {"expected_run_id": 54321},
            {"expected_run_attempt": 3},
            {"expected_sha": "b" * 40},
        ):
            values = {
                "expected_repository": bridge.REPOSITORY,
                "expected_sha": self.fixture.source_sha,
                "expected_run_id": 12345,
                "expected_run_attempt": 2,
                "expected_artifact": "suavoagent-v1-bridge-request-12345-2",
            } | overrides
            with self.subTest(overrides=overrides), self.assertRaises(bridge.BridgeError):
                bridge.validate_request(request, self.fixture.release, self.fixture.registry, **values)

    def test_v2_signatures_cannot_finalize_release_1(self) -> None:
        local = self.fixture.local_arguments()
        bridge.local_sign(local)
        response = json.loads(local.response_json.read_text(encoding="utf-8"))
        manifest = self.fixture.release / f"update-manifest-{self.fixture.version}.txt"
        manifest_der = bridge._sign_der(self.fixture.v2_key, manifest)
        manifest_hex = der_to_p1363_hex(manifest_der).encode("ascii")
        checksums_raw = bridge._expected_checksums(
            self.fixture.release, self.fixture.version, manifest_hex
        )
        checksums = self.fixture.root / "v2-checksums.sha256"
        checksums.write_bytes(checksums_raw)
        checksums_der = bridge._sign_der(self.fixture.v2_key, checksums)
        wrong = {
            **response,
            "checksumsBase64": base64.b64encode(checksums_raw).decode("ascii"),
            "manifestSignature": {
                **response["manifestSignature"],
                "signatureBase64": base64.b64encode(manifest_hex).decode("ascii"),
            },
            "checksumsSignature": {
                **response["checksumsSignature"],
                "inputSha256": bridge._sha256(checksums_raw),
                "signatureBase64": base64.b64encode(checksums_der).decode("ascii"),
            },
        }
        wrong_b64 = self.fixture.root / "wrong-v2.b64"
        wrong_b64.write_bytes(base64.b64encode(bridge._canonical_json(wrong)))
        with self.assertRaises(bridge.BridgeError):
            bridge.finalize_response(self.fixture.finalize_arguments(wrong_b64))

    def test_response_cannot_change_bound_authenticode_allowlist(self) -> None:
        local = self.fixture.local_arguments()
        bridge.local_sign(local)
        response = json.loads(local.response_json.read_text(encoding="utf-8"))
        response["authenticodeSignerSha256"] = "d" * 64
        wrong_b64 = self.fixture.root / "wrong-allowlist.b64"
        wrong_b64.write_bytes(base64.b64encode(bridge._canonical_json(response)) + b"\n")
        with self.assertRaises(bridge.BridgeError):
            bridge.finalize_response(self.fixture.finalize_arguments(wrong_b64))
        self.assertFalse((self.fixture.release / "checksums.sha256.sig").exists())

    def test_local_key_must_be_regular_owner_only_and_outputs_new(self) -> None:
        symlink = self.fixture.root / "linked.pem"
        symlink.symlink_to(self.fixture.v1_key)
        linked = self.fixture.local_arguments()
        linked.key = symlink
        with self.assertRaises(bridge.BridgeError):
            bridge.local_sign(linked)

        self.fixture.v1_key.chmod(0o644)
        with self.assertRaises(bridge.BridgeError):
            bridge.local_sign(self.fixture.local_arguments())
        self.fixture.v1_key.chmod(0o600)

        existing = self.fixture.root / "existing.json"
        existing.write_text("occupied", encoding="ascii")
        with self.assertRaises(bridge.BridgeError):
            bridge.local_sign(self.fixture.local_arguments(response_json=existing))

    def test_successful_stage_finalization_is_not_replayable(self) -> None:
        runs = self.fixture.root / "runs.json"
        runs.write_bytes(
            bridge._canonical_json(
                [
                    {"workflow_runs": []},
                    {"workflow_runs": [{
                        "id": 900,
                        "display_title": "Finalize v1 bridge stage 12345",
                        "conclusion": "success",
                    }]},
                ]
            )
        )
        with self.assertRaises(bridge.BridgeError):
            run_metadata.assert_not_replayed(
                argparse.Namespace(runs=runs, stage_run_id=12345, current_run_id=901)
            )

    def test_stage_metadata_binds_workflow_repo_sha_attempt_and_artifact(self) -> None:
        metadata = self.fixture.root / "metadata.json"
        metadata.write_bytes(
            bridge._canonical_json(
                {
                    "id": 12345,
                    "workflow_id": 111,
                    "run_attempt": 2,
                    "event": "workflow_dispatch",
                    "status": "completed",
                    "conclusion": "success",
                    "head_branch": "main",
                    "head_sha": self.fixture.source_sha,
                    "path": bridge.STAGE_WORKFLOW,
                    "name": run_metadata.STAGE_WORKFLOW_NAME,
                    "repository": {"full_name": bridge.REPOSITORY},
                }
            )
        )
        arguments = argparse.Namespace(
            metadata=metadata,
            expected_run_id=12345,
            expected_run_attempt=2,
            expected_sha=self.fixture.source_sha,
            expected_workflow_id=111,
        )
        run_metadata.validate_stage_metadata(arguments)
        altered = json.loads(metadata.read_text(encoding="utf-8")) | {"run_attempt": 3}
        metadata.write_bytes(bridge._canonical_json(altered))
        with self.assertRaises(bridge.BridgeError):
            run_metadata.validate_stage_metadata(arguments)

    def test_handoff_descriptor_binds_github_rest_artifact_digest(self) -> None:
        request_name = "suavoagent-v1-bridge-request-12345-2"
        request_digest = "c" * 64
        artifacts = self.fixture.root / "artifacts.json"

        def artifact(name: str, digest: str, identity: int) -> dict[str, object]:
            return {
                "id": identity,
                "name": name,
                "expired": False,
                "digest": f"sha256:{digest}",
                "workflow_run": {"id": 12345, "head_sha": self.fixture.source_sha},
            }

        artifacts.write_bytes(
            bridge._canonical_json(
                {"artifacts": [artifact(request_name, request_digest, 1)]}
            )
        )
        descriptor = self.fixture.root / "descriptor.json"
        run_metadata.write_descriptor(
            argparse.Namespace(
                output=descriptor,
                repository=bridge.REPOSITORY,
                source_sha=self.fixture.source_sha,
                stage_workflow=bridge.STAGE_WORKFLOW,
                stage_run_id=12345,
                stage_run_attempt=2,
                artifact_name=request_name,
                artifact_digest=request_digest,
                authorization_run_id=67890,
                authorization_run_attempt=3,
                artifacts=artifacts,
                request=self.fixture.stage / "bridge-signing-request.json",
                release_dir=self.fixture.release,
                registry=self.fixture.registry,
            )
        )
        descriptor_signature = self.fixture.root / "descriptor.sig"
        self.fixture.sign_file(self.fixture.v2_key, descriptor, descriptor_signature)
        validate = argparse.Namespace(
            descriptor=descriptor,
            signature=descriptor_signature,
            request=self.fixture.stage / "bridge-signing-request.json",
            release_dir=self.fixture.release,
            artifacts=artifacts,
            expected_sha=self.fixture.source_sha,
            expected_run_id=12345,
            expected_run_attempt=2,
            expected_artifact=request_name,
            expected_authorization_run_id=67890,
            expected_authorization_run_attempt=3,
            registry=self.fixture.registry,
        )
        run_metadata.validate_descriptor(validate)
        artifacts.write_bytes(
            bridge._canonical_json(
                {"artifacts": [artifact(request_name, "e" * 64, 1)]}
            )
        )
        with self.assertRaises(handoff.HandoffError):
            run_metadata.validate_descriptor(validate)

    def test_bridge_version_must_be_newest_and_have_no_leading_zero_alias(self) -> None:
        releases = self.fixture.root / "releases.json"
        releases.write_bytes(
            bridge._canonical_json(
                [[
                    {"tag_name": "v3.92.1", "draft": False, "prerelease": False},
                    {"tag_name": "v9.0.0-rc1", "draft": False, "prerelease": True},
                    {"tag_name": "v8.0.0", "draft": True, "prerelease": False},
                ]]
            )
        )
        run_metadata.assert_version_newest(argparse.Namespace(releases=releases, version="v4.0.0"))
        with self.assertRaises(bridge.BridgeError):
            run_metadata.assert_version_newest(argparse.Namespace(releases=releases, version="v3.92.1"))
        run_metadata.assert_release_absent(
            argparse.Namespace(releases=releases, version="v4.0.0")
        )
        draft = json.loads(releases.read_text(encoding="utf-8"))
        draft[0].append({"tag_name": "v4.0.0", "draft": True, "prerelease": False})
        releases.write_bytes(bridge._canonical_json(draft))
        with self.assertRaises(bridge.BridgeError):
            run_metadata.assert_release_absent(
                argparse.Namespace(releases=releases, version="v4.0.0")
            )
        with self.assertRaises(bridge.BridgeError):
            bridge._version_parts("v04.0.0")

    def test_bridge_roots_are_pinned_to_live_v1_and_fixed_v2_authorities(self) -> None:
        source = (ROOT / "scripts/v1_bridge_release.py").read_text(encoding="utf-8")
        self.assertIn("b3f5ddda0654713de31e6cbe3ae3b49ed53575d0938d4149779361c6d739e970", source)
        self.assertIn("6e4092980b1185627200476806d5063c43df77e5ac000b6b6ba72df89eb1406f", source)
        expected = dict(bridge.BRIDGE_ROOT_SHA256)
        bridge.BRIDGE_ROOT_SHA256 = expected | {bridge.V1_KEY_ID: "0" * 64}
        try:
            with self.assertRaises(bridge.BridgeError):
                bridge.assert_bridge_source(self.fixture.registry)
        finally:
            bridge.BRIDGE_ROOT_SHA256 = expected

    def test_tampered_final_signature_cannot_pass_read_only_revalidation(self) -> None:
        local = self.fixture.local_arguments()
        bridge.local_sign(local)
        bridge.finalize_response(self.fixture.finalize_arguments(local.response_b64))
        signature = self.fixture.release / "checksums.sha256.sig"
        tampered = bytes([signature.read_bytes()[0] ^ 1]) + signature.read_bytes()[1:]
        signature.write_bytes(tampered)
        validate = argparse.Namespace(
            stage_dir=self.fixture.stage,
            expected_repository=bridge.REPOSITORY,
            expected_sha=self.fixture.source_sha,
            expected_run_id=12345,
            expected_run_attempt=2,
            expected_artifact="suavoagent-v1-bridge-request-12345-2",
            registry=self.fixture.registry,
        )
        with self.assertRaises(bridge.BridgeError):
            bridge.validate_final(validate)
        self.assertEqual(tampered, signature.read_bytes())

    def test_workflows_never_expose_v1_or_aws_authority_to_handoff_jobs(self) -> None:
        workflow_root = ROOT / ".github" / "workflows"
        handoff_names = (
            "v1-bridge-stage.yml",
            "v1-bridge-finalize.yml",
        )
        handoffs = {name: (workflow_root / name).read_text(encoding="utf-8") for name in handoff_names}
        for name, document in handoffs.items():
            with self.subTest(workflow=name):
                for forbidden in (
                    "SIGNING_KEY_PEM",
                    "AWS_SIGNING_ROLE_ARN",
                    "AWS_SIGNING_REGION",
                    "OTA_KMS_KEY_ID",
                    "OTA_KMS_PUBLIC_KEY_DER_BASE64",
                ):
                    self.assertNotIn(forbidden, document)
        self.assertNotIn("contents: write", handoffs["v1-bridge-stage.yml"])
        self.assertNotIn("id-token: write", handoffs["v1-bridge-stage.yml"])
        finalize = handoffs["v1-bridge-finalize.yml"]
        verify = job_block(finalize, "verify-stage-response")
        publish = job_block(finalize, "attest-and-release")
        self.assertNotIn("contents: write", verify)
        self.assertNotIn("id-token: write", verify)
        self.assertIn("environment: suavoagent-v1-bridge-finalization", publish)
        self.assertIn("contents: write", publish)
        self.assertIn("id-token: write", publish)
        self.assertIn("attestations: write", publish)
        authorizer = (workflow_root / "v1-bridge-authorize.yml").read_text(encoding="utf-8")
        signer = (workflow_root / "production-signing.yml").read_text(encoding="utf-8")
        self.assertIn("uses: ./.github/workflows/production-signing.yml", authorizer)
        self.assertIn("v1_bridge_run_metadata.py write-descriptor", signer)
        self.assertIn("steps.stage.outputs.artifact_digest", signer)
        self.assertIn("AWS_SIGNING_ROLE_ARN", signer)
        self.assertNotIn("SIGNING_KEY_PEM", signer)
        exact_authorizer = (
            '[[ "$GITHUB_WORKFLOW_REF" == "MinaH153/SuavoAgent/.github/workflows/'
            'v1-bridge-authorize.yml@refs/heads/main" ]]'
        )
        self.assertEqual(3, signer.count(exact_authorizer))
        self.assertEqual(3, signer.count("GITHUB_WORKFLOW_REF"))
        descriptor_boundary = signer[
            signer.index("- name: Recompute the complete request") :
            signer.index("- name: Validate exact non-exportable", signer.index("- name: Recompute"))
        ]
        role_boundary = signer[
            signer.index("- name: Revalidate protected main") :
            signer.index("- name: Assume exact OIDC-bound", signer.index("- name: Revalidate protected main"))
        ]
        sign_boundary = signer[
            signer.index("- name: Sign descriptor specifically") :
            signer.index("- name: Upload separate authenticated", signer.index("- name: Sign descriptor specifically"))
        ]
        for block, operation in (
            (descriptor_boundary, "mkdir descriptor"),
            (role_boundary, 'gh api "repos/$GITHUB_REPOSITORY/actions/runs/$STAGE_RUN_ID"'),
            (sign_boundary, "bash scripts/aws-kms-sign-ecdsa-p256.sh"),
        ):
            self.assertEqual(1, block.count(exact_authorizer))
            self.assertLess(block.index(exact_authorizer), block.index(operation))
        self.assertIn("validate-descriptor", verify)
        self.assertIn("gh api --paginate --slurp", verify)
        self.assertIn("assert-version-newest", handoffs["v1-bridge-stage.yml"])
        self.assertIn(
            "--allow-missing-legacy-ota-signing-key-id",
            handoffs["v1-bridge-stage.yml"],
        )
        self.assertNotIn(
            "--allow-missing-legacy-ota-signing-key-id",
            (workflow_root / "production-release-signing.yml").read_text(
                encoding="utf-8"
            ),
        )
        self.assertIn("assert-version-newest", publish)
        self.assertEqual(
            2,
            handoffs["v1-bridge-stage.yml"].count(
                "Invoke-SuavoAgentInstallerRehearsal.ps1"
            ),
        )
        self.assertIn("-InstallerKind Msi", handoffs["v1-bridge-stage.yml"])
        self.assertIn("-InstallerKind Bundle", handoffs["v1-bridge-stage.yml"])
        self.assertIn("'/uninstall','/quiet','/norestart'", finalize)
        self.assertIn("gh release view", publish)
        self.assertIn("--json databaseId,isDraft,tagName,targetCommitish", publish)
        self.assertIn("An 11-field OTA success", publish)
        self.assertIn("graphical Burn bundle or MSI on every registered host", publish)
        self.assertNotIn("vars.AUTHENTICODE_SIGNER_SHA256", finalize)
        self.assertIn("authenticode_signer_sha256", finalize)
        self.assertGreaterEqual(finalize.count("assert-release-absent"), 2)
        self.assertNotIn('gh release edit "$VERSION"', publish)
        self.assertEqual(2, publish.count("validate-publication-state"))
        self.assertIn("--expected-immutable false", publish)
        self.assertIn("--expected-immutable true", publish)
        self.assertIn("--reference-assets prepublish-assets.json", publish)
        immutable_checks = [
            match.start()
            for match in re.finditer(
                '"repos/\\$GITHUB_REPOSITORY/immutable-releases"', publish
            )
        ]
        create_index = publish.index("gh release create")
        edit_index = publish.index("--method PATCH")
        self.assertEqual(2, len(immutable_checks))
        self.assertLess(immutable_checks[0], create_index)
        self.assertLess(create_index, immutable_checks[1])
        self.assertLess(immutable_checks[1], edit_index)
        tag_checks = [match.start() for match in re.finditer("git/ref/tags/\\$VERSION", publish)]
        self.assertTrue(any(index < edit_index for index in tag_checks))
        self.assertTrue(any(index > edit_index for index in tag_checks))

    def test_release_1_requires_full_installer_convergence_before_v2(self) -> None:
        signing = (ROOT / "docs/signing.md").read_text(encoding="utf-8")
        for marker in (
            "Mandatory fleet migration",
            "11-field OTA success is **not** trust convergence",
            "signed, PHI-negative convergence evidence",
            "additional v1-authorized",
        ):
            self.assertIn(marker, signing)

    def test_normal_release_and_hotfix_are_pinned_to_exact_v2_authority(self) -> None:
        for name in ("release.yml", "hotfix.yml"):
            document = (ROOT / ".github" / "workflows" / name).read_text(encoding="utf-8")
            preflight = job_block(document, "release-signing-preflight")
            with self.subTest(workflow=name):
                self.assertIn("v1_bridge_cli.py assert-normal-release", preflight)
                self.assertLess(
                    preflight.index("v1_bridge_cli.py assert-normal-release"),
                    preflight.index("missing=()"),
                )
                self.assertIn(".github/workflows/v1-bridge-stage.yml", (ROOT / "scripts/v1_bridge_release.py").read_text())
                self.assertIn("arn:aws:iam::855763870758:role/github-actions/SuavoAgentProductionOtaSigningV2", preflight)
                self.assertIn("AWS_SIGNING_REGION\" != \"us-east-1", preflight)
                self.assertIn("arn:aws:kms:us-east-1:855763870758:key/44bd84dc-8f6d-4692-b8ba-40a026db0331", preflight)


if __name__ == "__main__":
    unittest.main()
