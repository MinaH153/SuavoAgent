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
7. **Atomic action-decision via DB claim** (Codex review v1 CRITICAL, chunk F — race fix; round-2 HIGH adds `claim_expires_at` TTL): Workflow-side "search GH, then create" is NOT atomic — two concurrent workflows can both observe no match and both create issues. Move the create/bump/reopen decision into the Edge Function under a DB transaction:

   ```sql
   -- All inside ONE transaction:
   INSERT INTO public.fingerprint_issue_links (fingerprint_id, github_repo)
     VALUES ($1, $2)
     ON CONFLICT (fingerprint_id) DO NOTHING
     RETURNING fingerprint_id;
   -- If the INSERT returned a row → THIS request claimed creation → action = 'create'
   -- Else fetch existing row and decide:
   SELECT state, github_issue_number, claim_expires_at
     FROM public.fingerprint_issue_links
     WHERE fingerprint_id = $1
     FOR UPDATE;
   -- state = 'claimed' AND claim_expires_at < NOW() → previous create failed/stalled →
   --   bump claim_expires_at + retry action='create' (don't bump-a-NULL)
   -- state = 'claimed' AND claim_expires_at >= NOW() → another worker still creating → return 200 NoOp
   --   (the in-flight create will succeed and serve later occurrences)
   -- state IN ('open', 'reopened') → action = 'bump'
   -- state = 'closed' AND last_action_at < NOW() - INTERVAL '24h' → action = 'reopen'
   ```

   Round-2 HIGH (Comp 3 chunk F deadlock follow-up): without `claim_expires_at` a failed `create` workflow leaves the link in `state='claimed' AND github_issue_number IS NULL` forever; subsequent bumps target a non-existent issue. The TTL turns 'claimed' into a retryable state — the schema CHECK `(state = 'claimed' AND issue_number IS NULL) OR (other states AND issue_number IS NOT NULL)` plus the no-regression trigger guarantees only `create` (which writes the issue number on success) advances the row past 'claimed'.

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

**Round-2 HIGH (Comp 3): workflow MUST NOT trust the dispatched `client_payload.action`** — a compromised dispatch token could pass arbitrary actions and target any GH issue. The dispatch carries ONLY `job_id`. The workflow's first step is to fetch the authoritative job row from the DB and verify it; the workflow then operates on the DB-derived action + fingerprint_id + window_start + link row.

**Round-3 HIGH (Comp 3): `job_id`-only doesn't defeat a SPOOFED dispatch** — a compromised PAT could forge `repository_dispatch` with a valid existing `job_id` to re-trigger workflow runs. Add a per-dispatch HMAC so the workflow can verify the dispatch was issued by the legitimate dispatcher worker:

```jsonc
// client_payload shape (Edge Function / dispatcher writes this):
{
  "job_id": 12345,
  "attempt_token": "550e8400-e29b-41d4-a716-446655440000",
  "signed_at": "2026-06-15T18:24:00Z",
  "hmac": "<base64 HMAC-SHA256(job_id|attempt_token|signed_at, dispatch_signing_secret)>"
}
```

The workflow's first step (BEFORE the DB fetch) is HMAC verification against the shared secret (Vault-stored, rotated quarterly). If HMAC fails or `signed_at` is >5 min old, exit immediately. After HMAC passes, the DB fetch additionally verifies `attempt_token` matches the row's CURRENT `attempt_token` (defends against replays of an old dispatch after a re-claim).

**Job**:
1. Parse `client_payload`: `{ job_id, attempt_token, signed_at, hmac }`. Reject (exit 0, no-op) if any field is missing.
2. Verify HMAC against shared secret (Vault: `MESH_DISPATCH_SIGNING_SECRET`). Reject if HMAC mismatch OR `signed_at` is >5 min in past or >60s in future (clock skew tolerance).

   **Round-4 HIGH (Comp 3)**: HMAC-valid-but-stale-`signed_at` must record failure visibly — otherwise the dispatcher re-claims after lease expiry, signs a new payload, workflow rejects again, infinite loop with no DB-visible failure. On stale rejection:
   ```sql
   -- Token-checked failure record. Bumps failure_count; eventually
   -- crosses the failure_count >= 5 threshold and the dispatcher stops
   -- re-claiming (the job is operationally dead until oncall intervenes).
   SELECT public.record_fingerprint_issue_job_failure(
     $job_id, $supplied_attempt_token, 'stale_dispatch_signed_at_outside_window');
   ```
   Plus increment `mesh.dispatch_rejected_total{reason="stale_signed_at"}` Prometheus counter; page oncall if rate > 5/min for 5 min (signals dispatcher clock skew or sustained workflow backlog).
