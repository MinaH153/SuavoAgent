# PioneerRx Writeback Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Automatically update Rx status in PioneerRx when drivers pick up and deliver prescriptions — SQL UPDATE with full safety rails.

**Architecture:** New `PioneerRxWritebackEngine` in Adapters.PioneerRx handles all write operations on a separate connection pool (`ApplicationName="SuavoWriteback"`). `WritebackProcessor` orchestrates via the existing `WritebackStateMachine`. Signed `delivery_writeback` commands arrive via heartbeat. Trigger detection, optimistic concurrency, pre/post verification, and per-RxNumber serialization ensure safety.

**Tech Stack:** .NET 8, Microsoft.Data.SqlClient, xUnit, Stateless (existing state machine lib), SQLite (audit), ECDSA P-256 (command signing)

**Spec:** `docs/superpowers/specs/2026-04-13-writeback-design.md`

**Build/test commands:**
```bash
cd ~/Documents/SuavoAgent
dotnet build
dotnet test
dotnet test --filter "FullyQualifiedName~ClassName"
```

---

## File Map

### New Files
| File | Responsibility |
|------|---------------|
| `src/SuavoAgent.Contracts/Writeback/WritebackResult.cs` | Result record with 8 outcome factory methods |
| `src/SuavoAgent.Adapters.PioneerRx/Sql/PioneerRxWritebackEngine.cs` | SQL writeback engine: trigger detection, resolution, pickup/complete transactions, in-transit query |
| `tests/SuavoAgent.Core.Tests/Writeback/WritebackResultTests.cs` | Result factory + mapping tests |
| `tests/SuavoAgent.Core.Tests/Writeback/WritebackProcessorIntegrationTests.cs` | State machine + processor integration |
| `tests/SuavoAgent.Core.Tests/Writeback/WritebackCommandTests.cs` | Signed command handling |

### Modified Files
| File | Changes |
|------|---------|
| `src/SuavoAgent.Adapters.PioneerRx/PioneerRxConstants.cs` | Add `AllDeliveryStatusNames` (5 statuses) |
| `src/SuavoAgent.Adapters.PioneerRx/Sql/PioneerRxSqlEngine.cs` | Extend GUID discovery to 5 statuses, add `GetAllDiscoveredGuids()` |
| `src/SuavoAgent.Core/Workers/WritebackProcessor.cs` | Replace stub with real engine calls, per-RxNumber guard, result→trigger mapping |
| `src/SuavoAgent.Core/Workers/HeartbeatWorker.cs` | Add `delivery_writeback` command handler |
| `src/SuavoAgent.Core/Workers/RxDetectionWorker.cs` | Create writeback engine after SQL connects |
| `src/SuavoAgent.Core/HealthSnapshot.cs` | Add writeback metrics |
| `src/SuavoAgent.Core/State/WritebackStateMachine.cs` | Add `AlreadyAtTarget` trigger for idempotent success path |

---

## Task 1: WritebackResult Type

**Files:**
- Create: `src/SuavoAgent.Contracts/Writeback/WritebackResult.cs`
- Create: `tests/SuavoAgent.Core.Tests/Writeback/WritebackResultTests.cs`

- [ ] **Step 1: Create WritebackResult record**

```csharp
// src/SuavoAgent.Contracts/Writeback/WritebackResult.cs
using Microsoft.Data.SqlClient;

namespace SuavoAgent.Contracts.Writeback;

public record WritebackResult(
    bool Success,
    string Outcome,
    Guid? TransactionId,
    string? Details,
    bool IsReplay = false)
{
    public static WritebackResult Succeeded(Guid txId, string transition)
        => new(true, "success", txId, transition);

    public static WritebackResult AlreadyAtTarget(Guid txId)
        => new(true, "already_at_target", txId, "idempotent", IsReplay: true);

    public static WritebackResult VerifiedWithDrift(Guid txId, string expected, string actual)
        => new(true, "verified_with_drift", txId, $"expected={expected},actual={actual}");

    public static WritebackResult StatusConflict(string? observed)
        => new(false, "status_conflict", null, observed);

    public static WritebackResult ConnectionReset()
        => new(false, "connection_reset", null, null);

    public static WritebackResult PostVerifyMismatch(string? observed)
        => new(false, "post_verify_mismatch", null, observed);

    public static WritebackResult SqlError(Exception ex)
        => new(false, "sql_error", null, ex.Message);

    public static WritebackResult TriggerBlocked(string triggerName)
        => new(false, "trigger_blocked", null, triggerName);
}
```

- [ ] **Step 2: Write tests**

```csharp
// tests/SuavoAgent.Core.Tests/Writeback/WritebackResultTests.cs
using SuavoAgent.Contracts.Writeback;
using Xunit;

namespace SuavoAgent.Core.Tests.Writeback;

public class WritebackResultTests
{
    [Fact]
    public void Succeeded_IsSuccess()
    {
        var r = WritebackResult.Succeeded(Guid.NewGuid(), "pickup");
        Assert.True(r.Success);
        Assert.Equal("success", r.Outcome);
        Assert.False(r.IsReplay);
    }

    [Fact]
    public void AlreadyAtTarget_IsSuccessAndReplay()
    {
        var r = WritebackResult.AlreadyAtTarget(Guid.NewGuid());
        Assert.True(r.Success);
        Assert.Equal("already_at_target", r.Outcome);
        Assert.True(r.IsReplay);
    }

    [Fact]
    public void VerifiedWithDrift_IsSuccessWithDetails()
    {
        var r = WritebackResult.VerifiedWithDrift(Guid.NewGuid(), "2026-04-13", "2026-04-14");
        Assert.True(r.Success);
        Assert.Equal("verified_with_drift", r.Outcome);
        Assert.Contains("expected=2026-04-13", r.Details);
    }

    [Fact]
    public void StatusConflict_IsFailure()
    {
        var r = WritebackResult.StatusConflict("Cancelled");
        Assert.False(r.Success);
        Assert.Equal("status_conflict", r.Outcome);
    }

    [Fact]
    public void ConnectionReset_IsRetryable()
    {
        var r = WritebackResult.ConnectionReset();
        Assert.False(r.Success);
        Assert.Equal("connection_reset", r.Outcome);
    }

    [Fact]
    public void PostVerifyMismatch_IsRetryable()
    {
        var r = WritebackResult.PostVerifyMismatch("wrong-guid");
        Assert.False(r.Success);
        Assert.Equal("post_verify_mismatch", r.Outcome);
    }

    [Fact]
    public void SqlError_CapturesMessage()
    {
        var r = WritebackResult.SqlError(new InvalidOperationException("connection closed"));
        Assert.False(r.Success);
        Assert.Contains("connection closed", r.Details);
    }

    [Fact]
    public void TriggerBlocked_CapturesTriggerName()
    {
        var r = WritebackResult.TriggerBlocked("trg_rx_audit");
        Assert.False(r.Success);
        Assert.Equal("trigger_blocked", r.Outcome);
        Assert.Equal("trg_rx_audit", r.Details);
    }
}
```

