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

**Gate 2 — UI** (tree_hash overlap from UiaTreeObserver)
- Structural hashes of automation tree per screen
- Partial overlap valid: 8/10 matching screens → seeds for those 8 screens
- Available at: model phase entry

**Tiered fallback:**

| Schema | UI Overlap | Seed Content | Starting Confidence |
|---|---|---|---|
| Match | ≥ 1 tree_hash | Full correlations for overlapping screens | `min(0.6, aggregate_confidence × 0.7)` |
| Match | None | SQL shapes, status dictionaries, workflow hints only | N/A (structural, no correlations) |
| Mismatch | — | Nothing. Observe-only. | Default 0.3 |

**Transfer discount formula:** `seeded_confidence = min(0.6, aggregate_confidence × 0.7)`

- 15 pharmacies at 0.9 avg → 0.6 (capped)
- 3 pharmacies at 0.7 avg → 0.49
- 2 pharmacies at 0.4 avg → 0.28 (barely above default)

Encodes collective conviction. Weak signals stay weak.

---

## 2. Cloud Tables (7 Pattern-Centric)

Populated from two existing streams: POM (once at model phase) + heartbeat telemetry (continuous). Merge-on-ingest — new contributions upsert, never append pharmacy-specific rows.

| # | Table | Key | Source | Contains |
|---|---|---|---|---|
| 1 | `pms_fingerprints` | (adapter_type, pms_version_hash) | POM behavioral.screenFingerprints | Tree hashes, contributor_count, last_contributed_at |
| 2 | `schema_atlas` | (adapter_type, contract_fingerprint) | POM schemas + canary baselines | Table/column/type map, object_fingerprint, status_map_fingerprint, contributor_count, last_seen |
| 3 | `status_dictionaries` | (adapter_type, status_table, status_guid) | POM discovered_statuses + heartbeat | Description, contributor_count, first_seen, last_seen |
| 4 | `workflow_templates` | (routine_hash, contract_fingerprint) | POM behavioral.routines | Action path, avg_frequency, avg_confidence, has_writeback_candidate, contributor_count |
| 5 | `rx_queue_patterns` | (query_shape_hash, contract_fingerprint) | POM behavioral.writebackCandidates | Parameterized SQL, tables_referenced[], aggregate_confidence, aggregate_success_rate, contributor_count |
| 6 | `collective_correlations` | (correlation_key, contract_fingerprint, tree_hash) | POM writebackCandidates + feedback.confidenceTrajectory | element_id, control_type, query_shape_hash, aggregate_confidence, aggregate_success_rate, contributor_count |
| 7 | `seed_manifests` | (pharmacy_id, phase, seed_digest) | Cloud assembler at seed pull time | Seed package snapshot, source_contribution_ids (anonymized), gates_passed[], created_at, applied_at |

**Merge-on-ingest rules:**
- `contributor_count`: increment, deduplicated by contributing pharmacy hash
- `aggregate_confidence`: weighted average across contributors, weighted by writebackAttempts
- `aggregate_success_rate`: weighted average, same weighting
- `last_seen` / `last_contributed_at`: updated to now

**Staleness decay** (applied at merge recalculation):
- Last heartbeat >30 days ago → 0.5× weight
- Last heartbeat >90 days ago → 0× weight (effectively removed from aggregates)

**Decomposition flow:**
```
POM arrives (UploadPomAsync)
  → validate signature, store raw POM
  → extract behavioral.screenFingerprints  → upsert pms_fingerprints
  → extract schemas + canary baselines     → upsert schema_atlas
  → extract discovered_statuses            → upsert status_dictionaries
  → extract behavioral.routines            → upsert workflow_templates
  → extract writebackCandidates + feedback → upsert rx_queue_patterns
                                           → upsert collective_correlations (per tree_hash)
```

---

## 3. Seed Endpoint & Pull Protocol

### Endpoint

`GET /api/agent/seed` — ECDSA signed (existing auth pattern)

### Request

```json
{
  "pharmacy_id": "uuid",
  "adapter_type": "PioneerRx",
  "phase": "pattern | model",
  "contract_fingerprint": "sha256...",
  "tree_hashes": [],
  "last_seed_digest": "sha256... | null"
}
```

### Pull Timing

