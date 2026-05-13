# Phase 2 Component 3 — Sentry webhook → fingerprint_occurrences + GH issue lifecycle (DRAFT)

Source: spec §1 (Phase 2 row) + §6 (`context` JSONB allowlist + forbidden-keys enforcement).

Status: DRAFT — awaits 7-day Queen burn-in completion (2026-05-20) + `/plan-eng-review` + Codex review before code.

## Architecture: one webhook, two outcomes (recommended)

Sentry sends one webhook to a Supabase Edge Function. The Edge Function:
1. Validates the payload (HMAC signature + forbidden-keys reject)
2. Inserts row into `fingerprint_occurrences` (per Component 1)
3. Dispatches a GitHub Actions `repository_dispatch` event to manage the corresponding GH issue

```
[agent] → Sentry POST event (with fingerprint tag)
         ↓
       Sentry alert rule fires
         ↓
       Sentry webhook → POST our Edge Function
         ├─→ INSERT fingerprint_occurrences   (data path)
         └─→ POST GitHub /repos/.../dispatches (ops path)
                                ↓
                          GH Actions workflow
                                ↓
                          create / bump / reopen GH issue
```

**Why not Sentry → GH directly**: Sentry → GH integration is vendor-locked. We need per-fingerprint logic (alias_of resolution, occurrence_count thresholds) before deciding whether to create a NEW issue, bump an EXISTING one, or reopen a closed one. That logic lives in our cloud, not in Sentry's templating.

**Why dispatch GH from Edge Function (vs doing the GH API call inline)**: Edge Function p99 budget is tight (Sentry retries on 5xx → duplicate occurrences). GH API can be slow + rate-limited. Fire-and-forget dispatch to GH Actions decouples; if GH workflow fails, occurrence is still recorded.

## Edge Function: `supabase/functions/mesh-sentry-ingest/`

**Path**: `POST /functions/v1/mesh-sentry-ingest`

**Auth**: HMAC SHA-256 signature in `Sentry-Hook-Signature` header validated against shared secret (in Vault). Reject 401 if invalid.

**Payload shape** (Sentry "issue.alert" webhook):
```json
{
  "action": "triggered",
  "data": {
    "event": {
      "event_id": "abc123...",
      "tags": [
        ["fp_v1", "Core|managed_exception|System.NullReferenceException|||MyService.Method"],
        ["component", "Core"],
        ["signal_kind", "managed_exception"],
        ["pharmacy_id", "<uuid>"],
        ["agent_version", "3.14.5"],
        ["ruleset_version", "v1.0"]
      ],
      "contexts": { ... }  // ALLOWLIST-validated keys only
    }
  }
}
```

**Schema-versioned validator** (Codex review v1 HIGH, chunk E): Edge Function endpoint is versioned `mesh-sentry-ingest-v1`. Payload schema is enforced by a strict JSON validator with **checked fixtures** committed alongside the function (`supabase/functions/mesh-sentry-ingest/fixtures/sentry-issue-alert-v1.json`). Unknown shapes are REJECTED with 400 + a dedicated `mesh.sentry_schema_drift_total` counter, NOT silently parsed. When Sentry changes payload shape, we deploy `mesh-sentry-ingest-v2` alongside v1; old agents/Sentry settings continue to work; we cut over by re-configuring Sentry's webhook URL.

