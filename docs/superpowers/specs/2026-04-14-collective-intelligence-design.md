# Spec D: Collective Intelligence

**Date:** 2026-04-14
**Status:** Design approved
**Depends on:** Spec A (Learning Agent), Spec B (Behavioral Learning), Spec C (Self-Improving Feedback), Schema Canary

## Problem

Each pharmacy learns independently over a 30-day cycle. Pharmacy B re-discovers everything pharmacy A already knows — same PMS, same SQL shapes, same UI workflows. The collective has the answer; no individual pharmacy can access it.

## Solution

Cloud-side decomposition of existing POM + heartbeat streams into pattern-centric collective tables. Agent-side pull-based seeding at phase transitions. Dual-gate trust model prevents transferring knowledge that doesn't match. Confirmation-gated phase acceleration replaces time-based transitions.

**PHI boundary:** All transferred data is structural — parameterized SQL, tree hashes, status GUIDs, control types. No patient data, prescription content, or identifiers cross any boundary. Stays in Spec B's GREEN tier.

---

## 1. Dual-Gate Trust Model

Two independent gates determine transferability of collective knowledge:

**Gate 1 — Schema** (`contract_fingerprint` from schema canary)
- SHA-256 of (object + status_map + query + result_shape) fingerprints
- Validates: SQL shapes execute, table/column refs exist, status GUIDs match
- Available at: pattern phase entry

**Gate 2 — UI** (pms_version_hash + tree_hash overlap from UiaTreeObserver)
- `pms_version_hash`: hash of PMS executable version/build (exported in behavioral section, sent in seed request)
- Tree hashes: structural hashes of automation tree per screen
- Partial overlap valid: 8/10 matching screens → seeds for those 8 screens
- **Minimum overlap: ≥ 3 non-generic tree_hashes** (generic screens like login/splash excluded via a cloud-maintained exclusion list of high-frequency tree_hashes appearing across >80% of pms_version_hashes). One matching screen is insufficient — could be a common dialog shared across unrelated UI builds.
- Available at: model phase entry

**Tiered fallback:**

| Schema | UI Overlap | Seed Content | Starting Confidence |
|---|---|---|---|
| Match | ≥ 3 non-generic tree_hashes | Full correlations for overlapping screens | `min(0.6, aggregate_confidence × 0.7)` |
| Match | < 3 or none | SQL shapes, status dictionaries, workflow hints only | N/A (structural, no correlations) |
| Mismatch | — | Nothing. Observe-only. | Default 0.3 |

**Transfer discount formula:** `seeded_confidence = min(0.6, aggregate_confidence × 0.7)`

- 15 pharmacies at 0.9 avg → 0.6 (capped)
- 3 pharmacies at 0.7 avg → 0.49
- 2 pharmacies at 0.4 avg → 0.28 (barely above default)

Encodes collective conviction. Weak signals stay weak.

---

## 2. Cloud Tables (7 Pattern-Centric)

Populated from two existing streams: POM (once at model phase) + heartbeat telemetry (continuous). Two-layer storage: immutable **fact layer** preserves per-contributor data; **materialized aggregate layer** supports fast seed queries.

### Fact Layer (immutable, append-only)

| Table | Key | Contains |
|---|---|---|
| `contribution_facts` | (contributor_hash, pattern_type, pattern_key) | Raw contribution per pharmacy per pattern. pattern_type: `correlation`, `query_shape`, `status`, `workflow`, `screen`. Includes confidence, success_rate, sample_count, contributed_at, last_heartbeat_at. |

Each POM decomposition appends/upserts fact rows keyed by `contributor_hash` (SHA-256 of pharmacy_id — anonymized, no reverse lookup). Facts are never deleted, only superseded by newer facts from the same contributor.

### Aggregate Layer (materialized from facts)