| Phase | Agent Has | tree_hashes |
|---|---|---|
| Discovery | Nothing confirmed | **No pull.** |
| Pattern | adapter_type + contract_fingerprint | Empty array |
| Model | Above + tree_hashes from UIA walks | Populated |

### Idempotency

- `last_seed_digest` sent with request
- Exact match → 304 Not Modified (fast path)
- No exact match → similarity check: if <5% of correlations differ → 304 (fallback)
- Prevents restart loops from re-applying seeds over feedback-adjusted confidence

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

**Pattern phase:**
- Store query_shapes as observation hints for ActionCorrelator (`RegisterSeededShapes`)
- Merge status_mappings into local `discovered_statuses` with `source = seed`
- Store workflow_hints for RoutineDetector validation

**Model phase:**
- Insert correlations into `correlated_actions` with `confidence = seeded_confidence`, `source = seed`, `seed_digest = <digest>`
- **Local-wins rule:** Only insert if no local correlation exists for that key. Never overwrite local confidence with a weaker seed.
- Seeded correlations enter the normal feedback loop (Spec C mechanics)

**Confirmation callback:** `POST /api/agent/seed-confirm` with `{ pharmacy_id, seed_digest, applied_at, correlations_applied, correlations_skipped }` → updates `seed_manifests.applied_at`

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

- `PhaseGate` queries `correlated_actions WHERE source = 'seed' AND seed_digest = X` to identify seeded correlations (source column is **load-bearing**)
- A seeded pattern/correlation is "confirmed" when independently observed at least once through normal UIA/DMV observation
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

**Mechanism:** B's feedback loop demotes/prunes seeded correlation → B's next POM shows the regression → cloud decomposer detects it → reduces aggregate_confidence in collective tables.

**Failure weighting:** A pharmacy that *received* a seed and reported failure carries **1.5× weight** in aggregate recalculation (vs 1.0× for organic). Rationale: the seed was explicitly tested in a new environment — failure there is a stronger signal than organic fluctuation at the source.

**Circuit breaker:** If `aggregate_confidence` drops below 0.3, row marked `deprecated = true` and excluded from future seed assembly. Not deleted — history preserved for audit.

**No active recall.** Collective deprecation does NOT push commands to pharmacies already running the seed successfully. Local authority stays local. Collective authority stays collective.

---

## 6. Agent-Side Implementation

### New Components

| Component | Location | Responsibility |
|---|---|---|
| `SeedClient` | Core/Cloud/ | HTTP calls: GET seed, POST seed-confirm. 304 handling. |
| `SeedApplicator` | Core/Learning/ | Apply seeds to local state. Local-wins rule. Source tagging. |
| `PhaseGate` | Core/Learning/ | 4-gate evaluation. Returns `(ready, gates[])`. |

### Schema Changes (AgentStateDb)

**correlated_actions — new columns:**
- `source TEXT NOT NULL DEFAULT 'local'` — `'local'` or `'seed'`
- `seed_digest TEXT` — null for local correlations

**New table — applied_seeds:**
- `seed_digest TEXT PRIMARY KEY`
- `phase TEXT NOT NULL`
- `applied_at TEXT NOT NULL`
- `correlations_applied INTEGER NOT NULL`
- `correlations_skipped INTEGER NOT NULL`

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
| `/api/agent/seed` | GET | ECDSA signed | Assemble and return seed package |
| `/api/agent/seed-confirm` | POST | ECDSA signed | Record application, update seed_manifests |

### Cloud Decomposer

Triggered on POM receipt (existing `POST /api/agent/pom`). Upserts into 6 collective tables. Merge rules: weighted averages, deduplicated contributor counts, staleness decay at recalculation.

### Seed Assembler

Triggered on `GET /api/agent/seed`:
1. Match schema_atlas by adapter_type + contract_fingerprint → schema gate
2. If phase=model, match pms_fingerprints tree_hashes for overlap → UI gate
3. Assemble from matching rows in collective tables (filtered by matched tree_hashes)
4. Pre-compute `seeded_confidence = min(0.6, aggregate_confidence × 0.7)` per correlation
5. Compute seed_digest (SHA-256 of payload)
6. Check last_seed_digest: exact match → 304; <5% diff → 304; else → new seed
7. Create seed_manifests row, return payload

### Manifest Lifecycle

```
created (pharmacy pulled) → applied (agent confirmed) → [audit-only]
```
