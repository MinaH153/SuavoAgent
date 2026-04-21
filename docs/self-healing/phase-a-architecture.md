# Phase A — Observability Substrate (v0.1)

> Phase A is the nervous system of the self-healing architecture. Every later
> phase (L1 dispatch, L2 verbs, L3 plans, Mission Loop) consumes events Phase
> A produces. If Phase A is wrong, everything downstream inherits the defect.

**Locked date:** 2026-04-21
**Status:** v0.1 draft
**Depends on:** `invariants.md`, `audit-schema.md`, `key-custody.md`, `action-grammar-v1.md`
**Blocks:** Phase B (pharmacies 2–5 onboarding) and Phase C (L1 dispatch)

---

## Scope

Phase A ships six deliverables that together give us: **no pharmacy can go
silently unhealthy; every action is tamper-evidently audited; every binary is
cryptographically attested.**

| ID | Deliverable | Estimated effort | Blocks later phases? |
|---|---|---|---|
| A1 | Silent-agent alarm (cloud cron + Twilio + Slack) | ~1 day | Direct prereq for B |
| A2 | Hash-chained audit substrate (Postgres + S3 Object Lock) | ~2–3 days | Direct prereq for C, D, E, F, G |
| A3 | Crash-log cloud aggregation | ~1 day | Useful for B diagnostics |
| A4 | Agent version drift report (fleet portal UI) | ~0.5 day | B onboarding UX |
| A5 | `bootstrap --probe` non-destructive health scan | ~1 day | Input to A1 freshness check |
| A6 | Cryptographic binary + config + verb catalog attestation | ~3 days | Prereq for D (L2 signed verbs) |

Total: ~8–10 working days. Target delivery: week of May 5 (post-Nadim pilot
with 1 week buffer for pilot postmortem).

---

## Why Phase A first

Codex adversarial review (see `suavoagent-self-healing-research-findings.md`)
rated "invariants before dashboards" as a CRITICAL requirement: without
observability substrate that maps each signal to a specific invariant, every
later phase either flies blind or reinvents ingest.

The A1 (silent-agent alarm) is also the single highest-leverage wedge in the
self-healing arc — it directly addresses the Nadim-silent-for-12-days failure
mode that motivated this entire effort.

---

## Prerequisites

- [x] Phase 0 invariant docs locked (v0.1 → v1.0 post-Nadim)
- [x] SuavoAgent.Watchdog service deployed (v3.13.7)
- [x] `bootstrap.ps1 --repair` path shipped
- [x] `suavo-check.ps1` pre-flight probe published
- [ ] Nadim pilot stable for 72 hours (gate for Phase A code ship; spec can
      be written + reviewed before gate)
- [ ] AWS account + KMS key ring set up for dev/staging/prod isolation
      (§Phase A kickoff tasks)
- [ ] Supabase project has Edge Functions enabled + pg_cron extension

---

## Architecture overview

### Data flow

```
┌─────────────────────────┐                      ┌──────────────────────────┐
│    Pharmacy Workstation │                      │       Suavo Cloud         │
│                         │                      │  (suavollc.com + Supabase)│
│  ┌───────────────────┐  │                      │                          │
│  │ SuavoAgent.Core   │──┼──heartbeat (30s)────►│                          │
│  │ SuavoAgent.Broker │──┼──structured events──►│  /api/agent/events       │
│  │ SuavoAgent.Watchdog │─┼──crash logs─────────►│  /api/agent/crash-logs  │
│  │                   │  │                      │                          │
│  │ bootstrap --probe │──┼──probe JSON─────────►│  /api/agent/probe        │
│  │                   │  │                      │                          │
│  │ Attestation verify│◄─┼──expected SBOM──────┤  /api/agent/attestation  │
│  └───────────────────┘  │                      │        │                 │
└─────────────────────────┘                      │        ▼                 │
                                                 │  ┌───────────────────┐   │
                                                 │  │ Ingest validators │   │
                                                 │  │  - HMAC verify    │   │
                                                 │  │  - Schema check   │   │
                                                 │  │  - PHI redaction  │   │
                                                 │  │    coverage check │   │
                                                 │  └─────────┬─────────┘   │
                                                 │            ▼             │
                                                 │  ┌───────────────────┐   │
                                                 │  │ audit_events      │   │
                                                 │  │ (append-only, per-│   │
                                                 │  │  pharmacy chain)  │   │
                                                 │  └──┬───────┬────────┘   │
                                                 │     │       │            │
                                   ┌─────────────┼─────┘       └────────┐   │
                                   │             │                      │   │
                           03:00 UTC daily    A1 silence              A4 UI│
                           verify sweep      monitor                  drift│
                                   │             │                      │   │
                                   ▼             ▼                      ▼   │
                              S3 Object       Twilio/Slack           Fleet  │
                              Lock digest     alerts                 portal │
                              (compliance)                                  │
                                                                            │
                                                 └──────────────────────────┘
```

