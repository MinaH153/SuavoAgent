# SuavoAgent PioneerRx Writeback — Design Spec

**Date:** 2026-04-13
**Author:** Joshua Henein + Claude + Codex (2 adversarial review rounds)
**Repo:** github.com/MinaH153/SuavoAgent
**Branch:** main
**Status:** Approved design, pending implementation
**Review:** 10 findings from Codex review incorporated. All CRITICAL items resolved.
**Depends on:** Schema Canary (2026-04-13-schema-canary-design.md) — status GUID discovery, trigger detection patterns

---

## Problem

When a Suavo driver picks up prescriptions from a pharmacy or delivers them to a patient, the Rx status in PioneerRx must be updated. Today this is manual — the pharmacist changes the status by hand. This creates delays, errors, and friction. The `WritebackProcessor` exists but is stubbed ("Would send writeback").

**Fleet operator need:** One-tap dispatch in the dashboard triggers automatic PioneerRx status updates. No pharmacist intervention for routine deliveries.

## Solution

Direct SQL UPDATE on `Prescription.RxTransaction.RxTransactionStatusTypeID` with full safety rails: optimistic concurrency, pre/post verification, signed command authorization, supervised mode, trigger detection, per-RxNumber serialization, and idempotent crash recovery.

**Phase 1 (this spec):** Simple SQL UPDATE with safety rails. Immediate value.
**Phase 2 (separate spec):** Learned writeback from DMV observation during 30-day learning. Replicates PioneerRx's full write behavior.
**Phase 3 (separate spec):** UIA automation fallback for pharmacies where SQL writes are too risky.

## Constraints

- Read-write access to PMS database authorized by pharmacy. Pharmacy signs authorization.
- Status column is the ONLY column modified for pickup transition. Four columns for completion (status + CompletedDate + BinLocationID + BagID).
- Payment/POS columns NEVER touched via SQL. Delivery evidence stored in Suavo cloud.
- Every writeback requires ECDSA-signed command (same infrastructure as fetch_patient, approve_pom).
- Controlled substance deliveries: status change proceeds, but pharmacist gets POS entry alert.
- Must not deadlock or interfere with the detection polling connection.
- Invisible to PioneerRx — same ApplicationName masquerade pattern as reads.

---

## 1. Writeback Scope & Mechanism

### Two Transitions

| Event | From Status | To Status | Columns Modified |
|-------|------------|-----------|-----------------|
| Driver picks up Rx | "Waiting for Pick up" / "Waiting for Delivery" / "To Be Put in Bin" | "Out for Delivery" | `RxTransactionStatusTypeID` only |
| Driver delivers to patient | "Out for Delivery" | "Completed" | `RxTransactionStatusTypeID`, `CompletedDate`, `BinLocationID=NULL`, `BagID=NULL` |

### Status GUID Discovery — Mandatory for Writes

Extend existing `DiscoverStatusGuidsAsync` to discover ALL 5 delivery-related GUIDs at connect time:

| Status | Current Discovery | Write Requirement |
|--------|------------------|-------------------|
| Waiting for Pick up | Yes (DeliveryReadyStatusNames) | Source status for pickup |
| Waiting for Delivery | Yes | Source status for pickup |
| To Be Put in Bin | Yes | Source status for pickup |
| Out for Delivery | **No — must add** | Target for pickup, source for complete |
| Completed | **No — must add** | Target for complete |

Writeback engine REFUSES to initialize if "Out for Delivery" or "Completed" GUIDs are not discovered. Fallback GUIDs are for reads only — never used for writes.

### RxNumber → RxTransactionID Resolution

The writeback command carries `RxNumber` (int) and `FillNumber` (int). Resolution query is transition-specific:

**Pickup resolution:**
```sql
SELECT TOP 1 rt.RxTransactionID, rt.RxTransactionStatusTypeID
FROM Prescription.RxTransaction rt
JOIN Prescription.Rx r ON rt.RxID = r.RxID
WHERE r.RxNumber = @rxNumber
  AND rt.RefillNumber = @fillNumber
  AND rt.RxTransactionStatusTypeID IN (@readyGuid1, @readyGuid2, @readyGuid3)
```