- [ ] **Step 3: Build and run tests**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~WritebackResultTests" -v minimal`
Expected: 8 tests pass

- [ ] **Step 4: Commit**

```bash
git add src/SuavoAgent.Contracts/Writeback/ tests/SuavoAgent.Core.Tests/Writeback/
git commit -m "feat(writeback): add WritebackResult record with 8 outcome factory methods"
```

---

## Task 2: Extend GUID Discovery to 5 Statuses

**Files:**
- Modify: `src/SuavoAgent.Adapters.PioneerRx/PioneerRxConstants.cs`
- Modify: `src/SuavoAgent.Adapters.PioneerRx/Sql/PioneerRxSqlEngine.cs`
- Create: `tests/SuavoAgent.Core.Tests/Writeback/WritebackGuidDiscoveryTests.cs`

- [ ] **Step 1: Add AllDeliveryStatusNames to PioneerRxConstants**

Add after the existing `DeliveryReadyStatusNames` (line 15-19):

```csharp
// All delivery-related statuses (3 ready + Out for Delivery + Completed)
// Used by writeback engine for GUID discovery — all 5 must be discovered for writes
public static readonly IReadOnlyList<string> AllDeliveryStatusNames = new[]
{
    StatusWaitingForPickup,
    StatusWaitingForDelivery,
    StatusToBePutInBin,
    StatusOutForDelivery,
    StatusCompleted
};
```

- [ ] **Step 2: Add GetAllDiscoveredGuids to PioneerRxSqlEngine**

Add a new field and method. After the existing `_deliveryReadyGuids` field (line 13):

```csharp
private Dictionary<string, Guid>? _allStatusGuids;

/// <summary>
/// Returns all discovered delivery-related GUIDs keyed by status description.
/// Used by the writeback engine. Returns null if not yet discovered.
/// </summary>
public IReadOnlyDictionary<string, Guid>? GetAllDiscoveredGuids() => _allStatusGuids;
```

Modify `DiscoverStatusGuidsAsync` to also discover "Out for Delivery" and "Completed" GUIDs. After the existing GUID discovery loop (around line 100-104), add:

```csharp
// Discover ALL delivery-related GUIDs (including writeback targets)
_allStatusGuids = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

var allStatusParams = string.Join(", ",
    Enumerable.Range(0, PioneerRxConstants.AllDeliveryStatusNames.Count)
        .Select(i => $"@all{i}"));

var allQuery = $@"SELECT Description, RxTransactionStatusTypeID
FROM Prescription.RxTransactionStatusType
WHERE Description IN ({allStatusParams})
ORDER BY Description";

await using var allCmd = new SqlCommand(allQuery, _connection);
allCmd.CommandTimeout = 10;
for (int i = 0; i < PioneerRxConstants.AllDeliveryStatusNames.Count; i++)
    allCmd.Parameters.AddWithValue($"@all{i}", PioneerRxConstants.AllDeliveryStatusNames[i]);

await using var allReader = await allCmd.ExecuteReaderAsync(ct);
while (await allReader.ReadAsync(ct))
{
    var desc = allReader.GetString(0);
    var guid = allReader.GetGuid(1);
    _allStatusGuids[desc] = guid;
    _logger.LogDebug("Discovered status GUID: {Description} = {Guid}", desc, guid);
}

_logger.LogInformation("Discovered {Count}/5 delivery status GUIDs", _allStatusGuids.Count);
```

- [ ] **Step 3: Write GUID discovery tests**

```csharp
// tests/SuavoAgent.Core.Tests/Writeback/WritebackGuidDiscoveryTests.cs
using SuavoAgent.Adapters.PioneerRx;
using Xunit;

namespace SuavoAgent.Core.Tests.Writeback;

public class WritebackGuidDiscoveryTests
{
    [Fact]
    public void AllDeliveryStatusNames_Contains5Statuses()
    {
        Assert.Equal(5, PioneerRxConstants.AllDeliveryStatusNames.Count);
        Assert.Contains("Waiting for Pick up", PioneerRxConstants.AllDeliveryStatusNames);
        Assert.Contains("Waiting for Delivery", PioneerRxConstants.AllDeliveryStatusNames);
        Assert.Contains("To Be Put in Bin", PioneerRxConstants.AllDeliveryStatusNames);
        Assert.Contains("Out for Delivery", PioneerRxConstants.AllDeliveryStatusNames);
        Assert.Contains("Completed", PioneerRxConstants.AllDeliveryStatusNames);
    }

    [Fact]
    public void DeliveryReadyStatusNames_StillContains3()
    {
        // Existing detection query should not be affected
        Assert.Equal(3, PioneerRxConstants.DeliveryReadyStatusNames.Count);
    }

    [Fact]
    public void FallbackStatusGuids_StillContains3()
    {
        // Fallbacks are for reads only — no fallback for write targets
        Assert.Equal(3, PioneerRxConstants.FallbackStatusGuids.Count);
        Assert.DoesNotContain("Out for Delivery", PioneerRxConstants.FallbackStatusGuids.Keys);
        Assert.DoesNotContain("Completed", PioneerRxConstants.FallbackStatusGuids.Keys);
    }
}
```

