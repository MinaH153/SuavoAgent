using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Pricing;

namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    internal sealed record PricingTerminalAckOutboxEntry(
        string CommandId,
        PricingTerminalAck Ack,
        string PayloadSha256,
        string State,
        int AttemptCount,
        DateTimeOffset NextAttemptAt,
        DateTimeOffset CreatedAt,
        DateTimeOffset? DeliveredAt);

    internal PricingTerminalAckOutboxEntry StagePricingTerminalAck(
        string commandId,
        PricingTerminalAck ack)
    {
        if (!PricingTerminalAck.IsCanonicalCommandId(commandId))
            throw new ArgumentException("Pricing terminal ACK command id is invalid.");
        ack.Validated();
        var digest = ComputePricingTerminalAckDigest(ack);
        lock (_connLock)
        {
            using var transaction = _conn.BeginTransaction(
                System.Data.IsolationLevel.Serializable);
            var existing = ReadPricingTerminalAck(commandId, transaction);
            if (existing is not null)
            {
                transaction.Commit();
                if (!string.Equals(
                        existing.PayloadSha256,
                        digest,
                        StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Pricing terminal ACK identity conflict.");
                return existing;
            }

            var now = DateTimeOffset.UtcNow.ToString("O");
            using var insert = CreateCommand(transaction, """
                INSERT INTO pricing_terminal_ack_outbox (
                    command_id, result_kind, error_code, job_id, mode,
                    total_items, completed_items, failed_items, reason_code,
                    candidate_count, helper_version_suspect, cost_basis, payload_sha256,
                    state, attempt_count, next_attempt_at, created_at)
                VALUES (
                    @command, @kind, @error, @job, @mode,
                    @total, @completed, @failed, @reason,
                    @candidate_count, @helper_suspect, @cost_basis, @digest,
                    'pending', 0, @now, @now)
                """);
            AddPricingTerminalAckParameters(insert, commandId, ack, digest);
            insert.Parameters.AddWithValue("@now", now);
            insert.ExecuteNonQuery();
            var staged = ReadPricingTerminalAck(commandId, transaction) ??
                throw new InvalidOperationException(
                    "Pricing terminal ACK insert was not durable.");
            transaction.Commit();
            return staged;
        }
    }

    internal IReadOnlyList<PricingTerminalAckOutboxEntry>
        GetPendingPricingTerminalAcks(int maximum, bool includeDeferred = false)
    {
        if (maximum is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(maximum));
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = SelectPricingTerminalAckColumns + """
                 WHERE state = 'pending'
                   AND (@include_deferred = 1 OR next_attempt_at <= @now)
                 ORDER BY created_at ASC
                 LIMIT @maximum
                """;
            command.Parameters.AddWithValue(
                "@include_deferred", includeDeferred ? 1 : 0);
            command.Parameters.AddWithValue(
                "@now", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("@maximum", maximum);
            var values = new List<PricingTerminalAckOutboxEntry>();
            using var reader = command.ExecuteReader();
            while (reader.Read()) values.Add(MapPricingTerminalAck(reader));
            return values;
        }
    }

    internal PricingTerminalAckOutboxEntry? GetPricingTerminalAck(
        string commandId)
    {
        lock (_connLock)
            return ReadPricingTerminalAck(commandId, null);
    }

    internal void MarkPricingTerminalAckDelivered(
        string commandId,
        string payloadSha256)
    {
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                UPDATE pricing_terminal_ack_outbox
                   SET state = 'delivered', delivered_at = COALESCE(delivered_at, @now)
                 WHERE command_id = @command AND payload_sha256 = @digest
                   AND state IN ('pending','delivered')
                """;
            command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("@command", commandId);
            command.Parameters.AddWithValue("@digest", payloadSha256);
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException(
                    "Pricing terminal ACK delivery conflict.");
        }
    }

    internal void DelayPricingTerminalAck(
        string commandId,
        string payloadSha256,
        int priorAttempts)
    {
        var delaySeconds = Math.Min(3600, 15 * (1 << Math.Min(priorAttempts, 7)));
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                UPDATE pricing_terminal_ack_outbox
                   SET attempt_count = attempt_count + 1,
                       next_attempt_at = @next
                 WHERE command_id = @command AND payload_sha256 = @digest
                   AND state = 'pending'
                """;
            command.Parameters.AddWithValue(
                "@next", DateTimeOffset.UtcNow.AddSeconds(delaySeconds).ToString("O"));
            command.Parameters.AddWithValue("@command", commandId);
            command.Parameters.AddWithValue("@digest", payloadSha256);
            var affected = command.ExecuteNonQuery();
            if (affected == 1) return;
            var current = ReadPricingTerminalAck(commandId, null);
            if (current is null || current.PayloadSha256 != payloadSha256 ||
                current.State != "delivered")
                throw new InvalidOperationException(
                    "Pricing terminal ACK retry conflict.");
        }
    }

    internal static string ComputePricingTerminalAckDigest(PricingTerminalAck ack)
    {
        ack.Validated();
        var json = JsonSerializer.Serialize(new
        {
            status = "failed",
            result = ack.BuildResult(),
            error = ack.ErrorCode,
        });
        var bytes = Encoding.UTF8.GetBytes(json);
        try
        {
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private PricingTerminalAckOutboxEntry? ReadPricingTerminalAck(
        string commandId,
        SqliteTransaction? transaction)
    {
        using var command = transaction is null
            ? _conn.CreateCommand()
            : CreateCommand(transaction, "");
        command.CommandText = SelectPricingTerminalAckColumns +
            " WHERE command_id = @command LIMIT 1";
        command.Parameters.AddWithValue("@command", commandId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapPricingTerminalAck(reader) : null;
    }

    private static PricingTerminalAckOutboxEntry MapPricingTerminalAck(
        SqliteDataReader reader)
    {
        var ack = new PricingTerminalAck(
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetInt32(9),
            reader.IsDBNull(10) ? null : reader.GetInt64(10) == 1,
            reader.IsDBNull(17)
                ? reader.GetString(1) == PricingTerminalAck.PricingFailedResult
                    ? PricingApprovalContract.CostPerUnitBasis
                    : null
                : reader.GetString(17)).Validated();
        return new(
            reader.GetString(0),
            ack,
            reader.GetString(11),
            reader.GetString(12),
            reader.GetInt32(13),
            ParsePricingTerminalAckTimestamp(reader.GetString(14)),
            ParsePricingTerminalAckTimestamp(reader.GetString(15)),
            reader.IsDBNull(16)
                ? null
                : ParsePricingTerminalAckTimestamp(reader.GetString(16)));
    }

    private static void AddPricingTerminalAckParameters(
        SqliteCommand command,
        string commandId,
        PricingTerminalAck ack,
        string digest)
    {
        command.Parameters.AddWithValue("@command", commandId);
        command.Parameters.AddWithValue("@kind", ack.ResultKind);
        command.Parameters.AddWithValue("@error", ack.ErrorCode);
        command.Parameters.AddWithValue("@job", (object?)ack.JobId ?? DBNull.Value);
        command.Parameters.AddWithValue("@mode", (object?)ack.Mode ?? DBNull.Value);
        command.Parameters.AddWithValue("@total", (object?)ack.TotalItems ?? DBNull.Value);
        command.Parameters.AddWithValue("@completed", (object?)ack.CompletedItems ?? DBNull.Value);
        command.Parameters.AddWithValue("@failed", (object?)ack.FailedItems ?? DBNull.Value);
        command.Parameters.AddWithValue("@reason", (object?)ack.ReasonCode ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@candidate_count", (object?)ack.CandidateCount ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@helper_suspect",
            ack.HelperVersionSuspect is null
                ? DBNull.Value
                : ack.HelperVersionSuspect.Value ? 1 : 0);
        command.Parameters.AddWithValue(
            "@cost_basis", (object?)ack.CostBasis ?? DBNull.Value);
        command.Parameters.AddWithValue("@digest", digest);
    }

    private static DateTimeOffset ParsePricingTerminalAckTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    private const string SelectPricingTerminalAckColumns = """
        SELECT command_id, result_kind, error_code, job_id, mode,
               total_items, completed_items, failed_items, reason_code,
               candidate_count, helper_version_suspect, payload_sha256,
               state, attempt_count, next_attempt_at, created_at, delivered_at,
               cost_basis
          FROM pricing_terminal_ack_outbox
        """;
}
