# Saturday 2026-04-25 - Trip A Runbook for Nadim

Decision: ship `v3.13.9` live observe-only. This is not a demo and not a
`latest` install. The agent installs on Nadim's workstation, heartbeats to cloud,
captures encrypted UIA learning counters, and generates no rules, execution, or
writeback.

## Pre-drive Gates

- GitHub release exists: `gh release list --repo MinaH153/SuavoAgent --limit 10`
  must show `v3.13.9`.
- Legal: Nadim signs `docs/pilots/nadim-shadow-learning-addendum.md` before
  `Agent.LearningMode=true` or `Agent.TemplateLearning.Enabled=true`.
- Cloud migration applied:
  `supabase/migrations/20260425040000_agent_config_overrides_audit.sql`.
- Heartbeat cloud view shows these fields after staging smoke:
  `learning_mode`, `template_learning`, `behavioral_event_count`,
  `tree_snapshot_count`, `interaction_event_count`, `vision`, `receipt_only_mode`,
  and `writeback_engine_enabled`.
- Remote decommission smoke: queue signed `decommission` through
  `/api/agent/commands`; do not use `config_json.decommission`.
- Backup laptop demo works before driving, but only as abort path.

## Field Precheck

Run as elevated PowerShell:

```powershell
whoami /groups | findstr /i "S-1-5-32-544"
Test-NetConnection raw.githubusercontent.com -Port 443
Test-NetConnection github.com -Port 443
Test-NetConnection suavollc.com -Port 443
Get-ScheduledTask | ? { $_.TaskName -like "*SuavoAgent*" }
Test-Path C:\SuavoAgent
```

Abort before install if admin elevation is unavailable or `suavollc.com:443`
is blocked. Bootstrap quarantines `C:\SuavoAgent` and legacy scheduled tasks,
but confirm before it runs.

## Baseline Metrics

Before install, time three PioneerRx actions:

- Open Rx queue.
- Search by NDC.
- Print label.

Accept only if post-install p50 latency is less than `+20%`, idle CPU is less
than `+5pp`, and agent RAM is less than `+200 MB`.

## Install

Use a pinned release and no token literal in shell history:

```powershell
Set-ExecutionPolicy Bypass -Scope Process -Force
irm "https://raw.githubusercontent.com/MinaH153/SuavoAgent/v3.13.9/bootstrap.ps1" -OutFile $env:TEMP\suavo-bootstrap.ps1
& $env:TEMP\suavo-bootstrap.ps1 -ReleaseTag v3.13.9
```

Paste the one-time install token only into the hidden prompt.

Acceptance:

- Signed checksum passes.
- `SuavoAgent.Core`, `SuavoAgent.Broker`, and `SuavoAgent.Watchdog` running.
- Heartbeat lands within 60 seconds.
- Cloud version exactly `3.13.9`.
- `%ProgramData%\SuavoAgent\vision.json` absent or `Enabled=false`.
- `%ProgramData%\SuavoAgent\state.db` magic header is not `SQLite format 3`.
- No raw screenshots, OCR text, or screen captures under `%ProgramData%\SuavoAgent`.
- Watchdog kill test restarts Core within 60-90 seconds.

## Tier 0 Flip

Only after every gate above passes:

1. POST these overrides to `/api/admin/agent-config-overrides` (admin auth + CSRF). The audit trigger rejects writes missing `updated_by` with `23502`:

```text
Agent.LearningMode = true
Agent.TemplateLearning.Enabled = true
Agent.TemplateLearning.Mode = "capture"
Agent.TemplateLearning.SkillId = "nadim-pioneer-shadow"
Agent.TemplateLearning.ProcessNameGlob = "PioneerPharmacy*"
Agent.TemplateLearning.RuleGeneration = false
Agent.TemplateLearning.AutoApproveOnFingerprintMatch = false
Agent.AutoExecution.Enabled = false
Agent.AutoExecution.RequireConfirmation = true
Agent.AutoExecution.WritebackEnabled = false
Agent.FleetFeatures.SchemaAdaptation = false
Agent.ReceiptOnlyMode = true
MissionLoop.Phase1.Enabled = false
```

2. Restart `SuavoAgent.Core`. `LearningWorker`, `WritebackProcessor`, `ActionVerifier`, and `MissionExecutor` consume `IOptions<AgentOptions>` — frozen at DI construction. `ConfigSyncWorker` writes `config-overrides.json` to disk but the workers do not auto-reload. A restart is the only path to apply the new values. Watchdog restarts Core within 60-90s:

```powershell
taskkill /IM SuavoAgent.Core.exe /F
```

3. Confirm via heartbeat within 60s: cloud agent panel reflects `learning_mode=true`, `template_learning.mode="capture"`. Within 30 minutes of PioneerRx interaction, capture event counters go non-zero. If counters stay at zero, `ProcessNameGlob` isn't matching — debug onsite, do not promote to Tier 1.

## Abort Rules

No flip if any of these happen:

- Signed checksum mismatch.
- No admin elevation.
- `suavollc.com:443` blocked.
- Heartbeat missing after 60 seconds.
- Version not exactly `3.13.9`.
- Encrypted DB proof fails.
- Vision enabled or screenshots/OCR artifacts exist.
- Watchdog kill test fails.
- PioneerRx slowdown exceeds thresholds.
- Audit trigger, heartbeat fields, BAA/addendum, or decommission path are not live.

Rollback:

- Soft: set Tier 0 flags false, audit row fires, restart Core.
- Hard: signed two-phase `decommission` command, local archive ACK, then uninstall.
