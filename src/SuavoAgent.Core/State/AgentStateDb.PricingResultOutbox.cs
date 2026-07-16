using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Frozen;
using Microsoft.Data.Sqlite;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Pricing;

namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    internal sealed record PricingResultOutboxEntry(
        string JobId,
        string? CommandId,
        Guid? SourceUploadId,
        string PayloadJson,
        string PayloadSha256,
        int ItemCount,
        bool ExecutionOk,
        string State,
        int AttemptCount,
        DateTimeOffset CreatedAt,
        DateTimeOffset? AcceptedAt,
        string? AcceptedReceiptJson,
        string? AcceptedReceiptSha256,
        string? AcceptedResponseKeyId,
        string? AcceptedResponseSignature,
        DateTimeOffset? SourceFinalizedAt,
        int Generation,
        bool Legacy);

    internal sealed record PricingResultOutboxQuarantineEntry(
        string JobId,
        string PayloadSha256,
        string ReasonCode,
        DateTimeOffset QuarantinedAt,
        int? HttpStatus = null,
        string? ResponseJson = null,
        string? ResponseSha256 = null,
        string? ResponseKeyId = null,
        string? ResponseSignature = null);

    private static readonly FrozenSet<string> TerminalPricingResultCodes = new[]
    {
        "pricing_result_outbox_content_blocked",
        "pricing_result_command_ineligible",
        "pricing_result_payload_invalid",
        "pricing_result_payload_conflict",
        "pricing_result_job_agent_conflict",
        "pricing_result_job_not_eligible",
        "pricing_result_command_binding_invalid",
        "pricing_result_not_complete",
        "pricing_cost_basis_approval_revoked",
        "pricing_cloud_authority_revoked",
        "pricing_result_manual_reconciliation_required",
        "pricing_cost_basis_approval_expired",
        "pricing_cost_basis_approval_invalid",
        "pricing_cost_basis_approval_required",
        "pricing_job_authority_identity_invalid",
        "pricing_job_authority_binding_missing",
        "pricing_job_authority_binding_invalid",
    }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly Regex SafePricingEvidenceId = new(
        @"^[A-Za-z0-9:_-]{1,200}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal PricingResultOutboxEntry StagePricingResultPayload(
        string jobId,
        string? commandId,
        Guid? sourceUploadId,
        string payloadJson,
        int itemCount,
        bool executionOk)
    {
        ValidateCompletedPayload(
            jobId, commandId, payloadJson, itemCount, executionOk);
        var digest = Digest(payloadJson);
        lock (_connLock)
        {
            using var transaction = _conn.BeginTransaction();
            var entry = StagePricingResultPayload(
                transaction,
                jobId,
                commandId,
                sourceUploadId,
                payloadJson,
                digest,
                itemCount,
                executionOk);
            transaction.Commit();
            return entry;
        }
    }

    private PricingResultOutboxEntry StagePricingResultPayload(
        SqliteTransaction transaction,
        string jobId,
        string? commandId,
        Guid? sourceUploadId,
        string payloadJson,
        string digest,
        int itemCount,
        bool executionOk)
    {
        ValidateCompletedPayload(
            jobId, commandId, payloadJson, itemCount, executionOk);

        var matching = ReadV2ByDigest(transaction, jobId, digest);
        if (matching is not null)
        {
            AssertSameEvidence(
                matching, commandId, sourceUploadId, payloadJson, itemCount);
            return matching;
        }

        var latest = ReadLatestV2(transaction, jobId);
        if (latest is not null && !HasTerminalReceipt(transaction, jobId, latest.PayloadSha256))
            throw new InvalidOperationException("Pricing result outbox identity conflict.");

        var legacy = ReadLegacy(transaction, jobId);
        if (latest is null && legacy is not null &&
            legacy.ExecutionOk &&
            !HasTerminalReceipt(transaction, jobId, legacy.PayloadSha256) &&
            !HasLegacyQuarantine(transaction, jobId, legacy.PayloadSha256))
            throw new InvalidOperationException("Pricing result outbox identity conflict.");

        var generation = (latest?.Generation ?? 0) + 1;
        var now = DateTimeOffset.UtcNow.ToString("o");
        using (var insert = CreateCommand(transaction, """
            INSERT INTO pricing_result_outbox_v2 (
                job_id, generation, command_id, source_upload_id, payload_json,
                payload_sha256, item_count, execution_ok, state, attempt_count,
                next_attempt_at, created_at
            ) VALUES (
                @job, @generation, @command, @source, @payload,
                @digest, @count, 1, 'pending', 0, @now, @now
            )
            """))
        {
            insert.Parameters.AddWithValue("@job", jobId);
            insert.Parameters.AddWithValue("@generation", generation);
            insert.Parameters.AddWithValue("@command", (object?)commandId ?? DBNull.Value);
            insert.Parameters.AddWithValue(
                "@source", sourceUploadId?.ToString("D") ?? (object)DBNull.Value);
            insert.Parameters.AddWithValue("@payload", payloadJson);
            insert.Parameters.AddWithValue("@digest", digest);
            insert.Parameters.AddWithValue("@count", itemCount);
            insert.Parameters.AddWithValue("@now", now);
            insert.ExecuteNonQuery();
        }

        if (latest is not null)
            AppendSupersession(transaction, latest, digest, now);
        if (legacy is not null &&
            (!legacy.ExecutionOk ||
             HasTerminalReceipt(transaction, jobId, legacy.PayloadSha256) ||
             HasLegacyQuarantine(transaction, jobId, legacy.PayloadSha256)))
            AppendSupersession(transaction, legacy, digest, now);

        return ReadV2ByDigest(transaction, jobId, digest) ??
            throw new InvalidOperationException(
                "Pricing result outbox insert was not durable.");
    }

    internal PricingResultOutboxEntry? GetPricingResultOutbox(string jobId)
    {
        lock (_connLock)
            return ReadLatestV2(null, jobId) ?? ReadLegacy(null, jobId);
    }

    internal PricingResultOutboxEntry? GetPricingResultOutboxBySource(Guid sourceUploadId)
    {
        lock (_connLock)
        {
            using (var current = _conn.CreateCommand())
            {
                current.CommandText = V2SelectColumns +
                    " WHERE source_upload_id = @source ORDER BY generation DESC LIMIT 1";
                current.Parameters.AddWithValue("@source", sourceUploadId.ToString("D"));
                using var reader = current.ExecuteReader();
                if (reader.Read()) return MapPricingResultOutbox(reader);
            }
            using var legacy = _conn.CreateCommand();
            legacy.CommandText = LegacySelectColumns + " WHERE source_upload_id = @source";
            legacy.Parameters.AddWithValue("@source", sourceUploadId.ToString("D"));
            using var legacyReader = legacy.ExecuteReader();
            return legacyReader.Read() ? MapPricingResultOutbox(legacyReader) : null;
        }
    }

    internal IReadOnlyList<PricingResultOutboxEntry> GetPendingPricingResultPayloads(
        int limit) => GetPendingPricingResultPayloads(limit, dueOnly: true);

    internal IReadOnlyList<PricingResultOutboxEntry> GetAllPendingPricingResultPayloads(
        int limit) => GetPendingPricingResultPayloads(limit, dueOnly: false);

    private IReadOnlyList<PricingResultOutboxEntry> GetPendingPricingResultPayloads(
        int limit,
        bool dueOnly)
    {
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = PendingPricingResultPayloadsSql;
            command.Parameters.AddWithValue("@due_only", dueOnly ? 1 : 0);
            command.Parameters.AddWithValue(
                "@now", DateTimeOffset.UtcNow.ToString("o"));
            command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 20));
            using var reader = command.ExecuteReader();
            var values = new List<PricingResultOutboxEntry>();
            while (reader.Read()) values.Add(MapPricingResultOutbox(reader));
            return values;
        }
    }

    internal void QuarantineUnsafePricingResultPayload(
        string jobId,
        string payloadDigest) =>
        QuarantinePricingResultPayload(
            jobId,
            payloadDigest,
            "pricing_result_outbox_content_blocked",
            null,
            null,
            null,
            null);

    internal void QuarantinePricingResultPayload(
        string jobId,
        string payloadDigest,
        string reasonCode,
        int? httpStatus,
        string? responseJson,
        string? responseKeyId,
        string? responseSignature)
    {
        if (!TerminalPricingResultCodes.Contains(reasonCode) ||
            payloadDigest.Length != 64)
            throw new InvalidOperationException("Pricing result quarantine is invalid.");
        var localBlock = reasonCode is
            "pricing_result_outbox_content_blocked" or
            "pricing_result_command_ineligible" or
            "pricing_cost_basis_approval_revoked" or
            "pricing_cloud_authority_revoked" or
            "pricing_result_manual_reconciliation_required" or
            "pricing_cost_basis_approval_expired" or
            "pricing_cost_basis_approval_invalid" or
            "pricing_cost_basis_approval_required" or
            "pricing_job_authority_identity_invalid" or
            "pricing_job_authority_binding_missing" or
            "pricing_job_authority_binding_invalid";
        if (localBlock != (httpStatus is null && responseJson is null &&
                           responseKeyId is null && responseSignature is null) ||
            !localBlock && (httpStatus is not (400 or 409 or 413 or 422) ||
                string.IsNullOrWhiteSpace(responseJson) ||
                string.IsNullOrWhiteSpace(responseKeyId) ||
                string.IsNullOrWhiteSpace(responseSignature)))
            throw new InvalidOperationException("Pricing result quarantine is invalid.");
        var responseDigest = responseJson is null ? null : Digest(responseJson);

        lock (_connLock)
        {
            using var transaction = _conn.BeginTransaction();
            var evidence = ReadV2ByDigest(transaction, jobId, payloadDigest) ??
                ReadLegacy(transaction, jobId);
            if (evidence is null || evidence.PayloadSha256 != payloadDigest)
                throw new InvalidOperationException(
                    "Pricing result quarantine evidence not found.");
            if (localBlock && ReadLegacy(transaction, jobId)?.PayloadSha256 == payloadDigest)
                AppendLegacyQuarantine(transaction, jobId, payloadDigest);

            using (var insert = CreateCommand(transaction, """
                INSERT INTO pricing_result_outbox_terminal_receipts (
                    job_id, payload_sha256, reason_code, http_status,
                    response_json, response_sha256, response_key_id,
                    response_signature, quarantined_at
                ) VALUES (
                    @job, @digest, @reason, @status,
                    @response, @response_digest, @key_id, @signature, @now
                ) ON CONFLICT(job_id, payload_sha256) DO NOTHING
                """))
            {
                insert.Parameters.AddWithValue("@job", jobId);
                insert.Parameters.AddWithValue("@digest", payloadDigest);
                insert.Parameters.AddWithValue("@reason", reasonCode);
                insert.Parameters.AddWithValue("@status", (object?)httpStatus ?? DBNull.Value);
                insert.Parameters.AddWithValue("@response", (object?)responseJson ?? DBNull.Value);
                insert.Parameters.AddWithValue(
                    "@response_digest", (object?)responseDigest ?? DBNull.Value);
                insert.Parameters.AddWithValue(
                    "@key_id", (object?)responseKeyId ?? DBNull.Value);
                insert.Parameters.AddWithValue(
                    "@signature", (object?)responseSignature ?? DBNull.Value);
                insert.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
                insert.ExecuteNonQuery();
            }

            var persisted = ReadTerminalReceipt(transaction, jobId, payloadDigest);
            if (persisted is null ||
                persisted.ReasonCode != reasonCode ||
                persisted.HttpStatus != httpStatus ||
                persisted.ResponseJson != responseJson ||
                persisted.ResponseSha256 != responseDigest ||
                persisted.ResponseKeyId != responseKeyId ||
                persisted.ResponseSignature != responseSignature)
                throw new InvalidOperationException(
                    "Pricing result outbox quarantine conflict.");
            transaction.Commit();
        }
    }

    internal PricingResultOutboxQuarantineEntry? GetPricingResultOutboxQuarantine(
        string jobId)
    {
        lock (_connLock)
        {
            using (var command = _conn.CreateCommand())
            {
                command.CommandText = """
                    SELECT job_id, payload_sha256, reason_code, quarantined_at,
                           http_status, response_json, response_sha256,
                           response_key_id, response_signature
                      FROM pricing_result_outbox_terminal_receipts
                     WHERE job_id = @job
                     ORDER BY quarantined_at DESC
                     LIMIT 1
                    """;
                command.Parameters.AddWithValue("@job", jobId);
                using var reader = command.ExecuteReader();
                if (reader.Read()) return MapTerminalReceipt(reader);
            }
            using var legacy = _conn.CreateCommand();
            legacy.CommandText = """
                SELECT job_id, payload_sha256, reason_code, quarantined_at
                  FROM pricing_result_outbox_quarantine
                 WHERE job_id = @job
                """;
            legacy.Parameters.AddWithValue("@job", jobId);
            using var legacyReader = legacy.ExecuteReader();
            return legacyReader.Read()
                ? new PricingResultOutboxQuarantineEntry(
                    legacyReader.GetString(0),
                    legacyReader.GetString(1),
                    legacyReader.GetString(2),
                    ParseOutboxTimestamp(legacyReader.GetString(3)))
                : null;
        }
    }

    internal PricingResultOutboxQuarantineEntry? GetPricingResultOutboxQuarantine(
        string jobId,
        string payloadDigest)
    {
        lock (_connLock)
        {
            using (var command = _conn.CreateCommand())
            {
                command.CommandText = """
                    SELECT job_id, payload_sha256, reason_code, quarantined_at,
                           http_status, response_json, response_sha256,
                           response_key_id, response_signature
                      FROM pricing_result_outbox_terminal_receipts
                     WHERE job_id = @job AND payload_sha256 = @digest
                    """;
                command.Parameters.AddWithValue("@job", jobId);
                command.Parameters.AddWithValue("@digest", payloadDigest);
                using var reader = command.ExecuteReader();
                if (reader.Read()) return MapTerminalReceipt(reader);
            }
            using var legacy = _conn.CreateCommand();
            legacy.CommandText = """
                SELECT job_id, payload_sha256, reason_code, quarantined_at
                  FROM pricing_result_outbox_quarantine
                 WHERE job_id = @job AND payload_sha256 = @digest
                """;
            legacy.Parameters.AddWithValue("@job", jobId);
            legacy.Parameters.AddWithValue("@digest", payloadDigest);
            using var legacyReader = legacy.ExecuteReader();
            return legacyReader.Read()
                ? new PricingResultOutboxQuarantineEntry(
                    legacyReader.GetString(0),
                    legacyReader.GetString(1),
                    legacyReader.GetString(2),
                    ParseOutboxTimestamp(legacyReader.GetString(3)))
                : null;
        }
    }

    internal IReadOnlyList<PricingResultOutboxEntry>
        GetAcceptedPricingSourcesToFinalize(int limit)
    {
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = AcceptedPricingSourcesToFinalizeSql;
            command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 20));
            using var reader = command.ExecuteReader();
            var values = new List<PricingResultOutboxEntry>();
            while (reader.Read()) values.Add(MapPricingResultOutbox(reader));
            return values;
        }
    }

    private static void ValidateCompletedPayload(
        string jobId,
        string? commandId,
        string payloadJson,
        int itemCount,
        bool executionOk)
    {
        if (!executionOk)
            throw new InvalidOperationException("pricing_result_not_complete");
        if (!SafePricingEvidenceId.IsMatch(jobId) ||
            string.IsNullOrEmpty(payloadJson) ||
            PricingResultPayloadBudget.SerializedSize(payloadJson) >
                PricingResultPayloadBudget.MaximumSerializedBytes ||
            itemCount is < 0 or > PricingResultPayloadBudget.MaximumSerializedMetric)
            throw new InvalidOperationException(
                "Pricing result outbox payload is invalid.");
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (!document.RootElement.TryGetProperty("status", out var status) ||
                status.ValueKind != JsonValueKind.String ||
                status.GetString() != PricingJobStatus.Completed)
                throw new InvalidOperationException("pricing_result_not_complete");
            if (!document.RootElement.TryGetProperty(
                    "commandId", out var payloadCommandId) ||
                commandId is null && payloadCommandId.ValueKind != JsonValueKind.Null ||
                commandId is not null && (
                    payloadCommandId.ValueKind != JsonValueKind.String ||
                    payloadCommandId.GetString() != commandId))
                throw new InvalidOperationException(
                    "Pricing result outbox identity conflict.");
            if (!PricingJobCloudUploader.IsPersistedPayloadCloudSafe(
                    document.RootElement, jobId, itemCount))
                throw new InvalidOperationException(
                    "Pricing result outbox payload is invalid.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Pricing result outbox payload is invalid.");
        }
    }

    private static void AssertSameEvidence(
        PricingResultOutboxEntry existing,
        string? commandId,
        Guid? sourceUploadId,
        string payloadJson,
        int itemCount)
    {
        if (existing.CommandId != commandId ||
            existing.SourceUploadId != sourceUploadId ||
            existing.ItemCount != itemCount ||
            !existing.ExecutionOk ||
            existing.PayloadJson != payloadJson)
            throw new InvalidOperationException(
                "Pricing result outbox identity conflict.");
    }

    private static string Digest(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private void AppendSupersession(
        SqliteTransaction transaction,
        PricingResultOutboxEntry prior,
        string successorDigest,
        string now)
    {
        using var command = CreateCommand(transaction, """
            INSERT INTO pricing_result_outbox_supersessions (
                job_id, superseded_payload_sha256, successor_payload_sha256,
                reason_code, superseded_at
            ) VALUES (
                @job, @prior, @successor,
                'legacy_partial_replaced_by_completed', @now
            ) ON CONFLICT(job_id, superseded_payload_sha256) DO NOTHING
            """);
        command.Parameters.AddWithValue("@job", prior.JobId);
        command.Parameters.AddWithValue("@prior", prior.PayloadSha256);
        command.Parameters.AddWithValue("@successor", successorDigest);
        command.Parameters.AddWithValue("@now", now);
        command.ExecuteNonQuery();
    }

    private void AppendLegacyQuarantine(
        SqliteTransaction transaction,
        string jobId,
        string payloadDigest)
    {
        using var command = CreateCommand(transaction, """
            INSERT INTO pricing_result_outbox_quarantine (
                job_id, payload_sha256, reason_code, quarantined_at
            ) VALUES (
                @job, @digest, 'pricing_result_outbox_content_blocked', @now
            ) ON CONFLICT(job_id, payload_sha256) DO NOTHING
            """);
        command.Parameters.AddWithValue("@job", jobId);
        command.Parameters.AddWithValue("@digest", payloadDigest);
        command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        command.ExecuteNonQuery();
    }

    private bool HasTerminalReceipt(
        SqliteTransaction transaction,
        string jobId,
        string digest)
    {
        using var command = CreateCommand(transaction, """
            SELECT 1 FROM pricing_result_outbox_terminal_receipts
             WHERE job_id = @job AND payload_sha256 = @digest
            """);
        command.Parameters.AddWithValue("@job", jobId);
        command.Parameters.AddWithValue("@digest", digest);
        return command.ExecuteScalar() is not null;
    }

    private bool HasLegacyQuarantine(
        SqliteTransaction transaction,
        string jobId,
        string digest)
    {
        using var command = CreateCommand(transaction, """
            SELECT 1 FROM pricing_result_outbox_quarantine
             WHERE job_id = @job AND payload_sha256 = @digest
            """);
        command.Parameters.AddWithValue("@job", jobId);
        command.Parameters.AddWithValue("@digest", digest);
        return command.ExecuteScalar() is not null;
    }

    private PricingResultOutboxQuarantineEntry? ReadTerminalReceipt(
        SqliteTransaction transaction,
        string jobId,
        string digest)
    {
        using var command = CreateCommand(transaction, """
            SELECT job_id, payload_sha256, reason_code, quarantined_at,
                   http_status, response_json, response_sha256,
                   response_key_id, response_signature
              FROM pricing_result_outbox_terminal_receipts
             WHERE job_id = @job AND payload_sha256 = @digest
            """);
        command.Parameters.AddWithValue("@job", jobId);
        command.Parameters.AddWithValue("@digest", digest);
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapTerminalReceipt(reader) : null;
    }

    private static PricingResultOutboxQuarantineEntry MapTerminalReceipt(
        SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        ParseOutboxTimestamp(reader.GetString(3)),
        reader.IsDBNull(4) ? null : reader.GetInt32(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8));

    private PricingResultOutboxEntry? ReadLatestV2(
        SqliteTransaction? transaction,
        string jobId)
    {
        using var command = transaction is null
            ? _conn.CreateCommand()
            : CreateCommand(transaction, V2SelectColumns +
                " WHERE job_id = @job ORDER BY generation DESC LIMIT 1");
        if (transaction is null)
            command.CommandText = V2SelectColumns +
                " WHERE job_id = @job ORDER BY generation DESC LIMIT 1";
        command.Parameters.AddWithValue("@job", jobId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapPricingResultOutbox(reader) : null;
    }

    private PricingResultOutboxEntry? ReadV2ByDigest(
        SqliteTransaction transaction,
        string jobId,
        string digest)
    {
        using var command = CreateCommand(transaction, V2SelectColumns +
            " WHERE job_id = @job AND payload_sha256 = @digest");
        command.Parameters.AddWithValue("@job", jobId);
        command.Parameters.AddWithValue("@digest", digest);
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapPricingResultOutbox(reader) : null;
    }

    private PricingResultOutboxEntry? ReadLegacy(
        SqliteTransaction? transaction,
        string jobId)
    {
        using var command = transaction is null
            ? _conn.CreateCommand()
            : CreateCommand(transaction, LegacySelectColumns + " WHERE job_id = @job");
        if (transaction is null)
            command.CommandText = LegacySelectColumns + " WHERE job_id = @job";
        command.Parameters.AddWithValue("@job", jobId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapPricingResultOutbox(reader) : null;
    }

}
