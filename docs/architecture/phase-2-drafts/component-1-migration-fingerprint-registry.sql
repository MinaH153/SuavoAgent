-- ============================================================================
-- Phase 2 DRAFT — fingerprint_registry + fingerprint_occurrences (cloud-side)
-- ============================================================================
-- Source: docs/architecture/diagnostic-mesh-queen-first.md §6
-- Status: DRAFT — NOT shipped to prod. Awaits 7-day Queen burn-in (T+7d =
--   2026-05-20) + /plan-eng-review + Codex review.
-- When shipped: copy to ~/Code/Suavo/supabase/migrations/ with a timestamp
--   that slots past prod's last-applied (use the date of the deploy day +
--   buffer to survive concurrent merges). E.g. 20260601000000 or later.
-- ============================================================================

BEGIN;

-- ── fingerprint_registry: one row per unique fp_v1 fingerprint ──────────────
-- Agents emit (component, signal_kind, exception_type, primary_failure_site,
-- semantic_invariant_id) → SHA-256 → fp_v1. The cloud computes the SAME hash
-- on ingest to dedupe; the agent's tag is for the OTA ruleset feedback loop.
--
-- `alias_of` lets us merge fingerprints across ruleset versions without
-- agent changes: the new fp_v1 inserts as a new row pointing back at the
-- old row's id, and dashboards roll up via recursive CTE on alias_of.

CREATE TABLE IF NOT EXISTS public.fingerprint_registry (
  id                       BIGSERIAL    PRIMARY KEY,
  fingerprint              TEXT         NOT NULL,
  ruleset_version          TEXT         NOT NULL,
  component                TEXT         NOT NULL,
  signal_kind              TEXT         NOT NULL,
  exception_type           TEXT,
  stable_error_code        TEXT,
  primary_failure_site     TEXT,
  semantic_invariant_id    TEXT,
  first_seen_at            TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
  last_seen_at             TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
  occurrence_count         BIGINT       NOT NULL DEFAULT 0,
  alias_of                 BIGINT       REFERENCES public.fingerprint_registry(id),
  resolved_at              TIMESTAMPTZ,
  resolved_by_pr           TEXT,
  notes                    TEXT
);

-- Uniqueness on fingerprint string, but allow re-aliasing across ruleset
-- versions. fingerprint is enough — ruleset_version is metadata for audit,
-- not part of identity (the hash includes ruleset shape implicitly).
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'fingerprint_registry_fingerprint_key'
      AND connamespace = 'public'::regnamespace
      AND conrelid     = 'public.fingerprint_registry'::regclass
  ) THEN
    ALTER TABLE public.fingerprint_registry
      ADD CONSTRAINT fingerprint_registry_fingerprint_key
      UNIQUE (fingerprint);
  END IF;

  -- alias_of cycle safety (Codex review v1 HIGH, Comp 1 chunk 3):
  -- The FK alone proves the target exists but doesn't prevent A→B→A cycles.
  -- Direct CHECK blocks the trivial A→A case. Deeper cycles are caught by
  -- the constraint trigger below before write commits.
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'fingerprint_registry_alias_self_chk'
      AND connamespace = 'public'::regnamespace
      AND conrelid     = 'public.fingerprint_registry'::regclass
  ) THEN
    ALTER TABLE public.fingerprint_registry
      ADD CONSTRAINT fingerprint_registry_alias_self_chk
      CHECK (alias_of IS NULL OR alias_of <> id);
  END IF;
END $$;

-- Deeper cycle guard: BEFORE INSERT OR UPDATE trigger walks alias_of from
-- NEW.alias_of and rejects if NEW.id appears in the walk. Bounded by a
-- hop limit (32) to defend against pathological backfills.
CREATE OR REPLACE FUNCTION public.fingerprint_registry_no_alias_cycles()
  RETURNS trigger
  LANGUAGE plpgsql
  AS $$
DECLARE
  cur BIGINT := NEW.alias_of;
  hops INT := 0;
BEGIN
  WHILE cur IS NOT NULL AND hops < 32 LOOP
    IF cur = NEW.id THEN
      RAISE EXCEPTION 'fingerprint_registry: alias_of cycle would form via id=%', NEW.id
        USING ERRCODE = 'check_violation';
    END IF;
    -- Round-2 MEDIUM (Comp 1): without FOR UPDATE the walk can miss a cycle
    -- forming concurrently — two service transactions can set A→B and B→A
    -- at the same time, each reading the other's pre-image. Row-locking
    -- the walk serializes the conflict.
    SELECT alias_of INTO cur FROM public.fingerprint_registry WHERE id = cur FOR UPDATE;
    hops := hops + 1;
  END LOOP;
  IF hops >= 32 THEN
    RAISE EXCEPTION 'fingerprint_registry: alias_of walk exceeded 32 hops from id=%; refusing to commit', NEW.id
      USING ERRCODE = 'check_violation';
  END IF;
  RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS fingerprint_registry_no_alias_cycles_trg
  ON public.fingerprint_registry;
CREATE TRIGGER fingerprint_registry_no_alias_cycles_trg
  BEFORE INSERT OR UPDATE OF alias_of ON public.fingerprint_registry
  FOR EACH ROW WHEN (NEW.alias_of IS NOT NULL)
  EXECUTE FUNCTION public.fingerprint_registry_no_alias_cycles();

-- Spec §6 sketched a CHECK constraint enumerating components + signal_kinds.
-- We use SOFT validation (TEXT + secondary check trigger or app-layer) so
-- new component/signal additions don't require a schema migration. The
-- Edge Function on ingest validates against a whitelist that lives in
-- code alongside ruleset-v1.

CREATE INDEX IF NOT EXISTS fingerprint_registry_last_seen_idx
  ON public.fingerprint_registry (last_seen_at DESC);
CREATE INDEX IF NOT EXISTS fingerprint_registry_component_signal_idx
  ON public.fingerprint_registry (component, signal_kind);
CREATE INDEX IF NOT EXISTS fingerprint_registry_unresolved_idx
  ON public.fingerprint_registry (last_seen_at DESC)
  WHERE resolved_at IS NULL;

-- ── fingerprint_occurrences: many rows per fingerprint, per pharmacy ────────
-- One row per Sentry event the agent emits. `context` JSONB is allowlisted
-- (§6 enforcement section): only scrubbed counters + agent metadata, NO
-- raw frames, stack traces, file paths, SQL text, or PHI.
--
-- The Edge Function ingesting this MUST validate context keys against the
-- forbidden-keys list and reject with 4xx + mesh.context_schema_violations_total
-- increment. This table is defense-in-depth via CHECK + trigger; app-layer
-- enforcement is primary.

