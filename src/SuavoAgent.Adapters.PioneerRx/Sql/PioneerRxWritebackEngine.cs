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