**Complete resolution:**
```sql
SELECT TOP 1 rt.RxTransactionID, rt.RxTransactionStatusTypeID
FROM Prescription.RxTransaction rt
JOIN Prescription.Rx r ON rt.RxID = r.RxID
WHERE r.RxNumber = @rxNumber
  AND rt.RefillNumber = @fillNumber
  AND rt.RxTransactionStatusTypeID = @outForDeliveryGuid
```

If 0 rows → Rx not in expected state → abort. If >1 row → ambiguous → ManualReview.

### SQL Execution — Pickup

```sql
SET NOCOUNT ON;
-- Pre-verify
SELECT RxTransactionStatusTypeID
FROM Prescription.RxTransaction
WHERE RxTransactionID = @txId;

-- UPDATE with optimistic concurrency
UPDATE Prescription.RxTransaction
SET RxTransactionStatusTypeID = @outForDeliveryGuid
WHERE RxTransactionID = @txId
  AND RxTransactionStatusTypeID = @currentStatusGuid;
SELECT @@ROWCOUNT AS RowsAffected;

-- Post-verify
SELECT RxTransactionStatusTypeID
FROM Prescription.RxTransaction
WHERE RxTransactionID = @txId;
```

All statements in a single `SqlTransaction` at `READ COMMITTED`. `@@ROWCOUNT` captured via `SELECT` in same batch (NOCOUNT hides it from `ExecuteNonQuery` return value).

### SQL Execution — Complete

```sql
SET NOCOUNT ON;
UPDATE Prescription.RxTransaction
SET RxTransactionStatusTypeID = @completedGuid,
    CompletedDate = @deliveredAt,
    BinLocationID = NULL,
    BagID = NULL
WHERE RxTransactionID = @txId
  AND RxTransactionStatusTypeID = @outForDeliveryGuid;
SELECT @@ROWCOUNT AS RowsAffected;
```

Post-verify checks both `RxTransactionStatusTypeID = @completedGuid` AND `CompletedDate = @deliveredAt` (exact match, not just IS NOT NULL — detects trigger interference).

### Payment Columns NOT Touched

`PatientPaidAmount`, `TotalPricePaid`, `IngredientCostPaid` are NOT modified via SQL. The delivery evidence (recipient ID, signature SVG, counseling status, price, tax) from `DeliveryWritebackCommand` is stored in Suavo's cloud database for billing and compliance. PioneerRx's POS record for deliveries is intentionally incomplete — the pharmacist reconciles via their normal workflow.

### Controlled Substance Handling

If the Rx is DEA Schedule II-V (determined from `Inventory.Item.DeaSchedule`):
- Status change proceeds normally
- Writeback receipt includes `posEntryRequired: true`
- Cloud creates dashboard alert: "Rx #12345 (Schedule II) delivered — POS entry required in PioneerRx"
- Pharmacist must complete POS entry separately (recipient ID verification, signature, counseling are federal requirements for controlled substances that cannot be satisfied via SQL)

---

## 2. Command Flow & Authorization

### Signed Command: delivery_writeback

```json
{
  "command": "delivery_writeback",
  "data": {
    "transition": "pickup | complete",
    "rxNumber": 12345,
    "fillNumber": 1,
    "taskId": "delivery-task-uuid",
    "deliveredAt": "2026-04-13T14:30:00Z",
    "isControlledSubstance": false
  }
}
```

ECDSA P-256 signed. Canonical: `{command}|{agentId}|{fingerprint}|{timestamp}|{nonce}|{dataHash}`. Same verification as all existing signed commands.

### Supervised Mode

First 50 successful writebacks require fleet operator approval:

1. Cloud creates `pending_writeback_approval` in dashboard
2. Operator sees: "Rx #12345 — driver picked up. Write 'Out for Delivery' to PioneerRx?"
3. Approve → cloud signs command → delivered via next heartbeat
4. Reject → writeback skipped

