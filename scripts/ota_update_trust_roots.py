#!/usr/bin/env python3
"""Validate the reviewed OTA root registry and verify signatures against it."""

from __future__ import annotations

import argparse
import base64
import binascii
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile

from v1_bridge_file_io import SecureFileError, read_regular_once


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_REGISTRY = ROOT / "security/ota-update-trust-roots.json"
EXPECTED_IDS = {"ota-update-v1", "ota-update-v2"}
PENDING_V2 = "REPLACE_WITH_REVIEWED_AWS_KMS_P256_SPKI_DER_BASE64"
P256_SPKI_PREFIX = bytes.fromhex(
    "3059301306072a8648ce3d020106082a8648ce3d03010703420004"
)
EXPECTED_SPKI_SHA256 = {
    "ota-update-v1": "b3f5ddda0654713de31e6cbe3ae3b49ed53575d0938d4149779361c6d739e970",
    "ota-update-v2": "6e4092980b1185627200476806d5063c43df77e5ac000b6b6ba72df89eb1406f",
}


class TrustRegistryError(ValueError):
    pass


def _fail(message: str) -> None:
    print(message, file=sys.stderr)
    raise SystemExit(1)


def _read_regular(path: Path, maximum_bytes: int, label: str) -> bytes:
    try:
        return read_regular_once(path, maximum_bytes, label)
    except SecureFileError as error:
        raise TrustRegistryError(str(error)) from error


def _strict_p256_spki(value: str) -> bytes:
    try:
        decoded = base64.b64decode(value, validate=True)
    except (binascii.Error, ValueError) as error:
        raise TrustRegistryError("OTA public key is not canonical base64") from error
    if base64.b64encode(decoded).decode("ascii") != value:
        raise TrustRegistryError("OTA public key is not canonical base64")
    if len(decoded) != 91 or not decoded.startswith(P256_SPKI_PREFIX):
        raise TrustRegistryError("OTA public key is not exact P-256 SPKI DER")

    result = subprocess.run(
        ["/usr/bin/openssl", "pkey", "-pubin", "-inform", "DER", "-noout"],
        input=decoded,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        check=False,
    )
    if result.returncode != 0:
        raise TrustRegistryError("OTA public key is not a valid P-256 point")
    return decoded


def load_registry_configuration_bytes(raw: bytes) -> tuple[str, dict[str, bytes]]:
    try:
        document = json.loads(raw)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise TrustRegistryError("OTA trust registry is not strict JSON") from error
    if not isinstance(document, dict) or set(document) != {
        "schemaVersion", "signingKeyId", "roots"
    }:
        raise TrustRegistryError("OTA trust registry has unknown or missing fields")
    if document["schemaVersion"] != 1 or not isinstance(document["roots"], list):
        raise TrustRegistryError("OTA trust registry schema is unsupported")
    if len(document["roots"]) != 2:
        raise TrustRegistryError("OTA trust registry must contain exactly v1 and v2")

    signing_key_id = document["signingKeyId"]
    if signing_key_id not in EXPECTED_IDS:
        raise TrustRegistryError("OTA signing root selection is invalid")

    configured: dict[str, bytes] = {}
    seen: set[str] = set()
    for entry in document["roots"]:
        if not isinstance(entry, dict) or set(entry) != {"keyId", "publicKeyDerBase64"}:
            raise TrustRegistryError("OTA trust root has unknown or missing fields")
        key_id = entry["keyId"]
        public_key = entry["publicKeyDerBase64"]
        if key_id not in EXPECTED_IDS or key_id in seen or not isinstance(public_key, str):
            raise TrustRegistryError("OTA trust registry contains an invalid key id")
        seen.add(key_id)
        if key_id == "ota-update-v2" and public_key == PENDING_V2:
            continue
        decoded = _strict_p256_spki(public_key)
        if hashlib.sha256(decoded).hexdigest() != EXPECTED_SPKI_SHA256[key_id]:
            raise TrustRegistryError(f"{key_id} does not match its reviewed fleet root")
        configured[key_id] = decoded

    if seen != EXPECTED_IDS or "ota-update-v1" not in configured:
        raise TrustRegistryError("legacy ota-update-v1 bridge trust is missing")
    if len({value for value in configured.values()}) != len(configured):
        raise TrustRegistryError("OTA trust roots must be distinct")
    if signing_key_id not in configured:
        raise TrustRegistryError("selected OTA signing root is not configured")
    return signing_key_id, configured


