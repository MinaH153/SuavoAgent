#!/usr/bin/env bash
# ============================================================================
# Generate a new ECDsa P-256 ruleset-signing keypair.
# ============================================================================
# The PUBLIC key embeds into the agent at build time (Resources/ + csproj
# wildcard). The PRIVATE key uploads to Supabase Vault under the
# `ruleset-signing-key-<key_id>` secret name; the cloud Edge Function
# `agent-ruleset` reads it on demand to sign each fetched bundle.
#
# Key rotation policy: annual baseline, immediate on suspected compromise.
# Old keys stay in the agent trust store for one release cycle so already-
# deployed agents can still verify in-flight bundles signed with the
# rotated-out key. New bundles use the new key_id from the cutover date.
#
# Usage:
#   scripts/generate-ruleset-signing-key.sh <key-id>
#
# Example:
#   scripts/generate-ruleset-signing-key.sh ruleset-v1-key-2027-05-13
# ============================================================================

set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 <key-id>" >&2
  echo "Example: $0 ruleset-v1-key-$(date -u +%Y-%m-%d)" >&2
  exit 1
fi

KEY_ID="$1"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
RESOURCES_DIR="$REPO_ROOT/src/SuavoAgent.Diagnostics/Resources"
PUB_PATH="$RESOURCES_DIR/ruleset-signing-key-$KEY_ID.pub.pem"
PRIVATE_PATH="/tmp/ruleset-signing-key-$KEY_ID.private.pem"

if [[ ! -d "$RESOURCES_DIR" ]]; then
  echo "ERROR: $RESOURCES_DIR does not exist." >&2
  exit 1
fi

if [[ -f "$PUB_PATH" ]]; then
  echo "ERROR: $PUB_PATH already exists. Pick a fresh key-id (rotation should never overwrite)." >&2
  exit 1
fi

# Validate key-id shape — must match the trust-store ExtractKeyId regex.
if [[ ! "$KEY_ID" =~ ^[A-Za-z0-9._-]+$ ]]; then
  echo "ERROR: key-id may only contain [A-Za-z0-9._-]; got '$KEY_ID'." >&2
  exit 1
fi

echo "Generating ECDsa P-256 keypair for key_id=$KEY_ID..."
openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 \
  -outform PEM -out "$PRIVATE_PATH"
chmod 600 "$PRIVATE_PATH"

openssl pkey -in "$PRIVATE_PATH" -pubout -outform PEM -out "$PUB_PATH"

echo ""
echo "✓ Public key embedded at: $PUB_PATH"
echo "✓ Private key (temp): $PRIVATE_PATH"
echo ""
echo "Next steps:"
echo "  1. Upload PRIVATE key to Supabase Vault:"
echo "     supabase secrets set ruleset-signing-key-$KEY_ID=\"\$(cat $PRIVATE_PATH)\""
echo "  2. Wipe the local copy:"
echo "     shred -u $PRIVATE_PATH  # (or rm -P on macOS)"
echo "  3. Update the embedded ruleset-v1.json key_id to \"$KEY_ID\""
echo "     (only when the cutover date arrives — old key stays embedded"
echo "     for one release cycle to verify in-flight bundles)."
echo "  4. git add $PUB_PATH && commit + push to roll the agent trust store."
echo ""
echo "NEVER commit the private key. NEVER print the private key to logs."