Promotion to autonomous after 50 successes with <2% failure rate. Autonomous: cloud auto-signs pickup writebacks on driver confirmation. Complete writebacks still require driver delivery proof (photo, signature).

### Flow Diagram

```
Driver event (pickup/delivery)
  │
  ▼
Suavo Cloud → delivery_tasks.status updated
  │
  ▼
Supervised: dashboard approval gate ─── or ─── Autonomous: auto-sign
  │                                              │
  ▼                                              ▼
Cloud signs delivery_writeback command
  │
  ▼
Stored in agent_pending_commands
  │
  ▼
Next heartbeat (≤30s) → agent receives
  │
  ▼
ECDSA verify + nonce check + audit log
  │
  ▼
WritebackProcessor.EnqueueWriteback()
  │
  ▼
State machine: Queued → Claimed → InProgress → VerifyPending → Verified → Done
  │                                                                        │
  ▼ (failures)                                                             ▼
  SystemError (retry 3x) or BusinessError → ManualReview              Cloud notified
```

---

## 3. SQL Execution Engine & Safety Rails

### PioneerRxWritebackEngine — Separate from PioneerRxSqlEngine

Read path and write path are completely separate:
- Different class (single responsibility)
- Different connection pool (`ApplicationName = "SuavoWriteback"` — prevents MaxPoolSize=1 deadlock with the read engine's persistent connection)
- Different audit trail
- Same SQL credentials, same server

### Trigger Detection (cached per batch, 5-min TTL)

```sql
SELECT t.name, t.is_disabled,
       CASE WHEN t.is_instead_of_trigger = 1 THEN 'INSTEAD_OF' ELSE 'AFTER' END AS trigger_type
FROM sys.triggers t
JOIN sys.objects o ON t.parent_id = o.object_id
JOIN sys.schemas s ON o.schema_id = s.schema_id
WHERE s.name = 'Prescription' AND o.name = 'RxTransaction' AND t.type = 'TR'
```

| Trigger Type | Response |
|-------------|----------|
| `is_disabled = 1` | Log info, proceed |
| `INSTEAD_OF UPDATE` | **Hard block** — trigger replaces our UPDATE. ManualReview. |
| `AFTER UPDATE` | Warning — allow supervised-only writes. Autonomous blocked until operator acknowledges. |
| No triggers | Proceed normally |

### Connection Management

Fresh logical connection per writeback operation. `MaxPoolSize=1` on the writeback pool serializes writes naturally. ADO.NET reuses the physical TCP connection via pooling — no extra handshake overhead.

Connection guard: snapshot `ClientConnectionId` before pre-verify. Compare after pre-verify, before UPDATE. If different → connection reset mid-operation → abort cycle.

### WritebackResult

```csharp
public record WritebackResult(
    bool Success,
    string Outcome,
    Guid? TransactionId,
    string? Details,
    bool IsReplay = false);
```

Eight outcome types:

| Outcome | Meaning | State Machine Trigger | Terminal State |
|---------|---------|----------------------|----------------|
| `success` | UPDATE committed, post-verify matches | WriteComplete → VerifyMatch → SyncComplete | Done |
| `already_at_target` | Pre-verify: status already at target GUID | SyncComplete | Done (replay flagged) |
| `verified_with_drift` | Status correct but CompletedDate differs (trigger interference) | WriteComplete → VerifyMatch → SyncComplete | Done (drift logged) |
| `status_conflict` | Pre-verify: status is unexpected (Cancelled, etc.) | BusinessError | ManualReview |
| `connection_reset` | ClientConnectionId changed mid-operation | SystemError | Queued (retry) |
| `post_verify_mismatch` | Post-verify: status GUID doesn't match target | VerifyMismatch | InProgress (retry, max 3 → ManualReview) |
| `sql_error` | SqlException during transaction | SystemError | Queued (retry, max 3 → ManualReview) |
| `trigger_blocked` | INSTEAD_OF trigger detected on table | BusinessError | ManualReview |

### Per-RxNumber Serialization

Before processing a writeback, check if another writeback for the same RxNumber is in a non-terminal state. If so, skip this cycle. Prevents race between pickup and complete commands for the same Rx arriving in the same heartbeat.

### Audit Trail

Each writeback attempt generates a chained audit entry:
- Event type: `writeback_sql_executed`
- Fields: command type, outcome, rowcount, duration_ms, trigger status
- RxNumber: HMAC-hashed with per-session `hmac_salt` (never raw — linkable to patient in context)
- `ExportAuditArchiveJson()` strips raw RxNumbers before export

---

## 4. Integration Points

### HeartbeatWorker

New case in `ProcessSignedCommandAsync`:
```csharp
case "delivery_writeback":
    await HandleDeliveryWritebackAsync(scEl, cmd, ct);
    break;
```

### WritebackProcessor

Replace stub with real writeback engine calls:
- Receive `WritebackResult` from engine
- Map outcome to state machine trigger
- Per-RxNumber guard before processing
- Report results in heartbeat telemetry

### RxDetectionWorker

After SQL connects, create writeback engine:
- Build separate connection string with `ApplicationName = "SuavoWriteback"`
- Pass discovered status GUIDs (all 5)
- Run trigger detection
- New `ReadInTransitAsync` method for "Out for Delivery" tracking

### PioneerRxSqlEngine

Extend `DiscoverStatusGuidsAsync`:
- Add "Out for Delivery" and "Completed" to status description list
- New `GetAllDiscoveredGuids()` returning `Dictionary<string, Guid>`

### HealthSnapshot + Heartbeat

Add writeback metrics:
```json
{
  "writeback": {
    "pending": 3,
    "inTransit": 12,
    "completedToday": 8,
    "failedToday": 0,
    "manualReviewCount": 1,
    "triggerDetected": false,
    "writebackEnabled": true
  }
}
```

---

## 5. In-Transit Tracking

After pickup writeback changes status to "Out for Delivery", the Rx leaves the detection query results ("Waiting for Pick up" filter). A separate query tracks in-transit Rxs:

```sql
SELECT r.RxNumber, rt.RxTransactionID, rt.DateFilled, rt.DispensedQuantity
FROM Prescription.RxTransaction rt
JOIN Prescription.Rx r ON rt.RxID = r.RxID
WHERE rt.RxTransactionStatusTypeID = @outForDeliveryGuid
ORDER BY rt.DateFilled DESC
```

Reported in heartbeat as `writeback.inTransit`. Dashboard shows two queues:
- **Ready for Dispatch** — delivery-ready statuses (existing detection)
- **In Transit** — "Out for Delivery" status (new tracking)

Rxs stuck in "Out for Delivery" for >4 hours get flagged in the dashboard.

---

## 6. Testing Strategy — 34 Tests

### Writeback Engine Tests (14)

1. Pickup: delivery-ready → Out for Delivery → success
2. Complete: Out for Delivery → Completed + CompletedDate set → success
3. Pre-verify: status already at target → AlreadyAtTarget (idempotent)
4. Pre-verify: status is unexpected (Cancelled) → StatusConflict
5. UPDATE returns 0 rows (concurrent modification) → StatusConflict
6. Post-verify: status mismatch (trigger changed it) → PostVerifyMismatch
7. Post-verify: CompletedDate differs from submitted → VerifiedWithDrift
8. Connection reset mid-transaction → ConnectionReset
9. SQL exception → SqlError
10. INSTEAD_OF trigger detected → TriggerBlocked
11. AFTER trigger detected → warning logged, supervised-only
12. Disabled trigger → proceeds normally
13. Resolution: RxNumber + FillNumber → correct RxTransactionID
14. Resolution: RxNumber not found → null (abort)

### State Machine Integration Tests (10)

15. Success → WriteComplete → VerifyMatch → SyncComplete → Done
16. AlreadyAtTarget → SyncComplete → Done (flagged replay)
17. VerifiedWithDrift → Done (drift logged)
18. StatusConflict → BusinessError → ManualReview
19. SqlError → SystemError → Queued → retry (3x max)
20. PostVerifyMismatch → VerifyMismatch → InProgress → retry (3x max → ManualReview)
21. TriggerBlocked → BusinessError → ManualReview
22. ConnectionReset → SystemError → Queued → retry
23. Per-RxNumber guard: same Rx in-progress → skip this cycle
24. Crash recovery: recovered from InProgress → re-execute → AlreadyAtTarget → Done

### Heartbeat + Command Tests (6)

25. Valid delivery_writeback signed command → accepted, enqueued
26. Pickup command → WritebackProcessor.EnqueueWriteback with transition="pickup"
27. Complete command with isControlledSubstance=true → posEntryRequired flag
28. Replayed nonce → rejected
29. Wrong agent → rejected
30. Heartbeat payload includes writeback metrics + in-transit count

### GUID Discovery Tests (4)

31. DiscoverStatusGuidsAsync returns all 5 GUIDs
32. Missing "Out for Delivery" in lookup table → writeback engine refuses to initialize
33. Missing "Completed" in lookup table → writeback engine refuses to initialize
34. GetAllDiscoveredGuids returns dictionary keyed by description

---

## Files Modified

### New Files
| File | Responsibility |
|------|---------------|
| `src/SuavoAgent.Adapters.PioneerRx/Sql/PioneerRxWritebackEngine.cs` | SQL execution: trigger detection, resolution, pickup/complete transactions, in-transit query |
| `src/SuavoAgent.Contracts/Writeback/WritebackResult.cs` | Result record with 8 outcome types |
| `tests/SuavoAgent.Core.Tests/Writeback/WritebackEngineTests.cs` | Engine tests (14) |
| `tests/SuavoAgent.Core.Tests/Writeback/WritebackStateMappingTests.cs` | State machine integration (10) |
| `tests/SuavoAgent.Core.Tests/Writeback/WritebackCommandTests.cs` | Heartbeat + command tests (6) |
| `tests/SuavoAgent.Core.Tests/Writeback/WritebackGuidDiscoveryTests.cs` | GUID discovery tests (4) |

### Modified Files
| File | Change |
|------|--------|
| `src/SuavoAgent.Core/Workers/HeartbeatWorker.cs` | Add `delivery_writeback` command handler |
| `src/SuavoAgent.Core/Workers/WritebackProcessor.cs` | Replace stub with real engine calls, per-RxNumber guard, result→trigger mapping |
| `src/SuavoAgent.Core/Workers/RxDetectionWorker.cs` | Create writeback engine after SQL connects, in-transit tracking |
| `src/SuavoAgent.Adapters.PioneerRx/Sql/PioneerRxSqlEngine.cs` | Extend GUID discovery to 5 statuses, GetAllDiscoveredGuids |
| `src/SuavoAgent.Core/HealthSnapshot.cs` | Add writeback metrics + in-transit count |
| `src/SuavoAgent.Core/State/AgentStateDb.cs` | HMAC-hash RxNumber in audit entries |
| `src/SuavoAgent.Adapters.PioneerRx/PioneerRxConstants.cs` | Add StatusOutForDelivery, StatusCompleted to discovery list |

### Cloud-Side Changes (separate implementation)
| File | Change |
|------|--------|
| `src/app/api/agent/heartbeat/route.ts` | Deliver pending writeback commands |
| `src/app/api/agent/delivery-writeback/route.ts` | Already exists — verify compatibility |
| Dashboard | Supervised approval UI, in-transit tracking, controlled substance alerts |

---

## Not In Scope

- UIA-based writeback (Phase 2 — requires behavioral learning spec)
- Learned writeback from DMV observation (Phase 2 — requires expanded learning spec)
- PioneerRx API integration (requires Connected Vendor enrollment)
- Cloud-side dashboard changes for supervised approval UI
- Batch writeback optimization (multiple Rxs in one transaction)
- Expanded behavioral learning (Spec B — separate design cycle)
- Self-improving feedback system (Spec C)
- Collective intelligence (Spec D)