CREATE TABLE IF NOT EXISTS public.fingerprint_occurrences (
  id                BIGSERIAL    PRIMARY KEY,
  fingerprint_id    BIGINT       NOT NULL REFERENCES public.fingerprint_registry(id) ON DELETE CASCADE,
  pharmacy_id       UUID         NOT NULL REFERENCES public.pharmacy_profiles(id) ON DELETE CASCADE,
  agent_version     TEXT         NOT NULL,
  occurred_at       TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
  context           JSONB        NOT NULL DEFAULT '{}'::jsonb,
  sentry_event_id   TEXT,
  ingested_at       TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);

-- Dedupe on sentry_event_id to make ingest idempotent under Sentry retries.
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'fingerprint_occurrences_sentry_event_id_key'
      AND connamespace = 'public'::regnamespace
      AND conrelid     = 'public.fingerprint_occurrences'::regclass
  ) THEN
    ALTER TABLE public.fingerprint_occurrences
      ADD CONSTRAINT fingerprint_occurrences_sentry_event_id_key
      UNIQUE (sentry_event_id);
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS fingerprint_occurrences_pharmacy_idx
  ON public.fingerprint_occurrences (pharmacy_id, occurred_at DESC);
CREATE INDEX IF NOT EXISTS fingerprint_occurrences_fingerprint_idx
  ON public.fingerprint_occurrences (fingerprint_id, occurred_at DESC);
CREATE INDEX IF NOT EXISTS fingerprint_occurrences_ingested_idx
  ON public.fingerprint_occurrences (ingested_at DESC);
-- Round-5 MEDIUM (Comp 1): digest cursor range scan needs (fingerprint_id, id).
-- Existing (fingerprint_id, occurred_at DESC) doesn't help when filtering by
-- id > last_digest_occurrence_id since id and occurred_at aren't collinear.
CREATE INDEX IF NOT EXISTS fingerprint_occurrences_fingerprint_id_cursor_idx
  ON public.fingerprint_occurrences (fingerprint_id, id);

-- ── Defense-in-depth: CHECK constraint on forbidden context keys ────────────
-- Spec §6 forbidden-keys list. App layer rejects first; this catches drift
-- if app validation regresses. Uses jsonb_typeof checks rather than ?| so we
-- can express it as a STABLE CHECK.

CREATE OR REPLACE FUNCTION public.fingerprint_context_no_forbidden_keys(ctx jsonb)
  RETURNS boolean
  LANGUAGE plpgsql
  IMMUTABLE
  AS $$
DECLARE
  forbidden TEXT[] := ARRAY[
    'raw_stack', 'stacktrace', 'frames', 'file_path', 'line', 'column',
    'mvid', 'metadata_token', 'locals', 'arguments',
    'sql_text_raw', 'uia_title_raw', 'request_body', 'response_body',
    'connection_string', 'auth_token'
  ];
BEGIN
  IF ctx IS NULL OR jsonb_typeof(ctx) <> 'object' THEN
    RETURN TRUE;
  END IF;
  -- ?| is the IMMUTABLE "any key exists" operator — single op vs FOREACH
  -- loop on the hot path (Codex review v1 MEDIUM, Comp 1 chunk 2).
  RETURN NOT (ctx ?| forbidden);
END $$;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'fingerprint_occurrences_context_no_phi_keys'
      AND connamespace = 'public'::regnamespace
      AND conrelid     = 'public.fingerprint_occurrences'::regclass
  ) THEN
    ALTER TABLE public.fingerprint_occurrences
      ADD CONSTRAINT fingerprint_occurrences_context_no_phi_keys
      CHECK (public.fingerprint_context_no_forbidden_keys(context));
  END IF;
END $$;

-- ── Roll-up trigger: keep fingerprint_registry.last_seen_at + count synced ──
-- Avoids dashboard query running aggregates against fingerprint_occurrences
-- on every read.

CREATE OR REPLACE FUNCTION public.fingerprint_registry_rollup_on_occurrence()
  RETURNS trigger
  LANGUAGE plpgsql
  AS $$
BEGIN
  UPDATE public.fingerprint_registry
     SET last_seen_at = GREATEST(last_seen_at, NEW.occurred_at),
         occurrence_count = occurrence_count + 1
   WHERE id = NEW.fingerprint_id;
  RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS fingerprint_registry_rollup_trigger
  ON public.fingerprint_occurrences;
CREATE TRIGGER fingerprint_registry_rollup_trigger
  AFTER INSERT ON public.fingerprint_occurrences
  FOR EACH ROW EXECUTE FUNCTION public.fingerprint_registry_rollup_on_occurrence();

-- ── RLS: pharmacies read own occurrences, owners read all, service writes ───

ALTER TABLE public.fingerprint_registry    ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.fingerprint_occurrences ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS fingerprint_registry_owner_read
  ON public.fingerprint_registry;
CREATE POLICY fingerprint_registry_owner_read
  ON public.fingerprint_registry
  FOR SELECT
  TO authenticated
  USING (public.is_owner());

DROP POLICY IF EXISTS fingerprint_registry_service_all
  ON public.fingerprint_registry;
CREATE POLICY fingerprint_registry_service_all
  ON public.fingerprint_registry
  FOR ALL
  TO service_role
  USING (true)
  WITH CHECK (true);

DROP POLICY IF EXISTS fingerprint_occurrences_owner_read
  ON public.fingerprint_occurrences;
CREATE POLICY fingerprint_occurrences_owner_read
  ON public.fingerprint_occurrences
  FOR SELECT
  TO authenticated
  USING (public.is_owner());

DROP POLICY IF EXISTS fingerprint_occurrences_pharmacy_read
  ON public.fingerprint_occurrences;
CREATE POLICY fingerprint_occurrences_pharmacy_read
  ON public.fingerprint_occurrences
  FOR SELECT
  TO authenticated
  USING (pharmacy_id IN (SELECT public.pharmacy_ids_for_user(auth.uid())));

DROP POLICY IF EXISTS fingerprint_occurrences_service_all
  ON public.fingerprint_occurrences;
CREATE POLICY fingerprint_occurrences_service_all
  ON public.fingerprint_occurrences
  FOR ALL
  TO service_role
  USING (true)
  WITH CHECK (true);

