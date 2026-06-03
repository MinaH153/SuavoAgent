# Runbook: `MeshDeadJob` alert

**Severity:** PAGE
**Triggered by:** `fingerprint_issue_jobs.failure_count >= 5` for a specific
job_id. The dispatcher stops re-claiming dead jobs (per the partial-index
`claimable_idx` predicate `failure_count < 5`), so the row's lifecycle
freezes until operator intervention.

**Codex lineage:** Comp 3 design § "Dead-job recovery" (round-6 HIGH
chunk D).

---

## What the alert means

A coalesced `fingerprint_issue_jobs` row has tripped the failure-count
ceiling. The dispatcher's WHERE clause now excludes it, so:

- New occurrences for this fingerprint will accumulate in
  `fingerprint_occurrences` but **never surface to GitHub** because the
  dispatcher won't re-dispatch this row.
- The replay sweep's `gaps` CTE picks up these occurrences and tries to
  enqueue a NEW job (different `window_start`), so the fingerprint isn't
  permanently lost — but the alerted row stays orphaned.

**Common causes:**

| `last_error` value | Real cause |
|---|---|
| `http_5xx` or `network_*` | GH API outage during the dispatch window. Transient — recover via `retry`. |
| `stale_dispatch_signed_at_outside_window` | Dispatcher clock skew or sustained workflow backlog. Inspect dispatcher health before retrying. |
| `stale_dispatch_token_mismatch` | Re-claim race (see `mesh-stale-completion.md`). Recovery here is for the orphaned-after-stale case. |
| `gh issue create: rate limit exceeded` | GH API rate limit. Recover via `retry` after the rate-limit window resets. |
| `gh issue not found` | The GH issue referenced by `fingerprint_issue_links.github_issue_number` was deleted out-of-band. Recover via `abandon` + re-create. |
| Anything else | Inspect the row + escalate to `@MinaH153`. |

---

## Triage steps

### 1. Identify the row + read `last_error`

```sql
SELECT
  j.id              AS job_id,
  j.fingerprint_id,
  j.action,
  j.window_start,
  j.failure_count,
  j.last_error,
  j.attempt_token,
  j.dispatch_attempt_count,
  j.first_dispatched_at,
  j.last_dispatched_at,
  j.first_occurrence_id,
  j.last_occurrence_id,
  l.github_issue_number,
  l.github_repo,
  l.state             AS link_state,
  l.create_failure_count,
  l.create_suppressed_until,
  l.bump_failure_count,
  l.bump_suppressed_until
FROM public.fingerprint_issue_jobs j
LEFT JOIN public.fingerprint_issue_links l USING (fingerprint_id)
WHERE j.id = $alerted_job_id;
```

### 2. Decide: `retry` or `abandon`

| Symptom | Action |
|---|---|
| `last_error` is transient (network / rate-limit / 5xx) AND the underlying cause is resolved | `retry` |
| `last_error` is persistent (GH issue deleted, repo archived, permission error, ACL change) | `abandon` |
| Multiple jobs for the same fingerprint are dead AND `link.create_failure_count >= 3` (kill switch armed) | `abandon` + investigate the create path |
| Multiple jobs for the same fingerprint are dead AND `link.bump_failure_count >= 3` | `abandon` + investigate why bumps keep failing (issue moved? GH App permission scope changed?) |

### 3. Run the recovery RPC

⚠️ **Schema dependency**: `recover_fingerprint_issue_job` is defined in a
Comp 1.5 follow-up migration that has not yet shipped. Until that
migration lands, the recovery flow is manual:

```sql
-- Manual retry (raw SQL — only valid while the recovery RPC is missing):
UPDATE public.fingerprint_issue_jobs
   SET failure_count        = 0,
       last_error           = 'recovered: ' || $audit_reason,
       dispatch_lease_until = NULL,
       attempt_token        = gen_random_uuid()
 WHERE id = $alerted_job_id
   AND succeeded_at IS NULL;
```

Once Comp 1.5 ships:

```sql
SELECT public.recover_fingerprint_issue_job(
  p_job_id := $alerted_job_id,
  p_mode := 'retry',           -- or 'abandon'
  p_audit_reason := '2026-XX-XX MeshDeadJob: GH rate-limit cleared'
);
```

Returns `TRUE` if the row's state advanced; `FALSE` if the row was
already `succeeded_at IS NOT NULL` (someone else recovered first).

### 4. For `abandon` mode

The RPC marks the row `succeeded_at = NOW()` + `abandoned_at = NOW()`
+ clears `first_occurrence_id` / `last_occurrence_id`. The replay sweep
(`fingerprint_replay_sweep` cron, Comp 3 follow-up) picks up the now-
uncovered occurrence range and re-classifies a fresh job via the state
machine.

Also bumps the link's create_failure_count OR bump_failure_count by 1
(depending on the abandoned row's `action`). At >=3 the link sets
`*_suppressed_until = NOW() + 24h`. If you see the kill switch trip,
investigate before clearing — the issue is likely deeper than one bad
job.

### 5. Verify recovery worked

```sql
SELECT id, succeeded_at, abandoned_at, failure_count
  FROM public.fingerprint_issue_jobs
 WHERE id = $alerted_job_id;

-- For 'retry': succeeded_at IS NULL, failure_count = 0
-- For 'abandon': succeeded_at IS NOT NULL, abandoned_at IS NOT NULL,
--                first/last_occurrence_id IS NULL
```

For `retry`: the next dispatcher tick (every 30s) should re-claim the
row. Watch `j.dispatch_attempt_count` increment + the workflow
running in GitHub Actions.

For `abandon`: the next replay sweep tick (every 5min) should
enqueue a NEW job for the same fingerprint with a different
`window_start`. Watch
`SELECT * FROM fingerprint_issue_jobs WHERE fingerprint_id = $fp ORDER BY id DESC LIMIT 5;`.

---

## Mitigations to keep dead-job rate near zero

1. **Per-action lease TTL** sized to worst-case workflow runtime
   (round-3 HIGH): create=30m, reopen=10m, bump=5m. If `last_error` is
   `stale_dispatch_signed_at_outside_window` and the workflow logs show
   the workflow ran for 20+ min, bump the `create` TTL.

2. **GH API rate-limit headroom** — dispatcher caps at 90/hr (out of
   the 100/hr GH `repository_dispatch` cap, 10% headroom). If dead jobs
   start clustering during high-volume incidents, the dispatcher
   coalescing window may need widening (currently 5min).

3. **GH App scope review** — `bump_failure_count >= 3` on multiple
   fingerprints often indicates an App permission downgrade.
   `gh api /installation/repositories` to verify the App still has
   `Issues: write` on `MinaH153/SuavoAgent`.

---

## See also

- `docs/runbooks/mesh-stale-completion.md` — re-claim race recovery
- `docs/runbooks/mesh-dispatch-token-rotation.md` — HMAC secret + GH App key rotation
- `docs/architecture/phase-2-drafts/component-3-gh-actions-sentry-webhook-design.md`
  § "Dead-job recovery"
