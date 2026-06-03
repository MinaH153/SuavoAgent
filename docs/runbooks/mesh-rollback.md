# Runbook: Mesh ruleset rollback

**Severity:** P0 (rolling back active prod state)
**Triggered by:** agent regression observed AFTER an OTA ruleset adoption
that requires reverting to the previously embedded ruleset.

**Codex lineage:** Comp 2 design Round-7 MEDIUM + Round-8 MEDIUM —
**Comp 2.2 ship-gate**. The cache-newer-wins steady-state rule needs an
explicit operator escape hatch when an agent binary downgrade also
requires reverting ruleset state.

---

## When to use this runbook

The agent's boot logic prefers the CACHED ruleset over the EMBEDDED
ruleset when `cache.ruleset_version_int > embedded.ruleset_version_int`
(round-6 HIGH). This works for forward rollouts: cloud pushes v1.5, the
worker swaps + persists; on next agent restart the v1.5 cache wins.

The edge case: **binary rollback**. If ops rolls the agent back to a
previous version with a LOWER `embedded.ruleset_version_int`, the cached
newer ruleset still wins on next boot — even though the operator's
intent was to revert ALL changes including ruleset state.

This runbook is the operator's escape hatch.

---

## Detection

Symptoms of "rollback needed":

| Symptom | Likely cause |
|---|---|
| Agent panics / log floods of `wire_handler_failed` after a ruleset OTA | New ruleset's `calibration_fingerprints` reference code paths that don't exist in the agent binary |
| New `signal_kind` or `component` in `fingerprint_occurrences.context` that the agent's enum doesn't recognize | Cloud-side schema drift; new ruleset accidentally enabled before agent rollout |
| Heartbeat shows `mesh.ruleset_version_int = <high>` but the agent is on an OLD binary | Cache-newer-wins kept the new ruleset across an emergency binary rollback |

If any of these match, proceed to the appropriate rollback path below.

---

## Path A: Delete the disk cache (simplest)

Use when the operator wants the agent to fall back to the embedded
ruleset on next start.

### Steps

1. Stop the agent service:
   ```powershell
   # Windows host (Queen / pilot):
   Stop-Service -Name 'SuavoAgent.Core'
   Stop-Service -Name 'SuavoAgent.Broker'
   Stop-Service -Name 'SuavoAgent.Watchdog'
   ```

2. Delete the cache file(s):
   ```powershell
   $cacheDir = "$env:ProgramData\SuavoAgent\diagnostics\ruleset-cache"
   Remove-Item "$cacheDir\ruleset-current.json" -Force -ErrorAction SilentlyContinue
   Remove-Item "$cacheDir\ruleset-previous.json" -Force -ErrorAction SilentlyContinue
   ```

3. Verify the agent's embedded ruleset is the desired floor:
   ```powershell
   # Inspect the embedded resource in the running binary:
   $asm = [System.Reflection.Assembly]::LoadFrom(
     "$env:ProgramFiles\SuavoAgent\SuavoAgent.Diagnostics.dll")
   $stream = $asm.GetManifestResourceStream(
     ($asm.GetManifestResourceNames() | Where-Object { $_ -like '*ruleset-v1.json' }))
   $reader = [System.IO.StreamReader]::new($stream)
   $reader.ReadToEnd() | ConvertFrom-Json | Format-List ruleset_version, ruleset_version_int
   ```

4. Restart services:
   ```powershell
   Start-Service -Name 'SuavoAgent.Core'
   Start-Service -Name 'SuavoAgent.Broker'
   Start-Service -Name 'SuavoAgent.Watchdog'
   ```

5. Watch the local journal + heartbeat for confirmation:
   ```powershell
   Get-Content "$env:ProgramData\SuavoAgent\diagnostics\events.jsonl" -Tail 20 -Wait
   # Look for: signal_kind=ruleset_swapped → indicates the embedded
   # ruleset was adopted on boot (since no cache existed). The
   # heartbeat extras should show mesh.ruleset_version matching the
   # embedded ruleset's version string.
   ```

### Caveats

- The agent's `ConfigSyncWorker` will resume polling the cloud `agent-ruleset`
  endpoint on the next interval (5min). If the cloud is serving the SAME
  problematic ruleset, the cache will repopulate. To prevent that:
  - **Option A1**: Block the agent's outbound DNS to the cloud while the
    incident is open (firewall rule). Disruptive — also blocks config
    overrides.
  - **Option A2**: Roll back the cloud's `signed_rulesets` table to the
    prior bundle (separate Suavo-repo runbook). Preferred for fleet-wide
    rollback.

---

## Path B: Rollback-epoch sentinel (multi-boot pinning)

Use when the operator wants to PIN the agent at a specific
`ruleset_version_int` for multiple boots without deleting the cache
each time.

### Concept

