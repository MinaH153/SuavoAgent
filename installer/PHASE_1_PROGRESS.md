# Phase 1 progress log

Tracks day-by-day what's landed vs. pending for v4 Phase 1 (cross-platform packaging).
See `docs/superpowers/specs/2026-05-28-suavoagent-v4-consumer-install.md` §5 Phase 1.

## 2026-05-28 — Day 1 (scaffolding)

### Landed
- `installer/` directory structure created (windows, macos, shared subdirs)
- WiX 4 MSI source files written:
  - `SuavoAgent.wxs` — Product definition, MajorUpgrade, Feature tree, ARP entry, brand metadata
  - `Services.wxs` — Win32 service registration for Core/Broker/Watchdog with FirstFailure restart × 3, LocalSystem, delayed-auto-start
  - `DataDirectories.wxs` — `%ProgramData%\SuavoAgent\` scaffolding with secure ACLs per Codex 2026-05-28 §7 (SYSTEM/Administrators full, Authenticated Users write-only to logs/)
  - `ManagedSettings.wxs` — `managed.json` placement with `NeverOverwrite="yes" Permanent="yes"` so pharmacy IT overrides survive upgrade
  - `UI.wxs` — WixUI_FeatureTree config + EULA + banner refs
- macOS bundle pipeline written:
  - `Info.plist.template` — `com.suavollc.SuavoAgent` bundle ID, LSUIElement (menubar-only), screen capture + AppleEvents usage strings, TeamIdentifierPrefix substitution marker
  - `entitlements.plist` — hardened runtime + JIT/library-validation flags for llama.cpp Metal hot-paths + keychain-access-groups for Phase 3 System Keychain entry
  - `LaunchDaemon.plist.template` — Core as root-running LaunchDaemon (survives logout per Codex §9)
  - `LaunchAgent.plist.template` — Helper as per-user LaunchAgent (needs graphics context)
  - `bundle.sh` — full assemble → sign nested → sign bundle → DMG → sign DMG → notarize with 3× retry → staple → spctl-validate pipeline
- Documentation:
  - `installer/README.md` — overview of layout + Phase 1 success criteria
  - `installer/windows/README.md` — WiX 4 toolchain prerequisites, build invocation, service registration model
  - `installer/macos/README.md` — Apple Developer cert setup walkthrough + 7 GitHub secrets required
- `installer/shared/managed.default.json` — default channel-pinning + Tier-2 destructive-consent + retention settings (schema v1)

### Pending (Joshua's manual one-time setup — BLOCKS macOS CI path)
- [ ] Generate Developer ID Application cert via Apple Developer portal (MKM-org Apple ID)
- [ ] Generate Developer ID Installer cert
- [ ] Export both as `.p12` with passwords
- [ ] Generate app-specific password at appleid.apple.com (for `notarytool`)
- [ ] Base64-encode both P12s and add the 7 secrets to GitHub Actions (see `installer/macos/README.md` for the full secret name list)

Expected one-time effort: ~30 min on Joshua's Mac. Windows MSI path can ship without these.

### Pending (next coding sub-task)
- [ ] `.csproj` multi-RID support for `osx-arm64` + `osx-x64`. Today `SuavoAgent.Setup.csproj` has `OutputType=WinExe` and a `RuntimeIdentifier=win-x64` publish group. Needs conditional OutputType (`WinExe` on Win, `Exe` on Mac) and parallel publish properties for the Mac RIDs. Same for `SuavoAgent.Core/Broker/Helper/Watchdog`. ~half a day of plumbing.
- [ ] Brand artifacts (binary, can't be committed as text):
  - `installer/shared/SuavoAgent.ico` — Windows ARP icon (256×256 ICO)
  - `installer/shared/SuavoAgent.icns` — macOS bundle icon (ICNS with multiple resolutions)
  - `installer/shared/banner.bmp` — 500×58 WiX UI top banner (gold S on dark)
  - `installer/shared/dialog.bmp` — 500×312 WiX UI dialog background
  - `installer/shared/eula.rtf` — End-user license agreement text (legal review pending)
- [ ] `.github/workflows/release.yml` — extend with `package_msi_windows` job (after `sign_windows`) and `package_dmg_macos` matrix job (osx-arm64 + osx-x64). Gated on `vars.APPLE_SIGNING_ENABLED == 'true'` (similar to existing `SIGNING_ENABLED` gate) so Mac CI is skipped cleanly until Joshua's secrets land.
- [ ] First test tag (`v3.15.0-rc1`) — verifies MSI builds, services install, MSI is EV-signed by the existing eSigner pipeline. Mac side stays skipped until secrets are ready.
- [ ] Once secrets ready: re-tag to verify Mac DMG signs + notarizes end-to-end on the same release pipeline.

## Day 2 (planned)
csproj multi-RID + first Windows MSI smoke build (Joshua to run locally if his Win VM is up).

## Day 3-4 (planned)
.github/workflows/release.yml extension, gated. Land MSI side green in CI.

## Day 5-7 (planned)
Mac side: once Apple secrets in CI, end-to-end DMG signed + notarized + stapled green in CI.

## Day 8-10 (planned)
Brand artifact creation (icon design, banner art, EULA legal review with whoever Joshua uses for compliance docs). Spec-only deliverable; doesn't block Phase 2.
