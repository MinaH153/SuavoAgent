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

**Logic**:
1. Verify HMAC. Fail-closed on mismatch (return 401, log to `mesh.context_schema_violations_total`).
2. Extract `fp_v1`, `component`, `signal_kind`, `pharmacy_id`, `agent_version`, `ruleset_version` from tags. Reject 400 if any required tag missing.
3. Validate `contexts` keys against forbidden-keys list (defense-in-depth — CHECK constraint in Component 1 also catches but ingest should reject early). Reject 400 + increment counter on violation.
4. Look up `fingerprint_registry.id` by fingerprint string (resolve `alias_of` chain). UPSERT registry row if first-seen.
5. INSERT `fingerprint_occurrences` (idempotent on `sentry_event_id` UNIQUE constraint — second post is a no-op).
6. Decide GH issue action:
   - If `fingerprint_registry.first_seen_at` is in the last 60s → **create**
   - Else if registry row is `resolved_at IS NOT NULL` and last occurrence > 24h ago → **reopen**
   - Else → **bump** (add comment with occurrence count)
7. Dispatch GH Actions via `repository_dispatch` POST with payload: `{ "event_type": "mesh-fingerprint", "client_payload": { "fingerprint_id": ..., "action": "create|bump|reopen", ... } }`. Use short-lived token from Vault.
8. Return 200 with `{ "occurrence_id": ..., "registry_id": ..., "dispatched_action": "create|bump|reopen" }`.

**Idempotency**: `sentry_event_id` UNIQUE constraint protects DB. GH dispatch may double-fire on Sentry retry; the GH workflow itself idempotently checks for existing issue before creating (de-dupe by fingerprint tag in title or body).

**Failure modes**:
- HMAC mismatch → 401 (Sentry will retry; if persistent, alert via Vault rotation flag)
- Forbidden key in context → 400 + `mesh.context_schema_violations_total++` (Sentry-side alert if Sentry dashboard exposes 4xx rate)
- DB unreachable → 503 (Sentry retries with backoff)
- GH dispatch fails → 200 anyway with `dispatched_action: "failed"` field; occurrence still recorded; GH Actions catches up via a periodic sweep (cron) that scans recent occurrences without corresponding issue activity

## GitHub Actions workflow: `.github/workflows/mesh-fingerprint-issue-manager.yml`

**Trigger**: `repository_dispatch` event_type `mesh-fingerprint`.

**Job**:
1. Parse `client_payload`: `fingerprint_id`, `action`, `fingerprint_string`, `signal_kind`, `component`, `occurrence_count`, `last_pharmacy_id`.
2. Search existing issues with label `mesh-fingerprint` + body containing `Fingerprint: <fingerprint_string>`. (Use `gh issue list --label mesh-fingerprint --search "<fp>"`.)
3. Branch on action + match result:
   - `create` (no existing): `gh issue create` with title `[mesh] <signal_kind> in <component>` + body containing fingerprint, last 5 occurrence summary, link to dashboard query. Labels: `mesh-fingerprint`, `bug`, `component:<component>`, `signal:<signal_kind>`.
   - `bump` (existing OPEN): `gh issue comment` with new occurrence count + last_pharmacy_id + timestamp.
   - `reopen` (existing CLOSED): `gh issue reopen` + `gh issue comment` with reopen reason + new occurrence summary.
   - `failed` action from Edge Function: emit workflow warning + no-op (covered by periodic sweep).
4. On any GH API rate-limit (403 with `X-RateLimit-Remaining: 0`): wait + retry once; emit workflow warning if still failing.

**Permissions**: workflow needs `issues: write` + `contents: read`. Repository secret `MESH_DISPATCH_TOKEN` is the token Edge Function uses to call `repository_dispatch` (fine-grained PAT with only `repository_dispatch` scope on this repo).

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
