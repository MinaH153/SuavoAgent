# SuavoAgent v3 Spec C: Self-Improving Feedback System — Design Spec

**Date:** 2026-04-13
**Author:** Claude + Joshua Henein
**Status:** Draft v1 — design approved, pending implementation plan
**Depends on:** Spec A (Writeback), Spec B (Expanded Behavioral Learning)
**Feeds into:** Spec D (Collective Intelligence)

## Problem

SuavoAgent's behavioral learning system (Spec B) discovers UI↔SQL correlations and workflows, and the writeback system (Spec A) executes status updates. But the two systems are open-loop — writeback outcomes never feed back into correlation confidence, operator corrections don't adjust learned state, and schema drift doesn't surgically invalidate affected correlations. The agent learns once and operates on a frozen model. When conditions change (PMS update, workflow shift, schema drift), the agent continues executing stale correlations until a human intervenes.

A self-improving system needs a closed feedback loop: outcomes adjust confidence, operators steer learning, and environmental changes trigger surgical re-learning — all through a causal, auditable event log that can explain WHY any piece of learned state changed.

## Solution

An event-sourced feedback system with three signal sources (writeback outcomes, operator corrections, canary drift), one immutable event log (`feedback_events`), and two consumption modes (inline for latency-critical writeback outcomes, batch for everything else). Every mutation to learned state is traceable through a forward-only causal chain.

The key insight is **causality over auditability**. The event log doesn't just record what changed — it records why, enabling the agent (and operators) to reconstruct the full decision chain: "Operator rejected candidate X on April 15 → confidence on correlation Y dropped → routine Z lost writeback flag → agent stopped automating that path." Direct mutation destroys this chain. Event sourcing preserves it.

## Architecture

```
WRITEBACK OUTCOMES (inline)              OPERATOR COMMANDS (batch)         CANARY DRIFT (batch)
WritebackProcessor                       SignedCommandVerifier              SchemaCanaryEscalation
        │                                        │                                  │
        ▼                                        ▼                                  ▼
FeedbackCollector.RecordWritebackOutcome  FeedbackProcessor                 FeedbackProcessor
        │                                .ProcessOperatorDirectives         .ProcessCanaryDrift
        │                                        │                                  │
        └────────────────────┬───────────────────┘──────────────────────────────────┘
                             ▼
                    feedback_events (immutable, append-only)
                             │
                    ┌────────┴────────┐
                    ▼                 ▼
            ApplyDirective      ApplyDirective
            (inline, same tx)   (batch, 5-min tick)
                    │                 │
                    └────────┬────────┘
                             ▼
              ┌──────────────┼──────────────┐
              ▼              ▼              ▼
    correlated_actions  correlation_    learned_routines
    (confidence,        window_         (writeback flag
     operator flags,    overrides       updates)
     stale, suspend)
```

**Inline path** — WritebackProcessor records outcomes and applies confidence adjustments in a single SQLCipher transaction. Latency: zero — the feedback event is written and applied atomically with the writeback state transition.

**Batch path** — FeedbackProcessor runs on the existing LearningWorker 5-minute tick. Processes operator commands, canary drift, decay, recalibration, promotion health, and stale escalation. Reads `WHERE applied_at IS NULL`, applies directives, sets `applied_at`.

**Both consumers are idempotent.** If inline already applied a directive, batch skips it. Replay the entire `feedback_events` table from zero against reset target tables and the end state is identical.

**FeedbackProcessor is stateless.** Constructed fresh each tick, reads from tables, applies pending work, done. No lifecycle management, no in-memory state between ticks. All state lives in SQLCipher.

## Signal Sources

| Source | Frequency | Latency Requirement | Consumer | Phase Scope |
|--------|-----------|-------------------|----------|-------------|
| Writeback outcomes | 50-100/day | Immediate (inline) | WritebackProcessor via FeedbackCollector | active |
| Operator corrections | ~5-10/week | Batch (5-min tick) | FeedbackProcessor.ProcessOperatorDirectives | pattern, model, approved, active |
| Canary drift | ~1-2/month | Batch (5-min tick) | FeedbackProcessor.ProcessCanaryDrift | pattern, model, approved, active |
| Confidence decay | max 1/correlation/day | Batch (5-min tick) | FeedbackProcessor.ProcessDecay | pattern, model, approved, active |
| Recalibration | 1/qualifying-correlation/tick | Batch (5-min tick) | FeedbackProcessor.ProcessRecalibration | active |
| Promotion health | per-failure on promoted candidates | Inline (same tx) | FeedbackCollector | active |
| Stale escalation | 1/expired-stale/tick | Batch (5-min tick) | FeedbackProcessor.ProcessStaleEscalation | pattern, model, approved, active |

