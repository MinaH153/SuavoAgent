using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using SuavoAgent.Core.Autonomy;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;

namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private static readonly Regex AutonomyDigestShape = new(
        "^[a-f0-9]{64}$",
        RegexOptions.CultureInvariant);

    internal sealed record RecordedAutonomyEvidence(
        TaskAutonomyState State,
        SignedDeviceReceipt<AutonomyEvidenceDeviceReceipt> Signed);

    internal sealed record PendingAutonomyEvidence(
        SignedDeviceReceipt<AutonomyEvidenceDeviceReceipt> Signed,
        int AttemptCount);

    internal RecordedAutonomyEvidence RecordAutonomyEvidence(
        AutonomyRunEvidence evidence,
        AgentOptions options,
        IDeviceAuthoritySigner signer,
        int cleanRunsThreshold)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(signer);
        if (string.IsNullOrWhiteSpace(options.AgentId) ||
            string.IsNullOrWhiteSpace(options.PharmacyId) ||
            string.IsNullOrWhiteSpace(options.MachineFingerprint) ||
            !Guid.TryParseExact(options.AgentId, "D", out _) ||
            !Guid.TryParseExact(options.PharmacyId, "D", out _) ||
            !Guid.TryParseExact(evidence.RunId, "D", out _) ||
            !AutonomyDigestShape.IsMatch(evidence.PostconditionDigest) ||
            evidence.WorkItemCount is < 0 or > 1_000_000)
            throw new InvalidOperationException("Autonomy evidence identity is incomplete.");

        var scopeDigest = evidence.Scope.ScopeDigest;
        var semanticResult = evidence.SemanticResult.ToString().ToLowerInvariant();
        var threshold = Math.Max(1, cleanRunsThreshold);
        lock (_connLock)
        {
            using var transaction = _conn.BeginTransaction();
            var prior = ReadAutonomyState(transaction, scopeDigest, options.PharmacyId);
            var sameAuthority = prior.DeviceKeyId is not null &&
                string.Equals(prior.DeviceKeyId, signer.KeyId, StringComparison.Ordinal);
            var priorStreak = sameAuthority ? prior.ConsecutiveClean : 0;
            var priorTotal = sameAuthority ? prior.TotalRuns : 0;
            var nextStreak = TaskAutonomyEvaluator.NextStreak(priorStreak, evidence.Clean);
            var totalRuns = priorTotal + 1;
            var counter = NextAutonomyCounter(transaction);
            var receipt = new AutonomyEvidenceDeviceReceipt(
                1,
                evidence.RunId,
                options.AgentId,
                options.PharmacyId,
                options.MachineFingerprint,
                signer.KeyId,
                evidence.Scope.TaskType,
                evidence.Scope.TaskKey,
                evidence.Scope.AppId,
                evidence.Scope.AppVersion,
                evidence.Scope.SelectorDigest,
                evidence.Scope.TemplateDigest,
                evidence.Scope.ModelDigest,
                evidence.Scope.ExecutorMode,
                scopeDigest,
                evidence.Supervised,
                evidence.WorkItemCount,
                semanticResult,
                evidence.PostconditionSatisfied,
                evidence.PostconditionDigest,
                evidence.Clean,
                nextStreak,
                totalRuns,
                counter,
                evidence.CompletedAt.ToUniversalTime()
                    .ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"));
            var signed = signer.Sign(receipt);
            var receiptJson = JsonSerializer.Serialize(
                receipt,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var scopeJson = JsonSerializer.Serialize(
                evidence.Scope,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            using (var upsert = CreateCommand(transaction, """
                INSERT INTO task_autonomy (
                    task_key, pharmacy_id, consecutive_clean, total_runs,
                    last_outcome, updated_at, scope_json, device_key_id
                ) VALUES (
                    @scope, @pharmacy, @streak, @total,
                    @outcome, datetime('now'), @scope_json, @key
                ) ON CONFLICT(task_key, pharmacy_id) DO UPDATE SET
                    consecutive_clean = excluded.consecutive_clean,
                    total_runs = excluded.total_runs,
                    last_outcome = excluded.last_outcome,
                    updated_at = excluded.updated_at,
                    scope_json = excluded.scope_json,
                    device_key_id = excluded.device_key_id
                """))
            {
                upsert.Parameters.AddWithValue("@scope", scopeDigest);
                upsert.Parameters.AddWithValue("@pharmacy", options.PharmacyId);
                upsert.Parameters.AddWithValue("@streak", nextStreak);
                upsert.Parameters.AddWithValue("@total", totalRuns);
                upsert.Parameters.AddWithValue("@outcome", semanticResult);
                upsert.Parameters.AddWithValue("@scope_json", scopeJson);
                upsert.Parameters.AddWithValue("@key", signer.KeyId);
                upsert.ExecuteNonQuery();
            }
            using (var insert = CreateCommand(transaction, """
                INSERT INTO device_autonomy_evidence_outbox (
                    receipt_id, scope_digest, key_id, local_counter,
                    receipt_json, signature, canonical_digest,
                    committed_at, next_attempt_at
                ) VALUES (
                    @receipt, @scope, @key, @counter,
                    @json, @signature, @canonical,
                    @committed, @next
                )
                """))
            {
                var now = DateTimeOffset.UtcNow.ToString("o");
                insert.Parameters.AddWithValue("@receipt", receipt.ReceiptId);
                insert.Parameters.AddWithValue("@scope", scopeDigest);
                insert.Parameters.AddWithValue("@key", signed.KeyId);
                insert.Parameters.AddWithValue("@counter", counter);
                insert.Parameters.AddWithValue("@json", receiptJson);
                insert.Parameters.AddWithValue("@signature", signed.Signature);
                insert.Parameters.AddWithValue("@canonical", signed.CanonicalDigest);
                insert.Parameters.AddWithValue("@committed", now);
                insert.Parameters.AddWithValue("@next", now);
                insert.ExecuteNonQuery();
            }
            if (evidence.Supervised && evidence.Clean)
            {
                using var clearLatch = CreateCommand(transaction, """
                    UPDATE autonomy_safety_latches
                       SET disabled = 0,
                           reason_code = 'supervised_clean_recovery',
                           cleared_at = @cleared
                     WHERE task_type = @task AND disabled = 1
                    """);
                clearLatch.Parameters.AddWithValue("@task", evidence.Scope.TaskType);
                clearLatch.Parameters.AddWithValue("@cleared", DateTimeOffset.UtcNow.ToString("o"));
                clearLatch.ExecuteNonQuery();
            }
            transaction.Commit();
            return new(
                new(
                    scopeDigest,
                    options.PharmacyId,
                    nextStreak,
                    totalRuns,
                    TaskAutonomyEvaluator.LevelFor(nextStreak, threshold),
                    semanticResult),
                signed);
        }
    }

    internal void LatchAutonomyDisabled(string taskType, string reasonCode)
    {
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                INSERT INTO autonomy_safety_latches (
                    task_type, disabled, reason_code, latched_at, cleared_at
                ) VALUES (@task, 1, @reason, @latched, NULL)
                ON CONFLICT(task_type) DO UPDATE SET
                    disabled = 1,
                    reason_code = excluded.reason_code,
                    latched_at = excluded.latched_at,
                    cleared_at = NULL
                """;
            command.Parameters.AddWithValue("@task", taskType);
            command.Parameters.AddWithValue("@reason", reasonCode);
            command.Parameters.AddWithValue("@latched", DateTimeOffset.UtcNow.ToString("o"));
            command.ExecuteNonQuery();
        }
    }

    internal bool IsAutonomyDisabled(string taskType)
    {
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                SELECT disabled
                  FROM autonomy_safety_latches
                 WHERE task_type = @task
                """;
            command.Parameters.AddWithValue("@task", taskType);
            var value = command.ExecuteScalar();
            return value is not null && Convert.ToInt32(value) == 1;
        }
    }

    internal IReadOnlyList<PendingAutonomyEvidence> GetPendingAutonomyEvidence(int limit)
    {
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                SELECT key_id, receipt_json, signature, canonical_digest, attempt_count
                  FROM device_autonomy_evidence_outbox
                 WHERE accepted_at IS NULL AND next_attempt_at <= @now
                 ORDER BY local_counter
                 LIMIT @limit
                """;
            command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
            command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 20));
            using var reader = command.ExecuteReader();
            var pending = new List<PendingAutonomyEvidence>();
            while (reader.Read())
            {
                var receipt = JsonSerializer.Deserialize<AutonomyEvidenceDeviceReceipt>(
                    reader.GetString(1),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    ?? throw new InvalidOperationException("Autonomy evidence outbox is invalid.");
                pending.Add(new(
                    new(receipt, reader.GetString(0), reader.GetString(2), reader.GetString(3)),
                    reader.GetInt32(4)));
            }
            return pending;
        }
    }

    internal void MarkAutonomyEvidenceAccepted(string receiptId, long counter, string scopeDigest)
    {
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                UPDATE device_autonomy_evidence_outbox
                   SET accepted_at = COALESCE(accepted_at, @accepted)
                 WHERE receipt_id = @receipt
                   AND local_counter = @counter
                   AND scope_digest = @scope
                """;
            command.Parameters.AddWithValue("@accepted", DateTimeOffset.UtcNow.ToString("o"));
            command.Parameters.AddWithValue("@receipt", receiptId);
            command.Parameters.AddWithValue("@counter", counter);
            command.Parameters.AddWithValue("@scope", scopeDigest);
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Autonomy evidence acceptance conflict.");
        }
    }

    internal void DelayAutonomyEvidence(string receiptId, int priorAttempts)
    {
        var delaySeconds = Math.Min(3600, 15 * (1 << Math.Min(priorAttempts, 7)));
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                UPDATE device_autonomy_evidence_outbox
                   SET attempt_count = attempt_count + 1,
                       next_attempt_at = @next
                 WHERE receipt_id = @receipt AND accepted_at IS NULL
                """;
            command.Parameters.AddWithValue(
                "@next",
                DateTimeOffset.UtcNow.AddSeconds(delaySeconds).ToString("o"));
            command.Parameters.AddWithValue("@receipt", receiptId);
            command.ExecuteNonQuery();
        }
    }

    internal (
        int ConsecutiveClean,
        int TotalRuns,
        string? LastOutcome,
        string? DeviceKeyId,
        string? UpdatedAt)
        GetExactAutonomyState(string scopeDigest, string pharmacyId)
    {
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                SELECT consecutive_clean, total_runs, last_outcome, device_key_id, updated_at
                  FROM task_autonomy
                 WHERE task_key = @scope AND pharmacy_id = @pharmacy
                """;
            command.Parameters.AddWithValue("@scope", scopeDigest);
            command.Parameters.AddWithValue("@pharmacy", pharmacyId);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return (0, 0, null, null, null);
            return (
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4));
        }
    }

    private (int ConsecutiveClean, int TotalRuns, string? DeviceKeyId)
        ReadAutonomyState(SqliteTransaction transaction, string scopeDigest, string pharmacyId)
    {
        using var command = CreateCommand(transaction, """
            SELECT consecutive_clean, total_runs, device_key_id
              FROM task_autonomy
             WHERE task_key = @scope AND pharmacy_id = @pharmacy
            """);
        command.Parameters.AddWithValue("@scope", scopeDigest);
        command.Parameters.AddWithValue("@pharmacy", pharmacyId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return (0, 0, null);
        return (
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private long NextAutonomyCounter(SqliteTransaction transaction)
    {
        using var command = CreateCommand(transaction, """
            UPDATE autonomy_evidence_counter
               SET counter = counter + 1
             WHERE singleton = 1
            RETURNING counter
            """);
        return Convert.ToInt64(command.ExecuteScalar()
            ?? throw new InvalidOperationException("Autonomy evidence counter is missing."));
    }
}