### Component map

**Agent side (~/Code/SuavoAgent):**
- `SuavoAgent.Core` — extend HeartbeatWorker to emit structured events per §I.4
  data classification
- `SuavoAgent.Broker` — no changes Phase A (already ships crash logs via sinks)
- `SuavoAgent.Watchdog` — extend to emit `service.*` events
- `bootstrap.ps1` — add `--probe` flag
- New: attestation verifier in Core startup path

**Cloud side (~/Code/Suavo):**
- `src/app/api/agent/events/route.ts` — event ingest endpoint
- `src/app/api/agent/crash-logs/route.ts` — crash log ingest
- `src/app/api/agent/probe/route.ts` — probe result ingest
- `src/app/api/agent/attestation/route.ts` — expected SBOM query
- `src/app/api/internal/audit/verify-chain/route.ts` — nightly verify cron
- `src/app/api/internal/audit/daily-digest/route.ts` — daily S3 digest cron
- `src/app/api/internal/alerts/silent-agents/route.ts` — A1 cron
- Supabase migration: `audit_events` table with triggers + RLS
- Supabase migration: `agent_health_snapshots` table for A1 freshness check
- Supabase migration: `chain_verification` table for tamper-detection state
- Supabase migration: `attestation_manifests` table
- Fleet portal: `/fleet/cockpit/health` — A4 version drift UI

**Infrastructure:**
- AWS account + KMS key rings
- S3 bucket `suavo-audit-digests` in Object Lock compliance mode
- Cross-region replication us-west-1 → us-east-2
- Twilio phone number + Slack workspace webhook

---

## A1 — Silent-agent alarm

### Goal

Detect and alert on any pharmacy agent that has stopped emitting heartbeats
for >15 minutes. Nadim-silent-for-12-days doesn't happen again.

### Design

**Freshness check loop:** Supabase `pg_cron` job every 5 minutes runs:

```sql
-- Find pharmacies whose last_heartbeat is >15 min ago AND status is not
-- already 'silent_alert_fired' in last 6 hours (dedup)
SELECT p.id, p.name, p.last_heartbeat_at,
       NOW() - p.last_heartbeat_at AS silent_duration
FROM agent_instances p
WHERE p.status = 'active'
  AND p.last_heartbeat_at < NOW() - INTERVAL '15 minutes'
  AND NOT EXISTS (
    SELECT 1 FROM silent_alerts s
    WHERE s.pharmacy_id = p.id
      AND s.fired_at > NOW() - INTERVAL '6 hours'
  );
```

For each row:
1. Insert into `silent_alerts` with `(pharmacy_id, fired_at, silent_duration, acknowledged_at=NULL)`
2. Emit `heartbeat.silent_alarm` to pharmacy's audit chain
3. Send SMS via Twilio to pharmacy's emergency contact (from `pharmacy_profiles.emergency_phone`)
4. Send Slack notification to `#suavo-fleet-alerts` with pharmacy name, last
   heartbeat time, silence duration, and link to fleet cockpit
5. Send email to Joshua + fleet operator contacts via SendGrid

### Alarm escalation ladder