| # | Table | Key | Contains |
|---|---|---|---|
| 1 | `pms_fingerprints` | (adapter_type, pms_version_hash) | Tree hashes, contributor_count, last_contributed_at |
| 2 | `schema_atlas` | (adapter_type, contract_fingerprint) | Table/column/type map, object_fingerprint, status_map_fingerprint, contributor_count, last_seen |
| 3 | `status_dictionaries` | (adapter_type, status_table, status_guid) | Description, contributor_count, first_seen, last_seen |
| 4 | `workflow_templates` | (routine_hash, contract_fingerprint) | Action path, avg_frequency, avg_confidence, has_writeback_candidate, contributor_count |
| 5 | `rx_queue_patterns` | (query_shape_hash, contract_fingerprint) | Parameterized SQL, tables_referenced[], aggregate_confidence, aggregate_success_rate, contributor_count |
| 6 | `collective_correlations` | (correlation_key, contract_fingerprint, tree_hash) | element_id, control_type, query_shape_hash, aggregate_confidence, aggregate_success_rate, contributor_count |
| 7 | `seed_manifests` | (pharmacy_id, phase, seed_digest) | Seed package snapshot, source_contribution_ids (contributor_hashes from fact layer), gates_passed[], created_at, applied_at |

8 tables total (1 fact + 6 aggregate + 1 manifest).

**Materialization rules** (recomputed from `contribution_facts`):
- `contributor_count`: distinct contributor_hashes per pattern_key
- `aggregate_confidence`: weighted average across contributors, weighted by sample_count
- `aggregate_success_rate`: weighted average, same weighting
- `last_seen` / `last_contributed_at`: max(contributed_at) across contributors

**Staleness decay** (applied during materialization):
- contributor's last_heartbeat_at >30 days ago → 0.5× weight
- contributor's last_heartbeat_at >90 days ago → 0× weight (excluded from aggregates)

The fact layer enables correct reweighting when contributors go stale, accurate 1.5× failure weighting for seed recipients (Section 5), and real audit provenance through seed_manifests → contributor_hashes → contribution_facts.

**Decomposition flow:**
```
POM arrives (UploadPomAsync)
  → validate signature, store raw POM
  → compute contributor_hash = SHA-256(pharmacy_id)
  → upsert contribution_facts for each pattern (keyed by contributor_hash + pattern_type + pattern_key)
  → rematerialize affected aggregate rows:
      behavioral.screenFingerprints  → pms_fingerprints
      schemas + canary baselines     → schema_atlas
      discovered_statuses            → status_dictionaries
      behavioral.routines            → workflow_templates
      writebackCandidates + feedback → rx_queue_patterns
                                     → collective_correlations (per tree_hash)
```

---

## 3. Seed Endpoint & Pull Protocol

### Endpoint

`POST /api/agent/seed/pull` — HMAC signed (existing PostSignedAsync pattern)

### Request

```json
{
  "adapter_type": "PioneerRx",
  "phase": "pattern | model",
  "contract_fingerprint": "sha256...",
  "pms_version_hash": "sha256...",
  "tree_hashes": [],
  "last_seed_digest": "sha256... | null"
}
```

Note: `pharmacy_id` is NOT in the request body — derived from authenticated key context (see Section 7).

### Pull Timing

| Phase | Agent Has | tree_hashes |
|---|---|---|
| Discovery | Nothing confirmed | **No pull.** |
| Pattern | adapter_type + contract_fingerprint | Empty array |
| Model | Above + tree_hashes from UIA walks | Populated |

### Idempotency

- `last_seed_digest` sent with request
- Exact match → 304 Not Modified (fast path)
- No exact match → **class-aware similarity check:**
  - Any removal, deprecation, or gate-loss in the new package vs old → **force new seed** (never 304). Safety-critical changes must propagate even if they affect a single correlation.
  - Any correlation whose aggregate_confidence crossed a policy threshold (e.g., dropped below 0.5) → **force new seed**.
  - Otherwise, if <5% of correlations differ in non-safety ways → 304 (fallback).
- Prevents restart loops from re-applying seeds over feedback-adjusted confidence, while ensuring safety-critical removals always propagate.

### Response — Pattern Phase (schema-gated)