**Logic**:
1. Verify HMAC. Fail-closed on mismatch (return 401, increment `mesh.context_schema_violations_total`).
2. Strict-validate the payload against the `mesh-sentry-ingest-v1` schema. On schema drift (unknown shape / missing required field) → 400 + `mesh.sentry_schema_drift_total++`. Don't silently parse partial.
3. Extract `fp_v1`, `component`, `signal_kind`, `pharmacy_id`, `agent_version`, `ruleset_version` from tags. Reject 400 if any required tag missing.
4. Validate `contexts` keys against forbidden-keys list (defense-in-depth — CHECK constraint in Component 1 also catches; ingest rejects EARLY). Reject 400 + `mesh.context_schema_violations_total++`.
5. Look up `fingerprint_registry.id` by fingerprint string (resolve `alias_of` chain with cycle-safe walk per Component 1's trigger). UPSERT registry row if first-seen.
6. INSERT `fingerprint_occurrences` (idempotent on `sentry_event_id` UNIQUE — second post is a no-op).
7. **Atomic action-decision via DB claim** (Codex review v1 CRITICAL, chunk F — race fix): Workflow-side "search GH, then create" is NOT atomic — two concurrent workflows can both observe no match and both create issues. Move the create/bump/reopen decision into the Edge Function under a DB transaction:

   ```sql
   -- All inside ONE transaction:
   INSERT INTO public.fingerprint_issue_links (fingerprint_id, github_repo)
     VALUES ($1, $2)
     ON CONFLICT (fingerprint_id) DO NOTHING
     RETURNING fingerprint_id;
   -- If the INSERT returned a row → THIS request claimed creation → action = 'create'
   -- Else fetch existing row and decide:
   SELECT state, github_issue_number FROM public.fingerprint_issue_links WHERE fingerprint_id = $1;
   -- state IN ('open', 'reopened', 'claimed') → action = 'bump'
   -- state = 'closed' AND last_action_at < NOW() - INTERVAL '24h' → action = 'reopen'
   ```

   Only the winning INSERTer gets `action = 'create'`; everyone else sees an existing row and bumps. No race window.

8. **Coalesced queue dispatch** (Codex review v1 HIGH, chunk D — rate-limit fix): instead of one `repository_dispatch` per occurrence, UPSERT a `fingerprint_issue_jobs` row keyed by `(fingerprint_id, window_start)`. A 5-minute window: `window_start = date_trunc('minute', NOW()) - (EXTRACT(minute FROM NOW())::int % 5) * INTERVAL '1 minute'`. Storms of 100s/min collapse to one pending job per fingerprint per 5-min window. A background sweep worker (`fingerprint_issue_dispatcher`, Supabase Cron, every 30s) drains pending jobs under the `repository_dispatch` 100/hr budget. Recovery + replay via the `last_issue_sync_at` / `last_issue_occurrence_id` cursor on `fingerprint_registry` (Component 1 added these columns).
9. Return 200 with `{ "occurrence_id": ..., "registry_id": ..., "claimed_action": "create|bump|reopen", "job_id": ... }`.

**Idempotency**: `sentry_event_id` UNIQUE constraint protects occurrence DB. `fingerprint_issue_links` UNIQUE on `fingerprint_id` protects issue claim. `fingerprint_issue_jobs` UNIQUE on `(fingerprint_id, window_start)` collapses storms — UPSERT bumps `coalesced_count` instead of duplicating rows.

**Failure modes**:
- HMAC mismatch → 401 (Sentry will retry; if persistent, alert via Vault rotation flag).
- Schema drift → 400 + `mesh.sentry_schema_drift_total++` (alerting threshold: >0 in any 5-min window pages oncall).
- Forbidden key in context → 400 + `mesh.context_schema_violations_total++` (CHECK constraint catches if validator regresses — defense-in-depth).
- DB unreachable → 503 (Sentry retries with backoff).
- GH dispatcher job FAILED N times → after `failure_count >= 5`, page oncall via separate alert; `fingerprint_issue_links.state` stays at last successful state so subsequent posts still get correct action decisions.

## GitHub Actions workflow: `.github/workflows/mesh-fingerprint-issue-manager.yml`

**Trigger**: `repository_dispatch` event_type `mesh-fingerprint` (one dispatch per coalesced job, NOT per occurrence).

**Key shift from prior draft**: action decision (`create`/`bump`/`reopen`) is already decided by the Edge Function under DB lock. The workflow EXECUTES the pre-claimed action; it does NOT re-decide. This eliminates the create-race entirely (Codex review v1 CRITICAL fix, chunk F).

**Job**:
1. Parse `client_payload` from dispatch: `job_id`, `fingerprint_id`, `action` (already decided), `fingerprint_string`, `signal_kind`, `component`, `coalesced_count`, `window_start`, `last_pharmacy_id`.
2. Execute the pre-claimed action via `gh`:
   - `action = 'create'`: `gh issue create` with title `[mesh] <signal_kind> in <component>` + body containing fingerprint string, last-5-occurrence summary, link to dashboard query. Labels: `mesh-fingerprint`, `bug`, `component:<component>`, `signal:<signal_kind>`. Then update `fingerprint_issue_links.github_issue_number` + `state='open'` via service_role.
   - `action = 'bump'`: `gh issue comment <issue_number>` (issue_number from `fingerprint_issue_links.github_issue_number`) with `coalesced_count` new occurrences in this window + last_pharmacy_id + timestamp range.
   - `action = 'reopen'`: `gh issue reopen <issue_number>` + `gh issue comment` with reopen reason + new occurrence summary. Then update `fingerprint_issue_links.state='reopened'`.
3. On any GH API rate-limit (403 with `X-RateLimit-Remaining: 0`): wait + retry once. If still failing, write `fingerprint_issue_jobs.last_error` + `failure_count++` and let the dispatcher re-queue on next sweep.
4. On success: UPDATE `fingerprint_issue_jobs.succeeded_at = NOW()` AND advance `fingerprint_registry.last_issue_sync_at + last_issue_occurrence_id` cursor (Codex review v1 MEDIUM, chunk D fix — idempotent replay).

**No fingerprint search step**: removed. The Edge Function already resolved which issue (if any) corresponds to this fingerprint via `fingerprint_issue_links.github_issue_number`. The workflow just looks up that number from the DB; no `gh issue list` search needed.

**Permissions**: workflow needs `issues: write` + `contents: read`. Repository secret `MESH_DISPATCH_TOKEN` is the token Edge Function uses to call `repository_dispatch` (fine-grained PAT with only `repository_dispatch` scope on this repo). Workflow itself uses `${{ secrets.GITHUB_TOKEN }}` for issue ops.

**Concurrency**: still set `concurrency: { group: mesh-fp-${{ github.event.client_payload.fingerprint_id }}, cancel-in-progress: false }` as defense-in-depth, even though the race is now closed at the DB layer.

## Dispatcher worker (Supabase Cron): `fingerprint_issue_dispatcher`

Runs every 30s. Drains `fingerprint_issue_jobs` queue under the `repository_dispatch` 100/hr cap:

```sql
-- Atomically claim up to N pending jobs (FIFO by enqueued_at):
WITH claimed AS (
  SELECT id FROM public.fingerprint_issue_jobs
   WHERE succeeded_at IS NULL
     AND failure_count < 5
   ORDER BY enqueued_at
   LIMIT 50  -- per 30s cycle = 6000/hr max (well under 100/hr per repo with retry buffer)
   FOR UPDATE SKIP LOCKED
)
UPDATE public.fingerprint_issue_jobs SET dispatched_at = NOW()
 WHERE id IN (SELECT id FROM claimed)
RETURNING id, fingerprint_id, action, coalesced_count, window_start;
```

Then POST each as a `repository_dispatch` to GH. The 100/hr GH cap forces an actual throttle: dispatcher caps at 90 dispatches/hr (10% headroom for retries). With coalescing, a fleet-wide storm of even 10k events/hr collapses to ~1 dispatch/fingerprint/window.

## Open questions for Codex re-review (FOCUSED chunks)

Chunk D (Edge Function): rate-limiting strategy if Sentry alert storm produces 100s of dispatches/min? GH `repository_dispatch` is rate-limited to 100/hr on the repo — could throttle. Should we batch dispatches (one workflow run handling N occurrences) instead of one-per?

Chunk E (Sentry payload schema drift): Sentry occasionally changes webhook payload shape. Should we version-tag the Edge Function (`mesh-sentry-ingest-v1`) and reject unknown payload-shape versions with 400 + alarm, rather than silently parsing-ignoring missing fields?

Chunk F (GH issue de-dupe race): two near-simultaneous occurrences for a new fingerprint could both trigger `create` workflows; second sees no existing issue, creates duplicate. Mitigation: workflow takes a "lock" (e.g., name-based concurrency group keyed on fingerprint string) so only one runs at a time per fingerprint.

## Sentry-side configuration (manual one-time)

After deploying Edge Function + workflow:
1. In Sentry project settings → Alerts → create alert rule "On any new issue OR existing issue with > N events in 1h"
2. Action: HTTP webhook → `<edge-function-url>` with HMAC signature
3. Filter: project = `suavoagent-prod` (don't fire on Suavo web app crashes — different routing)
4. Disable Sentry's built-in GH integration for `suavoagent-prod` project (we own the issue lifecycle now)

## Test coverage

- Unit: HMAC verification (Sentry vector + bad-signature negative)
- Unit: forbidden-keys rejection (every key on the list)
- Unit: alias_of resolution (deep chains, cycles must reject)
- Integration: Edge Function + fake DB + fake GH dispatch endpoint → assert correct sequence on create/bump/reopen
- E2E: deploy to staging, send synthetic Sentry webhook, verify DB row + GH issue created/bumped
- Chaos: Sentry retry storm → assert no duplicate occurrences (UNIQUE constraint) + no duplicate GH issues (concurrency group)
