# SuavoAgent Self-Healing — Audit Schema (v0.1)

> The audit chain is a **legal artifact**, not a debug log. This schema is what
> makes every action attributable, tamper-evident, and defensible under HIPAA
> audit, OCR investigation, BAA dispute, or subpoena.

**Locked date:** 2026-04-22
**Status:** v0.1 draft
**Depends on:** `invariants.md` (read first)

---

## Design principles

1. **Append-only, never update, never delete.** Once an event is written, it is
   immutable. Corrections are emitted as new events that reference the corrected
   event. History is never rewritten.
2. **Per-pharmacy isolation.** Each pharmacy has its own independent hash chain.
   A compromise of pharmacy A's chain cannot forge pharmacy B's chain.
3. **Tamper-evident via SHA-256 hash linking.** Each event hashes the previous
   event's hash. Modifying history breaks the chain; verification sweeps catch it.
4. **Offsite immutable backup via S3 Object Lock compliance mode.** Daily
   digests to an S3 bucket that even Suavo engineers cannot delete without
   dual-key operator + Security Officer approval.
5. **Externally verifiable.** Pharmacies can download their chain and verify it
   against an open-source verifier. Trust doesn't depend on us being honest;
   math does.
6. **6-year retention minimum**, 7-year actual policy (one-year buffer).

---

## Event shape

Every audit event is a row in the `audit_events` Postgres table with this
shape:

```typescript
interface AuditEvent {
  // Identification
  id: string;                     // UUID v7 (time-sortable)
  pharmacy_id: string;            // salted hash, never raw
  sequence: number;               // monotonic per pharmacy, no gaps

  // Chain linking
  prev_hash: string;              // SHA-256 of previous event in this pharmacy's chain
  hash: string;                   // SHA-256(canonical_json(this event without `hash` field) || prev_hash)

  // Content
  type: EventType;                // enum — see §Event Types
  category: EventCategory;        // enum — taxonomy axis for reporting
  severity: 'info' | 'warn' | 'error' | 'critical';

  // Attribution
  actor_type: 'operator' | 'agent' | 'cloud_dispatcher' | 'system';
  actor_id: string;               // operator UUID, agent key ID, dispatcher session
  mission_charter_version: string; // vSemver of the in-force charter at event time

  // Payload
  payload: Record<string, unknown>; // event-type-specific, PHI-redacted
  redaction_ruleset_version: string; // version of redaction rules applied

  // Correlation
  correlation_id?: string;        // groups related events (e.g., one verb invocation)
  parent_id?: string;             // for child-of relationships (e.g., rollback of)

  // Timestamps (UTC, ISO 8601)
  occurred_at: string;            // when the underlying event happened on agent
  recorded_at: string;            // when cloud ingested it
  ingest_latency_ms: number;      // recorded_at - occurred_at
}
```

**Canonical JSON serialization** (for hashing): RFC 8785 JSON Canonicalization
Scheme. Keys sorted lexicographically, no whitespace, no duplicate keys.

**Hash computation:**
```
hash = SHA-256(canonicalize({...event, hash: undefined}) || prev_hash)
```

### Genesis event
The first event in every pharmacy's chain is a synthetic genesis event:

```json
{
  "type": "chain.genesis",
  "prev_hash": "0000000000000000000000000000000000000000000000000000000000000000",
  "sequence": 0,
  "payload": {
    "pharmacy_salt_hash": "<per-pharmacy salt, hashed>",
    "chain_version": "1.0.0",
    "created_by": "<Suavo staff member at install time>"
  }
}
```

All subsequent events chain back to this genesis eventually.

---

## Event types

Event types use dotted-notation for hierarchical organization. The full
registry lives in `docs/self-healing/event-registry.md` (to be created in
Phase A).

### Infrastructure events
- `agent.started` / `agent.stopped` / `agent.crashed`
- `service.restarted` / `service.failed` / `service.healthy`
- `heartbeat.emitted` / `heartbeat.silent_alarm`
- `config.override_applied` / `config.rollback_executed`
- `attestation.verified` / `attestation.mismatch`