## Feedback Event Model

### `feedback_events` Table

Immutable append-only log. Every feedback event records what happened, what caused it, and what directive it produced. Old rows are NEVER modified — not even to add forward references.

```sql
CREATE TABLE IF NOT EXISTS feedback_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id TEXT NOT NULL,
    event_type TEXT NOT NULL,          -- 'writeback_outcome', 'operator_command', 'canary_drift',
                                       --  'decay', 'recalibration', 'promotion_health', 'stale_escalation'
    source TEXT NOT NULL,              -- 'writeback', 'operator', 'canary', 'decay', 'recalibration',
                                       --  'promotion_health', 'stale_check'
    source_id TEXT,                    -- writeback task_id / command_id / canary_check_id
    target_type TEXT NOT NULL,         -- 'correlation_key', 'routine_hash', 'candidate', 'threshold'
    target_id TEXT NOT NULL,           -- the specific entity being affected
    payload_json TEXT,                 -- source-specific data (see payload schemas below)
    directive_type TEXT NOT NULL,      -- 'confidence_adjust', 'promote', 'demote', 'prune',
                                       --  'recalibrate', 're_learn', 'threshold_adjust',
                                       --  'suspend_promotion', 'escalate_stale'
    directive_json TEXT,               -- what was applied (delta, new value, scope, etc.)
    applied_at TEXT,                   -- null if pending, ISO timestamp if processed
    applied_by TEXT,                   -- 'inline' or 'batch'
    causal_chain_json TEXT,            -- JSON array of feedback_event IDs that caused this event
    created_at TEXT NOT NULL           -- ISO timestamp of event creation
);
CREATE INDEX IF NOT EXISTS idx_fe_pending ON feedback_events(session_id, applied_at)
    WHERE applied_at IS NULL;
CREATE INDEX IF NOT EXISTS idx_fe_target ON feedback_events(session_id, target_type, target_id);
CREATE INDEX IF NOT EXISTS idx_fe_type ON feedback_events(session_id, directive_type);
CREATE INDEX IF NOT EXISTS idx_fe_source_decay ON feedback_events(session_id, target_id, source, created_at)
    WHERE source = 'decay';
```

### Causal Chain

Forward-only. Each event's `causal_chain_json` references the IDs of events that caused it. Old events are never updated.

Walking the chain forward: "Which events were caused by event X?" → `SELECT * FROM feedback_events WHERE causal_chain_json LIKE '%X%'` (or JSON array contains). This is a read query, not a write.

Example chain:
1. Event #101: writeback failure on `a1b2:btnComplete:xyz789` → confidence 0.82 → 0.67
2. Event #102: writeback failure on same key → confidence 0.67 → 0.52, `causal_chain: [101]`
3. Event #103: writeback failure → confidence 0.52 → 0.37, `causal_chain: [101, 102]`
4. Event #104: writeback failure → confidence 0.37 → 0.22, `causal_chain: [101, 102, 103]`
5. Event #105: writeback failure → confidence 0.22 → 0.12, prune triggered, `causal_chain: [101, 102, 103, 104]`

Operator asks "why did the agent stop automating path Y?" → read event #105's causal chain → five consecutive failures.

### Payload Schemas

**Writeback outcome (`source = 'writeback'`)** — required fields:

```json
{
  "taskId": "wb-001",
  "rxNumber": "12345",
  "outcome": "success",
  "correlationKey": "a1b2:btnComplete:xyz789",
  "uiEventTimestamp": "2026-04-27T14:30:00.123Z",
  "sqlExecutionTimestamp": "2026-04-27T14:30:01.456Z",
  "latencyMs": 1333,
  "previousConfidence": 0.82,
  "newConfidence": 0.87,
  "consecutiveFailures": 0,
  "writebackState": "Done",
  "retryCount": 0
}
```

`uiEventTimestamp` and `sqlExecutionTimestamp` are **required fields**. FeedbackCollector validates their presence before persisting. ProcessRecalibration reads them directly from `payload_json` to compute latency distributions.

