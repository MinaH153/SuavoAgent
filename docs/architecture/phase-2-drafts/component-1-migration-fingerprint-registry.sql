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
  last_action_at      TIMESTAMPTZ  NOT NULL DEFAULT NOW()
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
  RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS fingerprint_issue_links_no_state_regression_trg
  ON public.fingerprint_issue_links;
CREATE TRIGGER fingerprint_issue_links_no_state_regression_trg
  BEFORE UPDATE OF state ON public.fingerprint_issue_links
  FOR EACH ROW EXECUTE FUNCTION public.fingerprint_issue_links_no_state_regression();

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
  id                   BIGSERIAL    PRIMARY KEY,
  fingerprint_id       BIGINT       NOT NULL REFERENCES public.fingerprint_registry(id) ON DELETE CASCADE,
  action               TEXT         NOT NULL CHECK (action IN ('create', 'bump', 'reopen')),
  window_start         TIMESTAMPTZ  NOT NULL,
  enqueued_at          TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
  -- Round-2 CRITICAL (Comp 1+3): `FOR UPDATE SKIP LOCKED` releases the row
  -- lock at claim-commit time; the cron's next 30s tick would re-claim the
  -- same row while the workflow is still mid-flight, causing duplicate
  -- repository_dispatch + duplicate GH issue ops. Lease defends against that:
  -- dispatcher claims only rows where lease is NULL/expired and writes a
  -- new lease 15min into the future.
  dispatch_lease_until TIMESTAMPTZ,
  dispatched_at        TIMESTAMPTZ,
  succeeded_at         TIMESTAMPTZ,
  failure_count        INT          NOT NULL DEFAULT 0,
  last_error           TEXT,
  coalesced_count      INT          NOT NULL DEFAULT 1, -- # occurrences folded into this job
  -- Round-2 HIGH (Comp 1): cursor advancement needs to know the MAX occurrence
  -- id rolled into this job. Without it, the worker has no safe way to advance
  -- last_issue_occurrence_id (querying live max can skip un-dispatched rows;
  -- using a stale id causes replay). Each UPSERT into this queue must
  -- GREATEST() this with the latest occurrence id.
  last_occurrence_id   BIGINT       REFERENCES public.fingerprint_occurrences(id)
);

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
CREATE INDEX IF NOT EXISTS fingerprint_issue_jobs_claimable_idx
  ON public.fingerprint_issue_jobs (window_start, enqueued_at)
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
GRANT  SELECT, INSERT, UPDATE, DELETE ON public.fingerprint_issue_jobs TO service_role;
GRANT  USAGE, SELECT ON SEQUENCE public.fingerprint_issue_jobs_id_seq TO service_role;

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
  -- Skip if cursor is being cleared or unchanged
  IF NEW.last_issue_occurrence_id IS NULL THEN
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
  'Mesh Phase 2: coalesced GH dispatch queue. UPSERT on (fingerprint_id, window_start) collapses alert storms into one pending job; batch worker drains under the 100/hr repository_dispatch cap. See docs/architecture/phase-2-drafts/component-3-* §D.';

COMMIT;
