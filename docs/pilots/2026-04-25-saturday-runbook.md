# Saturday 2026-04-25 — Runbook for Joshua at Nadim's

> Pragmatic cheat sheet. Keep open on phone during the pilot.

Full day-of context: `~/.claude/projects/-Users-joshuahenein/memory/nadim-pilot-flip-saturday-2026-04-25.md`

---

## Before leaving Bakersfield (Fri evening or Sat morning)

### Option A: v3.13.7 + Watchdog (recommended if smoke test green)

- [ ] On your Windows PC, run the v3.13.7 smoke test:
      ```powershell
      Set-ExecutionPolicy Bypass -Scope Process -Force
      irm "https://raw.githubusercontent.com/MinaH153/SuavoAgent/main/bootstrap.ps1?v=$(Get-Random)" | iex
      ```
- [ ] Verify 3 services RUNNING: `sc query SuavoAgent.Core`, `.Broker`, `.Watchdog`
- [ ] Simulate failure: `taskkill /IM SuavoAgent.Core.exe /F` — verify Watchdog restarts Core within 60-90s (check log at `C:\ProgramData\SuavoAgent\logs\watchdog-*.log`)
- [ ] If all green → stage `SuavoSetup.exe` from `%USERPROFILE%\Downloads` onto USB

### Option B: v3.13.0 + OTA path (fallback)

- [ ] Download `SuavoSetup-v3.13.0.exe` from https://github.com/MinaH153/SuavoAgent/releases/tag/v3.13.0
- [ ] Stage on USB

### Generating v3.13.7 OTA manifest (only if Option A + OTA desired)

The v3.13.7 release ships without an OTA manifest. If you want OTA path
with v3.13.7 (vs USB install), generate + upload:

```bash
cd ~/Code/SuavoAgent
./scripts/generate-ota-manifest.sh 3.13.7
# Uploads update-manifest-v3.13.7.txt + .sig to the GitHub release
```

Requires `~/.suavo/update-signing-p256.pem` (signing key) + `gh` CLI auth'd
as MinaH153 (use `GH_TOKEN=$(security find-internet-password -s github.com -w)`
prefix if gh is on wrong account).

All future releases auto-generate the manifest via the CI workflow change
shipped in commit `689c451`.

---

## At Nadim's (PIONEER10)

Follow `~/.claude/projects/-Users-joshuahenein/memory/nadim-pilot-flip-saturday-2026-04-25.md`
section-by-section. The verified IDs to use:

| Field | Value |
|---|---|
| agent_id | `959ee574-3f1c-44e4-887e-e9cfed555267` |
| pharmacy_id (FOR CONFIG OVERRIDE) | `3101283d-cbb5-4667-bc7c-254a4a7f9c88` |
| machine_name | `PIONEER10` |
| Operator UUID (for `updated_by`) | `ff71ef3f-8467-4c24-85df-a71cb6a2205d` |

### The v3.12 template flip (step 5 of day-of plan)

Paste-ready SQL for step 5 of the pilot-flip:

```sql
INSERT INTO agent_config_overrides (pharmacy_id, config_path, config_value, updated_by, updated_at)
VALUES
  ('3101283d-cbb5-4667-bc7c-254a4a7f9c88', 'Learning.Template.Enabled',          'true'::jsonb,                 'ff71ef3f-8467-4c24-85df-a71cb6a2205d', now()),
  ('3101283d-cbb5-4667-bc7c-254a4a7f9c88', 'Learning.Template.SkillId',          '"learned"'::jsonb,            'ff71ef3f-8467-4c24-85df-a71cb6a2205d', now()),
  ('3101283d-cbb5-4667-bc7c-254a4a7f9c88', 'Learning.Template.ProcessNameGlob',  '"PioneerPharmacy*"'::jsonb,   'ff71ef3f-8467-4c24-85df-a71cb6a2205d', now());
```

### Rollback (if trouble)

```sql
-- Path 1: disable template extraction (keep row, flip value)
UPDATE agent_config_overrides
SET config_value = 'false'::jsonb,
    updated_at = now(),
    updated_by = 'ff71ef3f-8467-4c24-85df-a71cb6a2205d'
WHERE pharmacy_id = '3101283d-cbb5-4667-bc7c-254a4a7f9c88'
  AND config_path = 'Learning.Template.Enabled';

-- Path 2: hard reset — delete all template overrides
DELETE FROM agent_config_overrides
WHERE pharmacy_id = '3101283d-cbb5-4667-bc7c-254a4a7f9c88'
  AND config_path LIKE 'Learning.Template.%';
```

Next ConfigSyncWorker poll (≤5 min) applies the rollback.

### OTA downgrade (if v3.13.x has field regression)

v3.12.0 release artifacts + signed manifest are still live on GitHub:
https://github.com/MinaH153/SuavoAgent/releases/tag/v3.12.0

Queue signed OTA downgrade command from fleet dashboard or via direct
`agent_commands` insert pointing at `update-manifest-v3.12.0.txt` + `.sig`.

---

## Post-pilot (Sun–Tue)

- Check cockpit daily — first WorkflowTemplate expected in 24-72h
- First Template appears in `/pharmacy/agent/approvals` as Pending
- Do NOT transition to Shadow until ≥10 shadow-runs with 0 mismatches
  (UI enforces this, worth knowing)

---

## Non-blocking post-Saturday work ready to kick off

If pilot stable for 72h:
- Phase 0 invariants → lock v0.1 to v1.0 (Codex review + Joshua sign-off)
- Phase A code kicks off week of May 5 per `docs/self-healing/phase-a-architecture.md` §Phase A kickoff tasks

If pilot has issues:
- Execute rollback path
- Post-mortem in `docs/self-healing/incidents/2026-04-25-nadim-pilot-postmortem.md`
- Update `action-grammar-v1.md` with whatever the pilot taught us
