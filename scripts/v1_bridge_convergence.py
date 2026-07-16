#!/usr/bin/env python3
"""Verify v2-attested, authoritative full-fleet convergence before v1 retirement."""

from __future__ import annotations

import base64
import binascii
from datetime import datetime, timedelta, timezone
from pathlib import Path
import re

from ota_update_trust_roots import TrustRegistryError, _strict_p256_spki, verify_signature_bytes
from v1_bridge_crypto import HistoricKeyError, open_historic_key
from v1_bridge_release import (
    BridgeError,
    COMMIT_RE,
    DEFAULT_REGISTRY,
    REPOSITORY,
    ROOT,
    SHA_RE,
    V1_KEY_ID,
    V2_KEY_ID,
    _canonical_json,
    _exclusive_write,
    _read_regular,
    _registry,
    _sha256,
    _strict_json,
    _strict_sha,
    _version_parts,
    assert_bridge_source,
)


CEREMONY = "ota-update-v1-bridge-fleet-convergence"
INVENTORY_PURPOSE = "authoritative-phi-negative-registered-host-inventory"
ATTESTATION_PURPOSE = "suavoagent-release1-device-convergence-attestation"
ATTESTATION_AUTHORITY = "enrolled-device-attestation-key-v1"
INSTALL_RECEIPT_PURPOSE = "suavoagent-release1-full-installer-receipt"
RESTART_RECEIPT_PURPOSE = "suavoagent-release1-post-install-restart-receipt"
V1_NOOP_RECEIPT_PURPOSE = "suavoagent-release1-v1-noop-rehearsal-receipt"
INSTALL_MODE = "full-reinstall"
RESTART_OUTCOME = "release1-active-after-restart"
V1_NOOP_OUTCOME = "already-current-noop"
PHI_CLASSIFICATION = "phi-negative"
EVIDENCE_RELATIVE_PATH = "security/ota-v1-bridge-convergence-evidence.json"
INVENTORY_RELATIVE_PATH = "security/ota-fleet-inventory-snapshot.json"
INVENTORY_SIGNATURE_RELATIVE_PATH = "security/ota-fleet-inventory-snapshot.sig"
DEFAULT_EVIDENCE = ROOT / EVIDENCE_RELATIVE_PATH
DEFAULT_INVENTORY = ROOT / INVENTORY_RELATIVE_PATH
DEFAULT_INVENTORY_SIGNATURE = ROOT / INVENTORY_SIGNATURE_RELATIVE_PATH
DEFAULT_CLAIM = ROOT / "security/ota-v1-bridge-convergence-claim.json"
DEFAULT_SIGNATURE = ROOT / "security/ota-v1-bridge-convergence-claim.sig"
UTC_RE = re.compile(
    r"20[0-9]{2}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12][0-9]|3[01])"
    r"T(?:[01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]Z"
)
P1363_HEX_RE = re.compile(r"[0-9a-f]{128}")
BASE64URL_P1363_RE = re.compile(r"[A-Za-z0-9_-]{86}")
MAX_INVENTORY_VALIDITY = timedelta(days=7)
MAX_INVENTORY_ISSUE_DELAY = timedelta(minutes=5)
INVENTORY_KEYS = frozenset(
    {
        "schemaVersion", "purpose", "repository", "bridgeReleaseTag",
        "bridgeSourceSha", "releasePublishedAtUtc", "snapshotCutoffUtc",
        "fleetRegistryEpoch", "enrollmentClosed", "registeredHostCount",
        "registeredHostSetSha256", "issuedAtUtc", "expiresAtUtc",
        "registeredHosts", "releaseBindings",
    }
)
INVENTORY_HOST_KEYS = frozenset(
    {
        "hostDigest", "attestationKeyId", "attestationPublicKeySpkiDerBase64",
        "maintenanceKeyId", "maintenancePublicKeySpkiDerBase64",
    }
)
RELEASE_KEYS = frozenset(
    {
        "burnArtifactName", "burnArtifactSha256", "msiArtifactName",
        "msiArtifactSha256", "coreArtifactSha256", "brokerArtifactSha256",
        "helperArtifactSha256", "watchdogArtifactSha256", "maintenanceHostSha256",
        "releaseReceiptSha256", "checksumsSha256", "checksumsSignatureSha256",
        "updateManifestName", "updateManifestSha256",
        "updateManifestSignatureSha256",
    }
)
EVIDENCE_KEYS = frozenset(
    {"schemaVersion", "ceremony", "bridgeReleaseTag", "bridgeSourceSha", "machines"}
)
MACHINE_KEYS = frozenset(
    {
        "attestation", "attestationSignatureBase64Url",
        "installReceiptSignatureBase64Url",
    }
)
ATTESTATION_KEYS = frozenset(
    {
        "schemaVersion", "purpose", "attestationAuthority", "attestationKeyId",
        "hostDigest", "inventorySha256", "installReceipt", "installReceiptSha256",
        "restartReceipt", "restartReceiptSha256", "v1NoopRehearsalReceipt",
        "v1NoopRehearsalReceiptSha256", "verifiedAtUtc", "phiClassification",
    }
)
INSTALL_RECEIPT_KEYS = frozenset(
    {
        "schemaVersion", "purpose", "hostDigest", "maintenanceKeyId",
        "installedReleaseTag", "installedSourceSha", "installerType",
        "installerArtifactSha256", "releaseReceiptSha256", "checksumsSha256",
        "checksumsSignatureSha256", "installedCohort", "installTransactionId",
        "installCompletedAtUtc", "bootIdAtInstall", "installMode",
    }
)
INSTALLED_COHORT_KEYS = frozenset(
    {
        "SuavoAgent.Core.exe", "SuavoAgent.Broker.exe", "SuavoAgent.Helper.exe",
        "SuavoAgent.Watchdog.exe", "SuavoSetup.exe",
    }
)
RESTART_RECEIPT_KEYS = frozenset(
    {
        "schemaVersion", "purpose", "hostDigest", "installReceiptSha256",
        "bootIdBeforeRestart", "bootIdAfterRestart", "runningReleaseTag",
        "runningSourceSha", "outcome", "restartObservedAtUtc",
    }
)
V1_NOOP_RECEIPT_KEYS = frozenset(
    {
        "schemaVersion", "purpose", "hostDigest", "inventorySha256",
        "installReceiptSha256", "restartReceiptSha256", "installedReleaseTag",
        "installedSourceSha", "otaSigningKeyId", "updateManifestName",
        "updateManifestCanonical", "updateManifestSignatureP1363Hex",
        "checksumsSha256", "checksumsSignatureSha256", "outcome", "observedAtUtc",
    }
)
CLAIM_KEYS = frozenset(
    {
        "schemaVersion", "ceremony", "bridgeReleaseTag", "bridgeSourceSha",
        "inventoryPath", "inventorySha256", "inventorySignaturePath",
        "evidenceBundlePath", "evidenceBundleSha256", "registeredHostSetSha256",
        "expectedMachineCount", "verifiedMachineCount", "releasePublishedAtUtc",
        "snapshotCutoffUtc", "fleetRegistryEpoch", "inventoryIssuedAtUtc",
        "inventoryExpiresAtUtc", "issuedAtUtc", "phiClassification",
        "verificationMode", "signingKeyId",
    }
)