### Diagnostic events (Phase C+)
- `diagnosis.requested` / `diagnosis.synthesized` / `diagnosis.published`
- `scout.dispatched` / `scout.returned` / `scout.timeout`
- `hypothesis.ranked` / `hypothesis.rejected_by_charter`

### Action events (Phase D+)
- `verb.proposed` / `verb.policy_evaluated` / `verb.approved` / `verb.rejected`
- `verb.signed` / `verb.dispatched` / `verb.executed` / `verb.verified`
- `verb.failed` / `verb.rolled_back`
- `grammar.version_mismatch` / `grammar.updated`

### Plan events (Phase E+)
- `plan.drafted` / `plan.reviewed` / `plan.approved` / `plan.rejected`
- `plan.step_executed` / `plan.step_failed` / `plan.compensated` / `plan.completed`

### Autonomy events (Phase F+)
- `autonomy.granted` / `autonomy.revoked` / `autonomy.threshold_reached`
- `retrospective.proposed_rule` / `retrospective.proposal_approved` / `retrospective.proposal_rejected`

### Consent events
- `consent.requested` / `consent.granted` / `consent.expired` / `consent.revoked`
- `baa.amendment_applied` / `baa.amendment_reverted`

### Security events
- `key.rotated` / `key.revoked` / `key.suspected_compromise`
- `kill_switch.triggered` / `kill_switch.cleared`
- `invariant.violated` / `invariant.violation_resolved`

### Federated learning events (Phase G+)
- `signature.emitted` / `signature.pattern_match` / `signature.pattern_novel`
- `fed_mesh.privacy_budget_consumed` / `fed_mesh.budget_exhausted`

---

## Event categories

Categories are orthogonal to types. Used for reporting and filtering.

- `install` — install, upgrade, uninstall
- `runtime` — ongoing operation
- `diagnosis` — L1 dispatch activity
- `remediation` — L2 verb activity
- `governance` — L3 plan + consent + autonomy
- `security` — kill switches, invariant violations
- `compliance` — BAA, HIPAA, auditor-facing
- `ops` — operator dashboard activity

---

## Postgres schema

```sql
CREATE TABLE audit_events (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  pharmacy_id TEXT NOT NULL,
  sequence BIGINT NOT NULL,
  prev_hash CHAR(64) NOT NULL,
  hash CHAR(64) NOT NULL,
  type TEXT NOT NULL,
  category TEXT NOT NULL,
  severity TEXT NOT NULL CHECK (severity IN ('info', 'warn', 'error', 'critical')),
  actor_type TEXT NOT NULL,
  actor_id TEXT NOT NULL,
  mission_charter_version TEXT NOT NULL,
  payload JSONB NOT NULL,
  redaction_ruleset_version TEXT NOT NULL,
  correlation_id UUID,
  parent_id UUID,
  occurred_at TIMESTAMPTZ NOT NULL,
  recorded_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  ingest_latency_ms INTEGER NOT NULL,

  CONSTRAINT no_update CHECK (false) NO INHERIT DEFERRABLE INITIALLY DEFERRED,
  UNIQUE (pharmacy_id, sequence),
  UNIQUE (pharmacy_id, hash)
);

-- Partitioned by pharmacy_id for RLS + performance
CREATE INDEX audit_events_pharmacy_time_idx
  ON audit_events (pharmacy_id, occurred_at DESC);

CREATE INDEX audit_events_correlation_idx
  ON audit_events (correlation_id) WHERE correlation_id IS NOT NULL;

-- RLS: pharmacies can only read their own events
ALTER TABLE audit_events ENABLE ROW LEVEL SECURITY;

CREATE POLICY "pharmacies read own events" ON audit_events
  FOR SELECT TO pharmacy_role
  USING (pharmacy_id = current_setting('request.jwt.claims.pharmacy_id_hash', true));

-- Trigger: reject any UPDATE or DELETE
CREATE OR REPLACE FUNCTION reject_audit_mutation()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION 'audit_events is append-only. Mutation rejected.';
END;
$$;

CREATE TRIGGER audit_events_no_update BEFORE UPDATE ON audit_events
  FOR EACH ROW EXECUTE FUNCTION reject_audit_mutation();
CREATE TRIGGER audit_events_no_delete BEFORE DELETE ON audit_events
  FOR EACH ROW EXECUTE FUNCTION reject_audit_mutation();
```