-- Grants — authenticated never writes; service_role inserts via Edge Function.
-- (Codex review v1 HIGH, Comp 1 chunk 1): RLS policies filter rows AFTER
-- privileges; the service_role policy alone doesn't grant table or sequence
-- privileges, so Edge Function inserts would fail without these explicit GRANTs.
REVOKE INSERT, UPDATE, DELETE ON public.fingerprint_registry    FROM authenticated, anon;
REVOKE INSERT, UPDATE, DELETE ON public.fingerprint_occurrences FROM authenticated, anon;
GRANT  SELECT                  ON public.fingerprint_registry    TO authenticated;
GRANT  SELECT                  ON public.fingerprint_occurrences TO authenticated;
GRANT  SELECT, INSERT, UPDATE, DELETE ON public.fingerprint_registry    TO service_role;
GRANT  SELECT, INSERT, UPDATE, DELETE ON public.fingerprint_occurrences TO service_role;
GRANT  USAGE, SELECT ON SEQUENCE public.fingerprint_registry_id_seq    TO service_role;
GRANT  USAGE, SELECT ON SEQUENCE public.fingerprint_occurrences_id_seq TO service_role;

COMMENT ON TABLE public.fingerprint_registry IS
  'Mesh Phase 2: one row per unique fp_v1 fingerprint. See docs/architecture/diagnostic-mesh-queen-first.md §6.';
COMMENT ON TABLE public.fingerprint_occurrences IS
  'Mesh Phase 2: per-pharmacy occurrence of a fingerprint. context JSONB is allowlist-only — see fingerprint_context_no_forbidden_keys() CHECK + Edge Function validation.';

-- ════════════════════════════════════════════════════════════════════════════
-- GH issue lifecycle: claims + jobs queue
-- ════════════════════════════════════════════════════════════════════════════
-- Codex review v1 CRITICAL/HIGH (Comp 3 chunks F + D): GH issue create-race +
-- repository_dispatch rate-limit (100/hr) cannot be solved by workflow-side
-- concurrency groups alone. The action decision (create / bump / reopen) MUST
-- be made in the cloud under a DB-level lock; the workflow only EXECUTES the
-- already-claimed action.

-- ── fingerprint_issue_links: 1:1 claim row per fingerprint → GH issue ───────
-- The Edge Function INSERTs ON CONFLICT DO NOTHING to claim creation of a new
-- GH issue. Only the transaction winner dispatches `create` to GH Actions; all
-- other concurrent Sentry posts read the (still-NULL or now-populated)
-- github_issue_number column and dispatch `bump` instead.

CREATE TABLE IF NOT EXISTS public.fingerprint_issue_links (
  fingerprint_id      BIGINT       PRIMARY KEY REFERENCES public.fingerprint_registry(id) ON DELETE CASCADE,
  github_issue_number INT,                  -- NULL until GH workflow reports back
  github_repo         TEXT         NOT NULL, -- 'MinaH153/SuavoAgent' etc.
  state               TEXT         NOT NULL DEFAULT 'claimed'
                                   CHECK (state IN ('claimed', 'open', 'closed', 'reopened')),
  claimed_at          TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
  -- Round-2 HIGH (Comp 3): without a TTL on 'claimed', a failed `create`
  -- leaves state='claimed' + github_issue_number=NULL forever and future
  -- posts bump-against-NULL. Edge Function treats expired claims as
  -- create-retryable (state stays 'claimed', it just re-attempts the GH
  -- create call). Default 15 minutes matches dispatcher lease.
  claim_expires_at    TIMESTAMPTZ  NOT NULL DEFAULT NOW() + INTERVAL '15 minutes',
  last_action_at      TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
  -- Round-7 HIGH #1 (Comp 3): reopen-budget enforcement state.
  -- try_enqueue_reopen_job() (defined below) atomically checks + updates
  -- these under FOR UPDATE on the link row.
  reopen_count_24h           INT          NOT NULL DEFAULT 0 CHECK (reopen_count_24h >= 0),
  reopen_count_window_at     TIMESTAMPTZ,        -- start of current 24h window
  do_not_reopen              BOOLEAN      NOT NULL DEFAULT FALSE,
  suppressed_until           TIMESTAMPTZ,        -- operator-set
  budget_exhausted_notified_at TIMESTAMPTZ,      -- round-7 HIGH #5: first-event-only alarm
  -- Round-7 HIGH #3 (Comp 3): fingerprint-level kill switch for create loops.
  -- After N consecutive failed creates in claimed state, stop enqueueing.
  create_failure_count       INT          NOT NULL DEFAULT 0 CHECK (create_failure_count >= 0),
  last_create_failed_at      TIMESTAMPTZ,
  create_suppressed_until    TIMESTAMPTZ,
  -- Round-8 MED (Comp 3): mirror kill switch for repeated bump failures
  -- on open/reopened issues. Same semantics: bump_failure_count++ on
  -- recover_fingerprint_issue_job(mode='abandon') for action='bump'; at
  -- >=3 set bump_suppressed_until = NOW() + 24h + page once.
  bump_failure_count         INT          NOT NULL DEFAULT 0 CHECK (bump_failure_count >= 0),
  last_bump_failed_at        TIMESTAMPTZ,
  bump_suppressed_until      TIMESTAMPTZ
);

-- Round-2 MEDIUM (Comp 1): state invariants — 'open'/'closed'/'reopened'
-- MUST have a github_issue_number; 'claimed' MUST NOT. Catches Edge Function
-- bugs that would write an inconsistent state row.
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'fingerprint_issue_links_state_invariant'
      AND connamespace = 'public'::regnamespace
      AND conrelid     = 'public.fingerprint_issue_links'::regclass
  ) THEN
    ALTER TABLE public.fingerprint_issue_links
      ADD CONSTRAINT fingerprint_issue_links_state_invariant
      CHECK (
        (state = 'claimed' AND github_issue_number IS NULL)
        OR (state IN ('open', 'closed', 'reopened') AND github_issue_number IS NOT NULL AND github_issue_number > 0)
      );
  END IF;
END $$;

-- Round-2 MEDIUM (Comp 1): forbid regressions back to 'claimed' once an issue
-- exists. Failed GH ops should advance state forward via the claim_expires_at
-- TTL, not by clearing the issue number.
--
-- Round-3 HIGH (Comp 1): ALSO forbid silent github_issue_number rewrites
-- (123 → 456). The CHECK constraint allows any non-NULL/positive number; a
-- stale workflow writing the wrong number would corrupt the link. Once set,
-- the number is permanent for the lifetime of the link row.
CREATE OR REPLACE FUNCTION public.fingerprint_issue_links_no_state_regression()
  RETURNS trigger
  LANGUAGE plpgsql
  AS $$
