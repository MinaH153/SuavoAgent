# Windows MSI build

Builds `SuavoAgent-vX.Y.Z-win-x64.msi` via WiX 4 toolset. EV-signed by the
existing SSL.com eSigner cloud-signing pipeline (already shipped 2026-05-28
in PR #97 / v3.14.6).

## Toolchain prerequisites

WiX 4 ships as a `dotnet` tool:

```
dotnet tool install --global wix
wix extension add -g WixToolset.UI.wixext
wix extension add -g WixToolset.Util.wixext
```

CI installs both in the `package_msi_windows` workflow job before building.

## File layout

| File | Purpose |
|---|---|
| `SuavoAgent.wxs` | Product definition: Product ID, name, manufacturer, version, upgrade code, features, ARP entry |
| `Services.wxs` | Win32 service registration for Core, Broker, Watchdog; Helper is a per-user app (not service) |
| `DataDirectories.wxs` | `%ProgramData%\SuavoAgent\` directory creation with ACLs scoped per Codex security review |
| `ManagedSettings.wxs` | `%ProgramData%\SuavoAgent\managed.json` scaffolding for Phase 5 channel-pinning |
| `UI.wxs` | WixUI_FeatureTree dialog set (Welcome → License → Features → Install → Finish) |

## Build invocation

```powershell
# from repo root (CI does this in the package_msi_windows job)
wix build `
  -arch x64 `
  -define "VERSION=$env:VERSION_NUMERIC" `
  -define "SOURCE_DIR=$env:GITHUB_WORKSPACE\release" `
  -ext WixToolset.UI.wixext `
  -ext WixToolset.Util.wixext `
  -out "$env:GITHUB_WORKSPACE\installer\windows\bin\SuavoAgent-v$env:VERSION-win-x64.msi" `
  installer\windows\SuavoAgent.wxs `
  installer\windows\Services.wxs `
  installer\windows\DataDirectories.wxs `
  installer\windows\ManagedSettings.wxs `
  installer\windows\UI.wxs
```

## Service registration model

Three Windows services register at install time:

| Service | Display name | Account | Start type | Recovery | Notes |
|---|---|---|---|---|---|
| `SuavoAgent.Core` | Suavo Agent Core | LocalSystem | Automatic (delayed) | Restart after 60s × 3 | Reads/writes `%ProgramData%\SuavoAgent\state.db`; runs reasoning + cloud sync |
| `SuavoAgent.Broker` | Suavo Agent IPC Broker | LocalSystem | Automatic (delayed) | Restart after 60s × 3 | Named-pipe bus between Core ↔ Helper ↔ Watchdog |
| `SuavoAgent.Watchdog` | Suavo Agent Watchdog | LocalSystem | Automatic (delayed) | Restart after 60s × 3 | Restarts Core/Broker if they fail; runs bootstrap.ps1 -Repair as last resort |

`SuavoAgent.Helper` does NOT install as a service — it's a per-user-session
process (run via Scheduled Task triggered on user logon) because it needs
the user-session graphics context for screen capture + UI actuation.

## Verification

After build, before signing:
```powershell
# MSI structure looks right
wix msi inspect installer\windows\bin\SuavoAgent-v3.15.0-rc1-win-x64.msi

# Smoke install in a clean VM
msiexec /i installer\windows\bin\SuavoAgent-v3.15.0-rc1-win-x64.msi /qn /l*v install.log
Get-Service SuavoAgent.Core, SuavoAgent.Broker, SuavoAgent.Watchdog
```

After signing (sslcom/actions-codesigner@develop with the existing eSigner
cloud HSM):
```powershell
signtool verify /pa /v installer\windows\bin\SuavoAgent-v3.15.0-rc1-win-x64.msi
```

Should output "Successfully verified" with MKM TECHNOLOGIES LLC as signer.

## SmartScreen reputation

Per today's eSigner verification (Joshua tested with SuavoSetup.exe), an
EV-signed Windows binary from a new publisher initially shows the
"Windows protected your PC — unrecognized app" panel with the "More info"
revealing the verified publisher. EV reputation builds with download +
run count.

**Why an EV-signed MSI is materially better than the today's EV-signed
ZIP-of-EXEs:** MSIs distribute as a single signed artifact and Windows
SmartScreen accumulates reputation per-publisher faster on MSI than on
loose EXEs. The "unrecognized app" panel typically clears within 1-2
weeks of low-volume distribution for EV-signed MSIs.