```json
{
  "seed_digest": "sha256...",
  "seed_version": 1,
  "phase": "pattern",
  "gates_passed": ["schema"],
  "query_shapes": [{
    "query_shape_hash": "abc...",
    "parameterized_sql": "UPDATE [Prescription].[RxTransaction] SET ...",
    "tables_referenced": ["Prescription.RxTransaction"],
    "aggregate_confidence": 0.88,
    "contributor_count": 12
  }],
  "status_mappings": [{
    "status_table": "RxTransactionStatusType",
    "status_guid": "...",
    "description": "Completed",
    "contributor_count": 15
  }],
  "workflow_hints": [{
    "routine_hash": "def...",
    "path_length": 4,
    "avg_frequency": 35,
    "has_writeback_candidate": true,
    "contributor_count": 8
  }]
}
```

Tells the agent *what to look for* — no UI→SQL correlations (no UI gate passed).

### Response — Model Phase (dual-gated)

```json
{
  "seed_digest": "sha256...",
  "seed_version": 2,
  "phase": "model",
  "gates_passed": ["schema", "ui"],
  "ui_overlap": { "matched": 8, "total_local": 10, "overlap_ratio": 0.8 },
  "correlations": [{
    "correlation_key": "a1b2:btnComplete:xyz789",
    "tree_hash": "a1b2",
    "element_id": "btnComplete",
    "control_type": "Button",
    "query_shape_hash": "xyz789",
    "aggregate_confidence": 0.91,
    "aggregate_success_rate": 0.94,
    "contributor_count": 14,
    "seeded_confidence": 0.6
  }],
  "query_shapes": [/* updated from pattern phase */],
  "status_mappings": [/* updated from pattern phase */]
}
```

### Agent-Side Application Rules

**POM export additions** (behavioral section gains `pms_version_hash`; feedback section gains per-correlation `origin`, `first_seed_digest`, `seeded_at`). These are the only changes to the POM structure from Spec D.

**Pattern phase:**
- Insert all received seeds into `seed_items` with appropriate `item_type` and `item_key`
- Store query_shapes as observation hints for ActionCorrelator (`RegisterSeededShapes`)
- Merge status_mappings into local `discovered_statuses` with `source = seed`
- Store workflow_hints for RoutineDetector validation

**Model phase:**
- Insert all received correlations into `seed_items` (item_type = `'correlation'`)
- Insert correlations into `correlated_actions` with `confidence = seeded_confidence`, `source = seed`, `seed_digest = <digest>`, `seeded_at = now`
- **Local-wins rule:** Only insert if no local correlation exists for that key. Never overwrite local confidence with a weaker seed. Skipped correlations still recorded in `seed_items` with `rejected_at` set.
- Seeded correlations enter the normal feedback loop (Spec C mechanics)

**Confirmation callback:** `POST /api/agent/seed/confirm` with `{ seed_digest, applied_at, correlations_applied, correlations_skipped }` → updates `seed_manifests.applied_at`. pharmacy_id derived from auth context.

---

## 4. Phase Acceleration

Phase transitions become **confirmation-gated** with calendar floors. Seeds tell the agent what to look for; confirmation gates verify the collective was right.

### Pattern Phase Gates (ALL must pass)

| Gate | Threshold | Purpose |
|---|---|---|
| Seeded pattern confirmation | ≥ 80% of schema-gated seeds independently observed | Collective knowledge validated locally |
| Unseeded activity minimum | ≥ 5 distinct unseeded interaction patterns | Catches local-only workflows (reduced from 10 for seeded pharmacies) |
| Calendar floor | ≥ 72 hours | Environmental diversity — weekday/weekend, daily batches, open/close |
| Canary clean | Zero warnings during phase | Schema stable enough to trust |

### Model Phase Gates (ALL must pass)

| Gate | Threshold | Purpose |
|---|---|---|
| Seeded correlation confirmation | ≥ 80% of dual-gated correlations independently observed | UI→SQL mappings work locally |
| Unseeded correlation minimum | ≥ 5 distinct unseeded correlations | Local-only patterns captured |
| Calendar floor | ≥ 48 hours | Shorter than pattern (weekday/weekend already covered) |
| Canary clean | Zero warnings during phase | Schema stable through model building |