BEGIN
  IF OLD.state <> 'claimed' AND NEW.state = 'claimed' THEN
    RAISE EXCEPTION 'fingerprint_issue_links: cannot regress state from % back to claimed (fingerprint_id=%)',
      OLD.state, NEW.fingerprint_id
      USING ERRCODE = 'check_violation';
  END IF;
  -- Issue-number is write-once. Block 123 → 456 rewrites.
  IF OLD.github_issue_number IS NOT NULL
     AND NEW.github_issue_number IS DISTINCT FROM OLD.github_issue_number THEN
    RAISE EXCEPTION 'fingerprint_issue_links: github_issue_number is write-once (fingerprint_id=%, OLD=%, NEW=%)',
      NEW.fingerprint_id, OLD.github_issue_number, NEW.github_issue_number
      USING ERRCODE = 'check_violation';
  END IF;
  RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS fingerprint_issue_links_no_state_regression_trg
  ON public.fingerprint_issue_links;
CREATE TRIGGER fingerprint_issue_links_no_state_regression_trg
  BEFORE UPDATE ON public.fingerprint_issue_links
  FOR EACH ROW EXECUTE FUNCTION public.fingerprint_issue_links_no_state_regression();

-- Round-4 MEDIUM (Comp 1): INSERT must start at state='claimed' with no
-- issue_number. Without this, a bug or backfill can INSERT directly into
-- state='open' with an arbitrary github_issue_number; the write-once
-- trigger then preserves the bogus mapping forever. Audited backfills
-- must go through a SECURITY DEFINER admin RPC (not modeled here).
CREATE OR REPLACE FUNCTION public.fingerprint_issue_links_insert_starts_claimed()
  RETURNS trigger
  LANGUAGE plpgsql
  AS $$
BEGIN
  IF NEW.state <> 'claimed' OR NEW.github_issue_number IS NOT NULL THEN
    RAISE EXCEPTION 'fingerprint_issue_links: INSERT must start with state=''claimed'' and github_issue_number IS NULL (fingerprint_id=%)',
      NEW.fingerprint_id
      USING ERRCODE = 'check_violation';
  END IF;
  RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS fingerprint_issue_links_insert_starts_claimed_trg
  ON public.fingerprint_issue_links;
CREATE TRIGGER fingerprint_issue_links_insert_starts_claimed_trg
  BEFORE INSERT ON public.fingerprint_issue_links
  FOR EACH ROW EXECUTE FUNCTION public.fingerprint_issue_links_insert_starts_claimed();

CREATE INDEX IF NOT EXISTS fingerprint_issue_links_open_idx
  ON public.fingerprint_issue_links (last_action_at DESC)
  WHERE state IN ('claimed', 'open', 'reopened');
CREATE INDEX IF NOT EXISTS fingerprint_issue_links_expired_claim_idx
  ON public.fingerprint_issue_links (claim_expires_at)
  WHERE state = 'claimed';

ALTER TABLE public.fingerprint_issue_links ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS fingerprint_issue_links_owner_read
  ON public.fingerprint_issue_links;
CREATE POLICY fingerprint_issue_links_owner_read
  ON public.fingerprint_issue_links
  FOR SELECT
  TO authenticated
  USING (public.is_owner());

DROP POLICY IF EXISTS fingerprint_issue_links_service_all
  ON public.fingerprint_issue_links;
CREATE POLICY fingerprint_issue_links_service_all
  ON public.fingerprint_issue_links
  FOR ALL
  TO service_role
  USING (true)
  WITH CHECK (true);

REVOKE INSERT, UPDATE, DELETE ON public.fingerprint_issue_links FROM authenticated, anon;
GRANT  SELECT                  ON public.fingerprint_issue_links TO authenticated;
GRANT  SELECT, INSERT, UPDATE, DELETE ON public.fingerprint_issue_links TO service_role;

-- ── fingerprint_issue_jobs: durable, coalesced GH dispatch queue ────────────
-- One pending row per fingerprint per dispatch window. Edge Function UPSERTs
-- on (fingerprint_id, window_start) so an alert storm of 100s/min for one
-- fingerprint collapses to one job; the batch worker drains it under the
-- repository_dispatch 100/hr cap, persisting cursor via last_issue_sync_at /
-- last_issue_occurrence_id on the registry row (added below).

