#!/usr/bin/env bash
set -euo pipefail

# Internal engineering automation may use shell tooling. Customer/runtime code may not
# download, invoke, or depend on a mutable Windows script.
runtime_hits="$({
  rg -n -i '(\.ps1\b|executionpolicy|"(?:powershell|pwsh)(?:\.exe)?")' \
    src/SuavoAgent.Setup \
    src/SuavoAgent.Watchdog \
    src/SuavoAgent.Broker \
    src/SuavoAgent.Core \
    -g '*.{cs,csproj,props,targets,json,axaml,xaml}' || true
} | rg -v '^src/SuavoAgent.Broker/Honeytoken/' \
    | rg -v '^src/SuavoAgent.Setup/Maintenance/LegacyLifecycleMigration.cs:' \
    | rg -v '^src/SuavoAgent.Setup/UninstallTerminalCleanup.cs:' || true)"

# The one production exception is the bounded native migration that recognizes
# old filenames so it can delete them. It may name those artifacts, but it must
# never launch either Windows script host.
legacy_migration_host_hits="$(
  rg -n -i '(FileName\s*=.*(?:powershell|pwsh)|Process\.Start\(.*(?:powershell|pwsh))' \
    src/SuavoAgent.Setup/Maintenance/LegacyLifecycleMigration.cs || true
)"

# Native uninstall parses the exact retired task XML only to prove ownership
# before deletion. Its launcher has a tested, case-sensitive allowlist containing
# only schtasks.exe and sc.exe.
legacy_cleanup_launcher_hits="$(
  rg -n 'if \(!IsApprovedCleanupExecutable\(executable\)\)' \
    src/SuavoAgent.Setup/UninstallTerminalCleanup.cs || true
)"
if [[ "$(printf '%s\n' "$legacy_cleanup_launcher_hits" | sed '/^$/d' | wc -l | tr -d ' ')" != "1" ]]; then
  legacy_cleanup_launcher_hits="native uninstall cleanup launcher allowlist is missing or ambiguous"
else
  legacy_cleanup_launcher_hits=""
fi

customer_hits="$(
  rg -n -i '(powershell|pwsh|executionpolicy|\.ps1\b|\.cmd\b|\.bat\b)' docs/sales || true
)"

release_instruction_hits="$(
  rg -n -i '(irm|iwr|invoke-webrequest).*\.(ps1|cmd|bat)\b' \
    .github/workflows/release.yml \
    .github/workflows/hotfix.yml || true
)"

native_secret_ingress_hits="$(
  rg -n -i '(SetupConfig\.Load|--api-key|setup\.json|InstallTokenService|ConnectingView)' \
    src/SuavoAgent.Setup \
    -g '*.{cs,csproj,props,targets,json,axaml,xaml}' || true
)"

movable_release_action_hits="$(
  rg -n 'uses:\s*(actions/(checkout|setup-dotnet|upload-artifact|download-artifact|cache)|softprops/action-gh-release)@(v[0-9]+|main|master)\b' \
    .github/workflows/ci.yml \
    .github/workflows/release.yml \
    .github/workflows/hotfix.yml || true
)"

broad_workflow_permission_hits="$(
  for workflow in .github/workflows/release.yml .github/workflows/hotfix.yml; do
    top_level_contents="$(awk '
      /^permissions:/ { in_permissions = 1; next }
      /^jobs:/ { in_permissions = 0 }
      in_permissions && /^  contents:/ { print; exit }
    ' "$workflow")"
    if [[ "$top_level_contents" != "  contents: read" ]]; then
      printf '%s: top-level %s\n' "$workflow" "${top_level_contents:-contents permission missing}"
    fi
  done
)"

# A later-created higher release must never become rollback for an older hotfix,
# while a first release with no lower stable cohort must fail the release gate.
rollback_fixture='[{"tagName":"v4.0.0","isDraft":false,"isPrerelease":false},{"tagName":"v3.9.0","isDraft":false,"isPrerelease":false},{"tagName":"v3.8.7","isDraft":false,"isPrerelease":false},{"tagName":"v3.9.1-rc.1","isDraft":false,"isPrerelease":true}]'
rollback_selected="$(printf '%s' "$rollback_fixture" | python3 scripts/select-release-rollback-tag.py v3.9.1)"
rollback_rc_selected="$(printf '%s' "$rollback_fixture" | python3 scripts/select-release-rollback-tag.py v3.9.1-rc.1)"
rollback_none="$(printf '%s' "$rollback_fixture" | python3 scripts/select-release-rollback-tag.py v3.0.0)"
rollback_selection_hits=""
if [[ "$rollback_selected" != "v3.9.0" ||
      "$rollback_rc_selected" != "v3.9.0" ||
      -n "$rollback_none" ]]; then
  rollback_selection_hits="release rollback semver selection contract failed"
fi

if [[ -n "$runtime_hits" ||
      -n "$legacy_migration_host_hits" ||
      -n "$legacy_cleanup_launcher_hits" ||
      -n "$customer_hits" ||
      -n "$release_instruction_hits" ||
      -n "$native_secret_ingress_hits" ||
      -n "$movable_release_action_hits" ||
      -n "$broad_workflow_permission_hits" ||
      -n "$rollback_selection_hits" ]]; then
  [[ -z "$runtime_hits" ]] || printf '%s\n' "$runtime_hits"
  [[ -z "$legacy_migration_host_hits" ]] || printf '%s\n' "$legacy_migration_host_hits"
  [[ -z "$legacy_cleanup_launcher_hits" ]] || printf '%s\n' "$legacy_cleanup_launcher_hits"
  [[ -z "$customer_hits" ]] || printf '%s\n' "$customer_hits"
  [[ -z "$release_instruction_hits" ]] || printf '%s\n' "$release_instruction_hits"
  [[ -z "$native_secret_ingress_hits" ]] || printf '%s\n' "$native_secret_ingress_hits"
  [[ -z "$movable_release_action_hits" ]] || printf '%s\n' "$movable_release_action_hits"
  [[ -z "$broad_workflow_permission_hits" ]] || printf '%s\n' "$broad_workflow_permission_hits"
  [[ -z "$rollback_selection_hits" ]] || printf '%s\n' "$rollback_selection_hits"
  echo "Production native/release boundary violated."
  exit 1
fi

echo "Production native/release boundary clean."
