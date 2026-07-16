#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "usage: aws-kms-sign-ecdsa-p256.sh <input> <output> <der|p1363-hex>" >&2
  exit 64
fi

input="$1"
output="$2"
format="$3"
: "${OTA_KMS_KEY_ID:?OTA_KMS_KEY_ID is required}"
: "${OTA_KMS_PUBLIC_KEY_DER_BASE64:?OTA_KMS_PUBLIC_KEY_DER_BASE64 is required}"
script_dir="$(cd -- "$(dirname -- "$0")" && pwd -P)"
key_arguments=()
if [[ -n "${OTA_KMS_EXPECTED_KEY_ID:-}" ]]; then
  key_arguments=(--key-id "$OTA_KMS_EXPECTED_KEY_ID")
fi
python3 "$script_dir/ota_update_trust_roots.py" \
  assert-signing-public-key "$OTA_KMS_PUBLIC_KEY_DER_BASE64" "${key_arguments[@]}"

if [[ ! -f "$input" || -L "$input" || -e "$output" ]]; then
  echo "input must be a regular non-link and output must not exist" >&2
  exit 65
fi
size="$(wc -c < "$input" | tr -d ' ')"
if (( size <= 0 || size > 4096 )); then
  echo "AWS KMS ECDSA RAW message must contain 1..4096 bytes" >&2
  exit 65
fi
if [[ "$format" != "der" && "$format" != "p1363-hex" ]]; then
  echo "unsupported signature format" >&2
  exit 65
fi

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
aws kms get-public-key \
  --key-id "$OTA_KMS_KEY_ID" \
  --query PublicKey \
  --output text | base64 --decode > "$tmp/public.der"
actual_public="$(base64 < "$tmp/public.der" | tr -d '\r\n')"
if [[ "$actual_public" != "$OTA_KMS_PUBLIC_KEY_DER_BASE64" ]]; then
  echo "configured KMS key does not match the reviewed OTA public key" >&2
  exit 66
fi
openssl pkey -pubin -inform DER -in "$tmp/public.der" -out "$tmp/public.pem" >/dev/null

aws kms sign \
  --key-id "$OTA_KMS_KEY_ID" \
  --message-type RAW \
  --signing-algorithm ECDSA_SHA_256 \
  --message "fileb://$input" \
  --query Signature \
  --output text | base64 --decode > "$tmp/signature.der"
openssl dgst -sha256 -verify "$tmp/public.pem" -signature "$tmp/signature.der" "$input" >/dev/null

if [[ "$format" == "der" ]]; then
  install -m 0644 "$tmp/signature.der" "$output"
else
  python3 "$script_dir/ecdsa_der_to_p1363.py" "$tmp/signature.der" "$output"
  [[ "$(wc -c < "$output" | tr -d ' ')" == "128" ]]
fi
