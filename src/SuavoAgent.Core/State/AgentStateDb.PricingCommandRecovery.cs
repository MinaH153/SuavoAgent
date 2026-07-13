using System.Globalization;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Pricing;

namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    internal sealed record PricingCommandExecutionIntent(
        string CommandId,
        string CommandKind,
        string OwnerId,
        string State,
        DateTimeOffset RegisteredAt,
        DateTimeOffset UpdatedAt,
        string? ExecutionMode = null,
        string? AutonomyExecutionMode = null,
        string? AdmissionScopeDigest = null,
        bool TrustedIdentity = false,
        DateTimeOffset? AdmittedAt = null,
        string? SignedCheckpointDigest = null,
        string? ApprovalId = null,
        string? GrantDigest = null);

    internal enum PricingCommandRecoveryKind
    {
        None,
        TerminalAck,
        ResultPending,
        ResultAccepted,
        ResultTerminal,
    }

    internal sealed record PricingCommandRecoveryEvidence(
        PricingCommandRecoveryKind Kind,
        PricingTerminalAck? TerminalAck = null);

    internal bool TryRecordNonceAndRegisterPricingIntent(
        string nonce,
        string commandId,
        string commandKind,
        string ownerId) => TryRecordNonceAndRegisterPricingIntent(
            nonce,
            commandId,
            commandKind,
            ownerId,
            verifiedCommand: null);

    internal bool TryRecordNonceAndRegisterPricingIntent(
        string nonce,
        string commandId,
        string commandKind,
        string ownerId,
        SignedCommand? verifiedCommand,
        string? approvalId = null,
        string? grantDigest = null)
    {
        ValidatePricingIntentIdentity(commandId, commandKind, ownerId);
        if ((approvalId is null) != (grantDigest is null) ||
            approvalId is not null && !IsCanonicalPricingApprovalId(approvalId) ||
            grantDigest is not null && !IsLowerHexSha256(grantDigest))
            throw new ArgumentException("Pricing grant binding is invalid.");
        var checkpoint = BuildSignedCheckpoint(
            nonce,
            commandId,
            commandKind,
            verifiedCommand);
        lock (_connLock)
        {
            try
            {
                using var transaction = _conn.BeginTransaction(
                    System.Data.IsolationLevel.Serializable);
                var now = DateTimeOffset.UtcNow.ToString("O");
                using (var nonceInsert = CreateCommand(transaction, """
                    INSERT INTO command_nonces (nonce, received_at)
                    VALUES (@nonce, @now)
                    """))
                {
                    nonceInsert.Parameters.AddWithValue("@nonce", nonce);
                    nonceInsert.Parameters.AddWithValue("@now", now);
                    nonceInsert.ExecuteNonQuery();
                }
                using (var intentInsert = CreateCommand(transaction, """
                    INSERT INTO pricing_command_execution_intents (
                        command_id, command_kind, owner_id, state,
                        registered_at, updated_at,
                        signed_agent_id, signed_machine_fingerprint,
                        signed_timestamp, signed_nonce, signed_data_hash,
                        signed_key_id, signed_signature, signed_expires_at,
                        signed_checkpoint_digest, pricing_approval_id,
                        pricing_grant_digest)
                    VALUES (
                        @command, @kind, @owner, 'in_progress', @now, @now,
                        @agent, @machine, @timestamp, @signed_nonce, @data_hash,
                        @key_id, @signature, @expires_at, @checkpoint,
                        @approval_id, @grant_digest)
                    """))
                {
                    intentInsert.Parameters.AddWithValue("@command", commandId);
                    intentInsert.Parameters.AddWithValue("@kind", commandKind);
                    intentInsert.Parameters.AddWithValue("@owner", ownerId);
                    intentInsert.Parameters.AddWithValue("@now", now);
                    intentInsert.Parameters.AddWithValue(
                        "@agent", (object?)checkpoint?.AgentId ?? DBNull.Value);
                    intentInsert.Parameters.AddWithValue(
                        "@machine", (object?)checkpoint?.MachineFingerprint ?? DBNull.Value);
                    intentInsert.Parameters.AddWithValue(
                        "@timestamp", (object?)checkpoint?.Timestamp ?? DBNull.Value);
                    intentInsert.Parameters.AddWithValue(
                        "@signed_nonce", (object?)checkpoint?.Nonce ?? DBNull.Value);
                    intentInsert.Parameters.AddWithValue(
                        "@data_hash", (object?)checkpoint?.DataHash ?? DBNull.Value);
                    intentInsert.Parameters.AddWithValue(
                        "@key_id", (object?)checkpoint?.KeyId ?? DBNull.Value);
                    intentInsert.Parameters.AddWithValue(
                        "@signature", (object?)checkpoint?.Signature ?? DBNull.Value);
                    intentInsert.Parameters.AddWithValue(
                        "@expires_at", (object?)checkpoint?.ExpiresAt ?? DBNull.Value);
                    intentInsert.Parameters.AddWithValue(
                        "@checkpoint", (object?)checkpoint?.Digest ?? DBNull.Value);
                    intentInsert.Parameters.AddWithValue(
                        "@approval_id", (object?)approvalId ?? DBNull.Value);
                    intentInsert.Parameters.AddWithValue(
                        "@grant_digest", (object?)grantDigest ?? DBNull.Value);
                    intentInsert.ExecuteNonQuery();
                }
                transaction.Commit();
                return true;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                // Either the signed envelope nonce or the command authority was
                // already consumed. Both are terminal replay rejections.
                return false;
            }
        }
    }

    internal bool MarkPricingCommandIntentAdmitted(
        string commandId,
        string executionMode,
        string autonomyExecutionMode,
        string scopeDigest,
        bool trustedIdentity)
    {
        if (!PricingTerminalAck.IsCanonicalCommandId(commandId) ||
            executionMode is not ("sql" or "uia" or "vision") ||
            autonomyExecutionMode is not ("supervised" or "auto") ||
            !IsLowerHex64(scopeDigest))
            throw new ArgumentException("Pricing command admission is invalid.");
        lock (_connLock)
        {
            using (var existing = _conn.CreateCommand())
            {
                existing.CommandText = """
                    SELECT execution_mode, autonomy_execution_mode,
                           admission_scope_digest, admission_trusted_identity,
                           admitted_at
                      FROM pricing_command_execution_intents
                     WHERE command_id = @command
                    """;
                existing.Parameters.AddWithValue("@command", commandId);
                using var reader = existing.ExecuteReader();
                if (!reader.Read()) return false;
                if (!reader.IsDBNull(4))
                    return reader.GetString(0) == executionMode &&
                           reader.GetString(1) == autonomyExecutionMode &&
                           reader.GetString(2) == scopeDigest &&
                           reader.GetInt32(3) == (trustedIdentity ? 1 : 0);
            }

            using var command = _conn.CreateCommand();
            command.CommandText = """
                UPDATE pricing_command_execution_intents
                   SET execution_mode = @execution,
                       autonomy_execution_mode = @autonomy,
                       admission_scope_digest = @scope,
                       admission_trusted_identity = @trusted,
                       admitted_at = @now,
                       updated_at = @now
                 WHERE command_id = @command
                   AND state = 'in_progress'
                   AND admitted_at IS NULL
                """;
            command.Parameters.AddWithValue("@execution", executionMode);
            command.Parameters.AddWithValue("@autonomy", autonomyExecutionMode);
            command.Parameters.AddWithValue("@scope", scopeDigest);
            command.Parameters.AddWithValue("@trusted", trustedIdentity ? 1 : 0);
            command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("@command", commandId);
            return command.ExecuteNonQuery() == 1;
        }
    }

    internal IReadOnlyList<PricingCommandExecutionIntent>
        GetRecoverablePricingCommandIntents(string currentOwnerId, int maximum)
    {
        ValidateOwnerId(currentOwnerId);
        if (maximum is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(maximum));
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                SELECT command_id, command_kind, owner_id, state,
                       registered_at, updated_at, pricing_approval_id,
                       pricing_grant_digest
                  FROM pricing_command_execution_intents
                 WHERE (owner_id != @owner AND state = 'in_progress')
                    OR state = 'result_pending'
                 ORDER BY registered_at ASC
                 LIMIT @maximum
                """;
            command.Parameters.AddWithValue("@owner", currentOwnerId);
            command.Parameters.AddWithValue("@maximum", maximum);
            var values = new List<PricingCommandExecutionIntent>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                values.Add(new(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    ParsePricingTerminalAckTimestamp(reader.GetString(4)),
                    ParsePricingTerminalAckTimestamp(reader.GetString(5)),
                    ApprovalId: reader.IsDBNull(6) ? null : reader.GetString(6),
                    GrantDigest: reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
            return values;
        }
    }

    internal IReadOnlyList<PricingCommandExecutionIntent>
        GetResumableAdmittedPricingCommandIntents(
            string currentOwnerId,
            int maximum,
            IReadOnlyDictionary<string, string>? trustedPublicKeys = null,
            DateTimeOffset? now = null)
    {
        ValidateOwnerId(currentOwnerId);
        if (maximum is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(maximum));
        trustedPublicKeys ??= RemoteCommandTrust.CreateProductionKeyRegistry();
        _ = now;
        var commandIds = new List<string>();
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = RecoveryCandidateSql("intent.owner_id != @owner") +
                " ORDER BY intent.registered_at ASC LIMIT @maximum";
            command.Parameters.AddWithValue("@owner", currentOwnerId);
            command.Parameters.AddWithValue("@maximum", maximum);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                commandIds.Add(reader.GetString(0));
        }

        return commandIds
            .Select(ReadPricingCommandExecutionIntent)
            .Where(intent => intent is not null &&
                TryReadVerifiedPricingCheckpoint(
                    intent.CommandId,
                    trustedPublicKeys,
                    out _))
            .Cast<PricingCommandExecutionIntent>()
            .ToArray();
    }

    internal IReadOnlyList<PricingCommandExecutionIntent>
        GetExpiredAdmittedPricingAuthorityCommandIntents(
            string currentOwnerId,
            int maximum,
            DateTimeOffset now,
            IReadOnlyDictionary<string, string>? trustedPublicKeys = null)
    {
        ValidateOwnerId(currentOwnerId);
        if (maximum is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(maximum));
        trustedPublicKeys ??= RemoteCommandTrust.CreateProductionKeyRegistry();
        var commandIds = new List<string>();
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                SELECT intent.command_id
                  FROM pricing_command_execution_intents intent
                  JOIN pricing_result_delivery_intents delivery
                    ON delivery.command_id = intent.command_id
                   AND delivery.source_mode = intent.execution_mode
                  JOIN pricing_jobs job ON job.job_id = delivery.job_id
                  JOIN pricing_job_input_identity identity
                    ON identity.job_id = job.job_id
                 WHERE intent.state = 'in_progress'
                   AND intent.owner_id != @owner
                   AND intent.admitted_at IS NOT NULL
                   AND intent.admission_trusted_identity = 1
                   AND intent.signed_checkpoint_digest IS NOT NULL
                   AND intent.pricing_approval_id IS NOT NULL
                   AND intent.pricing_grant_digest IS NOT NULL
                   AND delivery.approval_id = intent.pricing_approval_id
                   AND delivery.grant_digest = intent.pricing_grant_digest
                   AND job.approval_id = intent.pricing_approval_id
                   AND job.grant_digest = intent.pricing_grant_digest
                   AND identity.modality = intent.execution_mode
                   AND identity.authority_approval_id = intent.pricing_approval_id
                   AND identity.authority_approval_digest = intent.pricing_grant_digest
                   AND identity.authority_expires_at_utc <= @now
                   AND job.status IN ('running','halted')
                 ORDER BY intent.registered_at ASC
                 LIMIT @maximum
                """;
            command.Parameters.AddWithValue("@owner", currentOwnerId);
            command.Parameters.AddWithValue("@now", now.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("@maximum", maximum);
            using var reader = command.ExecuteReader();
            while (reader.Read()) commandIds.Add(reader.GetString(0));
        }

        return commandIds
            .Select(ReadPricingCommandExecutionIntent)
            .Where(intent => intent is not null &&
                TryReadVerifiedPricingCheckpoint(
                    intent.CommandId,
                    trustedPublicKeys,
                    out _))
            .Cast<PricingCommandExecutionIntent>()
            .ToArray();
    }

    internal bool IsPricingCommandResumeReady(
        string commandId,
        IReadOnlyDictionary<string, string>? trustedPublicKeys = null)
    {
        if (!PricingTerminalAck.IsCanonicalCommandId(commandId))
            return false;
        trustedPublicKeys ??= RemoteCommandTrust.CreateProductionKeyRegistry();
        lock (_connLock)
        {
            using var dbCommand = _conn.CreateCommand();
            dbCommand.CommandText = ResumeReadySql("intent.command_id = @command") +
                " LIMIT 1";
            dbCommand.Parameters.AddWithValue("@command", commandId);
            dbCommand.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
            if (dbCommand.ExecuteScalar() is null)
                return false;
        }
        return TryReadVerifiedPricingCheckpoint(
                   commandId,
                   trustedPublicKeys,
                   out var command) &&
               command is not null &&
               SignedCommandVerifier.VerifyExecutionAuthorityAt(
                   command,
                   DateTimeOffset.UtcNow).IsValid;
    }

    private static string ResumeReadySql(string extraPredicate) => $$"""
        SELECT intent.command_id
          FROM pricing_command_execution_intents intent
          JOIN pricing_result_delivery_intents delivery
            ON delivery.command_id = intent.command_id
           AND delivery.source_mode = intent.execution_mode
          JOIN pricing_jobs job ON job.job_id = delivery.job_id
          JOIN pricing_job_input_identity identity ON identity.job_id = job.job_id
         WHERE intent.state = 'in_progress'
           AND intent.admitted_at IS NOT NULL
           AND intent.admission_trusted_identity = 1
           AND intent.signed_checkpoint_digest IS NOT NULL
           AND intent.pricing_approval_id IS NOT NULL
           AND intent.pricing_grant_digest IS NOT NULL
           AND delivery.approval_id = intent.pricing_approval_id
           AND delivery.grant_digest = intent.pricing_grant_digest
           AND job.approval_id = intent.pricing_approval_id
           AND job.grant_digest = intent.pricing_grant_digest
           AND identity.modality = intent.execution_mode
           AND identity.authority_approval_id = intent.pricing_approval_id
           AND identity.authority_approval_digest = intent.pricing_grant_digest
           AND identity.fresh_until_utc >= @now
           AND identity.authority_expires_at_utc >= @now
           AND job.status IN ('running','halted')
           AND {{extraPredicate}}
        """;

    private static string RecoveryCandidateSql(string extraPredicate) => $$"""
        SELECT intent.command_id
          FROM pricing_command_execution_intents intent
          JOIN pricing_result_delivery_intents delivery
            ON delivery.command_id = intent.command_id
           AND delivery.source_mode = intent.execution_mode
          JOIN pricing_jobs job ON job.job_id = delivery.job_id
         WHERE intent.state = 'in_progress'
           AND intent.admitted_at IS NOT NULL
           AND intent.admission_trusted_identity = 1
           AND intent.signed_checkpoint_digest IS NOT NULL
           AND job.status IN ('running','halted')
           AND {{extraPredicate}}
        """;

    private PricingCommandExecutionIntent? ReadPricingCommandExecutionIntent(
        string commandId)
    {
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                SELECT command_id, command_kind, owner_id, state,
                       registered_at, updated_at, execution_mode,
                       autonomy_execution_mode, admission_scope_digest,
                       admission_trusted_identity, admitted_at,
                       signed_checkpoint_digest, pricing_approval_id,
                       pricing_grant_digest
                  FROM pricing_command_execution_intents
                 WHERE command_id = @command
                """;
            command.Parameters.AddWithValue("@command", commandId);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            return new(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                ParsePricingTerminalAckTimestamp(reader.GetString(4)),
                ParsePricingTerminalAckTimestamp(reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                !reader.IsDBNull(9) && reader.GetInt32(9) == 1,
                reader.IsDBNull(10)
                    ? null
                    : ParsePricingTerminalAckTimestamp(reader.GetString(10)),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13));
        }
    }

    internal PricingCommandRecoveryEvidence GetPricingCommandRecoveryEvidence(
        string commandId)
    {
        lock (_connLock)
        {
            if (ReadPricingTerminalAck(commandId, null) is not null)
                return new(PricingCommandRecoveryKind.TerminalAck);

            var result = ReadPricingResultRecoveryRow(commandId);
            if (result is null)
                return new(PricingCommandRecoveryKind.None);
            if (result.Value.State == "accepted")
                return new(PricingCommandRecoveryKind.ResultAccepted);
            var terminalReason = ReadPricingResultTerminalReason(
                result.Value.JobId,
                result.Value.PayloadSha256);
            if (terminalReason is null)
                return new(PricingCommandRecoveryKind.ResultPending);
            return new(
                PricingCommandRecoveryKind.ResultTerminal,
                TryBuildResultTerminalAck(
                    result.Value.JobId,
                    result.Value.PayloadJson,
                    terminalReason));
        }
    }

    internal bool MarkPricingCommandIntentResultPending(string commandId) =>
        MarkPricingCommandIntentState(
            commandId,
            "result_pending");

    internal bool MarkPricingCommandIntentCompleted(string commandId) =>
        MarkPricingCommandIntentState(
            commandId,
            "completed");

    internal bool MarkPricingCommandIntentTerminal(string commandId) =>
        MarkPricingCommandIntentState(
            commandId,
            "terminal");

    private bool MarkPricingCommandIntentState(
        string commandId,
        string state)
    {
        if (!PricingTerminalAck.IsCanonicalCommandId(commandId))
            throw new ArgumentException("Pricing command id is invalid.");
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = state switch
            {
                "result_pending" => """
                    UPDATE pricing_command_execution_intents
                       SET state = @state, updated_at = @now
                     WHERE command_id = @command AND state = 'in_progress'
                    """,
                "completed" => """
                    UPDATE pricing_command_execution_intents
                       SET state = @state, updated_at = @now
                     WHERE command_id = @command
                       AND state IN ('in_progress','result_pending','completed')
                    """,
                "terminal" => """
                    UPDATE pricing_command_execution_intents
                       SET state = @state, updated_at = @now
                     WHERE command_id = @command
                       AND state IN ('in_progress','result_pending','terminal')
                    """,
                _ => throw new ArgumentOutOfRangeException(nameof(state)),
            };
            command.Parameters.AddWithValue("@state", state);
            command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("@command", commandId);
            return command.ExecuteNonQuery() == 1;
        }
    }

    private (string JobId, string PayloadJson, string PayloadSha256, string State)?
        ReadPricingResultRecoveryRow(string commandId)
    {
        using (var current = _conn.CreateCommand())
        {
            current.CommandText = """
                SELECT job_id, payload_json, payload_sha256, state
                  FROM pricing_result_outbox_v2
                 WHERE command_id = @command
                 ORDER BY generation DESC
                 LIMIT 1
                """;
            current.Parameters.AddWithValue("@command", commandId);
            using var reader = current.ExecuteReader();
            if (reader.Read())
                return (
                    reader.GetString(0), reader.GetString(1),
                    reader.GetString(2), reader.GetString(3));
        }
        using var legacy = _conn.CreateCommand();
        legacy.CommandText = """
            SELECT job_id, payload_json, payload_sha256, state
              FROM pricing_result_outbox
             WHERE command_id = @command
             LIMIT 1
            """;
        legacy.Parameters.AddWithValue("@command", commandId);
        using var legacyReader = legacy.ExecuteReader();
        return legacyReader.Read()
            ? (
                legacyReader.GetString(0), legacyReader.GetString(1),
                legacyReader.GetString(2), legacyReader.GetString(3))
            : null;
    }

    private string? ReadPricingResultTerminalReason(
        string jobId,
        string payloadSha256)
    {
        using var command = _conn.CreateCommand();
        command.CommandText = """
            SELECT reason_code
              FROM pricing_result_outbox_terminal_receipts
             WHERE job_id = @job AND payload_sha256 = @digest
             LIMIT 1
            """;
        command.Parameters.AddWithValue("@job", jobId);
        command.Parameters.AddWithValue("@digest", payloadSha256);
        return command.ExecuteScalar() as string;
    }

    private static PricingTerminalAck? TryBuildResultTerminalAck(
        string jobId,
        string payloadJson,
        string terminalReason)
    {
        if (IsPermanentPricingAuthorityTerminalReason(terminalReason))
            return PricingTerminalAck.Early(terminalReason);

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            // The immutable result row binds job_id and command_id outside the
            // payload. The cloud body intentionally omits jobId because it is
            // already bound by the signed route.
            if (!root.TryGetProperty("status", out var status) ||
                status.ValueKind != JsonValueKind.String ||
                status.GetString() != "completed" ||
                !root.TryGetProperty("mode", out var mode) ||
                mode.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("totalItems", out var total) ||
                !total.TryGetInt32(out var totalItems) ||
                !root.TryGetProperty("completedItems", out var completed) ||
                !completed.TryGetInt32(out var completedItems) ||
                !root.TryGetProperty("failedItems", out var failed) ||
                !failed.TryGetInt32(out var failedItems) ||
                completedItems + failedItems != totalItems)
                return null;
            return PricingTerminalAck.PricingFailed(
                jobId,
                mode.GetString() ?? "",
                totalItems,
                completedItems,
                failedItems,
                "pricing_job_failed");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static bool IsPermanentPricingAuthorityTerminalReason(
        string code) => code is
        "pricing_cost_basis_approval_revoked" or
        "pricing_cloud_authority_revoked" or
        "pricing_result_manual_reconciliation_required" or
        "pricing_cost_basis_approval_expired" or
        "pricing_cost_basis_approval_invalid" or
        "pricing_cost_basis_approval_required" or
        "pricing_job_authority_identity_invalid" or
        "pricing_job_authority_binding_missing" or
        "pricing_job_authority_binding_invalid";

    private static void ValidatePricingIntentIdentity(
        string commandId,
        string commandKind,
        string ownerId)
    {
        if (!PricingTerminalAck.IsCanonicalCommandId(commandId) ||
            commandKind is not (
                "run_pricing_job" or "find_and_run_pricing_job"))
            throw new ArgumentException("Pricing command intent is invalid.");
        ValidateOwnerId(ownerId);
    }

    private static void ValidateOwnerId(string ownerId)
    {
        if (ownerId.Length != 32 ||
            ownerId.Any(ch => ch is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f')))
            throw new ArgumentException("Pricing command owner is invalid.");
    }
}