CREATE TABLE IF NOT EXISTS public.fingerprint_issue_jobs (
  id                    BIGSERIAL    PRIMARY KEY,
  fingerprint_id        BIGINT       NOT NULL REFERENCES public.fingerprint_registry(id) ON DELETE CASCADE,
  action                TEXT         NOT NULL CHECK (action IN ('create', 'bump', 'reopen')),
  window_start          TIMESTAMPTZ  NOT NULL,
  enqueued_at           TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
  -- Round-2 CRITICAL (Comp 1+3): `FOR UPDATE SKIP LOCKED` releases the row
  -- lock at claim-commit time; the cron's next 30s tick would re-claim the
  -- same row while the workflow is still mid-flight, causing duplicate
  -- repository_dispatch + duplicate GH issue ops. Lease defends against that:
  -- dispatcher claims only rows where lease is NULL/expired.
  --
  -- Round-3 HIGH (Comp 1): per-action lease TTL — `create` may run the full
  -- GH retry path (~10 min worst case under rate-limit retries); `bump` is
  -- fast. Hardcoded 15min for all actions risked dual side effects. Per-action
  -- TTL + an `attempt_token` (UUID) ties workflow completion to the specific
  -- attempt; a stale workflow that finishes after lease expiry tries to write
  -- succeeded_at with its token, but only the CURRENT attempt_token in the
  -- row is accepted (others rejected by the workflow's UPDATE ... WHERE
  -- attempt_token = $supplied_token).
  dispatch_lease_until  TIMESTAMPTZ,
  dispatch_lease_ttl    INTERVAL,                -- per-action TTL set on claim
  attempt_token         UUID,                    -- rotated each claim; workflow MUST present this to mark succeeded
  -- Round-3 MEDIUM (Comp 1): split single dispatched_at into first/last +
  -- attempt count so re-claims preserve audit trail. Without this, the
  -- "first dispatch" timestamp is lost on every re-claim and operators
  -- can't reason about end-to-end latency.
  first_dispatched_at   TIMESTAMPTZ,
  last_dispatched_at    TIMESTAMPTZ,
  dispatch_attempt_count INT         NOT NULL DEFAULT 0,
  succeeded_at          TIMESTAMPTZ,
  failure_count         INT          NOT NULL DEFAULT 0,
  last_error            TEXT,
  coalesced_count       INT          NOT NULL DEFAULT 1, -- # occurrences folded into this job
  -- Round-2 HIGH (Comp 1): cursor advancement needs to know the MAX occurrence
  -- id rolled into this job. Without it, the worker has no safe way to advance
  -- last_issue_occurrence_id (querying live max can skip un-dispatched rows;
  -- using a stale id causes replay). Each UPSERT into this queue must
  -- GREATEST() this with the latest occurrence id.
  last_occurrence_id    BIGINT       REFERENCES public.fingerprint_occurrences(id),
  -- Round-5 HIGH (Comp 3): digest cron needs to exclude occurrences ALREADY
  -- folded into a non-skipped bump/create/reopen. A bare last_occurrence_id
  -- comparison miscounts because older singletons before this job's range
  -- get falsely treated as covered. (first_occurrence_id, last_occurrence_id)
  -- specifies the actual occurrence range covered by THIS job; digest excludes
  -- only rows where occurrence_id BETWEEN first AND last.
  first_occurrence_id   BIGINT       REFERENCES public.fingerprint_occurrences(id),
  -- Round-7 HIGH #2 (Comp 3): abandoned rows are succeeded_at=NOW() to stop
  -- re-dispatch but MUST NOT count toward digest / replay-sweep "covered"
  -- exclusions. The replay sweep and digest cron both add
  -- `AND j.abandoned_at IS NULL` to their NOT EXISTS predicates.
  abandoned_at          TIMESTAMPTZ
);

-- Per-action TTL guidance (set by dispatcher on claim):
--   action='create' → 30 minutes (GH retry path + token refresh)
--   action='bump'   → 5 minutes  (single comment POST)
--   action='reopen' → 10 minutes (reopen + comment)
-- These are operational tuning knobs — bump or lower as observed latencies
-- inform real workflow runtime distributions.
--
-- Round-4 HIGH (Comp 1): bound dispatch_lease_ttl in SQL — 0/negative TTL
-- causes immediate re-dispatch (duplicate side effects); multi-hour/day TTL
-- stalls recovery. 1min-1hr bracket covers all reasonable worker times.

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'fingerprint_issue_jobs_lease_ttl_bounded'
      AND connamespace = 'public'::regnamespace
      AND conrelid     = 'public.fingerprint_issue_jobs'::regclass
  ) THEN
    ALTER TABLE public.fingerprint_issue_jobs
      ADD CONSTRAINT fingerprint_issue_jobs_lease_ttl_bounded
      CHECK (dispatch_lease_ttl IS NULL
             OR (dispatch_lease_ttl >= INTERVAL '1 minute'
                 AND dispatch_lease_ttl <= INTERVAL '1 hour'));
  END IF;

  -- Round-5 HIGH (Comp 1): bounding dispatch_lease_ttl alone is insufficient —
  -- the dispatcher could write a valid 30min TTL but a divergent multi-day
  -- dispatch_lease_until. Enforce that lease_until = last_dispatched_at +
  -- lease_ttl (with 5sec slack for clock skew across the UPDATE).
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'fingerprint_issue_jobs_lease_until_matches_ttl'
      AND connamespace = 'public'::regnamespace
      AND conrelid     = 'public.fingerprint_issue_jobs'::regclass
  ) THEN
    ALTER TABLE public.fingerprint_issue_jobs
      ADD CONSTRAINT fingerprint_issue_jobs_lease_until_matches_ttl
      CHECK (
        dispatch_lease_until IS NULL
        OR (
          dispatch_lease_ttl  IS NOT NULL
          AND last_dispatched_at IS NOT NULL
          AND abs(
                EXTRACT(EPOCH FROM ((dispatch_lease_until - last_dispatched_at) - dispatch_lease_ttl))
              ) <= 5
        )
      );
  END IF;

  -- Round-6 MEDIUM (Comp 1): first_occurrence_id and last_occurrence_id are
  -- paired — both NULL (claim-only row before first occurrence folded in)
  -- OR both NOT NULL with first <= last. Last-only is invalid; without this
  -- CHECK, a worker writing only last_occurrence_id creates a row that
  -- silently slips past the digest's BETWEEN range exclusion (BETWEEN
  -- requires both bounds non-null and produces UNKNOWN otherwise).
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'fingerprint_issue_jobs_occurrence_range_paired'
      AND connamespace = 'public'::regnamespace
      AND conrelid     = 'public.fingerprint_issue_jobs'::regclass
  ) THEN
    ALTER TABLE public.fingerprint_issue_jobs
      ADD CONSTRAINT fingerprint_issue_jobs_occurrence_range_paired
      CHECK (
        (first_occurrence_id IS NULL AND last_occurrence_id IS NULL)
        OR (
          first_occurrence_id IS NOT NULL
          AND last_occurrence_id IS NOT NULL
          AND first_occurrence_id <= last_occurrence_id
        )
      );
  END IF;
END $$;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'fingerprint_issue_jobs_unique_window'
      AND connamespace = 'public'::regnamespace
      AND conrelid     = 'public.fingerprint_issue_jobs'::regclass
  ) THEN
    ALTER TABLE public.fingerprint_issue_jobs
      ADD CONSTRAINT fingerprint_issue_jobs_unique_window
      UNIQUE (fingerprint_id, window_start);
  END IF;
END $$;

-- Round-2 MEDIUM (Comp 1): ORDER BY window_start, enqueued_at, id — Sentry
-- retries / delayed inserts can land in an older logical window after a
-- newer one opens; FIFO by enqueued_at alone makes comments + cursor
-- advancement non-chronological. window_start gives logical order, then
-- enqueued_at + id for stable tiebreak.
CREATE INDEX IF NOT EXISTS fingerprint_issue_jobs_pending_idx
  ON public.fingerprint_issue_jobs (window_start, enqueued_at, id)
  WHERE succeeded_at IS NULL;