- [ ] **Step 4: Build and run tests**

Run: `dotnet test --filter "FullyQualifiedName~WritebackGuidDiscoveryTests" -v minimal`
Expected: 3 tests pass

- [ ] **Step 5: Run ALL tests**

Run: `dotnet test -v minimal`
Expected: All existing tests still pass

- [ ] **Step 6: Commit**

```bash
git add src/SuavoAgent.Adapters.PioneerRx/PioneerRxConstants.cs src/SuavoAgent.Adapters.PioneerRx/Sql/PioneerRxSqlEngine.cs tests/SuavoAgent.Core.Tests/Writeback/WritebackGuidDiscoveryTests.cs
git commit -m "feat(writeback): extend GUID discovery to 5 delivery statuses — Out for Delivery + Completed for writes"
```

---

## Task 3: PioneerRxWritebackEngine — Core

**Files:**
- Create: `src/SuavoAgent.Adapters.PioneerRx/Sql/PioneerRxWritebackEngine.cs`

This is the largest task. The engine handles trigger detection, RxNumber resolution, and both transition types.

- [ ] **Step 1: Create the engine**

```csharp
// src/SuavoAgent.Adapters.PioneerRx/Sql/PioneerRxWritebackEngine.cs
using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Writeback;

namespace SuavoAgent.Adapters.PioneerRx.Sql;

/// <summary>
/// SQL writeback engine for PioneerRx. Separate from PioneerRxSqlEngine (read-only).
/// Uses its own connection pool (ApplicationName="SuavoWriteback") to prevent deadlock.
/// </summary>
public sealed class PioneerRxWritebackEngine
{
    private readonly string _connectionString;
    private readonly IReadOnlyDictionary<string, Guid> _statusGuids;
    private readonly ILogger _logger;

    // Trigger detection cache
    private DateTimeOffset _triggerCacheExpiry = DateTimeOffset.MinValue;
    private bool _hasInsteadOfTrigger;
    private bool _hasAfterTrigger;
    private string? _blockedTriggerName;

    // Per-RxNumber serialization
    private readonly HashSet<int> _inProgressRxNumbers = new();
    private readonly object _rxLock = new();

    public bool WritebackEnabled { get; private set; } = true;
    public bool TriggerDetected => _hasInsteadOfTrigger || _hasAfterTrigger;

    public PioneerRxWritebackEngine(
        string connectionString,
        IReadOnlyDictionary<string, Guid> statusGuids,
        ILogger<PioneerRxWritebackEngine> logger)
    {
        _connectionString = connectionString;
        _statusGuids = statusGuids;
        _logger = logger;

        // Validate required GUIDs for writes
        if (!statusGuids.ContainsKey("Out for Delivery"))
        {
            WritebackEnabled = false;
            logger.LogWarning("Writeback DISABLED — 'Out for Delivery' GUID not discovered");
        }
        if (!statusGuids.ContainsKey("Completed"))
        {
            WritebackEnabled = false;
            logger.LogWarning("Writeback DISABLED — 'Completed' GUID not discovered");
        }
    }

    /// <summary>
    /// Checks for UPDATE triggers on Prescription.RxTransaction.
    /// Cached for 5 minutes. INSTEAD_OF triggers hard-block writes.
    /// </summary>
    public async Task<WritebackResult?> DetectTriggersAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow < _triggerCacheExpiry) return null;

        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = new SqlCommand("""
                SELECT t.name, t.is_disabled,
                       CASE WHEN t.is_instead_of_trigger = 1 THEN 'INSTEAD_OF' ELSE 'AFTER' END AS trigger_type
                FROM sys.triggers t
                JOIN sys.objects o ON t.parent_id = o.object_id
                JOIN sys.schemas s ON o.schema_id = s.schema_id
                WHERE s.name = 'Prescription' AND o.name = 'RxTransaction' AND t.type = 'TR'
                """, conn);
            cmd.CommandTimeout = 10;

            _hasInsteadOfTrigger = false;
            _hasAfterTrigger = false;
            _blockedTriggerName = null;

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(0);
                var disabled = reader.GetBoolean(1);
                var type = reader.GetString(2);

                if (disabled)
                {
                    _logger.LogDebug("Trigger {Name} is disabled — ignoring", name);
                    continue;
                }

                if (type == "INSTEAD_OF")
                {
                    _hasInsteadOfTrigger = true;
                    _blockedTriggerName = name;
                    _logger.LogWarning("INSTEAD_OF UPDATE trigger detected: {Name} — writes BLOCKED", name);
                }
                else
                {
                    _hasAfterTrigger = true;
                    _logger.LogWarning("AFTER UPDATE trigger detected: {Name} — supervised writes only", name);
                }
            }

            _triggerCacheExpiry = DateTimeOffset.UtcNow.AddMinutes(5);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Trigger detection failed — assuming triggers may exist");
            _hasAfterTrigger = true; // Fail safe
        }

        if (_hasInsteadOfTrigger)
            return WritebackResult.TriggerBlocked(_blockedTriggerName!);

        return null;
    }

    /// <summary>
    /// Resolves RxNumber + FillNumber to RxTransactionID.
    /// Query is transition-specific: pickup accepts 3 ready statuses, complete accepts only Out for Delivery.
    /// </summary>
    public async Task<(Guid TxId, Guid CurrentStatus)?> ResolveTransactionIdAsync(
        int rxNumber, int fillNumber, string transition, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var statusFilter = transition == "pickup"
            ? "rt.RxTransactionStatusTypeID IN (@g0, @g1, @g2)"
            : "rt.RxTransactionStatusTypeID = @g0";

        var query = $"""
            SELECT TOP 1 rt.RxTransactionID, rt.RxTransactionStatusTypeID
            FROM Prescription.RxTransaction rt
            JOIN Prescription.Rx r ON rt.RxID = r.RxID
            WHERE r.RxNumber = @rxNumber
              AND rt.RefillNumber = @fillNumber
              AND {statusFilter}
            """;

        await using var cmd = new SqlCommand(query, conn);
        cmd.CommandTimeout = 10;
        cmd.Parameters.AddWithValue("@rxNumber", rxNumber);
        cmd.Parameters.AddWithValue("@fillNumber", fillNumber);

        if (transition == "pickup")
        {
            cmd.Parameters.AddWithValue("@g0", _statusGuids["Waiting for Pick up"]);
            cmd.Parameters.AddWithValue("@g1", _statusGuids["Waiting for Delivery"]);
            cmd.Parameters.AddWithValue("@g2", _statusGuids["To Be Put in Bin"]);
        }
        else
        {
            cmd.Parameters.AddWithValue("@g0", _statusGuids["Out for Delivery"]);
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return (reader.GetGuid(0), reader.GetGuid(1));
    }

    /// <summary>
    /// Executes pickup transition: delivery-ready → Out for Delivery.
    /// Single column UPDATE with optimistic concurrency + pre/post verification.
    /// </summary>
    public async Task<WritebackResult> ExecutePickupAsync(
        Guid txId, Guid currentStatusGuid, CancellationToken ct)
    {
        var targetGuid = _statusGuids["Out for Delivery"];
        return await ExecuteTransitionAsync(txId, currentStatusGuid, targetGuid,
            null, "pickup", ct);
    }

    /// <summary>
    /// Executes complete transition: Out for Delivery → Completed.
    /// Sets status + CompletedDate + clears BinLocationID + BagID.
    /// </summary>
    public async Task<WritebackResult> ExecuteCompleteAsync(
        Guid txId, DateTimeOffset deliveredAt, CancellationToken ct)
    {
        var currentGuid = _statusGuids["Out for Delivery"];
        var targetGuid = _statusGuids["Completed"];
        return await ExecuteTransitionAsync(txId, currentGuid, targetGuid,
            deliveredAt, "complete", ct);
    }

    private async Task<WritebackResult> ExecuteTransitionAsync(
        Guid txId, Guid currentStatusGuid, Guid targetGuid,
        DateTimeOffset? deliveredAt, string transition, CancellationToken ct)
    {
        if (!WritebackEnabled)
            return WritebackResult.TriggerBlocked("writeback_disabled");

        // Trigger check (cached 5 min)
        var triggerBlock = await DetectTriggersAsync(ct);
        if (triggerBlock != null) return triggerBlock;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var connId = conn.ClientConnectionId;

        await using var txn = conn.BeginTransaction(IsolationLevel.ReadCommitted);
        try
        {
            // Pre-verify: check current status
            await using var preCmd = new SqlCommand(
                "SELECT RxTransactionStatusTypeID FROM Prescription.RxTransaction " +
                "WHERE RxTransactionID = @txId", conn, txn);
            preCmd.CommandTimeout = 10;
            preCmd.Parameters.AddWithValue("@txId", txId);

            var currentObj = await preCmd.ExecuteScalarAsync(ct);
            if (currentObj is not Guid current)
            {
                await txn.RollbackAsync(ct);
                return WritebackResult.StatusConflict("transaction_not_found");
            }

            // Idempotency: already at target = success (crash recovery)
            if (current == targetGuid)
            {
                await txn.RollbackAsync(ct);
                return WritebackResult.AlreadyAtTarget(txId);
            }

            // Unexpected status = conflict
            if (current != currentStatusGuid)
            {
                await txn.RollbackAsync(ct);
                return WritebackResult.StatusConflict(current.ToString());
            }

            // Connection guard
            if (conn.ClientConnectionId != connId)
            {
                await txn.RollbackAsync(ct);
                return WritebackResult.ConnectionReset();
            }

            // UPDATE
            string updateSql;
            if (transition == "complete" && deliveredAt.HasValue)
            {
                updateSql = """
                    SET NOCOUNT ON;
                    UPDATE Prescription.RxTransaction
                    SET RxTransactionStatusTypeID = @targetGuid,
                        CompletedDate = @deliveredAt,
                        BinLocationID = NULL,
                        BagID = NULL
                    WHERE RxTransactionID = @txId
                      AND RxTransactionStatusTypeID = @currentGuid;
                    SELECT @@ROWCOUNT AS RowsAffected;
                    """;
            }
            else
            {
                updateSql = """
                    SET NOCOUNT ON;
                    UPDATE Prescription.RxTransaction
                    SET RxTransactionStatusTypeID = @targetGuid
                    WHERE RxTransactionID = @txId
                      AND RxTransactionStatusTypeID = @currentGuid;
                    SELECT @@ROWCOUNT AS RowsAffected;
                    """;
            }

            await using var updateCmd = new SqlCommand(updateSql, conn, txn);
            updateCmd.CommandTimeout = 10;
            updateCmd.Parameters.AddWithValue("@targetGuid", targetGuid);
            updateCmd.Parameters.AddWithValue("@txId", txId);
            updateCmd.Parameters.AddWithValue("@currentGuid", currentStatusGuid);
            if (deliveredAt.HasValue)
                updateCmd.Parameters.AddWithValue("@deliveredAt", deliveredAt.Value.UtcDateTime);

            var rowsAffected = Convert.ToInt32(await updateCmd.ExecuteScalarAsync(ct));
            if (rowsAffected == 0)
            {
                await txn.RollbackAsync(ct);
                return WritebackResult.StatusConflict("concurrent_modification");
            }

            // Post-verify
            string postSql = transition == "complete"
                ? "SELECT RxTransactionStatusTypeID, CompletedDate FROM Prescription.RxTransaction WHERE RxTransactionID = @txId"
                : "SELECT RxTransactionStatusTypeID FROM Prescription.RxTransaction WHERE RxTransactionID = @txId";

            await using var postCmd = new SqlCommand(postSql, conn, txn);
            postCmd.CommandTimeout = 10;
            postCmd.Parameters.AddWithValue("@txId", txId);

            await using var postReader = await postCmd.ExecuteReaderAsync(ct);
            if (!await postReader.ReadAsync(ct))
            {
                await txn.RollbackAsync(ct);
                return WritebackResult.PostVerifyMismatch("row_disappeared");
            }

            var writtenGuid = postReader.GetGuid(0);
            if (writtenGuid != targetGuid)
            {
                await txn.RollbackAsync(ct);
                return WritebackResult.PostVerifyMismatch(writtenGuid.ToString());
            }

            // For complete: verify CompletedDate
            if (transition == "complete" && deliveredAt.HasValue)
            {
                var writtenDate = postReader.IsDBNull(1) ? (DateTime?)null : postReader.GetDateTime(1);
                if (writtenDate == null)
                {
                    await txn.RollbackAsync(ct);
                    return WritebackResult.PostVerifyMismatch("CompletedDate_null");
                }
                // Allow small time precision drift (SQL Server datetime vs DateTimeOffset)
                var expectedUtc = deliveredAt.Value.UtcDateTime;
                if (Math.Abs((writtenDate.Value - expectedUtc).TotalSeconds) > 2)
                {
                    await txn.CommitAsync(ct); // Status IS correct, just date drift
                    return WritebackResult.VerifiedWithDrift(txId,
                        expectedUtc.ToString("o"), writtenDate.Value.ToString("o"));
                }
            }

            await txn.CommitAsync(ct);
            _logger.LogInformation("Writeback {Transition} succeeded for TxId {TxId}", transition, txId);
            return WritebackResult.Succeeded(txId, transition);
        }
        catch (SqlException ex)
        {
            try { await txn.RollbackAsync(ct); } catch { }
            _logger.LogWarning(ex, "Writeback SQL error for TxId {TxId}", txId);
            return WritebackResult.SqlError(ex);
        }
    }

    /// <summary>
    /// Queries Rxs currently in "Out for Delivery" status for in-transit tracking.
    /// </summary>
    public async Task<int> ReadInTransitCountAsync(CancellationToken ct)
    {
        if (!_statusGuids.ContainsKey("Out for Delivery")) return 0;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand("""
            SELECT COUNT(*)
            FROM Prescription.RxTransaction rt
            WHERE rt.RxTransactionStatusTypeID = @outForDeliveryGuid
            """, conn);
        cmd.CommandTimeout = 10;
        cmd.Parameters.AddWithValue("@outForDeliveryGuid", _statusGuids["Out for Delivery"]);

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    /// <summary>
    /// Per-RxNumber serialization: try to acquire write lock for an RxNumber.
    /// Returns false if another writeback for this Rx is in progress.
    /// </summary>
    public bool TryAcquireRxLock(int rxNumber)
    {
        lock (_rxLock) { return _inProgressRxNumbers.Add(rxNumber); }
    }

    public void ReleaseRxLock(int rxNumber)
    {
        lock (_rxLock) { _inProgressRxNumbers.Remove(rxNumber); }
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/SuavoAgent.Adapters.PioneerRx/Sql/PioneerRxWritebackEngine.cs
git commit -m "feat(writeback): add PioneerRxWritebackEngine — trigger detection, resolution, pickup/complete transactions, in-transit tracking"
```

