#!/bin/bash
# Signs an update manifest with the Ed25519 private key.
# Usage: ./sign-update.sh <url> <sha256> <version>
#
# Example:
#   ./sign-update.sh \
#     "https://github.com/MinaH153/SuavoAgent/releases/download/v2.0.0-alpha/SuavoAgent.Core.exe" \
#     "8a3ed143feb750105e2a68dd482b9fc421ecb46fa97a2ff0fea0729a0b257ce8" \
#     "2.1.0"
#
# Output: hex-encoded Ed25519 signature for the pending_update payload

set -e

KEY_PATH="${SUAVO_SIGNING_KEY:-$HOME/.suavo/update-signing.pem}"
URL="$1"
SHA256="$2"
VERSION="$3"

if [ -z "$URL" ] || [ -z "$SHA256" ] || [ -z "$VERSION" ]; then
  echo "Usage: $0 <url> <sha256> <version>" >&2
  exit 1
fi

if [ ! -f "$KEY_PATH" ]; then
  echo "Private key not found at $KEY_PATH" >&2
  echo "Set SUAVO_SIGNING_KEY or copy key to ~/.suavo/update-signing.pem" >&2
  exit 1
fi

# Create manifest: url\nsha256\nversion
MANIFEST=$(printf "%s\n%s\n%s" "$URL" "$SHA256" "$VERSION")

# Sign with Ed25519
SIGNATURE=$(echo -n "$MANIFEST" | openssl pkeyutl -sign -inkey "$KEY_PATH" | xxd -p -c 256)

echo ""
echo "=== Signed Update Manifest ==="
echo "URL:       $URL"
echo "SHA256:    $SHA256"
echo "Version:   $VERSION"
echo "Signature: $SIGNATURE"
echo ""
echo "=== Paste into pending_update ==="
echo "{\"url\":\"$URL\",\"sha256\":\"$SHA256\",\"version\":\"$VERSION\",\"signature\":\"$SIGNATURE\"}"