| Silent duration | Severity | Action |
|---|---|---|
| 15 min | warn | Slack ping to `#suavo-fleet-alerts` |
| 30 min | error | SMS to pharmacy emergency contact + Slack |
| 2 hours | error | Additional SMS + email to Joshua |
| 24 hours | critical | Page on-call operator via PagerDuty (future) |

### Deduplication

6-hour dedup window prevents alarm storms. An acknowledged alarm re-fires if
the agent is still silent at the 24-hour mark.

### Acknowledgment UX

Fleet cockpit shows active silent alerts at the top of the dashboard. Operator
clicks "Acknowledge" with a required reason dropdown (
  "working with pharmacy",
  "pharmacy off-hours, no action needed",
  "investigating",
  "false positive"
). Acknowledgment recorded to audit chain.

### False-positive guardrails

Known maintenance windows suppress alarms. `pharmacy_maintenance_windows`
table supports per-pharmacy scheduled silent periods (e.g., "closed Sunday
8pm–Monday 6am"). During maintenance, `heartbeat.silent_alarm` still emits
to audit for visibility but no SMS/Slack fires.

### Metrics we commit to

- Time-to-alert: <16 minutes from last heartbeat (measured as p99)
- False positive rate: <5% (false positive = "pharmacy was actually operating
  normally during alarm")
- Alarm acknowledgment rate: >95% within 30 minutes of fire

### Freshness signals beyond heartbeat

A1 can also fire on:
- `/api/agent/probe` result shows health_score < 0.5
- Attestation verification mismatch (A6)
- Event stream goes silent (heartbeats arriving but no other events for >2h — may indicate process partial-death)

---

## A2 — Hash-chained audit substrate

### Goal

Every action, every event, every decision in the Suavo fleet is recorded to
a tamper-evident per-pharmacy hash chain. Chain is externally verifiable.
Daily digests to S3 Object Lock. 7-year retention.

### Migration sequence

**Migration 1: core tables**
```sql
-- See audit-schema.md §Postgres schema for full DDL
CREATE TABLE audit_events (...);
CREATE TABLE chain_verification (...);
CREATE TABLE silent_alerts (...);
CREATE TABLE pharmacy_maintenance_windows (...);
```

**Migration 2: triggers + RLS**
```sql
-- reject_audit_mutation trigger
-- ALTER TABLE audit_events ENABLE ROW LEVEL SECURITY
-- CREATE POLICY "pharmacies read own events"
```

**Migration 3: pg_cron jobs**
```sql
SELECT cron.schedule('verify-chain', '0 3 * * *', $$
  SELECT net.http_post(
    url => 'https://suavollc.com/api/internal/audit/verify-chain',
    headers => jsonb_build_object('Authorization', 'Bearer ' || current_setting('cron.job_secret')),
    body => '{}'::jsonb
  );
$$);

SELECT cron.schedule('daily-digest', '0 4 * * *', $$...$$);
SELECT cron.schedule('silent-agents', '*/5 * * * *', $$...$$);
```

### Ingest path

`POST /api/agent/events`
1. Read `X-Pharmacy-Id` + `X-Signature` + `X-Timestamp` headers
2. Resolve pharmacy's current signing key from `pharmacy_keys` (KMS decrypt)
3. Verify HMAC-SHA256 signature over `body || timestamp`
4. Reject if timestamp skew >5 min (replay defense)
5. Parse body as `AuditEvent[]` (batch-ingest for efficiency)
6. For each event:
   a. Validate against event-type JSON schema
   b. Run PHI redaction coverage check (every field run through regex + allowlist)
   c. Acquire pg_advisory_xact_lock for pharmacy_id
   d. Compute next sequence + hash
   e. Insert row
7. Commit transaction; release lock
8. Return `(accepted_count, rejected_count, errors[])`

### Tamper detection

Nightly `verify-chain` endpoint runs per pharmacy:
1. Load last verified state from `chain_verification`
2. Fetch events with `sequence > last_verified_sequence`
3. Replay hash chain
4. Update `chain_verification` with new state
5. If hash mismatch: emit `invariant.violated` to admin channel + Security
   Officer alert

### S3 Object Lock daily digest

Daily `daily-digest` endpoint runs per pharmacy at 04:00 UTC:
1. Compute digest per `audit-schema.md` §Daily digest process
2. Sign digest with KMS Ed25519 key
3. Upload to S3 `suavo-audit-digests/<pharmacy_hash>/<date>.json` with
   `ObjectLockMode=COMPLIANCE`, `ObjectLockRetainUntilDate=+7years`
4. Emit `digest.uploaded` event to chain
5. Retry with exponential backoff on failure; page Security Officer on 3
   consecutive failures

### External verifier

Build as separate repo `MinaH153/SuavoAgent-AuditVerifier` (deferred to Phase
B; not a Phase A blocker).

### Metrics

- Ingest latency (p99): <500ms
- Chain verify sweep duration: <5 min for largest pharmacy's daily volume
- Digest upload success rate: >99.9%
- Zero tamper detections in normal operation (any detection = incident)

---

## A3 — Crash-log cloud aggregation

### Goal

Existing crash sinks (`startup-crash.log`, `broker-crash.log`,
`watchdog-crash.log`, `bootstrap-*.log`) auto-upload on next heartbeat so
Joshua never has to email a pharmacy operator asking for log files.

### Design

Agent-side: extend `HeartbeatWorker` to check `%ProgramData%\SuavoAgent\logs\`
for files modified since last successful upload. If found and file < 256 KB,
include in heartbeat payload under `payload.crash_logs[]`. If larger, chunk.

Cloud-side: `/api/agent/crash-logs` accepts uploads, stores raw bytes in S3
with server-side encryption, emits `crash.log_uploaded` event with S3 URL as
evidence reference in the audit chain.

Fleet cockpit UI: operator sees "3 crash logs in last 24h" badge per
pharmacy, clicks to drill into raw logs.

### PHI guardrails

Crash logs may contain stack traces that reference file paths, method names,
partial parameter values. Before upload, agent runs a pre-redaction pass:
- Replace any string matching `[\\/]Users[\\/]\w+` with `[\\/]Users[\\/][REDACTED_USER]`
- Replace any string matching `\d{3}-\d{2}-\d{4}` (SSN-shape) with `[REDACTED_SSN]`
- Replace any RxNumber-shape pattern
- Escalate to Security Officer on PHI-like pattern in crash content (log BUT
  don't upload if high-confidence PHI signal)

Crash logs marked with `phi_redaction_version` so post-hoc ruleset upgrades
can be re-applied.

### Retention

90 days in S3 hot storage; lifecycle moves to Glacier for full 7-year
retention. Delete after 7 years.

---

## A4 — Agent version drift report

### Goal

Fleet cockpit surface shows which pharmacies are on which agent version +
highlights drift (pharmacies on N-1 or older).

### Design

Query: `SELECT pharmacy_id, agent_version, last_heartbeat_at FROM agent_instances`
Group by `agent_version`, color-code by drift:
- Latest: green
- N-1: yellow
- N-2 or older: red + "upgrade recommended"
- Offline >24h: gray

Click a row → drill into per-pharmacy health (A1 state, A3 crash logs, A5
probe result).

### UX rule

Version drift is advisory, not alarming. We don't force upgrades. Pharmacies
get OTA notifications via existing `SelfUpdater` path; this report is for
fleet operator to see at a glance who needs attention.

---

## A5 — `bootstrap --probe`

### Goal

Non-destructive on-demand health scan. Structured output cloud can consume.

### Design

Add `--probe` switch to `bootstrap.ps1` that runs read-only checks (nothing
writes to disk, nothing restarts services, no cloud calls except result
upload at the end).

Reuses logic from `suavo-check.ps1` but skips install-time checks (since
agent is already installed). Adds agent-specific checks:

- Service states for Core/Broker/Watchdog
- Most recent heartbeat timestamp
- Current agent version + expected version per attestation
- SQL connectivity test (already-configured credentials)
- Disk space in `%ProgramData%\SuavoAgent\`
- Log file sizes (detect runaway log growth)
- Config file SHA-256 (compare to expected in attestation manifest)

Emits to `/api/agent/probe` endpoint + writes local copy to Desktop as JSON.

### Trigger paths

- Operator pushes "Run health probe" from fleet cockpit → signed command →
  agent executes → result to cloud + Desktop
- Silent-agent alarm auto-triggers probe as diagnostic step
- Cron: every 6 hours (lightweight, read-only, no pharmacy disruption)

---

## A6 — Cryptographic binary + config + verb catalog attestation

### Goal

On agent startup + periodically, verify every binary + config matches an
expected signed manifest. Mismatch = halt agent + alarm.

### Design

**Cloud produces manifests** per release:
- SBOM of every binary (SPDX format)
- SHA-256 of every file in install dir
- Ed25519 signature over the SBOM (signed by release signing key from
  `key-custody.md`)

**Agent verifies on startup:**
1. Hash every file in install dir
2. Fetch latest manifest from `/api/agent/attestation?version=3.13.7` (cache
   locally)
3. Verify manifest signature with embedded public key
4. Compare hashes
5. On mismatch: log `attestation.mismatch` event + halt all non-diagnostic
   operations + alarm cloud

**Periodic re-check:** every 30 minutes, re-hash files, compare. Detects
mid-run tampering (malware injecting into agent binary).

### sigstore integration

Use sigstore's cosign for the signature path. Public key in sigstore's
transparency log (Rekor) gives us independent verification. Agent embeds
sigstore's trust root.

### Config file hashing

`appsettings.json` contents are hashed AND verified. But the hash is
signed-by-cloud (not by release) because config is per-pharmacy. Cloud
signs each pharmacy's current expected config hash at the same time it
signs any `apply_config_override` verb invocation.

### Attestation mismatch response

If binary mismatch (possible tamper):
- Halt all outbound commands
- Continue observation (heartbeat) — we need to know the agent still phones
  home
- Operator alert: "Pharmacy X agent binary hash mismatch, potential tamper"
- Operator investigates (could be legitimate rollback to older version, could
  be tamper)

If config mismatch:
- Halt all mutation verbs on that pharmacy
- Observation continues
- Re-fetch expected config; if agent-side is ahead of cloud-side, surface
  for operator approval; if cloud-side is ahead, apply expected config via
  signed command

---

## Cross-cutting concerns

### Threat model

**Adversaries we design against:**
1. **Compromised cloud account** — attacker gains AWS/Supabase admin. Defenses:
   S3 Object Lock prevents digest tampering; dual-key emergency-stop prevents
   fleet-wide malicious commands; KMS keys scoped per-environment.
2. **Compromised agent key** — attacker extracts a pharmacy's HMAC key.
   Defenses: 90-day rotation; suspected-compromise detection + rotation;
   per-pharmacy scope limits blast.
3. **Compromised release signing key** — attacker signs malicious agent
   update. Defenses: dual-signature rotation protocol; embedded revocation
   list; sigstore transparency log gives independent verification.
4. **Malicious insider (Suavo employee)** — Defenses: two-person rule for
   key ceremonies; audit chain is immutable; cross-region S3 replication
   prevents single-region tampering; Joshua + Security Officer only dual-key
   capability for emergency stops / S3 deletion.
5. **Malicious pharmacy operator** — Defenses: operator can only affect
   their own pharmacy (RLS); signed consent per action; Mission Charter
   cannot be bypassed.

### PHI redaction enforcement

Per `invariants.md §I.1`, every outbound event payload passes redaction
before ingest. Enforcement is BOTH agent-side (first line) AND cloud-side
(redundant safety): if either detects PHI, the event is rejected.

Redaction ruleset version is recorded with every event. Rulesets live in
`docs/self-healing/redaction-rulesets/v1.0.0.yaml` (Phase A deliverable —
create in week 1).

### Testing strategy

**Unit tests:**
- Hash chain computation (given N events, verifier produces correct hash)
- Advisory lock ordering (concurrent inserts produce gap-free sequence)
- Trigger rejection (UPDATE/DELETE against audit_events raises exception)
- Redaction coverage (every known PHI pattern is caught)

**Integration tests:**
- Silent-agent alarm end-to-end (stub heartbeat → wait 16 min → verify
  alarm fired + Slack received)
- Crash log upload + retrieval with redaction applied
- Attestation mismatch halts agent correctly
- Chain verification sweep detects synthesized tampering

**Chaos tests (Phase A complete-criteria):**
- Kill cloud during agent heartbeat — agent queues locally, resumes on
  reconnect, chain remains gap-free
- Kill cloud during daily digest cron — retry succeeds next invocation
- Introduce hash chain tampering (direct SQL update) — verify sweep detects

**Load tests:**
- 100 concurrent pharmacies heartbeating — ingest latency p99 <500ms
- 10K events/pharmacy/day for 1 pharmacy — verify sweep completes in <5min

### Rollout gates

Per `action-grammar-v1.md` canary discipline, Phase A ships in tiers even
though it's "just cloud infrastructure":

| Tier | Coverage | Soak | Gate to advance |
|---|---|---|---|
| Dev | Joshua's test pharmacy record in staging | 48h | No ingest errors, no false alarms |
| Pilot | Nadim post-Saturday | 72h | All events ingesting, chain verifies green |
| 5% | Add 2–3 more pharmacies as they onboard | 7 days | Alarm precision >95%, zero chain-verify failures |
| 100% | All active pharmacies | Open-ended | Ongoing monitoring |

### Observability-of-observability

A1 itself needs observability. What if the silent-agent alarm cron is silent?
- Supabase + uptime monitoring (Better Uptime or similar) hits the cron
  endpoint every 5 min; if it doesn't respond, page Joshua
- Weekly "heartbeat of heartbeats" report: how many alarms fired, acknowledged,
  dedup rate

Same for audit ingest: if `/api/agent/events` endpoint goes down, alert Joshua
before we lose events.

---

## Exit criteria for Phase A complete

Phase A is "done" when ALL of:

1. ✅ All 6 deliverables (A1–A6) shipped to prod
2. ✅ 72 hours of Nadim-in-prod with chain-verify green, zero false alarms,
   zero ingest errors
3. ✅ Chaos test suite passes (kill cloud, kill agent, synthesize tamper)
4. ✅ External verifier repo published + pharmacy can self-verify their chain
5. ✅ PHI redaction ruleset reviewed by Joshua + designated Security Officer
6. ✅ Codex adversarial re-review finds no CRITICAL gaps
7. ✅ Documentation locked: this doc moves from v0.1 to v1.0

---

## Phase A kickoff tasks (week of May 5, if Nadim pilot stable)

Day 1 Monday:
- [ ] Write Supabase migration 1 (core tables) — PR for review
- [ ] Set up AWS KMS key ring in dev account
- [ ] Create S3 bucket `suavo-audit-digests` (dev) with Object Lock
- [ ] Scaffold `/api/agent/events` route skeleton

Day 2 Tuesday:
- [ ] Migration 2 (triggers + RLS)
- [ ] Implement HMAC verification middleware in cloud API
- [ ] Implement hash chain computation + advisory lock logic

Day 3 Wednesday:
- [ ] A1 silent-agent alarm cron + Twilio integration
- [ ] Slack webhook integration
- [ ] First end-to-end ingest test (synthetic agent)

Day 4 Thursday:
- [ ] A3 crash-log upload path
- [ ] Agent-side `HeartbeatWorker` extension for crash log attachment

Day 5 Friday:
- [ ] A5 `bootstrap --probe` flag + result upload endpoint
- [ ] A4 fleet cockpit drift UI (stub — data flow first, polish later)
- [ ] A2 daily digest cron + S3 upload

Day 6–8 (following week):
- [ ] A6 attestation manifest generation in release workflow
- [ ] A6 agent-side verifier on startup
- [ ] Chain verification sweep
- [ ] Chaos test suite
- [ ] Codex review + v1.0 lock

---

## Change log

- **2026-04-21 v0.1** — Initial draft. Six deliverables, cross-cutting
  concerns, threat model, kickoff tasks. Locks to v1.0 after Nadim pilot
  stabilizes + Codex re-review.
