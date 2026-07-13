#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 7 ]]; then
  echo "usage: $0 <base-url> <core-sha256> <broker-sha256> <helper-sha256> <version> <watchdog-sha256> <maintenance-sha256>" >&2
  exit 64
fi

base_url="${1%/}"
core_sha="$2"
broker_sha="$3"
helper_sha="$4"
version="$5"
watchdog_sha="$6"
maintenance_sha="$7"

if [[ "$base_url" != https://* || "$base_url" == *'|'* || "$base_url" == *$'\n'* || "$base_url" == *$'\r'* ]]; then
  echo "base URL must be a pipe-free HTTPS URL" >&2
  exit 65
fi

if [[ -z "$version" || "$version" == *'|'* || "$version" == *$'\n'* || "$version" == *$'\r'* ]]; then
  echo "version must be non-empty and pipe-free" >&2
  exit 65
fi

for value in "$core_sha" "$broker_sha" "$helper_sha" "$watchdog_sha" "$maintenance_sha"; do
  if [[ ! "$value" =~ ^[A-Fa-f0-9]{64}$ ]]; then
    echo "every OTA artifact SHA-256 must contain exactly 64 hexadecimal characters" >&2
    exit 65
  fi
done

# Rollout hop 1 is deliberately the previous-stable 11-field contract. Agents at
# v3.92.1 can parse this shape and receive the Core version that understands the
# 13-field native-maintenance extension. Only an explicit, case-sensitive repo
# variable value of "true" enables hop 2 after that parser has reached the fleet.
manifest="${base_url}/SuavoAgent.Core.exe|${core_sha}|${base_url}/SuavoAgent.Broker.exe|${broker_sha}|${base_url}/SuavoAgent.Helper.exe|${helper_sha}|${version}|net8.0|win-x64|${base_url}/SuavoAgent.Watchdog.exe|${watchdog_sha}"

if [[ "${OTA_FULL_COHORT_MANIFEST:-}" == "true" ]]; then
  manifest="${manifest}|${base_url}/SuavoSetup.exe|${maintenance_sha}"
fi

printf '%s' "$manifest"
