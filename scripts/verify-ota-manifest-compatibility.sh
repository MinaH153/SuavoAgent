#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
composer="$script_dir/compose-ota-manifest.sh"

sha_a="$(printf 'a%.0s' {1..64})"
sha_b="$(printf 'b%.0s' {1..64})"
sha_c="$(printf 'c%.0s' {1..64})"
sha_d="$(printf 'd%.0s' {1..64})"
sha_e="$(printf 'e%.0s' {1..64})"
args=(
  "https://github.com/MinaH153/SuavoAgent/releases/download/v9.8.7"
  "$sha_a" "$sha_b" "$sha_c" "9.8.7" "$sha_d" "$sha_e"
)

field_count() {
  awk -F'|' '{ print NF }' <<<"$1"
}

assert_nonempty_fields() {
  local manifest="$1"
  local field
  IFS='|' read -r -a fields <<<"$manifest"
  for field in "${fields[@]}"; do
    if [[ -z "$field" ]]; then
      echo "OTA manifest contains an empty field" >&2
      exit 1
    fi
  done
}

# This intentionally reproduces the previous-stable parser's shape gate:
# v3.92.1 accepts exactly 9 or 11 non-empty fields and rejects 13.
assert_previous_stable_parse() {
  local manifest="$1"
  local count
  count="$(field_count "$manifest")"
  if [[ "$count" -ne 9 && "$count" -ne 11 ]]; then
    echo "default OTA manifest is not parseable by the previous-stable 9/11 contract (fields=$count)" >&2
    exit 1
  fi
  assert_nonempty_fields "$manifest"
}

default_manifest="$(env -u OTA_FULL_COHORT_MANIFEST bash "$composer" "${args[@]}")"
assert_previous_stable_parse "$default_manifest"
if [[ "$(field_count "$default_manifest")" -ne 11 ]]; then
  echo "default OTA rollout must emit the 11-field transition manifest" >&2
  exit 1
fi

# The repository variable is deliberately case-sensitive: only exact "true"
# opts into the second hop.
non_exact_manifest="$(OTA_FULL_COHORT_MANIFEST=TRUE bash "$composer" "${args[@]}")"
if [[ "$non_exact_manifest" != "$default_manifest" ]]; then
  echo "non-exact OTA_FULL_COHORT_MANIFEST unexpectedly enabled full-cohort mode" >&2
  exit 1
fi

full_manifest="$(OTA_FULL_COHORT_MANIFEST=true bash "$composer" "${args[@]}")"
assert_nonempty_fields "$full_manifest"
if [[ "$(field_count "$full_manifest")" -ne 13 ]]; then
  echo "full-cohort OTA rollout must emit exactly 13 fields" >&2
  exit 1
fi
if [[ "${full_manifest%|*|*}" != "$default_manifest" ]]; then
  echo "full-cohort OTA manifest changed one of the backward-compatible first 11 fields" >&2
  exit 1
fi

echo "OTA manifest rollout compatibility clean (default=11, full-cohort=13)."