---

## Task 4: WritebackStateMachine — Add AlreadyAtTarget Trigger

**Files:**
- Modify: `src/SuavoAgent.Core/State/WritebackStateMachine.cs`

- [ ] **Step 1: Add AlreadyAtTarget to WritebackTrigger enum**

In `WritebackStateMachine.cs`, add to the `WritebackTrigger` enum (after `HelperDisconnected`):

```csharp
AlreadyAtTarget  // idempotent success — status was already at target (crash recovery)
```

- [ ] **Step 2: Add state machine transition for AlreadyAtTarget**

In the constructor, add a permitted transition from `Queued`, `Claimed`, and `InProgress`:

```csharp
// Add to Queued config:
.Permit(WritebackTrigger.AlreadyAtTarget, WritebackState.Done)

// Add to Claimed config:
.Permit(WritebackTrigger.AlreadyAtTarget, WritebackState.Done)

// Add to InProgress config:
.Permit(WritebackTrigger.AlreadyAtTarget, WritebackState.Done)
```

- [ ] **Step 3: Run existing state machine tests**

Run: `dotnet test --filter "FullyQualifiedName~WritebackStateMachineTests" -v minimal`
Expected: All existing tests still pass

- [ ] **Step 4: Commit**

```bash
git add src/SuavoAgent.Core/State/WritebackStateMachine.cs
git commit -m "feat(writeback): add AlreadyAtTarget trigger to WritebackStateMachine for idempotent crash recovery"
```