**Notes on the schema:**
- `CHECK (false)` on update is a paranoid secondary defense; the trigger is the
  primary defense.
- `pharmacy_id` is stored as the salted hash, not the UUID. The salt lookup
  table is separately protected.
- `JSONB` for payload so we can index specific payload fields per event type
  as needed (e.g., `payload->>'verb_name'`).
- RLS ensures pharmacies can read their own chain via API, never others'.

---

## Sequence number handling

Each pharmacy's `sequence` is strictly monotonic with no gaps. Implementation:

- Sequence is assigned at insert time via a per-pharmacy advisory lock:
  `SELECT pg_advisory_xact_lock(hashtext(pharmacy_id))`
- Next sequence = `(SELECT MAX(sequence) FROM audit_events WHERE pharmacy_id = $1) + 1`
- Insert row
- Release lock at transaction commit

**Rationale:** gaps in sequence are tamper evidence. If an auditor sees
1, 2, 3, 5, 6 with 4 missing, that's a red flag. With the advisory lock, gaps
cannot happen by concurrency.

**Worst case:** insert latency under contention scales with pharmacy event
volume. At 10K events/pharmacy/day, that's ~8 events/minute average. Advisory
lock overhead is negligible.

---

## Hash chain verification

### Verification algorithm (pseudocode)

```python
def verify_chain(events_sorted_by_sequence):
    prev_hash = GENESIS_HASH
    for event in events_sorted_by_sequence:
        expected_hash = sha256(
            canonicalize(event_without_hash_field(event)) + prev_hash
        )
        if event.hash != expected_hash:
            raise TamperDetected(f"At sequence {event.sequence}")
        prev_hash = event.hash
    return "VERIFIED"
```

### Nightly verification sweep

A cron job runs at 03:00 UTC per pharmacy:
1. Fetch all events since last sweep
2. Verify chain starting from last-verified-hash
3. Update `chain_verification` table with `(pharmacy_id, last_verified_sequence, last_verified_at, status)`
4. Any tamper detection fires an `invariant.violated` event + immediate alarm

### External verifier

Open-source tool `github.com/MinaH153/SuavoAgent-AuditVerifier` (to be created
Phase A) accepts:
- Pharmacy's own exported chain (HMAC-authenticated JSON file)
- Pharmacy's salt (stored locally by pharmacy)
- Optional: independently-fetched S3 Object Lock digest for cross-check

Outputs: `VERIFIED` or `TAMPERED AT sequence N`.

---

## Offsite digest to S3 Object Lock

### Daily digest process

Every day at 04:00 UTC per pharmacy:
1. Compute `digest = SHA-256(concatenation of all event hashes for the day, ordered by sequence)`
2. Create manifest:
   ```json
   {
     "pharmacy_id": "<salted>",
     "date": "2026-04-22",
     "first_sequence": 12345,
     "last_sequence": 12678,
     "event_count": 334,
     "digest": "<sha256>",
     "chain_head_hash": "<last event's hash>",
     "previous_day_digest": "<previous day's digest for chaining digests>"
   }
   ```
3. Sign manifest with MKM's daily-digest key
4. Upload to `s3://suavo-audit-digests/<pharmacy_id>/<date>.json` with
   Object Lock compliance mode, retention = 7 years
5. Record `digest.uploaded` event in the chain

### Why S3 Object Lock (compliance mode)

