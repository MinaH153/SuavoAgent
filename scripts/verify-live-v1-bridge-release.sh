#!/usr/bin/env bash
set -euo pipefail

: "${GH_TOKEN:?GH_TOKEN is required}"
: "${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is required}"
[[ "$GITHUB_REPOSITORY" == "MinaH153/SuavoAgent" ]]

claim="security/ota-v1-bridge-convergence-claim.json"
bridge_tag="$(jq -er '.bridgeReleaseTag | select(test("^v(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$"))' "$claim")"
gh api "repos/$GITHUB_REPOSITORY/releases/tags/$bridge_tag" > live-bridge-release.json
release_id="$(jq -er '.id | select(type == "number" and . > 0)' live-bridge-release.json)"
gh api "repos/$GITHUB_REPOSITORY/git/ref/tags/$bridge_tag" > live-bridge-tag-ref.json
gh api --paginate --slurp \
  "repos/$GITHUB_REPOSITORY/releases/$release_id/assets?per_page=100" \
  > live-bridge-assets.json
python3 scripts/v1_bridge_run_metadata.py validate-live-bridge-release \
  --release live-bridge-release.json \
  --assets live-bridge-assets.json \
  --tag-ref live-bridge-tag-ref.json \
  --inventory security/ota-fleet-inventory-snapshot.json \
  --inventory-signature security/ota-fleet-inventory-snapshot.sig \
  --evidence security/ota-v1-bridge-convergence-evidence.json \
  --claim security/ota-v1-bridge-convergence-claim.json \
  --claim-signature security/ota-v1-bridge-convergence-claim.sig \
  --registry security/ota-update-trust-roots.json