**Operator command (`source = 'operator'`):**

```json
{
  "commandId": "cmd-abc123",
  "commandType": "approve_candidate",
  "operatorId": "op-josh",
  "correlationKey": "a1b2:btnComplete:xyz789"
}
```

**Canary drift (`source = 'canary'`):**

```json
{
  "canaryCheckId": "chk-20260427",
  "severity": "critical",
  "affectedTables": ["Prescription.RxTransaction"],
  "driftDetails": "column RxTransactionStatusTypeID renamed to StatusTypeID"
}
```

**Decay (`source = 'decay'`):**

```json
{
  "previousConfidence": 0.72,
  "newConfidence": 0.71,
  "daysSinceLastActivity": 8,
  "decayApplied": 0.01
}
```

## Directive Catalog

### Seven Core Directives

| Directive | Trigger | Effect | Reversible? |
|-----------|---------|--------|------------|
| `confidence_adjust` | Writeback outcome (success/failure/mismatch) or decay | Adjust `correlated_actions.confidence` by delta | Yes — subsequent outcomes shift it back |
| `promote` | Operator approves writeback candidate via dashboard | Mark candidate as `operator_approved`, bypass confidence threshold for activation | Yes — operator can revoke |
| `demote` | Operator rejects writeback candidate | Set confidence to 0.0, add `operator_rejected` flag. Agent never re-promotes automatically — only operator can lift | Yes — operator re-approves |
| `prune` | Confidence drops below floor (0.1) after failure | Remove correlation key from writeback candidate set. Routine keeps running but loses writeback flag | No — correlation must be re-discovered from scratch via ActionCorrelator |
| `recalibrate` | Statistical analysis of writeback outcome latencies | Adjust ActionCorrelator's correlation window per-screen or per-element based on observed latency distribution | Yes — recalibrates on every batch tick |
| `re_learn` | Schema canary critical drift, or operator triggers via dashboard | Reset learning for affected scope (specific table/routine, not entire session). Affected correlations frozen as `stale` | Partially — old correlations preserved as `stale`, new ones can replace them |
| `threshold_adjust` | Accumulated outcome statistics | Adjust RoutineDetector's min_frequency or confidence thresholds per-pharmacy based on actual workflow cadence | Yes — re-derived each batch tick |

### Two Safety Directives

| Directive | Trigger | Effect |
|-----------|---------|--------|
| `suspend_promotion` | 5 consecutive failures on a promoted candidate | Set `promotion_suspended = true`, notify operator via heartbeat. Agent stops writeback attempts until operator re-approves | 
| `escalate_stale` | Stale correlation exceeds 14-day TTL with no replacement | Add to `staleEscalations` in health payload. Operator must acknowledge or force wider re-learn |

### Confidence Adjustment Rules (Inline)

WritebackProcessor applies these synchronously via FeedbackCollector:

| Outcome | Delta | Rationale |
|---------|-------|-----------|
| `success` | +0.05 | Clean success, confirmed correlation |
| `already_at_target` | +0.02 | Mild positive — idempotent success |
| `verified_with_drift` | +0.03 | Success but timing was off |
| `post_verify_mismatch` | -0.10 | Wrote but verification failed — correlation may be wrong |
| `status_conflict` | -0.15 | Wrong precondition — correlation may be stale |
| `sql_error` | -0.05 | Transient infrastructure issue, don't punish hard |
| `connection_reset` | 0.00 | Infrastructure, not correlation quality |
| `trigger_blocked` | -0.08 | Business rule prevented it — correlation may target wrong state |

**Floor:** 0.1. Below this, the next failure triggers a `prune` directive.

**Ceiling:** 0.95. Never reaches 1.0 — always room for a single failure to reduce without requiring many successes to recover.

### Confidence Decay

If a correlation key has zero writeback attempts for 7+ days, confidence decays at a flat rate:

```
new_confidence = max(0.5, current_confidence - 0.01)
```

Applied once per day per correlation. ProcessDecay checks:

```sql
SELECT 1 FROM feedback_events
WHERE target_id = @key
  AND directive_type = 'confidence_adjust'
  AND source = 'decay'
  AND created_at >= @today_start
```

If a decay event already exists for today, skip. This caps decay at `idle_correlations × 1/day`.