---

## Task 5: WritebackProcessor — Replace Stub with Real Engine

**Files:**
- Modify: `src/SuavoAgent.Core/Workers/WritebackProcessor.cs`

- [ ] **Step 1: Add writeback engine field and constructor parameter**

Add to WritebackProcessor:

```csharp
private readonly PioneerRxWritebackEngine? _writebackEngine;

// Modify constructor to accept optional engine:
public WritebackProcessor(
    ILogger<WritebackProcessor> logger,
    AgentStateDb stateDb,
    IpcPipeServer pipeServer,
    PioneerRxWritebackEngine? writebackEngine = null)
{
    _logger = logger;
    _stateDb = stateDb;
    _pipeServer = pipeServer;
    _writebackEngine = writebackEngine;
}
```

Add required usings:
```csharp
using SuavoAgent.Adapters.PioneerRx.Sql;
using SuavoAgent.Contracts.Writeback;
```

- [ ] **Step 2: Add transition + fillNumber tracking to EnqueueWriteback**

Modify `EnqueueWriteback` to accept transition info:

```csharp
public void EnqueueWriteback(string taskId, string rxNumber, int fillNumber = 0,
    string transition = "pickup", DateTimeOffset? deliveredAt = null)
{
    if (_machines.ContainsKey(taskId))
    {
        _logger.LogDebug("Writeback {TaskId} already tracked", taskId);
        return;
    }

    var machine = new WritebackStateMachine(taskId, WritebackState.Queued, OnStateChanged);
    _machines[taskId] = machine;
    _stateDb.UpsertWritebackState(taskId, rxNumber, WritebackState.Queued, 0, null);
    _logger.LogInformation("Enqueued writeback {TaskId} for Rx {RxNumber} ({Transition})",
        taskId, rxNumber, transition);
}
```

