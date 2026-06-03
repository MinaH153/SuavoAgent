# Runbook: `MeshStaleCompletion` alert

**Severity:** PAGE
**Triggered by:** `mesh-fingerprint-issue-manager.yml` exiting with code 1
on the `Mark job complete` step OR the `Fetch authoritative job row` step
detecting a stale `attempt_token`.

**Codex lineage:** round-6 HIGH chunk C in
`~/Code/SuavoAgent/docs/architecture/phase-2-drafts/component-3-gh-actions-sentry-webhook-design.md`.
Comp 2.2 ship-gate runbook (workflow PR is blocked on this file existing).

---

## What the alert means

The Diagnostic Mesh dispatcher re-claimed a `fingerprint_issue_jobs` row
while a GitHub Actions workflow was still executing the previous claim.
The previous workflow's GH issue operation (`create` / `bump` / `reopen`)
may or may not have completed; the live attempt's workflow is now also
acting on the same fingerprint.

Possible outcomes:

| Scenario | Symptom |
|---|---|
| Both workflows raced through `create` | Two GH issues for one fingerprint |
| Stale workflow `bump`ed, live workflow `bump`ed again | Duplicate comments |
| Stale workflow finished BEFORE the re-claim | First completion advanced cursor; live workflow's RPC returns FALSE — current behaviour, alarm fires harmlessly |
| Both workflows mid-flight | Indeterminate — needs human triage |

The `complete_fingerprint_issue_job` SECURITY DEFINER RPC rejects the
stale attempt's completion (the row's CURRENT `attempt_token` matches the
live attempt, not the stale one), so the database state is consistent.
**The risk is operational: duplicate GH issues / comments that confuse
on-call engineers.**

---

## Triage steps

### 1. Inspect the alerted job + linked issue

```sql
SELECT
  j.id              AS job_id,
  j.fingerprint_id,
  j.action,
  j.attempt_token,
  j.dispatch_attempt_count,
  j.failure_count,
  j.first_dispatched_at,
  j.last_dispatched_at,
  l.github_issue_number,
  l.github_repo,
  l.state           AS link_state
FROM public.fingerprint_issue_jobs j
LEFT JOIN public.fingerprint_issue_links l USING (fingerprint_id)
WHERE j.id = $alerted_job_id;
```

Look for:
- `dispatch_attempt_count > 1` — the row has been re-claimed at least once
- `failure_count > 0` — prior attempts already failed; the dispatcher
  is retrying. At `failure_count >= 5`, the dispatcher gives up.
- `link_state = 'claimed'` — no GH issue created yet; both racing
  workflows may have tried to create. Inspect step 2.
- `link_state IN ('open','reopened')` — issue already exists; check
  for duplicate comments in step 2.

### 2. Inspect GH issues for duplicates

```bash
# List recent mesh-fingerprint issues, sorted by creation.
gh issue list --repo "$LINK_GITHUB_REPO" \
  --label mesh-fingerprint \
  --state all \
  --search "in:title \"in $COMPONENT\"" \
  --limit 20
```

If you find **2 or more GH issues** for the same fingerprint:

- **Canonical = the lowest-numbered issue** UNLESS
  `fingerprint_issue_links.github_issue_number` already names a different
  one — in that case the link's `github_issue_number` is canonical
  (round-7 MED clarification).
- **Edge case (round-8 MED)**: if the link's issue is CLOSED and the
  duplicates are OPEN (the link was closed before the stale-completion
  event), the operator MUST update the link to point at one of the new
  open issues (lowest number). Use the audited admin RPC:

  ```sql
  -- Defined in a Comp 1.5 follow-up migration. Raw UPDATE on
  -- github_issue_number is blocked by the write-once trigger; this RPC
  -- bypasses with explicit audit_reason logging.
  SELECT public.update_fingerprint_issue_link(
    p_fingerprint_id := $alerted_fp_id,
    p_new_issue_number := $lowest_open_duplicate,
    p_audit_reason := 'stale-completion 2026-XX-XX — link pointed at closed issue; reassigned to open duplicate per runbook'
  );
  ```