3. Fetch the authoritative job row via service_role:
   ```sql
   SELECT j.id, j.fingerprint_id, j.action, j.window_start, j.coalesced_count, j.last_occurrence_id,
          j.dispatched_at, j.succeeded_at,
          l.github_issue_number, l.github_repo, l.state AS link_state, l.claim_expires_at,
          r.component, r.signal_kind, r.fingerprint AS fingerprint_string
     FROM public.fingerprint_issue_jobs j
     JOIN public.fingerprint_registry  r ON r.id = j.fingerprint_id
     LEFT JOIN public.fingerprint_issue_links l ON l.fingerprint_id = j.fingerprint_id
    WHERE j.id = $1;
   ```
   Reject (no-op workflow run) if: row missing, `succeeded_at IS NOT NULL` (already done), `failure_count >= 5` (gave up), OR the row's `attempt_token` doesn't match the supplied token (replay of stale dispatch after re-claim).
3. Execute the action read FROM THE DB ROW (not the dispatch payload):
   - `action = 'create'`: `gh issue create` with title `[mesh] <signal_kind> in <component>` + body containing fingerprint string, last-5-occurrence summary (read via `last_occurrence_id`), link to dashboard query. Labels: `mesh-fingerprint`, `bug`, `component:<component>`, `signal:<signal_kind>`. On success: UPDATE `fingerprint_issue_links` SET `github_issue_number=<new>`, `state='open'`, `last_action_at=NOW()` for THIS `fingerprint_id`.
   - `action = 'bump'`: bump comment policy (Codex round-3 MEDIUM + round-4 MEDIUM digest cursor):
       - `coalesced_count = 1`: SKIP the comment but still advance the main cursor (`last_issue_occurrence_id`). The single occurrence is recorded in `fingerprint_occurrences`; it's visible on the dashboard. Adding "1 new occurrence in this 5-min window" to a GH issue is noise.
       - `coalesced_count >= 2`: post comment with count + timestamp range + last_pharmacy_id.
       - Daily digest cron `fingerprint_digest_cron` (runs once/24h per fingerprint with state IN ('open','reopened')): aggregates skipped singletons via the INDEPENDENT `last_digest_occurrence_id` cursor (Comp 1 added this column to fingerprint_registry — see round-4 MEDIUM). Query:
         ```sql
         SELECT fingerprint_id, COUNT(*) AS singleton_count, MAX(id) AS max_id,
                ARRAY_AGG(DISTINCT pharmacy_id ORDER BY pharmacy_id) AS pharmacies,
                ARRAY_AGG(DISTINCT agent_version) AS agent_versions
           FROM public.fingerprint_occurrences
          WHERE id > COALESCE(last_digest_occurrence_id, 0)
            -- and NOT already covered by a non-skipped bump/create/reopen
            ...
         ```
         Post a "N occurrences in last 24h — top pharmacies: X (12), Y (8), Z (3); agent_versions: 3.14.5, 3.14.6" digest comment if N > 0; advance `last_digest_occurrence_id` + `last_digest_at` AFTER the comment posts. State='claimed' fingerprints (stuck in claim TTL) are SKIPPED by the digest — the operator gets paged via the singleton-flood alarm instead.
   - `action = 'reopen'`: `gh issue reopen <github_issue_number>` + comment. UPDATE link `state='reopened'`.
4. On any GH API rate-limit (403 with `X-RateLimit-Remaining: 0`): wait + retry once. If still failing, write `fingerprint_issue_jobs.last_error` + `failure_count++` and let the dispatcher re-queue on next sweep (the lease expires, the row becomes claimable again).
5. On success: via the SECURITY DEFINER RPC (Round-4 HIGH (Comp 1+3): direct UPDATE bypasses attempt_token; the RPC enforces `WHERE id=$1 AND attempt_token=$2 AND succeeded_at IS NULL` in SQL):
   ```sql
   SELECT public.complete_fingerprint_issue_job($job_id, $supplied_attempt_token, $last_occurrence_id);
   -- Returns TRUE if THIS attempt's row was completed; FALSE means a re-claim
   -- happened (different attempt_token now) — the workflow MUST NOT advance
   -- the registry cursor; the live attempt will advance it.
   ```
   Only if the RPC returned TRUE, advance the registry cursor (GREATEST + cross-fp trigger enforce monotonicity at schema level):
   ```sql
   UPDATE public.fingerprint_registry
      SET last_issue_sync_at       = NOW(),
          last_issue_occurrence_id = GREATEST(COALESCE(last_issue_occurrence_id, 0), $last_occurrence_id)
    WHERE id = $fingerprint_id;
   ```

**No fingerprint search step**: removed. The Edge Function already resolved which issue (if any) corresponds to this fingerprint via `fingerprint_issue_links.github_issue_number`. The workflow just looks up that number from the DB; no `gh issue list` search needed.

