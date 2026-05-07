# SuavoAgent Release Gate

This is the evidence checklist for every production SuavoAgent release.

## Required Evidence

- Tag: `vX.Y.Z`
- GitHub release URL: `https://github.com/MinaH153/SuavoAgent/releases/tag/vX.Y.Z`
- Installer ZIP URL: `https://github.com/MinaH153/SuavoAgent/releases/download/vX.Y.Z/suavoagent-vX.Y.Z-win-x64.zip`
- Checksum file: `checksums.sha256`
- Checksum signature: `checksums.sha256.sig`
- OTA manifest: `update-manifest-vX.Y.Z.txt`
- OTA manifest signature: `update-manifest-vX.Y.Z.sig`
- Authenticode mode: `signed` or `unsigned-passthrough`
- Windows release smoke: `windows-release-smoke` passed
- Bootstrap parse: Windows PowerShell 5.1 parse passed
- Runtime log encoding: Core, Broker, Helper, Watchdog, startup, and crash log files use UTF-8 with BOM so Windows PowerShell 5.1 support commands do not show mojibake.
- Test evidence: solution tests passed in the release workflow
- Production migration evidence: migration names, operator, timestamp, and Supabase project ID recorded in the release note or incident log
- Suavo web post-deploy smoke: `SMOKE_BASE_URL=<production>` passed after cloud route deployment and Supabase migrations, including `/api/agent/register`, `/api/agent/config`, `/api/agent/recover-key`, `/api/agent/sync`, `/api/agent/heartbeat`, and `/api/agent/install-telemetry`
- Runtime health evidence: running Core must produce `config-sync-health.json`; running Watchdog must produce `watchdog-health.json`; missing evidence on a running service is release-probe failure, not "not yet written."
- Cloud-auth health evidence: failed `/api/agent/recover-key` attempts write `cloud-auth-health.json`; heartbeat accepts the sanitized `runtimeHealth.cloudAuth` object; dashboard marks `http_401_Agent_not_found` and failed recovery outcomes critical without storing raw cloud response bodies.

## No-PHI Windows Probe

Before the release job creates the GitHub release, `windows-release-smoke` downloads the final binaries, builds the release ZIP, expands it on `windows-latest`, and runs:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-SuavoAgentReleaseProbe.ps1 -Mode ReleaseArtifact -ReleaseDir $expanded -BootstrapPath .\bootstrap.ps1 -Json
```

The probe validates required binaries, SHA-256 hashability, and the bootstrap repair path. It does not print appsettings values, patient data, Rx numbers, screenshots, or log bodies.

## Installed-Machine Probe

For a real pharmacy PC or smoke VM, run from an elevated PowerShell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-SuavoAgentReleaseProbe.ps1 -Mode Installed -Json
```

The installed-machine probe validates Core, Broker, and Watchdog services; installed binary hashes; bootstrap `--repair` readiness; appsettings ACLs needed for LocalService DPAPI sealing; redacted heartbeat prerequisites; a live HMAC GET to `/api/agent/config`; local `config-sync-health.json` and `watchdog-health.json` evidence; and optional `cloud-auth-health.json` recovery evidence. A missing Core/Watchdog health file while the matching service is running fails the probe. A cloud-auth failure is reported as a redacted status/reason such as `http_401_Agent_not_found`; the probe never prints the API key or response body.
