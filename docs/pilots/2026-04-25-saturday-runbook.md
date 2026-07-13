# Saturday 2026-04-25 - Trip A Runbook for Nadim

> **ARCHIVED / DO NOT USE — historical pilot evidence only.** The former
> command-based install and manual restart steps have been removed because they
> are no longer valid or safe field procedures. Use
> `docs/sales/windows-agent-lifecycle.md` for the signed native lifecycle.

Decision: ship `v3.13.9` live observe-only. This is not a demo and not a
`latest` install. The agent installs on Nadim's workstation, heartbeats to cloud,
captures encrypted UIA learning counters, and generates no rules, execution, or
writeback.

## Historical pre-drive gates

- The exact release artifact and release receipt existed for `v3.13.9`.
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

## Current field precheck

Use the dashboard and the native setup system check. Confirm the intended
pharmacy, MFA-protected pairing authority, connected signed installer, Windows
administrator approval, PioneerRx presence, and required network reachability.
Setup itself detects and removes the exact known retired lifecycle artifacts
after quiescing the installed services. Do not inspect or delete old files,
tasks, services, or registry entries by hand.

## Baseline Metrics

Before install, time three PioneerRx actions:

- Open Rx queue.
- Search by NDC.
- Print label.

Accept only if post-install p50 latency is less than `+20%`, idle CPU is less
than `+5pp`, and agent RAM is less than `+200 MB`.

## Retired install block

The 2026-04-25 pilot used a script downloaded from a repository. That path is
retired and the script is no longer shipped. The supported field flow is:

1. Download the connected `SuavoSetup.exe` from the authenticated dashboard.
2. Confirm Windows shows the expected MKM Technologies LLC signature.
3. Complete native pairing, disclosure, system checks, install, and verification.
4. Keep Autopilot off unless the dashboard health receipt and supervised pilot
   authority are both green.

Acceptance:

- The signed native release cohort passes verification.
- `SuavoAgent.Core`, `SuavoAgent.Broker`, and `SuavoAgent.Watchdog` running.
- Heartbeat lands within 60 seconds.
- Cloud version exactly `3.13.9`.
- The dashboard PHI-safe diagnostic receipt reports the intended privacy and
  observation posture.
- Native Repair is available from both the dashboard and Windows Settings.

## Tier 0 Flip

The values below are a historical record of the observe-only posture. They are
not a field procedure and must not be edited locally. Any current pilot posture
is selected through the authenticated dashboard, signed, audited, and confirmed
by the next health receipt.

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

Confirm through the dashboard that the signed posture was applied and the
expected observation counters advance. If they do not, leave Autopilot off and
use native Repair or the PHI-safe support escalation; do not restart services or
edit local configuration.

## Abort Rules

No flip if any of these happen:

- Signed checksum mismatch.
- No admin elevation.
- `suavollc.com:443` blocked.
- Heartbeat missing after 60 seconds.
- Version not exactly `3.13.9`.
- The PHI-safe privacy or encryption diagnostic gate fails.
- Vision or observation is active outside the disclosed, approved scope.
- Native repair or Watchdog health fails.
- PioneerRx slowdown exceeds thresholds.
- Audit trigger, heartbeat fields, BAA/addendum, or decommission path are not live.

Rollback uses the dashboard's signed posture control. Hard removal uses the
signed two-phase decommission flow followed by native uninstall from Windows
Settings; both require their matching receipts.