**Permissions** (Codex round-2 MEDIUM correction + round-3 HIGH: prod = GH App MANDATORY):

GitHub's `repository_dispatch` API requires `Contents: write` permission on the target repo (NOT a separate `repository_dispatch` scope).

- **Production**: GitHub App installation token ONLY. App has `Contents: write` + `Issues: write` permissions on `MinaH153/SuavoAgent`. Tokens are short-lived (1h), auto-rotated by the App. PAT in production is NOT allowed.
- **Development / staging**: fine-grained PAT with `Contents: write` is acceptable. Stored in Vault as `MESH_DISPATCH_TOKEN`. Expires every ≤90 days. Alarm on token age >70d (warning); >85d pages oncall.

**Rotation runbook** (Codex round-3 HIGH — policy-only rotation is theater): `docs/runbooks/mesh-dispatch-token-rotation.md` (to be written before Phase 2 prod cutover) specifies: owner = on-call engineer that week; trigger = (a) scheduled quarterly cycle for HMAC signing secret, (b) annual cycle for GH App key, (c) immediate on suspected compromise; verification = post-rotation smoke that fires a synthetic dispatch + asserts workflow succeeds.

**Round-4 MEDIUM (Comp 3) — deploy preflight gate**: a CI check (`scripts/preflight-check-mesh-tokens.ts`) reads Vault token metadata at deploy time and fails dev/staging deploys when:
- PAT age > 90d (hard fail; deploy blocked until rotation)
- PAT age 70-90d (warning surfaced in PR / Slack)
- PAT age > 85d (additional pager alert, independent of deploy)

The deploy gate enforces what the alerts can only signal.

**Round-4 MEDIUM (Comp 3) — workflow secret scoping**: the workflow MUST use protected environment secrets (`environment: mesh-prod`), pin all GH Action invocations to commit SHAs (not tags), require code-owner review on `.github/workflows/mesh-*` and `supabase/functions/mesh-*` changes, and declare narrow job permissions:
```yaml
permissions:
  contents: read
  issues: write
  # NO id-token, NO packages, NO actions
```
This shrinks the blast radius if a workflow file is ever compromised.

Workflow itself uses `${{ secrets.GITHUB_TOKEN }}` for issue ops (auto-scoped to the workflow run, no rotation needed).

**Concurrency**: still set `concurrency: { group: mesh-fp-<DB-derived-fingerprint_id>, cancel-in-progress: false }` as defense-in-depth, keyed on the DB-derived fingerprint_id (NOT the client_payload — same spoofing concern). The DB-level claim is the primary guard.

## Dispatcher worker (Supabase Cron): `fingerprint_issue_dispatcher`

Runs every 30s. Drains `fingerprint_issue_jobs` queue under the `repository_dispatch` 100/hr cap:

```sql
-- Round-2 CRITICAL (Comp 1+3): claim ONLY rows whose lease is NULL/expired;
-- write a fresh lease so the next 30s tick can't re-dispatch an in-flight job
-- whose workflow hasn't yet written succeeded_at.
-- Round-3 HIGH (Comp 1): rotate attempt_token + bump dispatch_attempt_count
-- on each claim. Round-4 HIGH (Comp 3): RETURN attempt_token so dispatcher
-- can sign the full HMAC payload (not just job_id).
WITH claimed AS (
  SELECT id, action FROM public.fingerprint_issue_jobs
   WHERE succeeded_at IS NULL
     AND failure_count < 5
     AND (dispatch_lease_until IS NULL OR dispatch_lease_until < NOW())
   ORDER BY window_start, enqueued_at, id   -- logical-window order (round-2 MEDIUM)
   LIMIT 50
   FOR UPDATE SKIP LOCKED
)
UPDATE public.fingerprint_issue_jobs j
   SET dispatch_lease_ttl     = CASE c.action
                                  WHEN 'create' THEN INTERVAL '30 minutes'
                                  WHEN 'reopen' THEN INTERVAL '10 minutes'
                                  ELSE              INTERVAL '5 minutes'
                                END,
       dispatch_lease_until   = NOW() + (CASE c.action
                                           WHEN 'create' THEN INTERVAL '30 minutes'
                                           WHEN 'reopen' THEN INTERVAL '10 minutes'
                                           ELSE              INTERVAL '5 minutes'
                                         END),
       attempt_token          = gen_random_uuid(),
       first_dispatched_at    = COALESCE(j.first_dispatched_at, NOW()),
       last_dispatched_at     = NOW(),
       dispatch_attempt_count = j.dispatch_attempt_count + 1
  FROM claimed c
 WHERE j.id = c.id
RETURNING j.id, j.attempt_token;  -- both go into the signed payload
```

