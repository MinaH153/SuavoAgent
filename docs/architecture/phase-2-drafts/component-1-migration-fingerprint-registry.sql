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
  ) THEN
    ALTER TABLE public.fingerprint_registry
      ADD CONSTRAINT fingerprint_registry_fingerprint_key
      UNIQUE (fingerprint);
  END IF;
END $$;

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
  k TEXT;
BEGIN
  IF ctx IS NULL OR jsonb_typeof(ctx) <> 'object' THEN
    RETURN TRUE;
  END IF;
  FOREACH k IN ARRAY forbidden LOOP
    IF ctx ? k THEN
      RETURN FALSE;
    END IF;
  END LOOP;
  RETURN TRUE;
END $$;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'fingerprint_occurrences_context_no_phi_keys'
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
REVOKE INSERT, UPDATE, DELETE ON public.fingerprint_registry    FROM authenticated, anon;
REVOKE INSERT, UPDATE, DELETE ON public.fingerprint_occurrences FROM authenticated, anon;
GRANT  SELECT                  ON public.fingerprint_registry    TO authenticated;
GRANT  SELECT                  ON public.fingerprint_occurrences TO authenticated;

COMMENT ON TABLE public.fingerprint_registry IS
  'Mesh Phase 2: one row per unique fp_v1 fingerprint. See docs/architecture/diagnostic-mesh-queen-first.md §6.';
COMMENT ON TABLE public.fingerprint_occurrences IS
  'Mesh Phase 2: per-pharmacy occurrence of a fingerprint. context JSONB is allowlist-only — see fingerprint_context_no_forbidden_keys() CHECK + Edge Function validation.';

COMMIT;