- Can't be deleted by anyone (even root account) during retention period
- WORM-protected, FINRA/SEC/HIPAA-aligned
- Cheap ($0.023/GB/month for digests — digests are tiny)
- No vendor lock (primitive service, AWS will support forever)

### AWS QLDB is NOT our choice

AWS announced QLDB end-of-life in 2024. Ruled out.

### Cross-region replication

S3 bucket replicates to a secondary region (us-east-2 → us-west-2) with CRR.
Disaster recovery; both regions have Object Lock.

---

## Query patterns

### Pharmacy downloads own chain
```sql
SELECT * FROM audit_events
WHERE pharmacy_id = $1
ORDER BY sequence ASC
```
(RLS ensures they can only see their own chain.)

### Operator investigating an incident
```sql
SELECT * FROM audit_events
WHERE pharmacy_id = $1
  AND (correlation_id = $2 OR parent_id = $2)
ORDER BY occurred_at ASC
```

### Fleet-wide report (sanitized)
Cross-tenant queries MUST NOT return per-pharmacy identifiers. Acceptable:
```sql
SELECT DATE(occurred_at), type, COUNT(*) as event_count
FROM audit_events
WHERE occurred_at > NOW() - INTERVAL '30 days'
GROUP BY 1, 2
ORDER BY 1, 2
```
Unacceptable:
```sql
SELECT pharmacy_id, ... FROM audit_events  -- ❌ cross-tenant with ID
```

Every cross-tenant query must be explicitly marked in a `cross_tenant_query_log`
table with justification.

---

## Ingest path

1. Agent emits event via `/api/agent/audit/append` (HMAC-authenticated)
2. Cloud API validates HMAC signature against pharmacy's current signing key
3. API validates payload against event type schema
4. API runs redaction coverage check — if violations, reject + alarm
5. API acquires advisory lock for pharmacy
6. API computes next sequence + chain hash
7. API inserts row
8. API releases lock
9. API returns (id, sequence, hash) to agent
10. Agent marks event as durably recorded (can release local copy)

**Failure modes:**
- Cloud unreachable: agent queues events locally in DPAPI-encrypted SQLite
  with size cap (1 MB). On reconnect, batch-upload in sequence.
- HMAC signature invalid: API rejects, emits `key.suspected_compromise` event,
  alerts Security Officer
- Redaction violation: API rejects, emits `invariant.violated` event, alerts
  Security Officer
- Sequence gap detected at nightly sweep: investigate, potentially re-fetch
  from S3 Object Lock digest

---

## Retention + deletion

### Retention policy
7 years from the later of:
- Date of event creation
- Date of most recent related event

### End-of-retention workflow
1. Events past retention marked `pending_deletion`
2. 90-day hold for dispute window
3. Cryptographic deletion: events remain in Postgres but payload is replaced
   with `{"deleted": "2033-04-22"}`. Hash chain preserved so chain integrity
   still verifiable.
4. S3 Object Lock digest for that period is deleted via dual-key approval
   (Joshua + Security Officer)
5. `chain.retention_purge` event emitted

### Never-delete tier
- Genesis events
- Events flagged by legal hold
- Events referenced by an unresolved incident

---

## Redaction ruleset

Every event payload passes through the redaction ruleset before insertion. The
ruleset version is recorded alongside the event.

Current rulesets live at `docs/self-healing/redaction-rulesets/v1.0.0.yaml`
(to be created Phase A). Example rule:

```yaml
rule: "patient-name-detection"
pattern: "\\b[A-Z][a-z]+\\s+[A-Z][a-z]+\\b"
action: "replace"
replacement: "[REDACTED_NAME]"
exemption_field_paths:
  - "payload.operator.full_name"  # operator names are allowed
```

Ruleset changes require PR + security review. New rules can add coverage
(strictly); relaxing coverage requires Joshua + Security Officer approval.

---

## Change log

- **2026-04-22 v0.1** — Initial draft. Locks to v1.0 after Nadim pilot + Codex review.