Drop a `ruleset-rollback-epoch.json` sentinel in the cache directory.
The agent's boot logic honours it as `max_allowed_ruleset_version_int`
for the next boot AND prevents the worker from swapping anything above
this value during the runtime session.

### Sentinel format

```json
{
  "max_allowed_ruleset_version_int": 11,
  "set_at": "2026-05-15T00:00:00Z",
  "set_by": "@oncall",
  "audit_reason": "v1.2 ruleset (version_int=12) caused wire_handler_failed flood; pinning to v1.1 (11) until cloud reverts"
}
```

### Steps

1. Stop services (same as Path A step 1).

2. Write the sentinel:
   ```powershell
   $sentinel = @{
     max_allowed_ruleset_version_int = 11
     set_at = (Get-Date -Format "yyyy-MM-ddTHH:mm:ssZ")
     set_by = "@$env:USERNAME"
     audit_reason = "<your reason>"
   } | ConvertTo-Json
   $sentinel | Out-File -FilePath "$env:ProgramData\SuavoAgent\diagnostics\ruleset-cache\ruleset-rollback-epoch.json" -Encoding utf8
   ```

3. Optionally delete the cache (Path A step 2) so the agent loads from
   embedded + applies the sentinel ceiling on top:

4. Restart services.

5. Verify in the journal:
   ```
   {"ts":"2026-05-15T00:01:00Z","signal_kind":"ruleset_rollback_epoch_applied",
    "max_allowed_ruleset_version_int":11,"audit_reason":"..."}
   ```

### Lifting the pin

When the underlying issue is resolved + the cloud has rolled back:

1. Delete the sentinel:
   ```powershell
   Remove-Item "$env:ProgramData\SuavoAgent\diagnostics\ruleset-cache\ruleset-rollback-epoch.json" -Force
   ```

2. Restart services. The next OTA poll picks up whatever the cloud is
   now serving (no ceiling).

### Sentinel implementation status

⚠️ As of 2026-05-14, the rollback-epoch sentinel is **NOT yet
implemented** in the agent's boot code. The `ConfigSyncWorker.InitializeRulesetCacheAsync`
method needs to:

1. Check for the sentinel file
2. If present, parse + record `max_allowed_ruleset_version_int`
3. Use it as a ceiling in both the cache-vs-embedded boot comparison
   AND every subsequent `PollRulesetAsync` swap check

Until this code lands (tracked in Comp 2.2 follow-up), **use Path A
(delete cache)** for rollbacks.

---

## Path C: Cloud-side `signed_rulesets` rollback

Use for fleet-wide rollback when the issue affects multiple agents and
deleting individual caches is impractical.

### Steps (high level — coordinate with the cloud-side runbook)

1. Identify the LAST GOOD `signed_rulesets` row:
   ```sql
   SELECT key_id, ruleset_version, ruleset_version_int, created_at,
          signature_base64
     FROM public.signed_rulesets
    WHERE key_id = $active_key_id
    ORDER BY ruleset_version_int DESC
    LIMIT 10;
   ```

2. The cloud Edge Function (`agent-ruleset`) returns whichever row has
   the highest `ruleset_version_int` for the active key_id. To roll
   back fleet-wide, you have two options:
   - **Soft rollback**: insert a NEW row with the same payload as the
     last-good row but a HIGHER `ruleset_version_int` (the monotonic
     sequence advances). Agents will adopt it as a forward swap.
   - **Hard rollback**: delete the bad row(s) from `signed_rulesets`.
     Agents that already cached the bad ruleset stay on the cache
     (cache-newer-wins until Path A or B clears it).

   **Prefer soft rollback** — it's idempotent + doesn't lose audit history.

3. Audit log the rollback in the cloud-side runbook.

---

## Post-rollback verification

Before declaring the incident resolved:

1. **Sentinel-canary heartbeat**: confirm 3 consecutive heartbeats from
   each affected agent show the expected `mesh.ruleset_version_int`
   value matching the rollback target.

2. **No wire_handler_failed regression**: tail
   `events.jsonl` for 15 min after restart, confirm no fresh
   `wire_handler_failed` entries.

3. **Sentry / heartbeat success rate**: `mesh.sentry_post_success_rate`
   gauge in the heartbeat shouldn't degrade after rollback.

4. **Post-mortem note**: add an entry to the wiki at
   `~/Code/obsidian-vault/wiki/concepts/` documenting (a) the bad ruleset
   version, (b) the symptom, (c) the recovery path used.

---

## See also

- `docs/runbooks/mesh-stale-completion.md`
- `docs/runbooks/mesh-dead-job.md`
- `docs/runbooks/mesh-dispatch-token-rotation.md`
- Comp 2 design § "B. Agent-side: extend ConfigSyncWorker" (cache-vs-embedded boot)
- Comp 2 design § "Failure modes (must be tested)"