- Edit every non-canonical duplicate's body to reference the canonical:
  ```bash
  gh issue comment "$DUP_ISSUE" --repo "$REPO" \
    --body "Closing as duplicate of #${CANONICAL}. Auto-created during stale-completion incident on $(date -u +%F)."
  gh issue close "$DUP_ISSUE" --repo "$REPO"
  ```

### 3. Mark the alerted job succeeded

Use the CURRENT `attempt_token` (the one the live attempt holds — **not**
the stale token from the alerted workflow's logs):

```sql
SELECT public.complete_fingerprint_issue_job(
  p_job_id := $alerted_job_id,
  p_attempt_token := $current_attempt_token,  -- from step 1's query
  p_last_occurrence_id := $alerted_job_last_occurrence_id
);
```

Returns `true` if the row advanced; `false` means the row was already
completed by another attempt and no further action is needed.

### 4. Do NOT advance any cursor manually

The `fingerprint_registry.last_issue_occurrence_id` cursor is advanced
**only** by `complete_fingerprint_issue_job`. Raw `UPDATE` on this column
is blocked by the `fingerprint_registry_cursor_monotonic` trigger and
the column-restricted `GRANT UPDATE` matrix.

If the alarm storm is large and you suspect cursor desync, use the
audited admin RPC (defined in the same Comp 1.5 follow-up migration):

```sql
-- Last-resort cursor repair. Requires an audit_reason.
SELECT public.repair_fingerprint_cursor(
  p_fingerprint_id := $alerted_fp_id,
  p_audit_reason := 'stale-completion incident 2026-XX-XX'
);
```

---

## Prevention / tuning knobs

If `MeshStaleCompletion` fires more than 1 in any 1-hour window, the
underlying issue is **dispatcher re-claim while workflow still running**.
Two mitigations:

1. **Increase per-action lease TTL** — `complete_fingerprint_issue_job`
   relies on `dispatch_lease_ttl` being longer than the worst-case
   workflow runtime. Current values (round-3 HIGH):
   - `create` → 30 minutes
   - `reopen` → 10 minutes
   - `bump` → 5 minutes

   If `gh issue create` consistently takes >25 min due to GH API
   rate-limit retries, bump the `create` TTL to 60 min in the
   dispatcher cron's `UPDATE` clause (Comp 3 cron definition).

2. **Token verification at the START of every GH op for long-running
   actions** — the workflow's `Fetch authoritative job row` step
   already does this once. For exceptionally long `create` runs, add a
   second re-fetch after the `gh issue create` step + before `Mark job
   complete` to catch a re-claim mid-action.

3. **Investigate the dispatcher's claim WHERE clause** — re-claim
   should only happen when `dispatch_lease_until < NOW()`. If the
   dispatcher is somehow claiming non-expired rows, that's a bug in the
   Comp 3 cron — escalate to `@MinaH153`.

---

## Alert rule (Prometheus)

```yaml
- alert: MeshStaleCompletion
  expr: increase(mesh_dispatch_rejected_total{reason="stale_completion"}[1h]) > 1
  for: 5m
  labels:
    severity: page
    team: mesh
  annotations:
    summary: "Mesh dispatcher producing stale-completion races"
    runbook: "docs/runbooks/mesh-stale-completion.md"
```

The workflow's `Mark job complete` step emits the
`mesh_dispatch_rejected_total{reason="stale_completion"}` counter via
the GitHub Actions Prometheus push gateway (Comp 3 follow-up — currently
absent; today the alert fires off the `exit 1` GitHub Actions UI state).

---

## See also

- `docs/runbooks/mesh-dead-job.md` (TODO) — `failure_count >= 5` triage
- `docs/runbooks/mesh-dispatch-token-rotation.md` (TODO) — rotation cadence
- `docs/architecture/phase-2-drafts/component-3-gh-actions-sentry-webhook-design.md`
  § "GitHub Actions workflow" — workflow design lineage
- `~/Code/Suavo/supabase/migrations/20260601000000_mesh_phase_2_fingerprint_registry.sql`
  — `complete_fingerprint_issue_job` RPC definition (PR #524, billing-blocked)