### Confirmation Mechanics

- `PhaseGate` queries `seed_items WHERE seed_digest = X` — unified across all seed types (query_shape, status_mapping, workflow_hint, correlation). Confirmation ratio = `COUNT(confirmed_at IS NOT NULL) / COUNT(*)`.
- A seed item is "confirmed" when `confirmed_at` is set — triggered when the agent independently observes the pattern through normal UIA/DMV observation (`local_match_count` increments, `confirmed_at` set on first match).
- Seed items that contradict local observation get `rejected_at` set and are excluded from the confirmation denominator (so a few bad seeds don't block phase exit).
- `correlated_actions.source = 'seed'` remains for provenance/POM export but is NOT the confirmation gate — `seed_items` is.
- ActionCorrelator hint: seeded query shape match reduces co-occurrence threshold from 3 to 2

### Timeline Impact

| Scenario | Discovery | Pattern | Model | Total |
|---|---|---|---|---|
| No seeds (baseline) | 7d | 14d | 9d | **30d** |
| Schema-gated only | 7d | 3-5d | 5-7d | **15-19d** |
| Fully dual-gated | 7d | 3-5d | 2-3d | **12-15d** |
| High-volume + dual-gated | 7d | 3d (floor) | 2d (floor) | **12d** |

### Fallback

Without seeds: existing time-based transitions (14d pattern, 9d model). Seeds are acceleration, not dependency.

**Abort trigger:** If canary fires OR seed confirmation drops below 50%, agent abandons acceleration and reverts to time-based duration. Applied seeds remain in feedback loop and get demoted/pruned naturally.

---

## 5. Negative Feedback Propagation

Seeded correlations that fail locally feed back to the collective through existing streams.

**POM provenance export** (required for cloud to distinguish seeded vs organic):

Spec C's `confidenceTrajectory` gains three new fields per correlation:
- `origin`: `'local'` or `'seed'`
- `first_seed_digest`: digest of the seed that introduced this correlation (null for local)
- `seeded_at`: timestamp when seed was applied (null for local)

These flow from `correlated_actions.source`, `.seed_digest`, `.seeded_at` into the POM export. No changes to feedback collection or processing — only to the exported shape.

**Mechanism:** B's feedback loop demotes/prunes seeded correlation → B's next POM includes the regression with `origin = 'seed'` + `first_seed_digest` → cloud decomposer matches to `contribution_facts` → reduces aggregate_confidence in materialized aggregates.

**Failure weighting:** A contribution_fact with `origin = 'seed'` (seed-recipient pharmacy reporting on a seeded correlation) carries **1.5× weight** in aggregate materialization (vs 1.0× for organic). Rationale: the seed was explicitly tested in a new environment — failure there is a stronger signal than organic fluctuation at the source. The `first_seed_digest` links back to `seed_manifests` for traceability.

**Circuit breaker:** If `aggregate_confidence` drops below 0.3, row marked `deprecated = true` and excluded from future seed assembly. Not deleted — history preserved for audit.

**No active recall.** Collective deprecation does NOT push commands to pharmacies already running the seed successfully. Local authority stays local. Collective authority stays collective.

---

## 6. Agent-Side Implementation

### New Components

| Component | Location | Responsibility |
|---|---|---|
| `SeedClient` | Core/Cloud/ | HTTP calls: POST seed/pull, POST seed/confirm via PostSignedAsync. 304 handling. |
| `SeedApplicator` | Core/Learning/ | Apply seeds to local state. Local-wins rule. Source tagging. |
| `PhaseGate` | Core/Learning/ | 4-gate evaluation. Returns `(ready, gates[])`. |

### Schema Changes (AgentStateDb)

**correlated_actions — new columns:**
- `source TEXT NOT NULL DEFAULT 'local'` — `'local'` or `'seed'`
- `seed_digest TEXT` — null for local correlations
- `seeded_at TEXT` — when the seed was applied (null for local)

**New table — applied_seeds:**
- `seed_digest TEXT PRIMARY KEY`
- `phase TEXT NOT NULL`
- `applied_at TEXT NOT NULL`
- `correlations_applied INTEGER NOT NULL`
- `correlations_skipped INTEGER NOT NULL`

**New table — seed_items** (tracks ALL seed items for confirmation gating):
- `id INTEGER PRIMARY KEY`
- `seed_digest TEXT NOT NULL`
- `item_type TEXT NOT NULL` — `'query_shape'`, `'status_mapping'`, `'workflow_hint'`, `'correlation'`
- `item_key TEXT NOT NULL` — type-specific key (query_shape_hash, status_guid, routine_hash, correlation_key)
- `applied_at TEXT NOT NULL`
- `confirmed_at TEXT` — null until independently observed locally
- `local_match_count INTEGER NOT NULL DEFAULT 0` — times independently observed
- `rejected_at TEXT` — set if local observation contradicts seed

PhaseGate queries `seed_items` (not `correlated_actions`) for confirmation ratios. This handles both pattern-phase seeds (query shapes, status mappings, workflow hints) and model-phase seeds (correlations) uniformly.

### Modified Components

| Component | Change |
|---|---|
| `LearningWorker` | At pattern/model entry: `SeedClient.PullAsync()` → `SeedApplicator.ApplyAsync()`. Each tick: `PhaseGate.Evaluate()`. |
| `ActionCorrelator` | New `RegisterSeededShapes(shapes)`. Seeded shape match reduces co-occurrence threshold 3→2. |

### Unchanged

SuavoCloudClient, WritebackProcessor, FeedbackCollector, FeedbackProcessor, SchemaCanary, RoutineDetector (receives hints passively via LearningWorker).

### Test Surface

- `SeedClientTests` — mock HTTP, request payloads, 304 short-circuit
- `SeedApplicatorTests` — local-wins, source tagging, digest tracking, partial overlap
- `PhaseGateTests` — each gate independently, all-pass, fallback on failure, source-column dependency
- Integration: seed pull → apply → observe → confirm → phase exit

---

## 7. Cloud-Side Contract

Defines the interface. Cloud implementation is a separate concern.

### Endpoints

| Endpoint | Method | Auth | Purpose |
|---|---|---|---|
| `/api/agent/seed/pull` | POST | HMAC signed (PostSignedAsync) | Assemble and return seed package |
| `/api/agent/seed/confirm` | POST | HMAC signed (PostSignedAsync) | Record application, update seed_manifests |

**Identity binding:** pharmacy_id is derived from the authenticated API key context on the server side. The request body does NOT carry pharmacy_id — prevents cross-tenant spoofing where one agent requests or confirms seeds on behalf of another pharmacy.

### Cloud Decomposer

Triggered on POM receipt (existing `POST /api/agent/pom`). Upserts into `contribution_facts` (fact layer), then rematerializes affected aggregate rows in 6 collective tables. Staleness decay applied during materialization.

### Seed Assembler

Triggered on `POST /api/agent/seed/pull` (pharmacy_id from auth context):
1. Match schema_atlas by adapter_type + contract_fingerprint → schema gate
2. If phase=model, match pms_fingerprints by pms_version_hash + tree_hashes for overlap → UI gate (≥ 3 non-generic tree_hashes required)
3. Assemble from matching rows in aggregate tables (filtered by matched tree_hashes)
4. Pre-compute `seeded_confidence = min(0.6, aggregate_confidence × 0.7)` per correlation
5. Compute seed_digest (SHA-256 of payload)
6. Check last_seed_digest: exact match → 304. Otherwise, class-aware similarity: any removal/deprecation/gate-loss/threshold-crossing → force new seed; else <5% non-safety diff → 304.
7. Create seed_manifests row (source_contribution_ids from contribution_facts), return payload

### Manifest Lifecycle

```
created (pharmacy pulled) → applied (agent confirmed) → [audit-only]
```
