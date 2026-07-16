#!/usr/bin/env python3
"""Fail closed when source-controlled release trust inputs are incomplete."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys


ROOT = Path(__file__).resolve().parents[1]
REQUIRED_TRACKED = (
    ".github/workflows/release.yml",
    ".github/workflows/hotfix.yml",
    ".github/workflows/v1-bridge-stage.yml",
    ".github/workflows/v1-bridge-authorize.yml",
    ".github/workflows/v1-bridge-finalize.yml",
    ".github/workflows/production-signing.yml",
    ".github/workflows/production-release-signing.yml",
    "global.json",
    "infrastructure/aws/suavoagent-production-signing-v2.template.json",
    "legal/external-assets.json",
    "legal/THIRD-PARTY-PROVENANCE.json",
    "legal/evidence/llamasharp-backend-cpu-0.24.0.json",
    "legal/evidence/qwen3-1.7b-q4-k-m.json",
    "legal/evidence/tesseract-native-5.2.0-eng.json",
    "legal/package-license-evidence.json",
    "scripts/Test-SuavoAgentReleaseProbe.ps1",
    "scripts/Test-SuavoAgentReleaseProbe.Legal.ps1",
    "scripts/Test-InstallerAuthenticode.ps1", "scripts/Invoke-SuavoAgentInstallerRehearsal.ps1",
    "tests/test_installer_rehearsal_script.py",
    "tests/test_resolve_release_rollback_evidence.py",
    "scripts/aggregate_coverage.py",
    "scripts/coverage_model.py",
    "scripts/coverage-noninstrumentable-sources.json",
    "scripts/generate-release-sbom.py", "scripts/generate-release-legal-bundle.py",
    "scripts/release_legal_catalog.py",
    "scripts/release_legal_evidence.py",
    "scripts/resolve-release-rollback-evidence.py",
    "scripts/ota_update_trust_roots.py",
    "scripts/ecdsa_der_to_p1363.py",
    "scripts/esigner-codesign-hardened.sh",
    "scripts/sign-ota-v1-bridge-local.sh",
    "scripts/sign-ota-v1-convergence-local.sh",
    "scripts/v1_bridge_cli.py",
    "scripts/v1_bridge_crypto.py",
    "scripts/v1_bridge_file_io.py",
    "scripts/v1_bridge_handoff.py",
    "scripts/v1_bridge_convergence.py",
    "scripts/v1_bridge_convergence_cli.py",
    "scripts/v1_bridge_release.py",
    "scripts/v1_bridge_run_metadata.py",
    "scripts/validate-release-tag-ruleset.py",
    "scripts/verify-live-v1-bridge-release.sh",
    "security/ota-update-trust-roots.json",
    "src/SuavoAgent.Contracts/Maintenance/OtaUpdateTrust.cs",
    "tests/test_ota_update_trust_roots.py",
    "tests/test_v1_bridge_release.py",
    "tests/test_v1_bridge_convergence.py",
    "tests/test_sign_ota_v1_bridge_local.py",
    "scripts/sync-pinned-package-license-evidence.py",
    "scripts/verify-external-release-assets.py",
    "legal/license-texts/Apache-2.0.txt",
    "legal/license-texts/Leptonica-BSD-2-Clause.txt",
    "legal/license-texts/llama.cpp-0.24.0-MIT.txt",
    "legal/license-texts/MPL-2.0.txt",
    "legal/license-texts/JsonCanonicalization-Apache-NOTICE.txt",
    "legal/license-texts/V8-DToA-BSD-3-Clause.txt",
    "legal/license-texts/NumberDToA-NOTICE.txt",
    "legal/vendored/json-canonicalization.json",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/JsonCanonicalizer.cs",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/NumberCachedPowers.cs",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/NumberDToA.Infrastructure.cs",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/NumberDToA.cs",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/NumberDToA.Formatting.cs",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/NumberDiyFp.cs",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/NumberDoubleHelper.cs",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/NumberFastDToA.cs",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/NumberFastDToABuilder.cs",
    "src/SuavoAgent.Diagnostics/ThirdParty/JsonCanonicalization/NumberToJson.cs",
    "src/SuavoAgent.Helper/Assets/pharmacist-panda-v2.png",
    "src/SuavoAgent.Helper/Assets/README.md",
    "src/SuavoAgent.Setup/Legal/THIRD-PARTY-NOTICES.txt",
)
CONVERGENCE_INPUT_TRACKED = frozenset(
    {
        "security/ota-fleet-inventory-snapshot.json",
        "security/ota-fleet-inventory-snapshot.sig",
        "security/ota-v1-bridge-convergence-evidence.json",
    }
)
CONVERGENCE_CLAIM_TRACKED = frozenset(
    {
        "security/ota-v1-bridge-convergence-claim.json",
        "security/ota-v1-bridge-convergence-claim.sig",
    }
)
CONVERGENCE_TRACKED = CONVERGENCE_INPUT_TRACKED | CONVERGENCE_CLAIM_TRACKED
FORBIDDEN_WORKFLOW_MARKERS = (
    "SIGNING_KEY_PEM",
    "resolve/main",
    "dotnet-version: 8.0.x",
)
REQUIRED_WORKFLOW_MARKERS = (
    "environment: suavoagent-production-signing",
    "actions/attest-build-provenance@977bb373ede98d70efdf65b84cb5f73e068dcc2a",
    "AUTHENTICODE_SIGNER_SHA256",
    "OTA_KMS_KEY_ID",
    "--require-release-eligible",
    "verify-external-release-assets.py",
    "resolve-release-rollback-evidence.py",
    "ota_update_trust_roots.py",
    "legal/evidence",
    "--minimum-line 80",
    "--minimum-branch 80",
    "--require-all-projects",
    "v1_bridge_cli.py assert-normal-release",
    "OTA_FULL_COHORT_MANIFEST",
    "ota-v1-bridge-convergence-claim.sig",
    "arn:aws:iam::855763870758:role/github-actions/SuavoAgentProductionOtaSigningV2",
    "AWS_SIGNING_REGION\" != \"us-east-1",
    "arn:aws:kms:us-east-1:855763870758:key/44bd84dc-8f6d-4692-b8ba-40a026db0331",
)
CALLER_WORKFLOW_MARKERS = (
    "workflow_dispatch:",
    "--trust-phase normal-v2",
    "uses: ./.github/workflows/production-release-signing.yml",
    "needs: [build, sign_windows, sign_msi, sign_bundle, windows-release-smoke]",
)
REUSABLE_RELEASE_MARKERS = (
    "workflow_call:",
    "MinaH153/SuavoAgent/.github/workflows/release.yml@refs/heads/main",
    "MinaH153/SuavoAgent/.github/workflows/hotfix.yml@refs/heads/main",
    '[[ "$GITHUB_REF_PROTECTED" == "true" ]]',
    "output-env-credentials: false",
    "role-duration-seconds: 900",
    "verify-live-v1-bridge-release.sh",
    "gh release create",
    "validate-publication-state",
    "immutable-releases",
    "--method PATCH",
    "--expected-immutable true",
    "--trust-phase normal-v2",
    '"otaSigningKeyId": "ota-update-v2"',
    '--bridge-release-tag "$BRIDGE_TAG"',
    '--bridge-source-sha "$BRIDGE_SOURCE_SHA"',
    '--bridge-receipt-sha256 "$BRIDGE_RECEIPT_SHA"',
)
EXPECTED_BRIDGE_AUTHORIZER_WORKFLOW_REF = (
    "MinaH153/SuavoAgent/.github/workflows/"
    "v1-bridge-authorize.yml@refs/heads/main"
)
EXPECTED_BRIDGE_AUTHORIZER_ASSERTION = (
    '[[ "$GITHUB_WORKFLOW_REF" == "'
    + EXPECTED_BRIDGE_AUTHORIZER_WORKFLOW_REF
    + '" ]]'
)

PRODUCTION_SIGNING_TEMPLATE = (
    ROOT / "infrastructure/aws/suavoagent-production-signing-v2.template.json"
)
EXPECTED_GITHUB_OIDC_PROVIDER_ARN = (
    "arn:aws:iam::855763870758:oidc-provider/token.actions.githubusercontent.com"
)
EXPECTED_OTA_KMS_KEY_ARN = (
    "arn:aws:kms:us-east-1:855763870758:key/44bd84dc-8f6d-4692-b8ba-40a026db0331"
)
EXPECTED_GITHUB_OIDC_SUBJECT = (
    "repo:MinaH153/SuavoAgent:environment:suavoagent-production-signing"
)
EXPECTED_GITHUB_OIDC_BASE_CONDITIONS = {
    "token.actions.githubusercontent.com:aud": "sts.amazonaws.com",
    "token.actions.githubusercontent.com:sub": EXPECTED_GITHUB_OIDC_SUBJECT,
    "token.actions.githubusercontent.com:repository_id": "1206437092",
    "token.actions.githubusercontent.com:repository_owner_id": "256881824",
    "token.actions.githubusercontent.com:repository": "MinaH153/SuavoAgent",
    "token.actions.githubusercontent.com:ref": "refs/heads/main",
    "token.actions.githubusercontent.com:environment": "suavoagent-production-signing",
}
EXPECTED_GITHUB_OIDC_CALL_PAIRS = (
    (
        "GitHubProtectedBridgeAuthorizerOnly",
        "OTA v1 bridge - authorize",
        "MinaH153/SuavoAgent/.github/workflows/production-signing.yml@refs/heads/main",
    ),
    (
        "GitHubProtectedNormalReleaseOnly",
        "Release",
        "MinaH153/SuavoAgent/.github/workflows/production-release-signing.yml@refs/heads/main",
    ),
    (
        "GitHubProtectedHotfixOnly",
        "Hotfix",
        "MinaH153/SuavoAgent/.github/workflows/production-release-signing.yml@refs/heads/main",
    ),
)
SETUP_JAVA_PIN = "actions/setup-java@0f481fcb613427c0f801b606911222b5b6f3083a"
ESIGNER_TOOL_SHA256 = "359782cee5c709b172610e2abd8cb49445bfadd26f44073ca18600c585b91b8d"


def fail(message: str) -> None:
    print(message, file=sys.stderr)
    raise SystemExit(1)


def trust_phase_required(phase: str, tracked: set[str]) -> frozenset[str]:
    if phase == "bridge-v1":
        forbidden = sorted(CONVERGENCE_TRACKED & tracked)
        if forbidden:
            fail(
                "bridge-v1 source must not contain post-install convergence artifacts: "
                + ", ".join(forbidden)
            )
        return frozenset()
    if phase == "convergence-v1":
        missing = sorted(CONVERGENCE_INPUT_TRACKED - tracked)
        forbidden = sorted(CONVERGENCE_CLAIM_TRACKED & tracked)
        if missing or forbidden:
            fail(
                "convergence-v1 source requires exact signed inputs and no claim outputs; "
                + ("missing: " + ", ".join(missing) if missing else "")
                + ("; " if missing and forbidden else "")
                + ("forbidden: " + ", ".join(forbidden) if forbidden else "")
            )
        return CONVERGENCE_INPUT_TRACKED
    if phase == "normal-v2":
        missing = sorted(CONVERGENCE_TRACKED - tracked)
        if missing:
            fail(
                "normal-v2 source is missing signed convergence artifacts: "
                + ", ".join(missing)
            )
        return CONVERGENCE_TRACKED
    fail("release trust phase is unsupported")


def resolve_trust_phase(requested: str, tracked: set[str], signing_key_id: str) -> str:
    if requested != "auto":
        expected_key = "ota-update-v2" if requested == "normal-v2" else "ota-update-v1"
        if signing_key_id != expected_key:
            fail(f"{requested} requires source signingKeyId={expected_key}")
        trust_phase_required(requested, tracked)
        return requested
    present = CONVERGENCE_TRACKED & tracked
    if signing_key_id == "ota-update-v1" and not present:
        return "bridge-v1"
    if signing_key_id == "ota-update-v1" and present == CONVERGENCE_INPUT_TRACKED:
        return "convergence-v1"
    if signing_key_id == "ota-update-v2" and present == CONVERGENCE_TRACKED:
        return "normal-v2"
    fail("source registry and tracked convergence artifacts do not form an allowed trust phase")


def validate_workflow_text(
    workflow_name: str, workflow: str, production_release_workflow: str
) -> None:
    combined = workflow + "\n" + production_release_workflow
    for forbidden in FORBIDDEN_WORKFLOW_MARKERS:
        if forbidden in combined:
            fail(
                f"forbidden mutable/exportable release input remains in "
                f"{workflow_name}: {forbidden}"
            )
    for required_text in CALLER_WORKFLOW_MARKERS:
        if required_text not in workflow:
            fail(f"release caller workflow gate missing in {workflow_name}: {required_text}")
    for required_text in REUSABLE_RELEASE_MARKERS:
        if required_text not in production_release_workflow:
            fail(f"reusable release workflow gate missing for {workflow_name}: {required_text}")
    for required_text in REQUIRED_WORKFLOW_MARKERS:
        if required_text not in combined:
            fail(f"release trust workflow gate missing in {workflow_name}: {required_text}")


def validate_immutable_publication_order(workflow_name: str, workflow: str) -> None:
    endpoint = '"repos/$GITHUB_REPOSITORY/immutable-releases"'
    create = workflow.find("gh release create")
    publish = workflow.find("--method PATCH", create + 1)
    checks = [match.start() for match in re.finditer(re.escape(endpoint), workflow)]
    if (
        create < 0
        or publish < 0
        or len(checks) != 2
        or not checks[0] < create < checks[1] < publish
    ):
        fail(
            f"immutable release support must be proven before draft creation and "
            f"revalidated before publication in {workflow_name}"
        )


def validate_hardened_signing_workflow(
    workflow_name: str, workflow: str, expected_signing_jobs: int
) -> None:
    if "sslcom/actions-codesigner@" in workflow:
        fail(f"deprecated eSigner container action remains in {workflow_name}")
    for marker in (
        SETUP_JAVA_PIN,
        "java-version: '11.0.31+11'",
        "verify-signature: true",
        "scripts/esigner-codesign-hardened.sh",
    ):
        if workflow.count(marker) != expected_signing_jobs:
            fail(f"hardened eSigner boundary drifted in {workflow_name}: {marker}")


def validate_bridge_signing_workflow(workflow: str) -> None:
    """Bind every privileged bridge phase to the exact protected caller."""
    assertion = EXPECTED_BRIDGE_AUTHORIZER_ASSERTION
    if workflow.count(assertion) != 3 or workflow.count("GITHUB_WORKFLOW_REF") != 3:
        fail("v1 bridge signer must assert the exact authorizer workflow at three boundaries")

    boundaries = (
        (
            "- name: Recompute the complete request and Authenticode-tested cohort binding",
            "- name: Validate exact non-exportable v2 signing authority",
            "mkdir descriptor",
        ),
        (
            "- name: Revalidate protected main and completed stage immediately before role assumption",
            "- name: Assume exact OIDC-bound production signer role",
            "gh api \"repos/$GITHUB_REPOSITORY/actions/runs/$STAGE_RUN_ID\"",
        ),
        (
            "- name: Sign descriptor specifically with reviewed ota-update-v2",
            "- name: Upload separate authenticated handoff descriptor",
            "bash scripts/aws-kms-sign-ecdsa-p256.sh",
        ),
    )
    for start_marker, end_marker, privileged_operation in boundaries:
        try:
            start = workflow.index(start_marker)
            end = workflow.index(end_marker, start)
        except ValueError:
            fail("v1 bridge signer privileged boundary is missing")
        block = workflow[start:end]
        if assertion not in block or privileged_operation not in block:
            fail("v1 bridge signer privileged boundary is incomplete")
        if block.count(assertion) != 1 or block.index(assertion) > block.index(privileged_operation):
            fail("v1 bridge signer authorizer identity check is not just-in-time")


def validate_hardened_signing_scripts(signer: str, installer: str) -> None:
    for marker in (
        "https://github.com/SSLcom/CodeSignTool/releases/download/v1.3.0/CodeSignTool-v1.3.0.zip",
        ESIGNER_TOOL_SHA256,
        'java_vendor" == "Eclipse Adoptium"',
        'java_runtime" == "11.0.31+11"',
        "args=(",
        "-malware_block=true",
    ):
        if marker not in signer:
            fail(f"hardened eSigner script is missing: {marker}")
    if "bash -c" in signer or re.search(r"\beval\b", signer):
        fail("hardened eSigner script must never rebuild a secret-bearing command string")
    for marker in (
        "TimeStamperCertificate",
        "1.3.6.1.5.5.7.3.8",
        "signtool.exe",
        "verify /pa /all /tw",
    ):
        if marker not in installer:
            fail(f"installer RFC3161 verification is missing: {marker}")


def validate_production_signing_template(template: dict[str, object]) -> None:
    """Preserve the full live stack while narrowing its exact workflow trust."""
    if "Parameters" in template:
        fail("production signing stack update must preserve its managed resource identities")
    if template.get("Description") != (
        "SuavoAgent production OTA v2 signing root, GitHub Actions OIDC provider, "
        "and exact protected-main workflow trust. Update-only template for the existing "
        "SuavoAgentProductionOtaSigningV2 stack; never create a replacement stack."
    ):
        fail("production signing template must identify the update-only live stack boundary")
    resources = template.get("Resources")
    if not isinstance(resources, dict) or set(resources) != {
        "GitHubActionsOidcProvider",
        "GitHubProductionSigningRole",
        "OtaSigningKey",
        "OtaSigningKeyAlias",
        "GitHubProductionSigningRoleInlinePolicy",
    }:
        fail("production signing template must preserve the exact live stack resource set")

    provider = resources.get("GitHubActionsOidcProvider")
    if provider != {
        "Type": "AWS::IAM::OIDCProvider",
        "Properties": {
            "Url": "https://token.actions.githubusercontent.com",
            "ClientIdList": ["sts.amazonaws.com"],
            "Tags": [
                {"Key": "Application", "Value": "SuavoAgent"},
                {"Key": "Purpose", "Value": "ProductionOtaSigning"},
            ],
        },
    }:
        fail("production signing template drifted the stack-owned GitHub OIDC provider")

    role = resources.get("GitHubProductionSigningRole")
    if (
        not isinstance(role, dict)
        or role.get("Type") != "AWS::IAM::Role"
        or set(role) != {"Type", "Properties"}
    ):
        fail("production signing template is missing the GitHub signing role")
    role_properties = role.get("Properties")
    if not isinstance(role_properties, dict):
        fail("production signing role properties are missing")
    if (
        role_properties.get("RoleName") != "SuavoAgentProductionOtaSigningV2"
        or role_properties.get("Path") != "/github-actions/"
        or role_properties.get("MaxSessionDuration") != 3600
        or set(role_properties) != {
            "RoleName", "Path", "Description", "MaxSessionDuration",
            "AssumeRolePolicyDocument",
        }
    ):
        fail("production signing role identity or session boundary drifted")
    expected_trust = {
        "Version": "2012-10-17",
        "Statement": [
            {
                "Sid": sid,
                "Effect": "Allow",
                "Principal": {"Federated": {"Ref": "GitHubActionsOidcProvider"}},
                "Action": "sts:AssumeRoleWithWebIdentity",
                "Condition": {
                    "StringEquals": EXPECTED_GITHUB_OIDC_BASE_CONDITIONS
                    | {
                        "token.actions.githubusercontent.com:workflow": workflow,
                        "token.actions.githubusercontent.com:job_workflow_ref": called_workflow,
                    }
                },
            }
            for sid, workflow, called_workflow in EXPECTED_GITHUB_OIDC_CALL_PAIRS
        ],
    }
    if role_properties.get("AssumeRolePolicyDocument") != expected_trust:
        fail("production signing OIDC trust must bind the exact repo, main ref, caller, reusable signer, and environment")

    role_arn = {"Fn::GetAtt": ["GitHubProductionSigningRole", "Arn"]}
    expected_key = {
        "Type": "AWS::KMS::Key",
        "DeletionPolicy": "Retain",
        "UpdateReplacePolicy": "Retain",
        "Properties": {
            "Description": "SuavoAgent production OTA update manifests and release checksums, v2 root",
            "Enabled": True,
            "KeySpec": "ECC_NIST_P256",
            "KeyUsage": "SIGN_VERIFY",
            "Origin": "AWS_KMS",
            "PendingWindowInDays": 30,
            "KeyPolicy": {
                "Version": "2012-10-17",
                "Statement": [
                    {
                        "Sid": "AccountKeyAdministrationWithoutSigningOrGrants",
                        "Effect": "Allow",
                        "Principal": {"AWS": {"Fn::Sub": (
                            "arn:${AWS::Partition}:iam::${AWS::AccountId}:root"
                        )}},
                        "Action": [
                            "kms:CancelKeyDeletion", "kms:CreateAlias", "kms:DeleteAlias",
                            "kms:DescribeKey", "kms:DisableKey", "kms:DisableKeyRotation",
                            "kms:EnableKey", "kms:EnableKeyRotation", "kms:GetKeyPolicy",
                            "kms:GetKeyRotationStatus", "kms:GetPublicKey", "kms:ListAliases",
                            "kms:ListGrants", "kms:ListKeyPolicies", "kms:ListResourceTags",
                            "kms:PutKeyPolicy", "kms:RevokeGrant", "kms:ScheduleKeyDeletion",
                            "kms:TagResource", "kms:UntagResource", "kms:UpdateAlias",
                            "kms:UpdateKeyDescription",
                        ],
                        "Resource": "*",
                    },
                    {
                        "Sid": "GitHubRoleMayReadPublicKey",
                        "Effect": "Allow",
                        "Principal": {"AWS": role_arn},
                        "Action": "kms:GetPublicKey",
                        "Resource": "*",
                    },
                    {
                        "Sid": "GitHubRoleMaySignOnlyRawEcdsaSha256",
                        "Effect": "Allow",
                        "Principal": {"AWS": role_arn},
                        "Action": "kms:Sign",
                        "Resource": "*",
                        "Condition": {"StringEquals": {
                            "kms:MessageType": "RAW",
                            "kms:SigningAlgorithm": "ECDSA_SHA_256",
                        }},
                    },
                ],
            },
        },
    }
    if resources.get("OtaSigningKey") != expected_key:
        fail("production signing stack-owned KMS key identity, policy, or retention drifted")

    if resources.get("OtaSigningKeyAlias") != {
        "Type": "AWS::KMS::Alias",
        "Properties": {
            "AliasName": "alias/suavoagent-production-ota-update-v2",
            "TargetKeyId": {"Ref": "OtaSigningKey"},
        },
    }:
        fail("production signing stack-owned KMS alias identity drifted")

    expected_key_arn = {"Fn::GetAtt": ["OtaSigningKey", "Arn"]}
    expected_use_statements = [
        {
            "Sid": "ReadOnlyTheV2PublicKey",
            "Effect": "Allow",
            "Action": "kms:GetPublicKey",
            "Resource": expected_key_arn,
        },
        {
            "Sid": "SignOnlyRawEcdsaSha256WithV2",
            "Effect": "Allow",
            "Action": "kms:Sign",
            "Resource": expected_key_arn,
            "Condition": {
                "StringEquals": {
                    "kms:MessageType": "RAW",
                    "kms:SigningAlgorithm": "ECDSA_SHA_256",
                }
            },
        },
    ]
    role_policy = resources.get("GitHubProductionSigningRoleInlinePolicy")
    if not isinstance(role_policy, dict) or not isinstance(role_policy.get("Properties"), dict):
        fail("production signing role policy is missing")
    role_policy_properties = role_policy["Properties"]
    policy_document = role_policy_properties.get("PolicyDocument")
    if (
        role_policy.get("Type") != "AWS::IAM::Policy"
        or role_policy_properties.get("PolicyName") != "SuavoAgentProductionOtaSigningV2"
        or role_policy_properties.get("Roles") != [{"Ref": "GitHubProductionSigningRole"}]
        or policy_document != {
            "Version": "2012-10-17", "Statement": expected_use_statements
        }
    ):
        fail("production signing role may only GetPublicKey and Sign on the exact v2 key")

    outputs = template.get("Outputs")
    if not isinstance(outputs, dict) or any(
        re.fullmatch(r"[A-Za-z0-9]+", name) is None for name in outputs
    ):
        fail("production signing CloudFormation output logical IDs must be alphanumeric")
    expected_outputs = {
        "GithubOidcProviderArn": {"Ref": "GitHubActionsOidcProvider"},
        "AwsSigningRoleArn": {"Fn::GetAtt": ["GitHubProductionSigningRole", "Arn"]},
        "AwsSigningRegion": {"Ref": "AWS::Region"},
        "OtaKmsKeyId": expected_key_arn,
        "OtaKmsKeyAlias": {"Ref": "OtaSigningKeyAlias"},
    }
    if set(outputs) != set(expected_outputs):
        fail("production signing CloudFormation outputs drifted")
    for name, expected_value in expected_outputs.items():
        output = outputs.get(name)
        if not isinstance(output, dict) or output.get("Value") != expected_value:
            fail(f"production signing CloudFormation output {name} drifted")


def git(*arguments: str) -> str:
    return subprocess.check_output(
        ("git", *arguments), cwd=ROOT, text=True, stderr=subprocess.STDOUT
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--allow-dirty", action="store_true")
    parser.add_argument(
        "--trust-phase",
        choices=("auto", "bridge-v1", "convergence-v1", "normal-v2"),
        required=True,
    )
    args = parser.parse_args()
    if not args.allow_dirty and git("status", "--porcelain", "--untracked-files=all").strip():
        fail("release source checkout is not clean")

    tracked = set(git("ls-files").splitlines())
    from ota_update_trust_roots import load_registry_configuration

    signing_key_id, _ = load_registry_configuration(
        ROOT / "security/ota-update-trust-roots.json"
    )
    trust_phase = resolve_trust_phase(args.trust_phase, tracked, signing_key_id)
    required = set(REQUIRED_TRACKED)
    required.update(trust_phase_required(trust_phase, tracked))
    package_licenses = json.loads(
        (ROOT / "legal/package-license-evidence.json").read_text()
    )
    required.update(
        entry["retainedFile"] for entry in package_licenses["evidence"]
    )
    external_assets = json.loads((ROOT / "legal/external-assets.json").read_text())
    required.update(
        license_entry["path"]
        for asset in external_assets["assets"]
        for license_entry in asset.get("licenseFiles", [])
    )
    required.update(
        evidence["path"]
        for asset in external_assets["assets"]
        if isinstance((evidence := asset.get("provenanceEvidence")), dict)
    )
    solution = (ROOT / "SuavoAgent.sln").read_text()
    project_paths = {
        match.replace("\\", "/")
        for match in re.findall(r'"([^"\r\n]+\.csproj)"', solution)
    }
    required.update(
        str(Path(project).parent / "packages.lock.json") for project in project_paths
    )
    missing = sorted(
        name for name in required
        if name not in tracked and (not args.allow_dirty or not (ROOT / name).is_file())
    )
    if missing:
        fail("release inputs absent from clean clone: " + ", ".join(missing))

    from v1_bridge_release import assert_bridge_source, assert_normal_release

    if trust_phase == "bridge-v1":
        assert_bridge_source(ROOT / "security/ota-update-trust-roots.json")
    elif trust_phase == "convergence-v1":
        from v1_bridge_convergence import (
            DEFAULT_EVIDENCE,
            DEFAULT_INVENTORY,
            DEFAULT_INVENTORY_SIGNATURE,
            validate_evidence_bundle,
        )

        assert_bridge_source(ROOT / "security/ota-update-trust-roots.json")
        validate_evidence_bundle(
            DEFAULT_EVIDENCE,
            DEFAULT_INVENTORY,
            DEFAULT_INVENTORY_SIGNATURE,
            ROOT / "security/ota-update-trust-roots.json",
        )
    else:
        assert_normal_release(
            ROOT / "security/ota-update-trust-roots.json", "true"
        )

    global_json = json.loads((ROOT / "global.json").read_text())
    if global_json.get("sdk") != {
        "version": "8.0.128",
        "rollForward": "disable",
        "allowPrerelease": False,
    }:
        fail("global.json must pin the reviewed SDK without roll-forward")

    panda = ROOT / "src/SuavoAgent.Helper/Assets/pharmacist-panda-v2.png"
    panda_hash = hashlib.sha256(panda.read_bytes()).hexdigest()
    external = json.loads((ROOT / "legal/external-assets.json").read_text())
    panda_entry = next(asset for asset in external["assets"] if asset["id"] == "pharmacist-panda")
    if panda_hash != panda_entry["sha256"]:
        fail("pharmacist panda does not match its reviewed provenance digest")

    production_release_workflow = (
        ROOT / ".github/workflows/production-release-signing.yml"
    ).read_text()
    validate_immutable_publication_order(
        ".github/workflows/production-release-signing.yml",
        production_release_workflow,
    )
    validate_immutable_publication_order(
        ".github/workflows/v1-bridge-finalize.yml",
        (ROOT / ".github/workflows/v1-bridge-finalize.yml").read_text(),
    )
    for workflow_name in (
        ".github/workflows/release.yml",
        ".github/workflows/hotfix.yml",
    ):
        workflow = (ROOT / workflow_name).read_text()
        validate_workflow_text(workflow_name, workflow, production_release_workflow)
        validate_hardened_signing_workflow(workflow_name, workflow, 3)

    bridge_stage = (ROOT / ".github/workflows/v1-bridge-stage.yml").read_text()
    validate_hardened_signing_workflow(
        ".github/workflows/v1-bridge-stage.yml", bridge_stage, 3
    )
    validate_bridge_signing_workflow(
        (ROOT / ".github/workflows/production-signing.yml").read_text()
    )
    validate_hardened_signing_scripts(
        (ROOT / "scripts/esigner-codesign-hardened.sh").read_text(),
        (ROOT / "scripts/Test-InstallerAuthenticode.ps1").read_text(),
    )
    for workflow_name, required_count in (
        (".github/workflows/release.yml", 1),
        (".github/workflows/hotfix.yml", 1),
        (".github/workflows/v1-bridge-stage.yml", 1),
        (".github/workflows/v1-bridge-finalize.yml", 2),
    ):
        document = (ROOT / workflow_name).read_text()
        if document.count("Test-InstallerAuthenticode.ps1") != required_count:
            fail(f"installer RFC3161 workflow gate drifted in {workflow_name}")
    for workflow_name in (".github/workflows/release.yml",
                          ".github/workflows/hotfix.yml",
                          ".github/workflows/v1-bridge-stage.yml"):
        document = (ROOT / workflow_name).read_text()
        required_rehearsal_markers = (
            "-InstallerKind Msi", "-InstallerKind Bundle",
            "-EvidenceDirectory 'installer-rehearsal-evidence/msi'",
            "-EvidenceDirectory 'installer-rehearsal-evidence/bundle'",
            "Upload installer rehearsal evidence", "path: installer-rehearsal-evidence/",
        )
        if document.count("Invoke-SuavoAgentInstallerRehearsal.ps1") != 2:
            fail(f"installer rehearsal invocation drifted in {workflow_name}")
        if document.count("-MsiPath $msi[0].FullName") != 2:
            fail(f"installer rehearsal MSI identity drifted in {workflow_name}")
        for marker in required_rehearsal_markers:
            if marker not in document: fail(
                f"installer rehearsal marker {marker!r} missing in {workflow_name}")

    validate_production_signing_template(
        json.loads(PRODUCTION_SIGNING_TEMPLATE.read_text())
    )

    provenance = json.loads((ROOT / "legal/THIRD-PARTY-PROVENANCE.json").read_text())
    forbidden_canonicalizer_packages = {
        package["name"] for package in provenance["packages"]
        if package["name"].casefold() in {"jsoncanonicalizer", "es6numberserializer"}
    }
    if forbidden_canonicalizer_packages:
        fail(
            "replaced unknown-license canonicalizer packages remain in release closure: "
            + ", ".join(sorted(forbidden_canonicalizer_packages))
        )
    forbidden_native = {
        package["name"] for package in provenance["packages"]
        if any(token in package["name"].casefold()
               for token in ("nativeassets.linux", "nativeassets.macos", "nativeassets.webassembly"))
    }
    if forbidden_native:
        fail("non-Windows packages leaked into win-x64 closure: " + ", ".join(sorted(forbidden_native)))
    vendored = provenance.get("vendoredSources")
    if not isinstance(vendored, list) or len(vendored) != 1:
        fail("exact vendored JSON canonicalization provenance is missing")
    canonicalizer = vendored[0]
    manifest = ROOT / "legal/vendored/json-canonicalization.json"
    if (
        canonicalizer.get("id") != "cyberphone-json-canonicalization-dotnet"
        or canonicalizer.get("manifestSha256")
        != hashlib.sha256(manifest.read_bytes()).hexdigest()
    ):
        fail("vendored JSON canonicalization manifest digest is stale")
    package_license_catalog = ROOT / "legal/package-license-evidence.json"
    if provenance.get("packageLicenseEvidenceCatalogSha256") != hashlib.sha256(
        package_license_catalog.read_bytes()
    ).hexdigest():
        fail("package license evidence catalog digest is stale")

    notices = (ROOT / "src/SuavoAgent.Setup/Legal/THIRD-PARTY-NOTICES.txt").read_text()
    for required_text in (
        "Apache License",
        "MIT License",
        "Copyright (C) 2001-2020 Leptonica",
        "Copyright 2018 The ANGLE Project Authors",
        "# HarfBuzz",
        "Mozilla Public License Version 2.0",
        "Copyright 2010 the V8 project authors",
        "The author of this software is David M. Gay.",
        "Copyright (c) 2023 SciSharp STACK",
        "Copyright (c) 2023-2024 The ggml authors",
        "Copyright 2012-2020 Charles Weld",
        "MICROSOFT .NET RUNTIME 8.0.28",
    ):
        if required_text not in notices:
            fail(f"required retained license or copyright text is missing: {required_text}")

    subprocess.run(
        [
            sys.executable,
            "scripts/generate-release-legal-bundle.py",
            "--check",
            "--require-release-eligible",
        ],
        cwd=ROOT,
        check=True,
    )
    subprocess.run(
        [sys.executable, "scripts/sync-pinned-package-license-evidence.py"],
        cwd=ROOT,
        check=True,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
