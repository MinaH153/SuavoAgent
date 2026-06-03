#!/bin/bash
#
# Generate + sign an OTA update manifest for a SuavoAgent GitHub release.
#
# This script fills the gap left after release.yml was rewritten — historically
# v3.12.0 and v3.13.0 got `update-manifest-vX.Y.Z.txt` + `.sig` artifacts by a
# process that isn't in the repo. v3.13.7 shipped without a manifest, which
# forced the Nadim pilot to plan on a USB install instead of OTA. This script
# is the reproducible generator so every future tag gets a manifest with one
# command.
#
# Usage:
#   ./scripts/generate-ota-manifest.sh <version> [<release-tag>]
#
#   <version>      Numeric version like "3.13.7" (no v prefix)
#   <release-tag>  Git tag like "v3.13.7" (defaults to v<version>)
#
# Environment:
#   SUAVO_SIGNING_KEY  Path to ECDSA P-256 private key PEM (default: ~/.suavo/update-signing-p256.pem)
#   GITHUB_OWNER       Repo owner (default: MinaH153)
#   GITHUB_REPO        Repo name (default: SuavoAgent)
#   GH_TOKEN           (Optional) token for `gh release upload` — omit for dry-run
#   SKIP_UPLOAD=1      Set to generate locally only, skip `gh release upload`
#   INCLUDE_WATCHDOG=1 Append the optional Watchdog binary (11-field manifest, self-heal Chunk B).
#                      Default off (9-field). ONLY enable once the whole fleet runs PR #120+, since
#                      an older agent rejects an 11-field manifest. Lets OTA deliver the Watchdog to
#                      a box that's missing it; the Broker then registers it as a service (PR #119).
#
# Writes:
#   /tmp/update-manifest-v<version>.txt  — the pipe-delimited manifest
#   /tmp/update-manifest-v<version>.sig  — ECDSA P-256 signature, hex-encoded
#
# And (unless SKIP_UPLOAD=1) uploads both to the GitHub release.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

VERSION="${1:-}"
TAG="${2:-v$VERSION}"

if [ -z "$VERSION" ]; then
    echo "Usage: $0 <version> [<release-tag>]" >&2
    echo "Example: $0 3.13.7" >&2
    exit 1
fi

KEY_PATH="${SUAVO_SIGNING_KEY:-$HOME/.suavo/update-signing-p256.pem}"
OWNER="${GITHUB_OWNER:-MinaH153}"
REPO="${GITHUB_REPO:-SuavoAgent}"

if [ ! -f "$KEY_PATH" ]; then
    echo "Private key not found at $KEY_PATH" >&2
    echo "Set SUAVO_SIGNING_KEY or copy key to ~/.suavo/update-signing-p256.pem" >&2
    exit 1
fi

TMP_DIR=$(mktemp -d)
trap "rm -rf $TMP_DIR" EXIT

echo "Fetching release $TAG from $OWNER/$REPO..."

# Download the binaries to /tmp so we can hash them. Watchdog is OPT-IN (self-heal Chunk B):
# only set INCLUDE_WATCHDOG=1 once the whole fleet is on a build that understands the 11-field
# manifest (PR #120+). An older agent REJECTS an 11-field manifest, so default-off keeps OTA working.
BINS="SuavoAgent.Core.exe SuavoAgent.Broker.exe SuavoAgent.Helper.exe"
if [ "${INCLUDE_WATCHDOG:-0}" = "1" ]; then
    BINS="$BINS SuavoAgent.Watchdog.exe"
fi
for BIN in $BINS; do
    URL="https://github.com/$OWNER/$REPO/releases/download/$TAG/$BIN"
    echo "  Fetching $BIN..."
    curl -sL -o "$TMP_DIR/$BIN" "$URL"
    if [ ! -s "$TMP_DIR/$BIN" ]; then
        echo "ERROR: $BIN not present in release $TAG" >&2
        exit 1
    fi
done