-- Partial index for the dispatcher's lease-aware claim query.
-- Round-3 LOW (Comp 1): match the documented claim sort order (window_start,
-- enqueued_at, id) so the planner can use this index for the ORDER BY without
-- a separate sort step. Postgres partial indexes can serve queries with
-- LITERAL `failure_count < 5` predicates; do NOT parameterize that threshold
-- in the worker (else this partial becomes unusable).
CREATE INDEX IF NOT EXISTS fingerprint_issue_jobs_claimable_idx
  ON public.fingerprint_issue_jobs (window_start, enqueued_at, id)
  WHERE succeeded_at IS NULL
    AND failure_count < 5;

ALTER TABLE public.fingerprint_issue_jobs ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS fingerprint_issue_jobs_owner_read
  ON public.fingerprint_issue_jobs;
CREATE POLICY fingerprint_issue_jobs_owner_read
  ON public.fingerprint_issue_jobs
  FOR SELECT
  TO authenticated
  USING (public.is_owner());

DROP POLICY IF EXISTS fingerprint_issue_jobs_service_all
  ON public.fingerprint_issue_jobs;
CREATE POLICY fingerprint_issue_jobs_service_all
  ON public.fingerprint_issue_jobs
  FOR ALL
  TO service_role
  USING (true)
  WITH CHECK (true);

REVOKE INSERT, UPDATE, DELETE ON public.fingerprint_issue_jobs FROM authenticated, anon;
GRANT  SELECT                  ON public.fingerprint_issue_jobs TO authenticated;
-- Round-5 HIGH (Comp 1): direct UPDATE on `succeeded_at` and `failure_count`
-- is REVOKED from service_role — those columns are mutated ONLY through the
-- token-checked RPCs (complete_/record_fingerprint_issue_job_failure).
--
-- Round-6 MEDIUM (Comp 1): identity columns (fingerprint_id, action,
-- window_start, enqueued_at) are write-once at INSERT and MUST NOT be
-- UPDATE-able after. Previous GRANT list included them; removed.
--
-- Round-6 LOW (Comp 1): future ADD COLUMNs to fingerprint_issue_jobs will
-- NOT be UPDATE-able by service_role unless the new column is explicitly
-- added to this GRANT list. This is fail-closed by design — operators
-- adding columns must explicitly opt in. Document in migration runbooks.
GRANT  INSERT, DELETE ON public.fingerprint_issue_jobs TO service_role;
GRANT  UPDATE (dispatch_lease_until, dispatch_lease_ttl, attempt_token,
               first_dispatched_at, last_dispatched_at, dispatch_attempt_count,
               coalesced_count, last_occurrence_id, first_occurrence_id, last_error)
         ON public.fingerprint_issue_jobs TO service_role;
GRANT  USAGE, SELECT ON SEQUENCE public.fingerprint_issue_jobs_id_seq TO service_role;