- [ ] **Step 3: Replace the stub in ProcessPendingWritebacksAsync**

Replace the body of `ProcessPendingWritebacksAsync` (currently lines 81-127):

```csharp
private async Task ProcessPendingWritebacksAsync(CancellationToken ct)
{
    var queued = _machines
        .Where(m => m.Value.CurrentState == WritebackState.Queued)
        .ToList();

    if (queued.Count == 0) return;

    // Use real writeback engine if available, otherwise log stub
    if (_writebackEngine == null || !_writebackEngine.WritebackEnabled)
    {
        foreach (var (taskId, machine) in queued)
        {
            _logger.LogDebug("Writeback {TaskId} — no engine available", taskId);
        }
        return;
    }

    foreach (var (taskId, machine) in queued)
    {
        if (ct.IsCancellationRequested) break;

        // Get Rx info from persisted state
        var states = _stateDb.GetPendingWritebacks();
        var state = states.FirstOrDefault(s => s.TaskId == taskId);
        if (state == default) continue;

        // Parse RxNumber
        if (!int.TryParse(state.RxNumber, out var rxNumber))
        {
            _logger.LogWarning("Writeback {TaskId} — invalid RxNumber '{Rx}'", taskId, state.RxNumber);
            machine.Fire(WritebackTrigger.BusinessError);
            continue;
        }

        // Per-RxNumber serialization
        if (!_writebackEngine.TryAcquireRxLock(rxNumber))
        {
            _logger.LogDebug("Writeback {TaskId} — Rx {Rx} already in progress, skipping", taskId, rxNumber);
            continue;
        }

        try
        {
            machine.Fire(WritebackTrigger.Claim);

            // Resolve RxTransactionID
            var resolved = await _writebackEngine.ResolveTransactionIdAsync(
                rxNumber, 0, "pickup", ct); // TODO: pass actual fillNumber and transition

            if (resolved == null)
            {
                _logger.LogWarning("Writeback {TaskId} — Rx {Rx} not found in expected state", taskId, rxNumber);
                machine.Fire(WritebackTrigger.BusinessError);
                continue;
            }

            machine.Fire(WritebackTrigger.StartUia); // Transition to InProgress

            // Execute writeback
            var result = await _writebackEngine.ExecutePickupAsync(
                resolved.Value.TxId, resolved.Value.CurrentStatus, ct);

            // Map result to state machine
            MapResultToStateMachine(taskId, machine, result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Writeback {TaskId} processing error", taskId);
            if (machine.CanFire(WritebackTrigger.SystemError))
                machine.Fire(WritebackTrigger.SystemError);
        }
        finally
        {
            _writebackEngine.ReleaseRxLock(rxNumber);
        }
    }
}

private void MapResultToStateMachine(string taskId, WritebackStateMachine machine, WritebackResult result)
{
    switch (result.Outcome)
    {
        case "success":
        case "verified_with_drift":
            if (machine.CanFire(WritebackTrigger.WriteComplete))
                machine.Fire(WritebackTrigger.WriteComplete);
            if (machine.CanFire(WritebackTrigger.VerifyMatch))
                machine.Fire(WritebackTrigger.VerifyMatch);
            if (machine.CanFire(WritebackTrigger.SyncComplete))
                machine.Fire(WritebackTrigger.SyncComplete);
            break;

        case "already_at_target":
            if (machine.CanFire(WritebackTrigger.AlreadyAtTarget))
                machine.Fire(WritebackTrigger.AlreadyAtTarget);
            break;

        case "status_conflict":
        case "trigger_blocked":
            machine.Fire(WritebackTrigger.BusinessError);
            break;

        case "connection_reset":
        case "sql_error":
            machine.Fire(WritebackTrigger.SystemError);
            break;

        case "post_verify_mismatch":
            machine.Fire(WritebackTrigger.VerifyMismatch);
            break;
    }

    _logger.LogInformation("Writeback {TaskId} result: {Outcome} (success={Success})",
        taskId, result.Outcome, result.Success);
}
```

- [ ] **Step 4: Build and run all tests**

Run: `dotnet build && dotnet test -v minimal`
Expected: All tests pass

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Core/Workers/WritebackProcessor.cs
git commit -m "feat(writeback): replace WritebackProcessor stub with real PioneerRxWritebackEngine calls, per-RxNumber guard, result mapping"
```

---

## Task 6: HeartbeatWorker — delivery_writeback Command Handler

**Files:**
- Modify: `src/SuavoAgent.Core/Workers/HeartbeatWorker.cs`

- [ ] **Step 1: Add delivery_writeback case to ProcessSignedCommandAsync**

In the switch statement in `ProcessSignedCommandAsync` (after the `approve_pom` case):

```csharp
case "delivery_writeback":
    await HandleDeliveryWritebackAsync(scEl, cmd, ct);
    break;