# Compute SHA256 for each
CORE_SHA=$(shasum -a 256 "$TMP_DIR/SuavoAgent.Core.exe" | awk '{print $1}')
BROKER_SHA=$(shasum -a 256 "$TMP_DIR/SuavoAgent.Broker.exe" | awk '{print $1}')
HELPER_SHA=$(shasum -a 256 "$TMP_DIR/SuavoAgent.Helper.exe" | awk '{print $1}')

BASE_URL="https://github.com/$OWNER/$REPO/releases/download/$TAG"

# Pipe-delimited manifest (matches v3.12.0 + v3.13.0 format exactly):
# <core_url>|<core_sha>|<broker_url>|<broker_sha>|<helper_url>|<helper_sha>|<version>|net8.0|win-x64
MANIFEST=$(printf "%s|%s|%s|%s|%s|%s|%s|net8.0|win-x64" \
    "$BASE_URL/SuavoAgent.Core.exe"   "$CORE_SHA" \
    "$BASE_URL/SuavoAgent.Broker.exe" "$BROKER_SHA" \
    "$BASE_URL/SuavoAgent.Helper.exe" "$HELPER_SHA" \
    "$VERSION")

# Self-heal Chunk B: append the optional Watchdog fields, matching the agent's
# UpdateManifest.ToCanonical() exactly — the two new fields go AFTER |net8.0|win-x64.
# Lets the cloud deliver SuavoAgent.Watchdog.exe to a box that's missing it; the Broker
# (LocalSystem) then registers it as a service (PR #119). Default-off (9 fields) = unchanged.
if [ "${INCLUDE_WATCHDOG:-0}" = "1" ]; then
    WATCHDOG_SHA=$(shasum -a 256 "$TMP_DIR/SuavoAgent.Watchdog.exe" | awk '{print $1}')
    MANIFEST="$MANIFEST|$BASE_URL/SuavoAgent.Watchdog.exe|$WATCHDOG_SHA"
    echo "INCLUDE_WATCHDOG=1 — manifest carries the Watchdog binary (11-field form)"
fi

MANIFEST_FILE="/tmp/update-manifest-$TAG.txt"
SIG_FILE="/tmp/update-manifest-$TAG.sig"

printf '%s' "$MANIFEST" > "$MANIFEST_FILE"
echo "Manifest written: $MANIFEST_FILE"

# Sign with ECDSA P-256 + SHA-256. OpenSSL emits DER; the .NET updater
# verifies raw IEEE P1363 r||s bytes, so convert before publishing.
DER_SIG="$TMP_DIR/update-manifest.der.sig"
printf '%s' "$MANIFEST" | openssl dgst -sha256 -sign "$KEY_PATH" -out "$DER_SIG"
python3 "$SCRIPT_DIR/ecdsa_der_to_p1363.py" "$DER_SIG" "$SIG_FILE"
if [ "$(wc -c < "$SIG_FILE")" -ne 128 ]; then
    echo "ERROR: P1363 signature must be 128 hex chars" >&2
    exit 1
fi
echo "Signature written: $SIG_FILE"

echo ""
echo "=== Manifest content ==="
cat "$MANIFEST_FILE"
echo ""
echo ""
echo "=== Signature (hex) ==="
cat "$SIG_FILE"

if [ "${SKIP_UPLOAD:-0}" = "1" ]; then
    echo ""
    echo "SKIP_UPLOAD=1 — not uploading. Files staged at $MANIFEST_FILE + $SIG_FILE."
    exit 0
fi

if ! command -v gh >/dev/null 2>&1; then
    echo ""
    echo "gh CLI not found — skipping upload. Files staged at $MANIFEST_FILE + $SIG_FILE."
    echo "Upload manually with:"
    echo "  gh release upload $TAG $MANIFEST_FILE $SIG_FILE --repo $OWNER/$REPO"
    exit 0
fi

echo ""
echo "=== Uploading to GitHub release $TAG ==="
gh release upload "$TAG" "$MANIFEST_FILE" "$SIG_FILE" --repo "$OWNER/$REPO" --clobber

echo ""
echo "Done. Manifest + signature attached to $TAG."