**Decay floor: 0.5.** A correlation that was once strong doesn't get pruned by time alone, only by active failure. Decay signals "haven't verified this recently" not "this is wrong."

**No double-counting.** The formula uses flat -0.01 per daily application, not `0.01 * days_idle`. Each day's decay is independent of prior days. Over 40 idle days: 0.9 → 0.5 linearly.

## Promoted Candidate Auto-Suspend

Operator-approved candidates bypass the confidence threshold — they execute writebacks even at low confidence. This creates a risk: if the correlation becomes stale (PMS update, workflow change), the agent hammers a broken writeback indefinitely.

**Auto-suspend rule:**
- Tracked via `consecutive_failures` column on `correlated_actions`
- Reset to 0 on any successful writeback outcome
- Incremented on any failure outcome (sql_error, status_conflict, post_verify_mismatch, trigger_blocked)
- When `consecutive_failures` reaches 5 on a candidate where `operator_approved = true`:
  1. Set `promotion_suspended = true`
  2. Emit `suspend_promotion` feedback event
  3. WritebackProcessor skips candidates where `promotion_suspended = true`
  4. Operator notified via `suspendedPromotions` array in health payload
- Operator can re-approve via `reapprove_candidate` signed command, which clears `promotion_suspended` and resets `consecutive_failures`

The agent doesn't blindly trust a stale approval contradicted by live failures.

## Recalibration with Sample Floor

FeedbackProcessor.ProcessRecalibration computes per-correlation latency statistics to tune the ActionCorrelator's correlation window.

**Minimum sample size: 20.** A correlation with fewer than 20 writeback observations in the 7-day analysis window does not get recalibrated. Below 20 samples, the p50/p95 is not statistically meaningful. The correlation keeps its current window or the global default (2s calibrated / 5s uncalibrated).

**Algorithm:**
1. For each correlation key with writeback activity in the last 7 days:
   - Count writeback observations in the window
   - If count < 20 → skip
   - If count >= 20 → extract `uiEventTimestamp` and `sqlExecutionTimestamp` from `payload_json` of writeback outcome events
   - Compute p50 and p95 of `(sqlExecutionTimestamp - uiEventTimestamp)`
   - If p95 > current correlation window → widen window to p95 × 1.2 (20% headroom)
   - If p50 < 50% of current window → tighten window to p50 × 2.0 (keeps p95 within range)
   - Emit `recalibrate` directive
2. Upsert `correlation_window_overrides` table

**ActionCorrelator integration:** On each correlation attempt, ActionCorrelator checks `correlation_window_overrides` for a per-key override. Falls back to global 2s/5s default when no override exists.

### `correlation_window_overrides` Table

```sql
CREATE TABLE IF NOT EXISTS correlation_window_overrides (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id TEXT NOT NULL,
    tree_hash TEXT NOT NULL,
    element_id TEXT NOT NULL,
    window_seconds REAL NOT NULL,
    sample_count INTEGER NOT NULL,
    computed_at TEXT NOT NULL,
    UNIQUE(session_id, tree_hash, element_id)
);
```

## Surgical Re-Learning (Canary Drift)

When SchemaCanaryEscalation transitions to `drift_hold`:

1. FeedbackProcessor.ProcessCanaryDrift queries which tables are affected by the drift
2. For each `correlated_action` referencing an affected table (via `tables_referenced` JSON):
   - Set `stale = true`, `stale_since = now`
   - Emit `re_learn` directive scoped to that table
3. WritebackProcessor skips correlations where `stale = true`
4. ActionCorrelator continues running — new correlations for the affected tables can form from fresh observations

**Scope is surgical.** If canary detects a column rename in `Prescription.RxTransaction`, only correlations touching that table get stale-flagged. Correlations on `Inventory.ItemPricing` are untouched.

### Stale TTL & Escalation

Stale correlations have a 14-day TTL:

1. FeedbackProcessor.ProcessStaleEscalation queries `correlated_actions WHERE stale = true AND stale_since < (now - 14 days)`
2. For each expired stale correlation:
   - Check if a replacement exists (same `tree_hash + element_id`, different `query_shape_hash`, `stale = false`)
   - If replacement exists → delete the stale row (superseded)
   - If no replacement → emit `escalate_stale` feedback event, add to `staleEscalations` in health payload
3. Operator sees: "Canary drift on `Prescription.RxTransaction` — re-learning has not produced a replacement in 14 days. Manual review required."
4. Operator can: acknowledge (prune the stale correlation via `acknowledge_stale` command), or force re-learn with wider scope