-- Round-4 HIGH (Comp 1): completion auth via SECURITY DEFINER RPC.
-- Round-5 HIGH (Comp 1): RPC must ALSO update fingerprint_registry cursor
-- in the same transaction; otherwise a workflow crash between RPC success
-- and the separate registry UPDATE leaves the cursor stale (replay sweep
-- doesn't know to advance it). One transaction, two table updates, atomic.
CREATE OR REPLACE FUNCTION public.complete_fingerprint_issue_job(
  p_job_id             BIGINT,
  p_attempt_token      UUID,
  p_last_occurrence_id BIGINT
) RETURNS BOOLEAN
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public, pg_temp
AS $$
DECLARE
  rows_updated     INT;
  v_fingerprint_id BIGINT;
BEGIN
  -- Step 1: token-checked job completion. Captures fingerprint_id for step 2.
  UPDATE public.fingerprint_issue_jobs
     SET succeeded_at         = NOW(),
         dispatch_lease_until = NULL,
         last_occurrence_id   = GREATEST(COALESCE(last_occurrence_id, 0), p_last_occurrence_id)
   WHERE id             = p_job_id
     AND attempt_token  = p_attempt_token
     AND succeeded_at   IS NULL
   RETURNING fingerprint_id INTO v_fingerprint_id;

  GET DIAGNOSTICS rows_updated = ROW_COUNT;
  IF rows_updated <> 1 THEN
    RETURN FALSE;  -- stale attempt — caller MUST NOT advance any cursor.
  END IF;

  -- Step 2: atomically advance registry cursor in the SAME transaction.
  -- GREATEST + cursor monotonicity trigger together enforce no regression.
  UPDATE public.fingerprint_registry
     SET last_issue_sync_at       = NOW(),
         last_issue_occurrence_id = GREATEST(COALESCE(last_issue_occurrence_id, 0), p_last_occurrence_id)
   WHERE id = v_fingerprint_id;

  RETURN TRUE;
END $$;

COMMENT ON FUNCTION public.complete_fingerprint_issue_job IS
  'Round-4 HIGH (Comp 1): only completion path. Returns true iff THIS attempt_token matches the current row + succeeded_at was still NULL. Stale workers (whose attempt_token was rotated by a re-claim) get false and must not advance the registry cursor. Round-5 HIGH (Comp 1): updates fingerprint_registry cursor atomically in the same transaction — workflow crash window between RPC success and separate cursor write is closed.';

REVOKE EXECUTE ON FUNCTION public.complete_fingerprint_issue_job(BIGINT, UUID, BIGINT) FROM PUBLIC, authenticated, anon;
GRANT  EXECUTE ON FUNCTION public.complete_fingerprint_issue_job(BIGINT, UUID, BIGINT) TO service_role;

-- Round-5 HIGH (Comp 1): companion failure-record RPC referenced by Comp 3
-- for stale-dispatch + GH-API-failure recording. Token-checked same as
-- completion so stale attempts can't poison the row.
CREATE OR REPLACE FUNCTION public.record_fingerprint_issue_job_failure(
  p_job_id        BIGINT,
  p_attempt_token UUID,
  p_reason        TEXT
) RETURNS BOOLEAN
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public, pg_temp
AS $$
DECLARE
  rows_updated INT;
BEGIN
  UPDATE public.fingerprint_issue_jobs
     SET failure_count        = failure_count + 1,
         last_error           = p_reason,
         dispatch_lease_until = NULL          -- release lease so next sweep can re-claim
   WHERE id            = p_job_id
     AND attempt_token = p_attempt_token
     AND succeeded_at  IS NULL;
  GET DIAGNOSTICS rows_updated = ROW_COUNT;
  RETURN rows_updated = 1;
END $$;

COMMENT ON FUNCTION public.record_fingerprint_issue_job_failure IS
  'Round-5 HIGH (Comp 1+3): token-checked failure record. Workflow / Edge Function call this on GH API failure, HMAC mismatch with valid token, stale signed_at, etc. Bumps failure_count; at >=5 the dispatcher stops re-claiming.';

REVOKE EXECUTE ON FUNCTION public.record_fingerprint_issue_job_failure(BIGINT, UUID, TEXT) FROM PUBLIC, authenticated, anon;
GRANT  EXECUTE ON FUNCTION public.record_fingerprint_issue_job_failure(BIGINT, UUID, TEXT) TO service_role;

-- Round-7 HIGH #1 (Comp 3): try_enqueue_reopen_job — atomically apply
-- reopen budget + operator override. Returns 'reopen' if a reopen job
-- should be enqueued; 'noop' otherwise (budget exhausted / suppressed /
-- do_not_reopen). Callers (Edge Function ingest + replay sweep) use the
-- returned action; this function does NOT enqueue the job itself.
--
-- Round-8 MED (Comp 1): KNOWN ACCEPTABLE LEAK — if the caller returns
-- 'reopen' but then fails to INSERT INTO fingerprint_issue_jobs (e.g.,
-- transient DB error), the counter has been incremented but no job
-- enqueued. Worst-case impact: 1-slot-per-failure leak in the 3/24h
-- budget. At observed dispatcher failure rates (<0.1%), the leak is
-- bounded and self-resets on 24h window rotation. Atomic-with-INSERT
-- would require this RPC to take all job parameters, significantly
-- larger interface. Accepted as design trade-off; revisit if leak rate
-- becomes operationally visible via mesh.reopen_count_24h time-series.
--
-- Side effect: on 'reopen', atomically increments reopen_count_24h
-- (resetting the window if older than 24h) and updates last_action_at.
-- On first transition to budget-exhausted state per window, sets
-- budget_exhausted_notified_at so the alarm fires exactly once per
-- 24h window per fingerprint (round-7 HIGH #5).
CREATE OR REPLACE FUNCTION public.try_enqueue_reopen_job(
  p_fingerprint_id BIGINT
) RETURNS TEXT  -- 'reopen' | 'noop_suppressed' | 'noop_budget_exhausted_first' | 'noop_budget_exhausted_repeat' | 'noop_no_link'
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public, pg_temp
AS $$
DECLARE
  v_link  public.fingerprint_issue_links%ROWTYPE;
  v_now   TIMESTAMPTZ := NOW();
BEGIN
  SELECT * INTO v_link FROM public.fingerprint_issue_links
   WHERE fingerprint_id = p_fingerprint_id
   FOR UPDATE;
  IF NOT FOUND THEN
    -- No link row = first-time fingerprint; sweep should classify as 'create' not 'reopen'.
    RETURN 'noop_no_link';
  END IF;

  -- Operator override wins over budget.
  IF v_link.do_not_reopen
     OR (v_link.suppressed_until IS NOT NULL AND v_link.suppressed_until > v_now) THEN
    RETURN 'noop_suppressed';
  END IF;

  -- Reset 24h window if it's older than 24h or never set.
  IF v_link.reopen_count_window_at IS NULL
     OR v_link.reopen_count_window_at < v_now - INTERVAL '24 hours' THEN
    UPDATE public.fingerprint_issue_links
       SET reopen_count_24h           = 0,
           reopen_count_window_at     = v_now,
           budget_exhausted_notified_at = NULL  -- new window → re-enable alarm
     WHERE fingerprint_id = p_fingerprint_id;
    v_link.reopen_count_24h := 0;
    v_link.reopen_count_window_at := v_now;
    v_link.budget_exhausted_notified_at := NULL;
  END IF;

  IF v_link.reopen_count_24h >= 3 THEN
    -- Budget exhausted. Round-8 HIGH (Comp 3): distinguish first-time from
    -- repeat in the RETURN value atomically so callers can emit the alarm
    -- metric without racing a separate SELECT.
    IF v_link.budget_exhausted_notified_at IS NULL THEN
      UPDATE public.fingerprint_issue_links
         SET budget_exhausted_notified_at = v_now
       WHERE fingerprint_id = p_fingerprint_id;
      RETURN 'noop_budget_exhausted_first';
    END IF;
    RETURN 'noop_budget_exhausted_repeat';
  END IF;

  -- Approve reopen. Increment counter + bump last_action_at.
  UPDATE public.fingerprint_issue_links
     SET reopen_count_24h = reopen_count_24h + 1,
         last_action_at   = v_now
   WHERE fingerprint_id = p_fingerprint_id;
  RETURN 'reopen';
END $$;

REVOKE EXECUTE ON FUNCTION public.try_enqueue_reopen_job(BIGINT) FROM PUBLIC, authenticated, anon;
GRANT  EXECUTE ON FUNCTION public.try_enqueue_reopen_job(BIGINT) TO service_role;

COMMENT ON FUNCTION public.try_enqueue_reopen_job IS
  'Round-7 HIGH + Round-8 HIGH (Comp 3): atomic reopen-budget + operator-override check. Returns reopen/noop_suppressed/noop_budget_exhausted_first/noop_budget_exhausted_repeat/noop_no_link. Side-effects window reset + count increment + first-time-exhaustion notification stamp.';

-- Round-8 HIGH (Comp 3 chunk 3): natural-expiry reset for create/bump kill
-- switches. Without this, create_failure_count stays at 3 after suppressed_until
-- expires; the next create attempt fails → count goes to 4 → re-trips the
-- >=3 check → permanent suppression unless an operator manually resets.
-- This RPC is called by the classifier (or its caller) when it observes
-- a kill switch whose suppression has naturally expired: ON ENTRY the
-- counter is reset to 0 so the fingerprint gets a fresh attempt window.
CREATE OR REPLACE FUNCTION public.reset_expired_kill_switches(
  p_fingerprint_id BIGINT
) RETURNS VOID
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public, pg_temp
AS $$
BEGIN
  UPDATE public.fingerprint_issue_links
     SET create_failure_count = 0
   WHERE fingerprint_id = p_fingerprint_id
     AND create_suppressed_until IS NOT NULL
     AND create_suppressed_until < NOW();
  UPDATE public.fingerprint_issue_links
     SET bump_failure_count = 0
   WHERE fingerprint_id = p_fingerprint_id
     AND bump_suppressed_until IS NOT NULL
     AND bump_suppressed_until < NOW();
END $$;

REVOKE EXECUTE ON FUNCTION public.reset_expired_kill_switches(BIGINT) FROM PUBLIC, authenticated, anon;
GRANT  EXECUTE ON FUNCTION public.reset_expired_kill_switches(BIGINT) TO service_role;

-- ── Sweep cursor columns on fingerprint_registry (Comp 3 MEDIUM) ────────────
-- Persist per-fingerprint cursor so the recovery sweep advances atomically
-- after each GH lifecycle action succeeds (idempotent replay).

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.columns
    WHERE table_schema = 'public'
      AND table_name   = 'fingerprint_registry'
      AND column_name  = 'last_issue_sync_at'
  ) THEN
    ALTER TABLE public.fingerprint_registry
      ADD COLUMN last_issue_sync_at TIMESTAMPTZ;
  END IF;

  IF NOT EXISTS (
    SELECT 1 FROM information_schema.columns
    WHERE table_schema = 'public'
      AND table_name   = 'fingerprint_registry'
      AND column_name  = 'last_issue_occurrence_id'
  ) THEN
    ALTER TABLE public.fingerprint_registry
      ADD COLUMN last_issue_occurrence_id BIGINT REFERENCES public.fingerprint_occurrences(id);
  END IF;

  -- Round-4 MEDIUM (Comp 3): independent digest cursor for the singleton
  -- daily-digest cron. last_issue_occurrence_id tracks the main
  -- create/bump/reopen cursor; last_digest_occurrence_id tracks which
  -- skipped singletons have been rolled into a digest comment. Without
  -- this separation, the digest either misses singletons or double-counts.
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.columns
    WHERE table_schema = 'public'
      AND table_name   = 'fingerprint_registry'
      AND column_name  = 'last_digest_occurrence_id'
  ) THEN
    ALTER TABLE public.fingerprint_registry
      ADD COLUMN last_digest_occurrence_id BIGINT REFERENCES public.fingerprint_occurrences(id);
  END IF;
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.columns
    WHERE table_schema = 'public'
      AND table_name   = 'fingerprint_registry'
      AND column_name  = 'last_digest_at'
  ) THEN
    ALTER TABLE public.fingerprint_registry
      ADD COLUMN last_digest_at TIMESTAMPTZ;
  END IF;
