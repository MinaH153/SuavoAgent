using Microsoft.Data.Sqlite;

namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private int _pricingCloudAuthorityRevokedInProcess;

    internal static readonly TimeSpan PricingCloudAuthorityOfflineGrace =
        TimeSpan.FromMinutes(15);

    internal static readonly TimeSpan PricingCloudAuthorityClockRollbackTolerance =
        TimeSpan.FromMinutes(5);

    private const string PricingCloudAuthorityTerminalReason =
        "agent_binding_inactive";

    private sealed record PricingCloudAuthorityLeaseState(
        DateTimeOffset? LastSuccessServerUtc,
        DateTimeOffset LocalHighWaterUtc,
        DateTimeOffset? TerminalAtUtc,
        string? TerminalReason);

    /// <summary>
    /// Renews pricing authority only from a successfully authenticated cloud
    /// heartbeat. A terminal workstation response is never cleared by a later
    /// success; re-enrollment must create fresh local state.
    /// </summary>
    internal bool RecordPricingCloudAuthorityHeartbeat(
        DateTimeOffset serverTime,
        DateTimeOffset observedAt,
        out string code)
    {
        serverTime = serverTime.ToUniversalTime();
        observedAt = observedAt.ToUniversalTime();

        if (Volatile.Read(ref _pricingCloudAuthorityRevokedInProcess) != 0)
        {
            code = "pricing_cloud_authority_revoked";
            return false;
        }

        lock (_connLock)
        {
            if (Volatile.Read(ref _pricingCloudAuthorityRevokedInProcess) != 0)
            {
                code = "pricing_cloud_authority_revoked";
                return false;
            }
            using var transaction = _conn.BeginTransaction(
                System.Data.IsolationLevel.Serializable);
            var state = ReadPricingCloudAuthorityLease(transaction);
            if (state?.TerminalReason is not null)
            {
                transaction.Commit();
                code = "pricing_cloud_authority_revoked";
                return false;
            }

            if (state?.LastSuccessServerUtc is { } previousServerTime &&
                serverTime + PricingCloudAuthorityClockRollbackTolerance <
                    previousServerTime)
            {
                transaction.Commit();
                code = "pricing_cloud_authority_server_clock_regression";
                return false;
            }

            var effectiveServerTime = state?.LastSuccessServerUtc is { } prior &&
                prior > serverTime
                    ? prior
                    : serverTime;
            var highWater = MaxUtc(
                state?.LocalHighWaterUtc,
                observedAt,
                effectiveServerTime);

            using var command = _conn.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO pricing_cloud_authority_lease (
                    singleton_id, last_success_server_utc,
                    local_high_water_utc, terminal_at_utc,
                    terminal_reason, updated_at_utc)
                VALUES (1, @server, @high_water, NULL, NULL, @updated)
                ON CONFLICT(singleton_id) DO UPDATE SET
                    last_success_server_utc = excluded.last_success_server_utc,
                    local_high_water_utc = excluded.local_high_water_utc,
                    updated_at_utc = excluded.updated_at_utc
                WHERE pricing_cloud_authority_lease.terminal_reason IS NULL
                """;
            command.Parameters.AddWithValue("@server", Utc(effectiveServerTime));
            command.Parameters.AddWithValue("@high_water", Utc(highWater));
            command.Parameters.AddWithValue("@updated", Utc(highWater));
            if (command.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                code = "pricing_cloud_authority_lease_persist_failed";
                return false;
            }

            transaction.Commit();
            code = "pricing_cloud_authority_lease_renewed";
            return true;
        }
    }

    /// <summary>
    /// Permanently latches the exact cloud lifecycle response. This is a local
    /// tombstone, not a transient connectivity failure.
    /// </summary>
    internal void LatchPricingCloudAuthorityRevocation(
        DateTimeOffset observedAt)
    {
        observedAt = observedAt.ToUniversalTime();
        // Publish the process tombstone before waiting behind any send that
        // already won linearization. Row execution and artifact publication
        // stop immediately even if that earlier HTTP operation is still live.
        Volatile.Write(ref _pricingCloudAuthorityRevokedInProcess, 1);
        using var authorityMutation = EnterPricingAuthorityMutation();
        lock (_connLock)
        {
            // This process-memory tombstone is the first mutation under the
            // same lock used by admission/publication. It survives any later
            // SQLite exception and cannot be interleaved with an authorized
            // atomic workbook publication on this ledger instance.
            using var transaction = _conn.BeginTransaction(
                System.Data.IsolationLevel.Serializable);
            var state = ReadPricingCloudAuthorityLease(transaction);
            if (state?.TerminalReason is not null)
            {
                transaction.Commit();
                return;
            }

            var highWater = MaxUtc(
                state?.LocalHighWaterUtc,
                observedAt,
                observedAt);
            using var command = _conn.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO pricing_cloud_authority_lease (
                    singleton_id, last_success_server_utc,
                    local_high_water_utc, terminal_at_utc,
                    terminal_reason, updated_at_utc)
                VALUES (1, NULL, @high_water, @terminal_at, @reason, @updated)
                ON CONFLICT(singleton_id) DO UPDATE SET
                    local_high_water_utc = excluded.local_high_water_utc,
                    terminal_at_utc = excluded.terminal_at_utc,
                    terminal_reason = excluded.terminal_reason,
                    updated_at_utc = excluded.updated_at_utc
                WHERE pricing_cloud_authority_lease.terminal_reason IS NULL
                """;
            command.Parameters.AddWithValue("@high_water", Utc(highWater));
            command.Parameters.AddWithValue("@terminal_at", Utc(observedAt));
            command.Parameters.AddWithValue(
                "@reason",
                PricingCloudAuthorityTerminalReason);
            command.Parameters.AddWithValue("@updated", Utc(highWater));
            if (command.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                throw new InvalidOperationException(
                    "pricing_cloud_authority_revocation_persist_failed");
            }
            transaction.Commit();
        }
    }

    /// <summary>
    /// Fail-closed admission used at job start and every pricing row. The
    /// persisted local high-water prevents a restart plus wall-clock rollback
    /// from extending the authenticated offline grace.
    /// </summary>
    internal bool TryAdmitPricingCloudAuthority(
        DateTimeOffset now,
        out string code)
    {
        now = now.ToUniversalTime();
        if (Volatile.Read(ref _pricingCloudAuthorityRevokedInProcess) != 0)
        {
            code = "pricing_cloud_authority_revoked";
            return false;
        }
        lock (_connLock)
        {
            if (Volatile.Read(ref _pricingCloudAuthorityRevokedInProcess) != 0)
            {
                code = "pricing_cloud_authority_revoked";
                return false;
            }
            using var transaction = _conn.BeginTransaction(
                System.Data.IsolationLevel.Serializable);
            var state = ReadPricingCloudAuthorityLease(transaction);
            if (state?.TerminalReason is not null)
            {
                transaction.Commit();
                code = "pricing_cloud_authority_revoked";
                return false;
            }
            if (state?.LastSuccessServerUtc is not { } lastSuccess)
            {
                transaction.Commit();
                code = "pricing_cloud_authority_lease_unavailable";
                return false;
            }
            if (now + PricingCloudAuthorityClockRollbackTolerance <
                state.LocalHighWaterUtc)
            {
                transaction.Commit();
                code = "pricing_cloud_authority_clock_rollback";
                return false;
            }

            var effectiveNow = now > state.LocalHighWaterUtc
                ? now
                : state.LocalHighWaterUtc;
            if (effectiveNow > state.LocalHighWaterUtc)
                PersistPricingCloudAuthorityHighWater(transaction, effectiveNow);

            var expiresAt = lastSuccess + PricingCloudAuthorityOfflineGrace;
            transaction.Commit();
            if (effectiveNow >= expiresAt)
            {
                code = "pricing_cloud_authority_lease_expired";
                return false;
            }

            code = "pricing_cloud_authority_lease_active";
            return true;
        }
    }

    private PricingCloudAuthorityLeaseState? ReadPricingCloudAuthorityLease(
        SqliteTransaction transaction)
    {
        using var command = _conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT last_success_server_utc, local_high_water_utc,
                   terminal_at_utc, terminal_reason
              FROM pricing_cloud_authority_lease
             WHERE singleton_id = 1
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new PricingCloudAuthorityLeaseState(
            reader.IsDBNull(0) ? null : ParseUtc(reader.GetString(0)),
            ParseUtc(reader.GetString(1)),
            reader.IsDBNull(2) ? null : ParseUtc(reader.GetString(2)),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private void PersistPricingCloudAuthorityHighWater(
        SqliteTransaction transaction,
        DateTimeOffset highWater)
    {
        using var command = _conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE pricing_cloud_authority_lease
               SET local_high_water_utc = @high_water,
                   updated_at_utc = @high_water
             WHERE singleton_id = 1
               AND terminal_reason IS NULL
            """;
        command.Parameters.AddWithValue("@high_water", Utc(highWater));
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException(
                "pricing_cloud_authority_high_water_persist_failed");
    }

    private static DateTimeOffset MaxUtc(
        DateTimeOffset? first,
        DateTimeOffset second,
        DateTimeOffset third)
    {
        var maximum = first is { } value && value > second ? value : second;
        return third > maximum ? third : maximum;
    }
}