Without a TTL, stale correlations silently accumulate as dead weight.

## Data Model Changes

### New Tables

**`feedback_events`** — see schema above.

**`correlation_window_overrides`** — see schema above.

### Column Additions to `correlated_actions`

Added via `TryAlter` migration pattern (existing pattern in AgentStateDb):

```sql
ALTER TABLE correlated_actions ADD COLUMN operator_approved INTEGER DEFAULT 0;
ALTER TABLE correlated_actions ADD COLUMN operator_rejected INTEGER DEFAULT 0;
ALTER TABLE correlated_actions ADD COLUMN promotion_suspended INTEGER DEFAULT 0;
ALTER TABLE correlated_actions ADD COLUMN consecutive_failures INTEGER DEFAULT 0;
ALTER TABLE correlated_actions ADD COLUMN stale INTEGER DEFAULT 0;
ALTER TABLE correlated_actions ADD COLUMN stale_since TEXT;
```

## Components

### FeedbackCollector (Static Utility)

Lives in `SuavoAgent.Core.Behavioral`. No state, no lifecycle. Called inline by WritebackProcessor.

```
FeedbackCollector
├── RecordWritebackOutcome(sessionId, taskId, correlationKey, outcome,
│                          uiEventTimestamp, sqlExecutionTimestamp)
│   ├── Compute confidence delta from outcome lookup table
│   ├── Build FeedbackEvent with directive_type = confidence_adjust
│   ├── Check consecutive failure count on correlation key
│   ├── If promoted + 5 consecutive failures → also emit suspend_promotion event
│   ├── If confidence drops below 0.1 floor → also emit prune event
│   ├── Write event(s) AND apply directive(s) in single SQLCipher transaction
│   └── Return new confidence value
└── ApplyDirective(db, event)  — shared idempotent applicator
```

**Validation:** `RecordWritebackOutcome` validates that `uiEventTimestamp` and `sqlExecutionTimestamp` are non-null before persisting. Missing timestamps → log warning, skip recalibration-relevant fields but still record the outcome.

### FeedbackProcessor (Batch Consumer)

Lives in `SuavoAgent.Core.Behavioral`. Stateless — constructed fresh each LearningWorker tick.

```
FeedbackProcessor
├── ProcessPendingFeedback()            — main entry point
│   ├── ProcessOperatorDirectives()     — signed commands → feedback_events → apply
│   ├── ProcessCanaryDrift()            — check canary state → surgical re_learn
│   ├── ProcessDecay()                  — daily -0.01 on idle correlations
│   ├── ProcessRecalibration()          — latency stats → window tuning (≥20 samples)
│   ├── ProcessPromotionHealth()        — check for suspended promotions needing escalation
│   └── ProcessStaleEscalation()        — 14-day TTL check → operator notification
└── ApplyDirective(db, event)           — same shared applicator as FeedbackCollector
```

### ApplyDirective (Shared Idempotent Applicator)

Single static method used by both inline and batch paths. Takes a `FeedbackEvent` with populated `directive_type` and `directive_json`:

1. If `applied_at` is already set → return (no-op, idempotent)
2. Switch on `directive_type`:
   - `confidence_adjust` → update `correlated_actions.confidence` for target key
   - `promote` → set `operator_approved = true`, clear `promotion_suspended`, reset `consecutive_failures`
   - `demote` → set `operator_rejected = true`, `confidence = 0.0`
   - `prune` → remove writeback candidate flag from correlation and associated routines
   - `recalibrate` → upsert `correlation_window_overrides`
   - `re_learn` → set `stale = true`, `stale_since = now` on affected correlations
   - `threshold_adjust` → update per-pharmacy threshold overrides
   - `suspend_promotion` → set `promotion_suspended = true` on target correlation
   - `escalate_stale` → no mutation (health payload picks it up from the event log)
3. Set `applied_at = now`, `applied_by = 'inline'|'batch'`

All mutations are idempotent. Applying `confidence_adjust` with `new_confidence = 0.87` sets confidence to 0.87 regardless of current value. The directive stores the computed new value, not a relative delta.

## Signed Command Extensions

New operator commands via existing SignedCommandVerifier path:

| Command | Payload | Directive |
|---------|---------|-----------|
| `approve_candidate` | `{correlationKey}` | `promote` |
| `reject_candidate` | `{correlationKey}` | `demote` |
| `reapprove_candidate` | `{correlationKey}` | `promote` (clears suspension) |
| `force_relearn` | `{scope: "table"\|"routine"\|"session", target: "..."}` | `re_learn` |
| `adjust_window` | `{elementId, treeHash, windowSeconds}` | `recalibrate` (manual override) |
| `acknowledge_stale` | `{correlationKey}` | `prune` |

All commands carry a `command_id` in `source_id` for audit linkage.

## LearningWorker Integration

The 5-minute tick gains one new call after RoutineDetector:

```csharp
// Feedback processing (batch)
if (session.Phase is "pattern" or "model" or "approved" or "active")
{
    var feedbackProcessor = new FeedbackProcessor(_db, _sessionId, _actionCorrelator);
    feedbackProcessor.ProcessPendingFeedback();
}
```

Phase scope: feedback processing runs from `pattern` through `active`. During `pattern` and `model` there are no writeback outcomes (no writebacks happening), but decay, canary drift, and operator corrections still apply.

## POM Export Extension

PomExporter gains a `feedback` section:

```json
{
  "feedback": {
    "totalFeedbackEvents": 847,
    "observationWindow": {
      "firstEvent": "2026-04-15",
      "lastEvent": "2026-04-27",
      "days": 12
    },
    "confidenceTrajectory": [
      {
        "correlationKey": "a1b2:btnComplete:xyz789",
        "initialConfidence": 0.3,
        "currentConfidence": 0.87,
        "writebackAttempts": 47,
        "successRate": 0.94,
        "lastOutcome": "success",
        "operatorApproved": true,
        "promotionSuspended": false
      }
    ],
    "prunedCorrelations": [
      {
        "correlationKey": "c3d4:btnRefill:abc123",
        "prunedAt": "2026-04-22",
        "reason": "confidence_floor",
        "causalEventCount": 8
      }
    ],
    "windowOverrides": [
      {
        "treeHash": "a1b2c3...",
        "elementId": "btnComplete",
        "windowSeconds": 1.4,
        "sampleCount": 34,
        "computedAt": "2026-04-27"
      }
    ],
    "staleCorrelations": 0,
    "decayActive": true,
    "avgSuccessRate7d": 0.91
  }
}
```

**What's exported:** Confidence trajectories (how each correlation evolved), pruned correlations with reason codes, active window overrides, aggregate success rates. All PHI-free.

**What's NOT exported:** Raw feedback event payloads (contain timestamps that could fingerprint workflow timing patterns), causal chain JSON (internal debugging), individual writeback task IDs, rxNumber (stays in local SQLCipher).

**Spec D hook:** The `confidenceTrajectory` array is what Spec D's collective intelligence needs. If pharmacy A's correlation stabilized at 0.87 after 47 attempts, that seeds pharmacy B's starting confidence above the default 0.3.

## HealthSnapshot Extension

HealthSnapshot gains a `feedback` section:

```json
{
  "feedback": {
    "totalEvents": 847,
    "pendingDirectives": 0,
    "appliedInline": 812,
    "appliedBatch": 35,
    "prunedCorrelations": 3,
    "suspendedPromotions": ["a1b2:btnComplete:xyz789"],
    "staleEscalations": [],
    "lastRecalibrationAt": "2026-04-27T14:30:00Z",
    "avgConfidenceDelta7d": -0.02,
    "activeOverrides": 4
  }
}
```

## Integration with Existing Architecture

### What stays the same:
- Three-process model (Broker/Core/Helper)
- Heartbeat + signed commands
- Audit chain
- SQLCipher encryption
- Self-update mechanism
- IPC framing protocol
- All Spec B components (observers, correlator, routine detector)
- WritebackStateMachine states and transitions
- LearningWorker phase management

### What changes:
- **WritebackProcessor** gains FeedbackCollector.RecordWritebackOutcome call after each outcome
- **Core** gains FeedbackCollector (static), FeedbackProcessor (batch consumer)
- **AgentStateDb** gains `feedback_events` table, `correlation_window_overrides` table, 6 new columns on `correlated_actions`
- **ActionCorrelator** reads per-key window overrides from `correlation_window_overrides`
- **HealthSnapshot** gains feedback telemetry section
- **PomExporter** gains feedback export section
- **LearningWorker** calls FeedbackProcessor.ProcessPendingFeedback on 5-minute tick
- **SignedCommandVerifier/IpcPipeServer** gains 6 new operator command types