def _exact_utc(value: object, label: str) -> datetime:
    if not isinstance(value, str) or UTC_RE.fullmatch(value) is None:
        raise BridgeError(f"{label} must be exact whole-second UTC")
    try:
        parsed = datetime.strptime(value, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)
    except ValueError as error:
        raise BridgeError(f"{label} is not a real UTC timestamp") from error
    if parsed.strftime("%Y-%m-%dT%H:%M:%SZ") != value:
        raise BridgeError(f"{label} is not canonical UTC")
    return parsed


def _positive_integer(value: object, label: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value <= 0:
        raise BridgeError(f"{label} must be a positive integer")
    return value


def _schema_version(value: object, expected: int, label: str) -> None:
    if isinstance(value, bool) or not isinstance(value, int) or value != expected:
        raise BridgeError(f"{label} schema is unsupported")


def _p1363_base64url(value: object, label: str) -> bytes:
    if not isinstance(value, str) or BASE64URL_P1363_RE.fullmatch(value) is None:
        raise BridgeError(f"{label} is not exact unpadded P-256 P1363 Base64Url")
    try:
        decoded = base64.urlsafe_b64decode(value + "==")
    except (binascii.Error, ValueError) as error:
        raise BridgeError(f"{label} is not canonical Base64Url") from error
    encoded = base64.urlsafe_b64encode(decoded).decode("ascii").rstrip("=")
    if len(decoded) != 64 or encoded != value:
        raise BridgeError(f"{label} is not exact unpadded P-256 P1363 Base64Url")
    return decoded


def _p1363_hex(value: object, label: str) -> bytes:
    if not isinstance(value, str) or P1363_HEX_RE.fullmatch(value) is None:
        raise BridgeError(f"{label} is not exact lowercase P-256 P1363 hex")
    return value.encode("ascii")


def _verify_p1363(public_key: bytes, key_id: str, payload: bytes, signature: bytes) -> bool:
    return verify_signature_bytes(
        {key_id: public_key}, payload, signature.hex().encode("ascii"), "p1363-hex"
    )


def _receipt_hash(receipt: dict[str, object]) -> str:
    return _sha256(_canonical_json(receipt))


def _enrollment_key(entry: dict[str, object], prefix: str, label: str) -> tuple[str, bytes]:
    key_id = _strict_sha(entry.get(f"{prefix}KeyId"), SHA_RE, f"{label} key id")
    encoded = entry.get(f"{prefix}PublicKeySpkiDerBase64")
    if not isinstance(encoded, str):
        raise BridgeError(f"{label} public key is not Base64")
    try:
        public_key = _strict_p256_spki(encoded)
    except TrustRegistryError as error:
        raise BridgeError(f"{label} public key is invalid: {error}") from error
    if key_id != _sha256(public_key):
        raise BridgeError(f"{label} key id does not match its P-256 SPKI")
    return key_id, public_key


def _device_keys(entry: object) -> tuple[str, str, bytes, str, bytes]:
    if not isinstance(entry, dict) or set(entry) != INVENTORY_HOST_KEYS:
        raise BridgeError("authoritative registered host has unknown or missing fields")
    host = _strict_sha(entry.get("hostDigest"), SHA_RE, "registered host digest")
    attestation_key_id, attestation_public_key = _enrollment_key(
        entry, "attestation", "registered device attestation"
    )
    maintenance_key_id, maintenance_public_key = _enrollment_key(
        entry, "maintenance", "registered maintenance"
    )
    if attestation_key_id == maintenance_key_id:
        raise BridgeError("device attestation and maintenance keys must be distinct")
    return (
        host,
        attestation_key_id,
        attestation_public_key,
        maintenance_key_id,
        maintenance_public_key,
    )


def validate_inventory(
    path: Path, signature_path: Path, registry_path: Path
) -> tuple[dict[str, object], bytes]:
    inventory, raw = _strict_json(path, 2 * 1024 * 1024, "authoritative fleet inventory")
    if set(inventory) != INVENTORY_KEYS:
        raise BridgeError("authoritative fleet inventory schema is unsupported")
    _schema_version(inventory.get("schemaVersion"), 3, "authoritative fleet inventory")
    if inventory.get("purpose") != INVENTORY_PURPOSE or inventory.get("repository") != REPOSITORY:
        raise BridgeError("authoritative fleet inventory identity is wrong")
    _version_parts(inventory.get("bridgeReleaseTag"))
    _strict_sha(inventory.get("bridgeSourceSha"), COMMIT_RE, "inventory bridge source SHA")
    published = _exact_utc(inventory.get("releasePublishedAtUtc"), "release publication time")
    cutoff = _exact_utc(inventory.get("snapshotCutoffUtc"), "inventory cutoff time")
    issued = _exact_utc(inventory.get("issuedAtUtc"), "inventory issue time")
    expires = _exact_utc(inventory.get("expiresAtUtc"), "inventory expiry time")
    if (
        cutoff < published
        or issued < cutoff
        or issued - cutoff > MAX_INVENTORY_ISSUE_DELAY
        or expires <= issued
        or expires - issued > MAX_INVENTORY_VALIDITY
        or issued > datetime.now(timezone.utc) + timedelta(minutes=5)
    ):
        raise BridgeError("authoritative fleet inventory has impossible time boundaries")
    _positive_integer(inventory.get("fleetRegistryEpoch"), "fleet registry epoch")
    if inventory.get("enrollmentClosed") is not True:
        raise BridgeError("authoritative fleet inventory enrollment must be closed")
    hosts = inventory.get("registeredHosts")
    if not isinstance(hosts, list) or not hosts:
        raise BridgeError("authoritative fleet inventory contains no registered hosts")
    normalized_hosts = [_device_keys(host) for host in hosts]
    normalized = [host[0] for host in normalized_hosts]
    key_ids = [key_id for host in normalized_hosts for key_id in (host[1], host[3])]
    if normalized != sorted(normalized) or len(normalized) != len(set(normalized)):
        raise BridgeError("registered host digests must be unique and sorted")
    if len(key_ids) != len(set(key_ids)):
        raise BridgeError("each registered host must have unique device and maintenance keys")
    registered_host_count = _positive_integer(
        inventory.get("registeredHostCount"), "registered host count"
    )
    if registered_host_count != len(normalized):
        raise BridgeError("registered host count does not match the exact inventory")
    expected_host_set = _sha256(_canonical_json(normalized))
    if inventory.get("registeredHostSetSha256") != expected_host_set:
        raise BridgeError("registered host set digest does not match the exact inventory")
    release = inventory.get("releaseBindings")
    if not isinstance(release, dict) or set(release) != RELEASE_KEYS:
        raise BridgeError("authoritative Release 1 bindings are incomplete")
    if release["burnArtifactName"] != "SuavoAgent-Setup.exe":
        raise BridgeError("authoritative Burn artifact name is wrong")
    expected_msi = f"SuavoAgent-{inventory['bridgeReleaseTag']}-win-x64.msi"
    if release["msiArtifactName"] != expected_msi:
        raise BridgeError("authoritative MSI artifact name is wrong")
    expected_manifest = f"update-manifest-{inventory['bridgeReleaseTag']}.txt"
    if release["updateManifestName"] != expected_manifest:
        raise BridgeError("authoritative update manifest name is wrong")
    for key in RELEASE_KEYS - {"burnArtifactName", "msiArtifactName", "updateManifestName"}:
        _strict_sha(release[key], SHA_RE, f"inventory {key}")
    _, roots, _ = _registry(registry_path)
    signature = _read_regular(signature_path, 512, "authoritative inventory signature")
    if V2_KEY_ID not in roots or not verify_signature_bytes(
        {V2_KEY_ID: roots[V2_KEY_ID]}, raw, signature, "der"
    ):
        raise BridgeError("authoritative fleet inventory is not signed by reviewed ota-update-v2")
    return inventory, raw


def _expected_release_manifest(inventory: dict[str, object]) -> bytes:
    release = inventory["releaseBindings"]
    if not isinstance(release, dict):
        raise BridgeError("authoritative Release 1 bindings are incomplete")
    tag, numeric = _version_parts(inventory["bridgeReleaseTag"])
    base = f"https://github.com/{REPOSITORY}/releases/download/{tag}"
    fields = (
        f"{base}/SuavoAgent.Core.exe", release["coreArtifactSha256"],
        f"{base}/SuavoAgent.Broker.exe", release["brokerArtifactSha256"],
        f"{base}/SuavoAgent.Helper.exe", release["helperArtifactSha256"],
        numeric, "net8.0", "win-x64",
        f"{base}/SuavoAgent.Watchdog.exe", release["watchdogArtifactSha256"],
    )
    return "|".join(fields).encode("ascii")


def _validate_install_receipt(
    receipt: object,
    inventory: dict[str, object],
    host_digest: str,
    maintenance_key_id: str,
) -> tuple[dict[str, object], datetime, str]:
    if not isinstance(receipt, dict) or set(receipt) != INSTALL_RECEIPT_KEYS:
        raise BridgeError("full installer receipt has unknown or missing fields")
    _schema_version(receipt.get("schemaVersion"), 1, "full installer receipt")
    if (
        receipt.get("purpose") != INSTALL_RECEIPT_PURPOSE
        or receipt.get("hostDigest") != host_digest
        or receipt.get("maintenanceKeyId") != maintenance_key_id
        or receipt.get("installedReleaseTag") != inventory["bridgeReleaseTag"]
        or receipt.get("installedSourceSha") != inventory["bridgeSourceSha"]
        or receipt.get("installMode") != INSTALL_MODE
    ):
        raise BridgeError("full installer receipt does not bind exact Release 1 authority")
    release = inventory["releaseBindings"]
    if receipt.get("installerType") != "msi":
        raise BridgeError("full installer receipt must prove the committed Release 1 MSI")
    if receipt.get("installerArtifactSha256") != release["msiArtifactSha256"]:
        raise BridgeError("full installer receipt does not bind the Release 1 MSI")
    for field in (
        "releaseReceiptSha256", "checksumsSha256", "checksumsSignatureSha256"
    ):
        if receipt.get(field) != release[field]:
            raise BridgeError(f"full installer receipt {field} does not match Release 1")
    cohort = receipt.get("installedCohort")
    if not isinstance(cohort, dict) or set(cohort) != INSTALLED_COHORT_KEYS:
        raise BridgeError("full installer receipt installed cohort is incomplete")
    expected_cohort = {
        "SuavoAgent.Core.exe": release["coreArtifactSha256"],
        "SuavoAgent.Broker.exe": release["brokerArtifactSha256"],
        "SuavoAgent.Helper.exe": release["helperArtifactSha256"],
        "SuavoAgent.Watchdog.exe": release["watchdogArtifactSha256"],
        "SuavoSetup.exe": release["maintenanceHostSha256"],
    }
    if cohort != expected_cohort:
        raise BridgeError("full installer receipt installed cohort is not exact Release 1")
    _strict_sha(receipt.get("installTransactionId"), SHA_RE, "install transaction id")
    _strict_sha(receipt.get("bootIdAtInstall"), SHA_RE, "install boot id")
    completed = _exact_utc(receipt.get("installCompletedAtUtc"), "install completion time")
    published = _exact_utc(inventory["releasePublishedAtUtc"], "release publication time")
    expires = _exact_utc(inventory["expiresAtUtc"], "inventory expiry time")
    if completed < published or completed > expires:
        raise BridgeError("full installer receipt time is outside Release 1 authority")
    return receipt, completed, _receipt_hash(receipt)


def _validate_restart_receipt(
    receipt: object,
    inventory: dict[str, object],
    host_digest: str,
    install_receipt: dict[str, object],
    install_receipt_sha256: str,
    install_completed: datetime,
) -> tuple[dict[str, object], datetime, str]:
    if not isinstance(receipt, dict) or set(receipt) != RESTART_RECEIPT_KEYS:
        raise BridgeError("post-install restart receipt has unknown or missing fields")
    _schema_version(receipt.get("schemaVersion"), 1, "post-install restart receipt")
    if (
        receipt.get("purpose") != RESTART_RECEIPT_PURPOSE
        or receipt.get("hostDigest") != host_digest
        or receipt.get("installReceiptSha256") != install_receipt_sha256
        or receipt.get("bootIdBeforeRestart") != install_receipt["bootIdAtInstall"]
        or receipt.get("runningReleaseTag") != inventory["bridgeReleaseTag"]
        or receipt.get("runningSourceSha") != inventory["bridgeSourceSha"]
        or receipt.get("outcome") != RESTART_OUTCOME
    ):
        raise BridgeError("post-install restart receipt does not bind exact Release 1 activation")
    before = _strict_sha(
        receipt.get("bootIdBeforeRestart"), SHA_RE, "pre-restart boot id"
    )
    after = _strict_sha(
        receipt.get("bootIdAfterRestart"), SHA_RE, "post-restart boot id"
    )
    if before == after:
        raise BridgeError("post-install restart receipt did not cross a Windows boot")
    observed = _exact_utc(receipt.get("restartObservedAtUtc"), "restart observation time")
    issued = _exact_utc(inventory["issuedAtUtc"], "inventory issue time")
    expires = _exact_utc(inventory["expiresAtUtc"], "inventory expiry time")
    if observed < max(install_completed, issued) or observed > expires:
        raise BridgeError("post-install restart receipt time is outside the install campaign")
    return receipt, observed, _receipt_hash(receipt)


def _validate_v1_noop_receipt(
    receipt: object,
    inventory: dict[str, object],
    inventory_sha256: str,
    host_digest: str,
    install_receipt_sha256: str,
    restart_receipt_sha256: str,
    restart_observed: datetime,
    registry_path: Path,
) -> tuple[dict[str, object], datetime, str]:
    if not isinstance(receipt, dict) or set(receipt) != V1_NOOP_RECEIPT_KEYS:
        raise BridgeError("v1 no-op rehearsal receipt has unknown or missing fields")
    _schema_version(receipt.get("schemaVersion"), 1, "v1 no-op rehearsal receipt")
    release = inventory["releaseBindings"]
    if (
        receipt.get("purpose") != V1_NOOP_RECEIPT_PURPOSE
        or receipt.get("hostDigest") != host_digest
        or receipt.get("inventorySha256") != inventory_sha256
        or receipt.get("installReceiptSha256") != install_receipt_sha256
        or receipt.get("restartReceiptSha256") != restart_receipt_sha256
        or receipt.get("installedReleaseTag") != inventory["bridgeReleaseTag"]
        or receipt.get("installedSourceSha") != inventory["bridgeSourceSha"]
        or receipt.get("otaSigningKeyId") != V1_KEY_ID
        or receipt.get("updateManifestName") != release["updateManifestName"]
        or receipt.get("checksumsSha256") != release["checksumsSha256"]
        or receipt.get("checksumsSignatureSha256") != release["checksumsSignatureSha256"]
        or receipt.get("outcome") != V1_NOOP_OUTCOME
    ):
        raise BridgeError("v1 no-op rehearsal receipt does not bind exact Release 1 authority")
    manifest = receipt.get("updateManifestCanonical")
    if not isinstance(manifest, str):
        raise BridgeError("v1 no-op rehearsal manifest is not canonical ASCII")
    try:
        manifest_raw = manifest.encode("ascii")
    except UnicodeEncodeError as error:
        raise BridgeError("v1 no-op rehearsal manifest is not canonical ASCII") from error
    if (
        not manifest_raw
        or len(manifest_raw) > 128 * 1024
        or manifest_raw != _expected_release_manifest(inventory)
        or _sha256(manifest_raw) != release["updateManifestSha256"]
    ):
        raise BridgeError("v1 no-op rehearsal manifest is not exact Release 1")
    signature = _p1363_hex(
        receipt.get("updateManifestSignatureP1363Hex"),
        "v1 no-op rehearsal manifest signature",
    )
    if _sha256(signature) != release["updateManifestSignatureSha256"]:
        raise BridgeError("v1 no-op rehearsal manifest signature digest is wrong")
    _, roots, _ = _registry(registry_path)
    if V1_KEY_ID not in roots or not verify_signature_bytes(
        {V1_KEY_ID: roots[V1_KEY_ID]}, manifest_raw, signature, "p1363-hex"
    ):
        raise BridgeError("v1 no-op rehearsal manifest is not signed by historic ota-update-v1")
    observed = _exact_utc(receipt.get("observedAtUtc"), "v1 no-op observation time")
    issued = _exact_utc(inventory["issuedAtUtc"], "inventory issue time")
    expires = _exact_utc(inventory["expiresAtUtc"], "inventory expiry time")
    if observed < max(restart_observed, issued) or observed > expires:
        raise BridgeError("v1 no-op rehearsal receipt time is outside the inventory campaign")
    return receipt, observed, _receipt_hash(receipt)


def validate_evidence_bundle(
    path: Path,
    inventory_path: Path,
    inventory_signature_path: Path,
    registry_path: Path,
) -> tuple[dict[str, object], bytes, dict[str, object], bytes]:
    inventory, inventory_raw = validate_inventory(
        inventory_path, inventory_signature_path, registry_path
    )
    evidence, raw = _strict_json(path, 8 * 1024 * 1024, "convergence evidence bundle")
    if set(evidence) != EVIDENCE_KEYS:
        raise BridgeError("convergence evidence bundle schema is unsupported")
    _schema_version(evidence.get("schemaVersion"), 4, "convergence evidence bundle")
    if evidence.get("ceremony") != CEREMONY:
        raise BridgeError("convergence evidence bundle ceremony is wrong")
    for key in ("bridgeReleaseTag", "bridgeSourceSha"):
        if evidence.get(key) != inventory.get(key):
            raise BridgeError(f"convergence evidence has wrong {key}")
    machines = evidence.get("machines")
    if not isinstance(machines, list) or not machines:
        raise BridgeError("convergence evidence bundle contains no machine attestations")
    inventory_sha256 = _sha256(inventory_raw)
    registered_hosts = {
        host_digest: (
            attestation_key_id,
            attestation_public_key,
            maintenance_key_id,
            maintenance_public_key,
        )
        for (
            host_digest,
            attestation_key_id,
            attestation_public_key,
            maintenance_key_id,
            maintenance_public_key,
        ) in map(_device_keys, inventory["registeredHosts"])
    }
    host_digests: list[str] = []
    for machine in machines:
        if not isinstance(machine, dict) or set(machine) != MACHINE_KEYS:
            raise BridgeError("machine convergence evidence has unknown or missing fields")
        attestation = machine.get("attestation")
        if not isinstance(attestation, dict) or set(attestation) != ATTESTATION_KEYS:
            raise BridgeError("machine attestation schema is incomplete")
        _schema_version(attestation.get("schemaVersion"), 2, "machine attestation")
        signature = _p1363_base64url(
            machine.get("attestationSignatureBase64Url"),
            "machine attestation signature",
        )
        install_signature = _p1363_base64url(
            machine.get("installReceiptSignatureBase64Url"),
            "full installer receipt signature",
        )
        host_digest = _strict_sha(attestation["hostDigest"], SHA_RE, "host digest")
        enrolled = registered_hosts.get(host_digest)
        if enrolled is None:
            raise BridgeError("machine attestation host is not in the signed authoritative inventory")
        key_id, public_key, maintenance_key_id, maintenance_public_key = enrolled
        if attestation.get("attestationKeyId") != key_id:
            raise BridgeError("machine attestation key id does not match the enrolled device key")
        if not _verify_p1363(public_key, key_id, _canonical_json(attestation), signature):
            raise BridgeError("machine attestation is not signed by its enrolled device key")
        host_digests.append(host_digest)
        if (
            attestation["purpose"] != ATTESTATION_PURPOSE
            or attestation["attestationAuthority"] != ATTESTATION_AUTHORITY
            or attestation["inventorySha256"] != inventory_sha256
            or attestation["phiClassification"] != PHI_CLASSIFICATION
        ):
            raise BridgeError("machine attestation does not bind exact Release 1 authority")
        install_receipt, install_completed, install_receipt_sha256 = _validate_install_receipt(
            attestation.get("installReceipt"),
            inventory,
            host_digest,
            maintenance_key_id,
        )
        if attestation.get("installReceiptSha256") != install_receipt_sha256:
            raise BridgeError("machine attestation full installer receipt digest is wrong")
        if not _verify_p1363(
            maintenance_public_key,
            maintenance_key_id,
            _canonical_json(install_receipt),
            install_signature,
        ):
            raise BridgeError("full installer receipt is not signed by enrolled maintenance key")
        restart_receipt, restart_observed, restart_receipt_sha256 = _validate_restart_receipt(
            attestation.get("restartReceipt"),
            inventory,
            host_digest,
            install_receipt,
            install_receipt_sha256,
            install_completed,
        )
        if attestation.get("restartReceiptSha256") != restart_receipt_sha256:
            raise BridgeError("machine attestation restart receipt digest is wrong")
        _, noop_observed, noop_receipt_sha256 = _validate_v1_noop_receipt(
            attestation.get("v1NoopRehearsalReceipt"),
            inventory,
            inventory_sha256,
            host_digest,
            install_receipt_sha256,
            restart_receipt_sha256,
            restart_observed,
            registry_path,
        )
        if attestation.get("v1NoopRehearsalReceiptSha256") != noop_receipt_sha256:
            raise BridgeError("machine attestation v1 no-op receipt digest is wrong")
        verified = _exact_utc(attestation["verifiedAtUtc"], "machine verification time")
        expires = _exact_utc(inventory["expiresAtUtc"], "inventory expiry time")
        if verified < noop_observed or verified > expires:
            raise BridgeError("machine verification time falls outside signed inventory bounds")
    inventory_hosts = [_device_keys(host)[0] for host in inventory["registeredHosts"]]
    if host_digests != sorted(host_digests) or len(host_digests) != len(set(host_digests)):
        raise BridgeError("machine attestations must be unique and sorted by opaque host digest")
    if host_digests != inventory_hosts:
        raise BridgeError("machine attestations are not the exact authoritative registered-host set")
    return evidence, raw, inventory, inventory_raw


def _validate_claim(
    claim_path: Path,
    evidence_path: Path,
    inventory_path: Path,
    inventory_signature_path: Path,
    registry_path: Path,
) -> tuple[dict[str, object], bytes]:
    evidence, evidence_raw, inventory, inventory_raw = validate_evidence_bundle(
        evidence_path, inventory_path, inventory_signature_path, registry_path
    )
    claim, claim_raw = _strict_json(claim_path, 32 * 1024, "convergence claim")
    if set(claim) != CLAIM_KEYS:
        raise BridgeError("convergence claim has unknown or missing fields")
    _schema_version(claim.get("schemaVersion"), 4, "convergence claim")
    _positive_integer(claim.get("fleetRegistryEpoch"), "claim fleet registry epoch")
    _positive_integer(claim.get("expectedMachineCount"), "claim expected machine count")
    _positive_integer(claim.get("verifiedMachineCount"), "claim verified machine count")
    count = inventory["registeredHostCount"]
    expected = {
        "schemaVersion": 4,
        "ceremony": CEREMONY,
        "bridgeReleaseTag": inventory["bridgeReleaseTag"],
        "bridgeSourceSha": inventory["bridgeSourceSha"],
        "inventoryPath": INVENTORY_RELATIVE_PATH,
        "inventorySha256": _sha256(inventory_raw),
        "inventorySignaturePath": INVENTORY_SIGNATURE_RELATIVE_PATH,
        "evidenceBundlePath": EVIDENCE_RELATIVE_PATH,
        "evidenceBundleSha256": _sha256(evidence_raw),
        "registeredHostSetSha256": inventory["registeredHostSetSha256"],
        "expectedMachineCount": count,
        "verifiedMachineCount": len(evidence["machines"]),
        "releasePublishedAtUtc": inventory["releasePublishedAtUtc"],
        "snapshotCutoffUtc": inventory["snapshotCutoffUtc"],
        "fleetRegistryEpoch": inventory["fleetRegistryEpoch"],
        "inventoryIssuedAtUtc": inventory["issuedAtUtc"],
        "inventoryExpiresAtUtc": inventory["expiresAtUtc"],
        "phiClassification": PHI_CLASSIFICATION,
        "verificationMode": (
            "closed-inventory-v2-device-and-maintenance-attested-"
            "reinstall-restart-v1-manifest-noop"
        ),
        "signingKeyId": V1_KEY_ID,
    }
    for key, value in expected.items():
        if claim.get(key) != value:
            raise BridgeError(f"convergence claim has wrong {key}")
    issued = _exact_utc(claim.get("issuedAtUtc"), "convergence claim issue time")
    inventory_issued = _exact_utc(
        claim["inventoryIssuedAtUtc"], "inventory issue time"
    )
    inventory_expires = _exact_utc(
        claim["inventoryExpiresAtUtc"], "inventory expiry time"
    )
    latest_verified = max(
        _exact_utc(machine["attestation"]["verifiedAtUtc"], "machine verification time")
        for machine in evidence["machines"]
    )
    if (
        issued < max(inventory_issued, latest_verified)
        or issued > inventory_expires
        or issued > datetime.now(timezone.utc) + timedelta(minutes=5)
    ):
        raise BridgeError("convergence claim issue time is outside inventory validity")
    return claim, claim_raw


def verify_convergence_claim(
    registry_path: Path = DEFAULT_REGISTRY,
    evidence_path: Path = DEFAULT_EVIDENCE,
    claim_path: Path = DEFAULT_CLAIM,
    signature_path: Path = DEFAULT_SIGNATURE,
    inventory_path: Path = DEFAULT_INVENTORY,
    inventory_signature_path: Path = DEFAULT_INVENTORY_SIGNATURE,
) -> dict[str, object]:
    claim, claim_raw = _validate_claim(
        claim_path, evidence_path, inventory_path, inventory_signature_path, registry_path
    )
    _, roots, _ = _registry(registry_path)
    signature = _read_regular(signature_path, 512, "convergence claim signature")
    if not verify_signature_bytes({V1_KEY_ID: roots[V1_KEY_ID]}, claim_raw, signature, "der"):
        raise BridgeError("convergence claim did not verify specifically with historic ota-update-v1")
    return claim


def sign_convergence_claim(arguments: argparse.Namespace) -> None:
    assert_bridge_source(arguments.registry)
    release_tag, _ = _version_parts(arguments.bridge_release_tag)
    source_sha = _strict_sha(arguments.bridge_source_sha, COMMIT_RE, "bridge source SHA")
    evidence, evidence_raw, inventory, inventory_raw = validate_evidence_bundle(
        arguments.evidence,
        arguments.inventory,
        arguments.inventory_signature,
        arguments.registry,
    )
    if inventory["bridgeReleaseTag"] != release_tag or inventory["bridgeSourceSha"] != source_sha:
        raise BridgeError("authoritative inventory does not match exact Release 1 tag and source")
    if any(path.exists() or path.is_symlink() for path in (arguments.claim, arguments.signature)):
        raise BridgeError("convergence claim output already exists")
    issued = datetime.now(timezone.utc).replace(microsecond=0)
    inventory_issued = _exact_utc(inventory["issuedAtUtc"], "inventory issue time")
    inventory_expires = _exact_utc(inventory["expiresAtUtc"], "inventory expiry time")
    latest_verified = max(
        _exact_utc(machine["attestation"]["verifiedAtUtc"], "machine verification time")
        for machine in evidence["machines"]
    )
    if issued < max(inventory_issued, latest_verified) or issued > inventory_expires:
        raise BridgeError("historic convergence claim must be issued within inventory validity")
    claim = {
        "bridgeReleaseTag": release_tag,
        "bridgeSourceSha": source_sha,
        "ceremony": CEREMONY,
        "evidenceBundlePath": EVIDENCE_RELATIVE_PATH,
        "evidenceBundleSha256": _sha256(evidence_raw),
        "expectedMachineCount": inventory["registeredHostCount"],
        "fleetRegistryEpoch": inventory["fleetRegistryEpoch"],
        "inventoryPath": INVENTORY_RELATIVE_PATH,
        "inventoryExpiresAtUtc": inventory["expiresAtUtc"],
        "inventoryIssuedAtUtc": inventory["issuedAtUtc"],
        "inventorySha256": _sha256(inventory_raw),
        "inventorySignaturePath": INVENTORY_SIGNATURE_RELATIVE_PATH,
        "issuedAtUtc": issued.strftime("%Y-%m-%dT%H:%M:%SZ"),
        "phiClassification": PHI_CLASSIFICATION,
        "registeredHostSetSha256": inventory["registeredHostSetSha256"],
        "releasePublishedAtUtc": inventory["releasePublishedAtUtc"],
        "schemaVersion": 4,
        "signingKeyId": V1_KEY_ID,
        "snapshotCutoffUtc": inventory["snapshotCutoffUtc"],
        "verificationMode": (
            "closed-inventory-v2-device-and-maintenance-attested-"
            "reinstall-restart-v1-manifest-noop"
        ),
        "verifiedMachineCount": len(evidence["machines"]),
    }
    claim_raw = _canonical_json(claim)
    _, roots, _ = _registry(arguments.registry)
    try:
        with open_historic_key(arguments.key) as key:
            if key.public_der() != roots[V1_KEY_ID]:
                raise BridgeError("local private key does not match historic ota-update-v1")
            signature = key.sign_der(claim_raw)
        if not verify_signature_bytes(
            {V1_KEY_ID: roots[V1_KEY_ID]}, claim_raw, signature, "der"
        ):
            raise BridgeError("new convergence claim failed immediate historic v1 verification")
        _exclusive_write(arguments.claim, claim_raw)
        try:
            _exclusive_write(arguments.signature, signature)
        except BaseException:
            arguments.claim.unlink(missing_ok=True)
            raise
    except HistoricKeyError as error:
        raise BridgeError(str(error)) from error
    print(f"claim_sha256={_sha256(claim_raw)}")
    print(f"evidence_sha256={_sha256(evidence_raw)}")
    print(f"inventory_sha256={_sha256(inventory_raw)}")