For each `(id, attempt_token)` pair the dispatcher then POSTs a `repository_dispatch` with the full signed payload:

```jsonc
{
  "event_type": "mesh-fingerprint",
  "client_payload": {
    "job_id": <id>,
    "attempt_token": "<uuid>",
    "signed_at": "<ISO 8601 UTC>",
    "hmac": "<base64 HMAC-SHA256(job_id|attempt_token|signed_at, MESH_DISPATCH_SIGNING_SECRET)>"
  }
}
```

(Round-4 HIGH (Comp 3): the round-3 prose declared this payload but the actual SQL still emitted `RETURNING id`. Now wired.)

The 100/hr GH cap forces an actual throttle: dispatcher caps at 90 dispatches/hr (10% headroom for retries). With coalescing, a fleet-wide storm of even 10k events/hr collapses to ~1 dispatch/fingerprint/window.

## Replay sweep (Codex round-2 MEDIUM, Comp 3 chunk D + round-3 HIGH state machine)

A separate Supabase Cron `fingerprint_replay_sweep` runs every 5 min and finds occurrences that landed but never got into a job (Edge Function crashed between INSERT occurrence + UPSERT job, etc.). Bounded by the registry cursor for forward-only walk.

**Round-3 HIGH (Comp 3 chunk 4)**: hardcoded `action='bump'` produces INVALID jobs for fingerprints that have no link row yet — the workflow would try to bump-a-NULL issue (CHECK rejects). The sweep MUST derive the correct action using the same state machine as ingest, joining `fingerprint_issue_links`:

```sql
WITH gaps AS (
  SELECT o.fingerprint_id,
         MAX(o.id)            AS max_occ_id,
         COUNT(*)              AS gap_count,
         l.state               AS link_state,
         l.claim_expires_at,
         l.last_action_at
    FROM public.fingerprint_occurrences o
    JOIN public.fingerprint_registry   r ON r.id = o.fingerprint_id
    LEFT JOIN public.fingerprint_issue_links l ON l.fingerprint_id = o.fingerprint_id
   WHERE o.id > COALESCE(r.last_issue_occurrence_id, 0)
     AND NOT EXISTS (
       SELECT 1 FROM public.fingerprint_issue_jobs j
        WHERE j.fingerprint_id = o.fingerprint_id
          AND j.last_occurrence_id >= o.id
     )
   GROUP BY o.fingerprint_id, l.state, l.claim_expires_at, l.last_action_at
),
classified AS (
  SELECT g.fingerprint_id, g.max_occ_id, g.gap_count,
         CASE
           -- No link row → first-time issue → create
           WHEN g.link_state IS NULL                                   THEN 'create'
           -- claimed + expired → previous create stalled → retry create
           WHEN g.link_state = 'claimed' AND g.claim_expires_at < NOW() THEN 'create'
           -- claimed + active → another worker mid-flight → skip (NULL classified → filtered out below)
           WHEN g.link_state = 'claimed'                               THEN NULL
           WHEN g.link_state IN ('open', 'reopened')                   THEN 'bump'
           -- Round-4 MEDIUM (Comp 3): ANY post-close occurrence is a signal
           -- that the bug isn't fixed; reopen + comment is the right
           -- treatment. (Prior version bumped on "recent" closures, which
           -- comments on a closed issue and gets ignored.) Throttle via
           -- the dispatcher's coalesce — repeated reopens within a window
           -- get one reopen + one digest.
           WHEN g.link_state = 'closed'                                THEN 'reopen'
           ELSE NULL
         END AS action
    FROM gaps g
)
INSERT INTO public.fingerprint_issue_jobs (fingerprint_id, action, window_start, coalesced_count, last_occurrence_id)
SELECT c.fingerprint_id,
       c.action,
       date_trunc('minute', NOW()) - (EXTRACT(minute FROM NOW())::int % 5) * INTERVAL '1 minute',
       c.gap_count,
       c.max_occ_id
  FROM classified c
 WHERE c.action IS NOT NULL  -- skip in-flight claimed rows
ON CONFLICT (fingerprint_id, window_start) DO UPDATE
   SET coalesced_count    = fingerprint_issue_jobs.coalesced_count + EXCLUDED.coalesced_count,
       last_occurrence_id = GREATEST(fingerprint_issue_jobs.last_occurrence_id, EXCLUDED.last_occurrence_id);
```

For action='create' coming from the sweep (rather than first-time ingest), the dispatcher worker also runs the `INSERT INTO fingerprint_issue_links ON CONFLICT DO NOTHING` claim BEFORE dispatching, so the create-race remains protected.

This is idempotent: re-running the sweep without new occurrences is a no-op (the cursor has been advanced after the prior job succeeded).

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
