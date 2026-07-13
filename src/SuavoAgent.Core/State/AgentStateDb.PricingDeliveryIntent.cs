using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Cloud;

namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    internal sealed record PricingResultDeliveryQuarantine(
        string JobId,
        string? CommandId,
        string SourceMode,
        string ReasonCode,
        DateTimeOffset QuarantinedAt);

    private void RecoverTerminalPricingDeliveries()
    {
        lock (_connLock)
        {
            using var transaction = _conn.BeginTransaction();
            var terminals = new List<(string JobId, string Status, int Total,
                int Completed, int Failed, string? CommandId, string SourceMode)>();
            using (var command = CreateCommand(transaction, """
                SELECT j.job_id, j.status, j.total_items,
                       j.completed_items, j.failed_items,
                       i.command_id, i.source_mode
                  FROM pricing_jobs j
                  JOIN pricing_result_delivery_intents i ON i.job_id = j.job_id
                  LEFT JOIN pricing_result_outbox o ON o.job_id = j.job_id
                  LEFT JOIN pricing_result_outbox_v2 v ON v.job_id = j.job_id
                 WHERE o.job_id IS NULL
                   AND v.job_id IS NULL
                   AND j.status = 'completed'
                   AND i.terminal_at IS NULL
                 ORDER BY i.prepared_at
                """))
            {
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    terminals.Add((
                        reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                        reader.GetInt32(3), reader.GetInt32(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.GetString(6)));
            }
            foreach (var terminal in terminals)
            {
                if (terminal.SourceMode == "manual")
                    QuarantineLegacyManualPricingDelivery(
                        transaction,
                        terminal.JobId,
                        terminal.CommandId);
                else
                    StageTerminalPricingPayloadIfPrepared(
                        transaction,
                        terminal.JobId,
                        terminal.Status,
                        terminal.Total,
                        terminal.Completed,
                        terminal.Failed);
            }
            transaction.Commit();
        }
    }

    private void QuarantineLegacyManualPricingDelivery(
        SqliteTransaction transaction,
        string jobId,
        string? commandId)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        using (var insert = CreateCommand(transaction, """
            INSERT INTO pricing_result_delivery_quarantine (
                job_id, command_id, source_mode, reason_code, quarantined_at)
            VALUES (
                @job, @command, 'manual',
                'pricing_result_source_invalid', @now)
            ON CONFLICT(job_id) DO NOTHING
            """))
        {
            insert.Parameters.AddWithValue("@job", jobId);
            insert.Parameters.AddWithValue(
                "@command", (object?)commandId ?? DBNull.Value);
            insert.Parameters.AddWithValue("@now", now);
            insert.ExecuteNonQuery();
        }
        using (var verify = CreateCommand(transaction, """
            SELECT command_id, source_mode, reason_code
              FROM pricing_result_delivery_quarantine
             WHERE job_id = @job
            """))
        {
            verify.Parameters.AddWithValue("@job", jobId);
            using var reader = verify.ExecuteReader();
            if (!reader.Read() ||
                (reader.IsDBNull(0) ? null : reader.GetString(0)) != commandId ||
                reader.GetString(1) != "manual" ||
                reader.GetString(2) != "pricing_result_source_invalid")
                throw new InvalidOperationException(
                    "pricing_result_delivery_quarantine_conflict");
        }
        using var terminal = CreateCommand(transaction, """
            UPDATE pricing_result_delivery_intents
               SET terminal_at = COALESCE(terminal_at, @now)
             WHERE job_id = @job AND source_mode = 'manual'
            """);
        terminal.Parameters.AddWithValue("@now", now);
        terminal.Parameters.AddWithValue("@job", jobId);
        if (terminal.ExecuteNonQuery() != 1)
            throw new InvalidOperationException(
                "pricing_result_delivery_quarantine_conflict");
    }

    internal PricingResultDeliveryQuarantine?
        GetPricingResultDeliveryQuarantine(string jobId)
    {
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                SELECT job_id, command_id, source_mode, reason_code,
                       quarantined_at
                  FROM pricing_result_delivery_quarantine
                 WHERE job_id = @job
                """;
            command.Parameters.AddWithValue("@job", jobId);
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new PricingResultDeliveryQuarantine(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    DateTimeOffset.Parse(reader.GetString(4)))
                : null;
        }
    }

    internal void PreparePricingResultDelivery(
        PricingJobSpec spec,
        string? commandId,
        Guid? sourceUploadId,
        string sourceMode)
    {
        if (!SafePricingEvidenceId.IsMatch(spec.JobId) ||
            commandId is not null && !SafePricingEvidenceId.IsMatch(commandId) ||
            (spec.ApprovalId is null) != (spec.GrantDigest is null) ||
            commandId is not null &&
            (spec.ApprovalId is null || spec.GrantDigest is null) ||
            spec.ApprovalId is not null &&
            !IsCanonicalPricingApprovalId(spec.ApprovalId) ||
            spec.GrantDigest is not null &&
            !IsLowerHexSha256(spec.GrantDigest))
            throw new InvalidOperationException("pricing_result_identity_invalid");
        if (sourceMode is not ("sql" or "uia" or "vision"))
            throw new InvalidOperationException("Pricing delivery source is invalid.");
        lock (_connLock)
        {
            using var transaction = _conn.BeginTransaction();
            if (commandId is not null)
            {
                using var commandAuthority = CreateCommand(transaction, """
                    SELECT pricing_approval_id, pricing_grant_digest
                      FROM pricing_command_execution_intents
                     WHERE command_id = @command
                    """);
                commandAuthority.Parameters.AddWithValue("@command", commandId);
                using var reader = commandAuthority.ExecuteReader();
                if (!reader.Read() || reader.IsDBNull(0) || reader.IsDBNull(1) ||
                    !string.Equals(
                        reader.GetString(0),
                        spec.ApprovalId,
                        StringComparison.Ordinal) ||
                    !FixedApprovalHexEquals(
                        reader.GetString(1),
                        spec.GrantDigest))
                    throw new InvalidOperationException(
                        "pricing_job_authority_binding_invalid");
            }
            UpsertPricingJobRow(
                transaction, spec, PricingJobStatus.Pending, 0, 0, 0);
            using (var existing = CreateCommand(transaction, """
                SELECT command_id, source_upload_id, source_mode,
                       approval_id, grant_digest
                  FROM pricing_result_delivery_intents
                 WHERE job_id = @job
                """))
            {
                existing.Parameters.AddWithValue("@job", spec.JobId);
                using var reader = existing.ExecuteReader();
                if (reader.Read())
                {
                    var priorCommand = reader.IsDBNull(0) ? null : reader.GetString(0);
                    var priorSource = reader.IsDBNull(1)
                        ? (Guid?)null
                        : Guid.ParseExact(reader.GetString(1), "D");
                    if (priorCommand != commandId || priorSource != sourceUploadId ||
                        reader.GetString(2) != sourceMode ||
                        (reader.IsDBNull(3) ? null : reader.GetString(3)) !=
                            spec.ApprovalId ||
                        (reader.IsDBNull(4) ? null : reader.GetString(4)) !=
                            spec.GrantDigest)
                        throw new InvalidOperationException(
                            "Pricing delivery intent identity conflict.");
                    transaction.Commit();
                    return;
                }
            }

            using var insert = CreateCommand(transaction, """
                INSERT INTO pricing_result_delivery_intents (
                    job_id, command_id, source_upload_id, source_mode,
                    approval_id, grant_digest, prepared_at
                ) VALUES (
                    @job, @command, @source, @mode,
                    @approval, @grant, @prepared)
                """);
            insert.Parameters.AddWithValue("@job", spec.JobId);
            insert.Parameters.AddWithValue("@command", (object?)commandId ?? DBNull.Value);
            insert.Parameters.AddWithValue(
                "@source", sourceUploadId?.ToString("D") ?? (object)DBNull.Value);
            insert.Parameters.AddWithValue("@mode", sourceMode);
            insert.Parameters.AddWithValue(
                "@approval", (object?)spec.ApprovalId ?? DBNull.Value);
            insert.Parameters.AddWithValue(
                "@grant", (object?)spec.GrantDigest ?? DBNull.Value);
            insert.Parameters.AddWithValue("@prepared", DateTimeOffset.UtcNow.ToString("o"));
            insert.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    private void UpsertPricingJobAtomic(
        PricingJobSpec spec,
        string status,
        int total,
        int completed,
        int failed)
    {
        lock (_connLock)
        {
            using var transaction = _conn.BeginTransaction();
            UpsertPricingJobRow(transaction, spec, status, total, completed, failed);
            if (status == PricingJobStatus.Completed)
                StageTerminalPricingPayloadIfPrepared(
                    transaction, spec.JobId, status, total, completed, failed);
            transaction.Commit();
        }
    }

    private void StageTerminalPricingPayloadIfPrepared(
        SqliteTransaction transaction,
        string jobId,
        string status,
        int total,
        int completed,
        int failed)
    {
        if (status != PricingJobStatus.Completed)
            return;

        string? commandId;
        Guid? sourceUploadId;
        string sourceMode;
        string? approvalId;
        string? grantDigest;
        using (var intent = CreateCommand(transaction, """
            SELECT delivery.command_id, delivery.source_upload_id,
                   delivery.source_mode, delivery.approval_id,
                   delivery.grant_digest,
                   identity.authority_approval_id,
                   identity.authority_approval_digest,
                   job.approval_id, job.grant_digest,
                   command_intent.pricing_approval_id,
                   command_intent.pricing_grant_digest,
                   identity.modality
              FROM pricing_result_delivery_intents delivery
              JOIN pricing_jobs job ON job.job_id = delivery.job_id
              JOIN pricing_job_input_identity identity
                ON identity.job_id = job.job_id
              LEFT JOIN pricing_command_execution_intents command_intent
                ON command_intent.command_id = delivery.command_id
             WHERE delivery.job_id = @job
            """))
        {
            intent.Parameters.AddWithValue("@job", jobId);
            using var reader = intent.ExecuteReader();
            if (!reader.Read()) return;
            commandId = reader.IsDBNull(0) ? null : reader.GetString(0);
            sourceUploadId = reader.IsDBNull(1)
                ? null
                : Guid.ParseExact(reader.GetString(1), "D");
            sourceMode = reader.GetString(2);
            approvalId = reader.IsDBNull(5) ? null : reader.GetString(5);
            grantDigest = reader.IsDBNull(6) ? null : reader.GetString(6);
            if (approvalId is null || grantDigest is null ||
                commandId is not null &&
                 (Enumerable.Range(3, 8).Any(reader.IsDBNull) ||
                 !string.Equals(
                    reader.GetString(3), approvalId, StringComparison.Ordinal) ||
                 !FixedApprovalHexEquals(reader.GetString(4), grantDigest) ||
                 !string.Equals(
                    reader.GetString(7), approvalId, StringComparison.Ordinal) ||
                 !FixedApprovalHexEquals(reader.GetString(8), grantDigest) ||
                 !string.Equals(
                     reader.GetString(9), approvalId, StringComparison.Ordinal) ||
                 !FixedApprovalHexEquals(reader.GetString(10), grantDigest) ||
                 !string.Equals(
                     reader.GetString(11), sourceMode, StringComparison.Ordinal)))
                throw new InvalidOperationException(
                    "pricing_job_authority_binding_invalid");
        }

        var results = ReadPricingResults(transaction, jobId);
        var payload = PricingJobCloudUploader.BuildPersistedPayloadEnvelope(
            jobId, commandId, status, sourceMode, total, completed, failed,
            results, approvalId, grantDigest);
        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(payload.Json))).ToLowerInvariant();
        StagePricingResultPayload(
            transaction,
            jobId,
            commandId,
            sourceUploadId,
            payload.Json,
            digest,
            payload.ItemCount,
            status == PricingJobStatus.Completed);

        using var mark = CreateCommand(transaction, """
            UPDATE pricing_result_delivery_intents
               SET terminal_at = COALESCE(terminal_at, @terminal)
             WHERE job_id = @job
            """);
        mark.Parameters.AddWithValue("@terminal", DateTimeOffset.UtcNow.ToString("o"));
        mark.Parameters.AddWithValue("@job", jobId);
        if (mark.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("Pricing delivery intent terminal conflict.");
    }

    private static void UpsertPricingJobRow(
        SqliteTransaction transaction,
        PricingJobSpec spec,
        string status,
        int total,
        int completed,
        int failed)
    {
        if ((spec.ApprovalId is null) != (spec.GrantDigest is null) ||
            spec.ApprovalId is not null &&
            !IsCanonicalPricingApprovalId(spec.ApprovalId) ||
            spec.GrantDigest is not null &&
            !IsLowerHexSha256(spec.GrantDigest))
            throw new InvalidOperationException("pricing_result_identity_invalid");

        using (var identity = transaction.Connection!.CreateCommand())
        {
            identity.Transaction = transaction;
            identity.CommandText = """
                SELECT excel_path, ndc_column, supplier_column, cost_column,
                       approval_id, grant_digest
                  FROM pricing_jobs
                 WHERE job_id = @id
                """;
            identity.Parameters.AddWithValue("@id", spec.JobId);
            using var reader = identity.ExecuteReader();
            if (reader.Read() &&
                (reader.GetString(0) != spec.ExcelPath ||
                 reader.GetString(1) != spec.NdcColumn ||
                 reader.GetString(2) != spec.SupplierColumn ||
                 reader.GetString(3) != spec.CostColumn ||
                 (reader.IsDBNull(4) ? null : reader.GetString(4)) !=
                    spec.ApprovalId ||
                 (reader.IsDBNull(5) ? null : reader.GetString(5)) !=
                    spec.GrantDigest))
                throw new InvalidOperationException(
                    "pricing_job_spec_identity_conflict");
        }

        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO pricing_jobs (
                job_id, excel_path, ndc_column, supplier_column, cost_column,
                approval_id, grant_digest,
                status, total_items, completed_items, failed_items, updated_at
            ) VALUES (
                @id, @path, @ndc, @supplier, @cost,
                @approval, @grant,
                @status, @total, @completed, @failed, datetime('now')
            ) ON CONFLICT(job_id) DO UPDATE SET
                status = excluded.status,
                total_items = excluded.total_items,
                completed_items = excluded.completed_items,
                failed_items = excluded.failed_items,
                updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("@id", spec.JobId);
        command.Parameters.AddWithValue("@path", spec.ExcelPath);
        command.Parameters.AddWithValue("@ndc", spec.NdcColumn);
        command.Parameters.AddWithValue("@supplier", spec.SupplierColumn);
        command.Parameters.AddWithValue("@cost", spec.CostColumn);
        command.Parameters.AddWithValue(
            "@approval", (object?)spec.ApprovalId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@grant", (object?)spec.GrantDigest ?? DBNull.Value);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@total", total);
        command.Parameters.AddWithValue("@completed", completed);
        command.Parameters.AddWithValue("@failed", failed);
        command.ExecuteNonQuery();
    }

    private static List<SupplierPriceResult> ReadPricingResults(
        SqliteTransaction transaction,
        string jobId)
    {
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT job_id, row_index, ndc, found, supplier_name, cost_per_unit,
                   baseline_cost_per_unit, quantity, error_message, observations_json,
                   omitted_selector_observations
              FROM pricing_results
             WHERE job_id = @job
             ORDER BY row_index
            """;
        command.Parameters.AddWithValue("@job", jobId);
        using var reader = command.ExecuteReader();
        var results = new List<SupplierPriceResult>();
        while (reader.Read())
        {
            results.Add(new(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetInt32(3) == 1,
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : (decimal)reader.GetDouble(5),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9)
                    ? null
                    : JsonSerializer.Deserialize<List<SelectorObservation>>(
                        reader.GetString(9)),
                reader.IsDBNull(6) ? null : (decimal)reader.GetDouble(6),
                reader.IsDBNull(7) ? null : (decimal)reader.GetDouble(7),
                reader.GetInt32(10)));
        }
        return results;
    }
}