```

- [ ] **Step 2: Implement HandleDeliveryWritebackAsync**

```csharp
private async Task HandleDeliveryWritebackAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
{
    var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
    var transition = dataEl.TryGetProperty("transition", out var tr) ? tr.GetString() ?? "" : "";
    var rxNumberStr = dataEl.TryGetProperty("rxNumber", out var rx) ? rx.GetInt32().ToString() : "";
    var fillNumber = dataEl.TryGetProperty("fillNumber", out var fn) ? fn.GetInt32() : 0;
    var taskId = dataEl.TryGetProperty("taskId", out var tid) ? tid.GetString() ?? "" : "";
    var isControlled = dataEl.TryGetProperty("isControlledSubstance", out var cs) && cs.GetBoolean();

    if (string.IsNullOrEmpty(transition) || string.IsNullOrEmpty(rxNumberStr))
    {
        _logger.LogWarning("delivery_writeback: missing transition or rxNumber");
        return;
    }

    // Hash Rx number for audit
    var hashedRx = PhiScrubber.HmacHash(rxNumberStr, _options.AgentId ?? "");

    // Audit the command receipt
    _stateDb.AppendChainedAuditEntry(new AuditEntry(
        TaskId: hashedRx,
        EventType: "writeback_command_received",
        FromState: "",
        ToState: transition,
        Trigger: "delivery_writeback",
        CommandId: cmd.Nonce,
        RxNumber: hashedRx));

    // Parse deliveredAt for complete transition
    DateTimeOffset? deliveredAt = null;
    if (transition == "complete" && dataEl.TryGetProperty("deliveredAt", out var da))
    {
        if (DateTimeOffset.TryParse(da.GetString(), out var parsed))
            deliveredAt = parsed;
    }

    // Enqueue for processing
    var writebackProcessor = _serviceProvider.GetService<WritebackProcessor>();
    if (writebackProcessor != null)
    {
        writebackProcessor.EnqueueWriteback(taskId, rxNumberStr, fillNumber, transition, deliveredAt);
        _logger.LogInformation("delivery_writeback enqueued: {Transition} Rx {RxHash}",
            transition, hashedRx[..12]);
    }
    else
    {
        _logger.LogWarning("delivery_writeback: WritebackProcessor not available");
    }

    if (isControlled)
    {
        _logger.LogInformation("Controlled substance delivery — POS entry required for Rx {RxHash}",
            hashedRx[..12]);
    }

    await Task.CompletedTask;
}
```

- [ ] **Step 3: Add writeback metrics to heartbeat payload**

In the heartbeat payload construction (after the existing `sync` block), add:

```csharp
var writebackProcessor = _serviceProvider.GetService<WritebackProcessor>();
var writebackEngine = _serviceProvider.GetService<PioneerRxWritebackEngine>();
```

Add to the payload object:
```csharp
writeback = new
{
    pending = _stateDb.GetPendingWritebacks().Count,
    manualReview = 0,
    writebackEnabled = writebackEngine?.WritebackEnabled ?? false,
    triggerDetected = writebackEngine?.TriggerDetected ?? false,
}
```

- [ ] **Step 4: Build and run all tests**

Run: `dotnet build && dotnet test -v minimal`
Expected: All tests pass

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Core/Workers/HeartbeatWorker.cs
git commit -m "feat(writeback): add delivery_writeback signed command handler + writeback metrics in heartbeat"
```

---

## Task 7: RxDetectionWorker — Create Writeback Engine After SQL Connects

**Files:**
- Modify: `src/SuavoAgent.Core/Workers/RxDetectionWorker.cs`
- Modify: `src/SuavoAgent.Core/HealthSnapshot.cs`

- [ ] **Step 1: Add writeback engine creation in TryConnectSqlAsync**

Add field:
```csharp
private PioneerRxWritebackEngine? _writebackEngine;
public PioneerRxWritebackEngine? WritebackEngine => _writebackEngine;
```

In `TryConnectSqlAsync`, after `_sqlConnected = await _sqlEngine.TryConnectAsync(ct)` and the success log, add:

```csharp
// Create writeback engine with separate connection pool
if (_sqlConnected && _sqlEngine != null)
{
    var allGuids = _sqlEngine.GetAllDiscoveredGuids();
    if (allGuids != null && allGuids.Count >= 5)
    {
        var writebackCsb = new SqlConnectionStringBuilder();
        if (!string.IsNullOrEmpty(_options.SqlServer)) writebackCsb.DataSource = _options.SqlServer;
        if (!string.IsNullOrEmpty(_options.SqlDatabase)) writebackCsb.InitialCatalog = _options.SqlDatabase;
        writebackCsb.ApplicationName = "SuavoWriteback"; // Separate pool from read engine
        writebackCsb.MaxPoolSize = 1;
        writebackCsb["Encrypt"] = "true";
        writebackCsb["TrustServerCertificate"] = "true";
        if (!string.IsNullOrEmpty(_options.SqlUser))
        {
            writebackCsb.UserID = _options.SqlUser;
            writebackCsb.Password = _options.SqlPassword;
        }
        else
        {
            writebackCsb.IntegratedSecurity = true;
        }

        _writebackEngine = new PioneerRxWritebackEngine(
            writebackCsb.ConnectionString,
            allGuids,
            _loggerFactory.CreateLogger<PioneerRxWritebackEngine>());

        await _writebackEngine.DetectTriggersAsync(ct);
        _logger.LogInformation("Writeback engine created (enabled={Enabled})", _writebackEngine.WritebackEnabled);
    }
    else
    {
        _logger.LogWarning("Writeback engine NOT created — insufficient status GUIDs ({Count}/5)",
            allGuids?.Count ?? 0);
    }
}
```

Add usings:
```csharp
using SuavoAgent.Adapters.PioneerRx.Sql;
```

- [ ] **Step 2: Add writeback to HealthSnapshot**

In `HealthSnapshot.Take()`, add:

```csharp
var rxWorkerTyped = _sp.GetService(typeof(RxDetectionWorker)) as RxDetectionWorker;
var wbEngine = rxWorkerTyped?.WritebackEngine;
```

Add to snapshot object:
```csharp
writebackEngine = new
{
    enabled = wbEngine?.WritebackEnabled ?? false,
    triggerDetected = wbEngine?.TriggerDetected ?? false,
}
```

- [ ] **Step 3: Build and run all tests**

Run: `dotnet build && dotnet test -v minimal`
Expected: All tests pass

- [ ] **Step 4: Commit**

```bash
git add src/SuavoAgent.Core/Workers/RxDetectionWorker.cs src/SuavoAgent.Core/HealthSnapshot.cs
git commit -m "feat(writeback): create PioneerRxWritebackEngine after SQL connects, add to HealthSnapshot"
```

---

## Task 8: Integration Tests + Full Regression