END $$;

-- Round-2 HIGH (Comp 1+3): cursor must NEVER regress + must NEVER point at an
-- occurrence belonging to a different fingerprint. A stale dispatcher can
-- otherwise write an old occurrence id and we lose forward progress, or
-- cross-write to the wrong fingerprint and corrupt replay logic.
CREATE OR REPLACE FUNCTION public.fingerprint_registry_cursor_monotonic()
  RETURNS trigger
  LANGUAGE plpgsql
  AS $$
DECLARE
  occ_fp BIGINT;
BEGIN
  -- Round-3 HIGH (Comp 1): early-return on NULL was a regression bypass —
  -- it allowed clearing an established cursor (a stale dispatcher writes
  -- NULL and loses forward progress). Reject NULL when OLD was non-NULL.
  --
  -- Round-4 HIGH (Comp 1): admin-repair path MUST NOT be DELETE+INSERT
  -- (cascades to occurrences, jobs, links — data loss). The repair path
  -- is an audited SECURITY DEFINER admin RPC (to be defined separately,
  -- not in this migration) that takes a session-var bypass key and
  -- updates last_issue_occurrence_id with explicit logging. Operators
  -- never run raw UPDATE/DELETE on this table.
  IF NEW.last_issue_occurrence_id IS NULL THEN
    IF OLD.last_issue_occurrence_id IS NOT NULL THEN
      RAISE EXCEPTION 'fingerprint_registry: cursor cannot be cleared from non-NULL (id=%, OLD=%, NEW=NULL)',
        NEW.id, OLD.last_issue_occurrence_id
        USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
  END IF;
  IF OLD.last_issue_occurrence_id IS NOT NULL
     AND NEW.last_issue_occurrence_id < OLD.last_issue_occurrence_id THEN
    RAISE EXCEPTION 'fingerprint_registry: cursor regression rejected (id=%, % → %)',
      NEW.id, OLD.last_issue_occurrence_id, NEW.last_issue_occurrence_id
      USING ERRCODE = 'check_violation';
  END IF;
  -- Verify the occurrence actually belongs to THIS fingerprint
  SELECT fingerprint_id INTO occ_fp
    FROM public.fingerprint_occurrences
   WHERE id = NEW.last_issue_occurrence_id;
  IF occ_fp IS NULL THEN
    RAISE EXCEPTION 'fingerprint_registry: cursor points at non-existent occurrence id=%',
      NEW.last_issue_occurrence_id
      USING ERRCODE = 'check_violation';
  END IF;
  IF occ_fp <> NEW.id THEN
    RAISE EXCEPTION 'fingerprint_registry: cursor cross-fingerprint (registry.id=% pointing at occurrence belonging to fingerprint_id=%)',
      NEW.id, occ_fp
      USING ERRCODE = 'check_violation';
  END IF;
  RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS fingerprint_registry_cursor_monotonic_trg
  ON public.fingerprint_registry;
CREATE TRIGGER fingerprint_registry_cursor_monotonic_trg
  BEFORE UPDATE OF last_issue_occurrence_id ON public.fingerprint_registry
  FOR EACH ROW EXECUTE FUNCTION public.fingerprint_registry_cursor_monotonic();

COMMENT ON TABLE public.fingerprint_issue_links IS
  'Mesh Phase 2: 1:1 claim row per fingerprint → GH issue. Edge Function INSERT ON CONFLICT DO NOTHING resolves the create/bump/reopen action decision at the DB layer (not workflow-side). See docs/architecture/phase-2-drafts/component-3-* §F.';
COMMENT ON TABLE public.fingerprint_issue_jobs IS
  'Mesh Phase 2: coalesced GH dispatch queue. UPSERT on (fingerprint_id, window_start) collapses alert storms into one pending job; batch worker drains under the 100/hr repository_dispatch cap. See docs/architecture/phase-2-drafts/component-3-* §D. service_role UPDATE is column-restricted (succeeded_at / failure_count via token-checked RPCs only; identity columns write-once at INSERT); future ADD COLUMNs are silently un-GRANTed and require explicit GRANT UPDATE opt-in (round-7 LOW).';

COMMIT;