### WritebackProcessor changes are minimal:
The only code change in WritebackProcessor is a single call after `MapResultToStateMachine`:

```csharp
FeedbackCollector.RecordWritebackOutcome(
    _sessionId, taskId, correlationKey, result.Outcome,
    uiEventTimestamp, sqlExecutionTimestamp);
```

The correlation key and timestamps come from the writeback task's metadata (resolved during EnqueueWriteback). WritebackProcessor doesn't need to understand feedback logic — FeedbackCollector encapsulates all of it.

## Success Criteria

| # | Criterion | Measurement |
|---|-----------|-------------|
| 1 | Writeback success/failure adjusts correlation confidence within the same transaction | Unit test: confidence changes atomically with outcome recording |
| 2 | 5 consecutive failures on a promoted candidate triggers auto-suspension | Unit test: mock 5 failures, verify `promotion_suspended = true` and feedback event emitted |
| 3 | Operator approve/reject/reapprove commands produce correct directives | Unit test: each signed command → correct directive type and target mutation |
| 4 | Canary drift triggers surgical re-learn scoped to affected tables only | Integration test: simulate drift on one table, verify only its correlations get `stale = true` |
| 5 | Stale correlations escalate after 14-day TTL with no replacement | Unit test: set `stale_since` to 15 days ago, run ProcessStaleEscalation, verify escalation event |
| 6 | Recalibration only fires with >= 20 samples in 7-day window | Unit test: 19 samples → no recalibrate directive; 20 → directive emitted |
| 7 | Decay emits max 1 event per correlation per day | Unit test: run ProcessDecay twice in same day for same key, verify single event |
| 8 | Decay is flat -0.01 per day, no double-counting | Unit test: confidence 0.90, decay day 1 → 0.89, decay day 2 → 0.88 (not 0.88 → 0.86) |
| 9 | Confidence floor (0.1) triggers prune directive | Unit test: push confidence to 0.11, apply one more failure, verify prune |
| 10 | Confidence ceiling (0.95) caps upward adjustment | Unit test: confidence at 0.93, apply success (+0.05), verify result is 0.95 not 0.98 |
| 11 | Decay stops at 0.5 | Unit test: confidence at 0.51, apply decay → 0.50; at 0.50 → no change |
| 12 | Causal chain is forward-only — no retroactive mutation | Unit test: create event A, then event B referencing A. Verify A's row unchanged after B is created |
| 13 | ApplyDirective is idempotent | Unit test: apply same directive twice, verify target state identical and no duplicate mutation |
| 14 | Replay produces identical end state | Integration test: insert N feedback events, apply them, record end state. Reset `correlated_actions` to original values, replay same events via batch path, verify identical end state |
| 15 | POM export includes feedback section with confidence trajectories | Unit test: seed feedback data, export POM, verify feedback section shape |
| 16 | Health payload includes suspended promotions and stale escalations | Unit test: create suspended promotion, take health snapshot, verify it appears |
| 17 | `feedback_events` table stays bounded — < 500 events/day for a typical pharmacy | Calculated: ~100 writeback outcomes + ~100 decay + ~5 operator + ~5 recalibrate + ~1 canary = ~211/day |

## Implementation Order

1. `FeedbackEvent` types + `feedback_events` schema migration
2. `ApplyDirective` (shared idempotent applicator)
3. `FeedbackCollector` (static, inline path) + writeback outcome recording
4. Inline integration into WritebackProcessor
5. `correlated_actions` column migrations (operator_approved, operator_rejected, promotion_suspended, consecutive_failures, stale, stale_since)
6. `correlation_window_overrides` table migration
7. `FeedbackProcessor` shell + ProcessDecay (simplest batch directive, validates daily cap)
8. ProcessOperatorDirectives + signed command extensions
9. ProcessCanaryDrift + surgical re-learn
10. ProcessRecalibration + sample floor + ActionCorrelator override reads
11. ProcessPromotionHealth (auto-suspend on promoted candidates)
12. ProcessStaleEscalation (14-day TTL)
13. POM export feedback section
14. HealthSnapshot feedback section
15. LearningWorker integration (wire FeedbackProcessor into 5-minute tick)