def load_registry_configuration(
    path: Path = DEFAULT_REGISTRY,
) -> tuple[str, dict[str, bytes]]:
    return load_registry_configuration_bytes(
        _read_regular(path, 16 * 1024, "OTA trust registry")
    )


def load_registry(path: Path = DEFAULT_REGISTRY) -> dict[str, bytes]:
    return load_registry_configuration(path)[1]


def _p1363_to_der(signature: bytes) -> bytes:
    if len(signature) != 64:
        raise TrustRegistryError("P1363 signature must be exactly 64 bytes")

    def integer(value: bytes) -> bytes:
        value = value.lstrip(b"\x00") or b"\x00"
        if value[0] & 0x80:
            value = b"\x00" + value
        return b"\x02" + bytes([len(value)]) + value

    body = integer(signature[:32]) + integer(signature[32:])
    return b"\x30" + bytes([len(body)]) + body


def verify_signature(
    registry: dict[str, bytes],
    payload_path: Path,
    signature_path: Path,
    signature_format: str,
) -> bool:
    payload = _read_regular(payload_path, 128 * 1024, "signed OTA payload")
    signature = _read_regular(signature_path, 512, "OTA signature")
    return verify_signature_bytes(registry, payload, signature, signature_format)


def verify_signature_bytes(
    registry: dict[str, bytes],
    payload: bytes,
    signature: bytes,
    signature_format: str,
) -> bool:
    if not payload or len(payload) > 128 * 1024 or not signature or len(signature) > 512:
        return False
    if signature_format == "p1363-hex":
        try:
            text = signature.decode("ascii")
            if len(text) != 128 or any(character not in "0123456789abcdefABCDEF" for character in text):
                return False
            signature = _p1363_to_der(bytes.fromhex(text))
        except (UnicodeDecodeError, ValueError, TrustRegistryError):
            return False
    elif signature_format != "der":
        raise TrustRegistryError("unsupported OTA signature format")

    for public_key in registry.values():
        convert = subprocess.run(
            ["/usr/bin/openssl", "pkey", "-pubin", "-inform", "DER", "-outform", "PEM"],
            input=public_key,
            capture_output=True,
            check=False,
        )
        if convert.returncode != 0:
            return False
        with tempfile.TemporaryFile() as public_file, tempfile.TemporaryFile() as signature_file:
            public_file.write(convert.stdout)
            signature_file.write(signature)
            public_file.flush()
            signature_file.flush()
            public_file.seek(0)
            signature_file.seek(0)
            verify = subprocess.run(
                [
                    "/usr/bin/openssl", "dgst", "-sha256",
                    "-verify", f"/dev/fd/{public_file.fileno()}",
                    "-signature", f"/dev/fd/{signature_file.fileno()}",
                ],
                input=payload,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                pass_fds=(public_file.fileno(), signature_file.fileno()),
                check=False,
            )
        if verify.returncode == 0:
            return True
    return False


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--registry", type=Path, default=DEFAULT_REGISTRY)
    subparsers = parser.add_subparsers(dest="command", required=True)

    validate = subparsers.add_parser("validate")
    validate.add_argument("--require-key-id", choices=sorted(EXPECTED_IDS))

    assert_public = subparsers.add_parser("assert-signing-public-key")
    assert_public.add_argument("public_key_der_base64")
    assert_public.add_argument("--key-id", choices=sorted(EXPECTED_IDS))

    verify = subparsers.add_parser("verify")
    verify.add_argument("--input", type=Path, required=True)
    verify.add_argument("--signature", type=Path, required=True)
    verify.add_argument("--format", choices=("der", "p1363-hex"), required=True)

    arguments = parser.parse_args()
    try:
        signing_key_id, registry = load_registry_configuration(arguments.registry)
        if arguments.command == "validate":
            if arguments.require_key_id and arguments.require_key_id not in registry:
                raise TrustRegistryError(
                    f"reviewed {arguments.require_key_id} public key is not configured"
                )
            return 0
        if arguments.command == "assert-signing-public-key":
            candidate = _strict_p256_spki(arguments.public_key_der_base64)
            expected_key_id = arguments.key_id or signing_key_id
            if candidate != registry[expected_key_id]:
                raise TrustRegistryError(
                    f"configured KMS public key is not reviewed {expected_key_id}"
                )
            return 0
        if not verify_signature(
            registry,
            arguments.input,
            arguments.signature,
            arguments.format,
        ):
            raise TrustRegistryError("OTA signature did not verify with a reviewed root")
        return 0
    except (OSError, subprocess.SubprocessError, TrustRegistryError) as error:
        _fail(str(error))
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