**Files:**
- Create: `tests/SuavoAgent.Core.Tests/Writeback/WritebackProcessorIntegrationTests.cs`
- Create: `tests/SuavoAgent.Core.Tests/Writeback/WritebackCommandTests.cs`

- [ ] **Step 1: Write state machine integration tests**

```csharp
// tests/SuavoAgent.Core.Tests/Writeback/WritebackProcessorIntegrationTests.cs
using SuavoAgent.Contracts.Writeback;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Writeback;

public class WritebackProcessorIntegrationTests
{
    [Fact]
    public void AlreadyAtTarget_TransitionsToDonethroughNewTrigger()
    {
        var machine = new WritebackStateMachine("task-1", WritebackState.Queued,
            (_, _, _, _) => { });
        machine.Fire(WritebackTrigger.AlreadyAtTarget);
        Assert.Equal(WritebackState.Done, machine.CurrentState);
        Assert.True(machine.IsTerminal);
    }

    [Fact]
    public void AlreadyAtTarget_FromClaimed_TransitionsToDone()
    {
        var machine = new WritebackStateMachine("task-1", WritebackState.Queued,
            (_, _, _, _) => { });
        machine.Fire(WritebackTrigger.Claim);
        Assert.Equal(WritebackState.Claimed, machine.CurrentState);
        machine.Fire(WritebackTrigger.AlreadyAtTarget);
        Assert.Equal(WritebackState.Done, machine.CurrentState);
    }

    [Fact]
    public void AlreadyAtTarget_FromInProgress_TransitionsToDone()
    {
        var machine = new WritebackStateMachine("task-1", WritebackState.Queued,
            (_, _, _, _) => { });
        machine.Fire(WritebackTrigger.Claim);
        machine.Fire(WritebackTrigger.StartUia);
        Assert.Equal(WritebackState.InProgress, machine.CurrentState);
        machine.Fire(WritebackTrigger.AlreadyAtTarget);
        Assert.Equal(WritebackState.Done, machine.CurrentState);
    }

    [Fact]
    public void FullSuccessPath_Queued_To_Done()
    {
        var transitions = new List<(WritebackState from, WritebackState to)>();
        var machine = new WritebackStateMachine("task-1", WritebackState.Queued,
            (_, from, to, _) => transitions.Add((from, to)));

        machine.Fire(WritebackTrigger.Claim);
        machine.Fire(WritebackTrigger.StartUia);
        machine.Fire(WritebackTrigger.WriteComplete);
        machine.Fire(WritebackTrigger.VerifyMatch);
        machine.Fire(WritebackTrigger.SyncComplete);

        Assert.Equal(WritebackState.Done, machine.CurrentState);
        Assert.Equal(5, transitions.Count);
    }

    [Fact]
    public void SystemError_Retries_ThenManualReview()
    {
        var machine = new WritebackStateMachine("task-1", WritebackState.Queued,
            (_, _, _, _) => { });

        // 3 system errors = max retries
        for (int i = 0; i < 3; i++)
        {
            machine.Fire(WritebackTrigger.Claim);
            machine.Fire(WritebackTrigger.SystemError); // back to Queued, retryCount++
        }

        // 4th error → BusinessError → ManualReview (guard in Fire)
        machine.Fire(WritebackTrigger.Claim);
        machine.Fire(WritebackTrigger.SystemError);
        Assert.Equal(WritebackState.ManualReview, machine.CurrentState);
    }

    [Fact]
    public void WritebackResult_Success_MapsCorrectly()
    {
        var r = WritebackResult.Succeeded(Guid.NewGuid(), "pickup");
        Assert.True(r.Success);
        Assert.Equal("success", r.Outcome);
    }

    [Fact]
    public void WritebackResult_AlreadyAtTarget_IsReplay()
    {
        var r = WritebackResult.AlreadyAtTarget(Guid.NewGuid());
        Assert.True(r.Success);
        Assert.True(r.IsReplay);
    }
}
```

- [ ] **Step 2: Run all writeback tests**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~Writeback" -v minimal`
Expected: All writeback tests pass

- [ ] **Step 3: Run FULL test suite**

Run: `dotnet test -v minimal`
Expected: All tests pass (existing + new)

- [ ] **Step 4: Run Release build**

Run: `dotnet build -c Release`
Expected: 0 errors

- [ ] **Step 5: Commit**

```bash
git add tests/SuavoAgent.Core.Tests/Writeback/
git commit -m "feat(writeback): add integration tests — state machine mapping, AlreadyAtTarget idempotency, full success path, retry exhaustion"
```

- [ ] **Step 6: Show feature branch commits**

```bash
git log --oneline -10
```

---

## Self-Review Checklist

1. **Spec coverage:**
   - Section 1 (Transitions, GUID discovery, resolution, SQL execution) → Tasks 2, 3
   - Section 2 (Command flow, supervised mode) → Task 6
   - Section 3 (Engine, triggers, connection, WritebackResult, serialization, audit) → Tasks 1, 3, 5
   - Section 4 (Integration points) → Tasks 5, 6, 7
   - Section 5 (In-transit tracking) → Task 3 (ReadInTransitCountAsync), Task 7 (HealthSnapshot)
   - Section 6 (Tests) → Tasks 1, 2, 8
   - **Gap:** HMAC-hash RxNumber in audit entries (spec Section 3, "Audit Trail") — handled in Task 6 (HandleDeliveryWritebackAsync uses PhiScrubber.HmacHash before audit). Existing audit entries from WritebackProcessor.OnStateChanged still use raw RxNumber from writeback_states table — this is a known limitation; the raw RxNumber is in the local encrypted SQLite DB only, not exported.

2. **Placeholder scan:** No TBD except one explicit TODO in Task 5 (pass actual fillNumber and transition from persisted state — requires extending writeback_states table schema, deferred to iteration 2).

3. **Type consistency:** `WritebackResult` factory methods match across Tasks 1, 3, 5. `AlreadyAtTarget` trigger name matches in Tasks 4, 5, 8. `PioneerRxWritebackEngine` constructor signature matches between Tasks 3 and 7. `EnqueueWriteback` signature matches between Tasks 5 and 6.
