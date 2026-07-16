using System.Text.Json;
using Microsoft.Data.Sqlite;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.Workers;

namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    internal enum PomApprovalLedgerKind
    {
        Apply,
        Resume,
        Terminal,
        Conflict,
    }

    internal sealed record PomApprovalLedgerResult(
        PomApprovalLedgerKind Kind,
        bool Succeeded,
        string OutcomeCode);

    internal sealed record PomApprovalLedgerRow(
        string CommandId,
        string PayloadDigest,
        string? PomId,
        string? SessionId,
        string? ModelDigest,
        string? TemplateDigest,
        string? ApprovedBy,
        string ResultCode,
        string AppliedAt,
        string? CompletedAt);

    /// <summary>
    /// Under one SQLite transaction, validates the exact frozen POM binding,
    /// persists the human approval, advances model -&gt; approved/supervised, and
    /// records the stable command id + exact payload digest.  No ACK may be sent
    /// until this transaction has committed.
    /// </summary>
    internal PomApprovalLedgerResult ApplyPomApproval(
        PomApprovalCommand command,
        string expectedPharmacyId)
    {
        lock (_connLock)
        {
            using var txn = _conn.BeginTransaction();
            var existing = ReadPomApprovalLedger(txn, command.CommandId);
            if (existing is not null)
            {
                txn.Commit();
                if (!string.Equals(existing.PayloadDigest, command.PayloadDigest, StringComparison.Ordinal))
                    return new(PomApprovalLedgerKind.Conflict, false, "command_payload_conflict");
                if (existing.ResultCode == "applying")
                    return new(PomApprovalLedgerKind.Resume, false, "applying");
                return new(
                    PomApprovalLedgerKind.Terminal,
                    IsPomApprovalSuccess(existing.ResultCode),
                    existing.ResultCode);
            }

            if (string.IsNullOrWhiteSpace(expectedPharmacyId))
                return CommitRejectedPomApproval(txn, command, "pom_approval_pharmacy_missing");

            string? pharmacyId = null;
            string? phase = null;
            string? mode = null;
            string? storedDigest = null;
            string? frozenPom = null;
            using (var current = CreateCommand(txn, """
                SELECT pharmacy_id, phase, mode, approved_model_digest, pom_snapshot
                  FROM learning_session
                 WHERE id = @session
                """))
            {
                current.Parameters.AddWithValue("@session", command.SessionId);
                using var reader = current.ExecuteReader();
                if (reader.Read())
                {
                    pharmacyId = reader.GetString(0);
                    phase = reader.GetString(1);
                    mode = reader.GetString(2);
                    storedDigest = reader.IsDBNull(3) ? null : reader.GetString(3);
                    frozenPom = reader.IsDBNull(4) ? null : reader.GetString(4);
                }
            }

            if (pharmacyId is null)
                return CommitRejectedPomApproval(txn, command, "pom_approval_session_not_found");
            if (!string.Equals(pharmacyId, expectedPharmacyId, StringComparison.Ordinal))
                return CommitRejectedPomApproval(txn, command, "pom_approval_pharmacy_mismatch");
            if (phase is not ("model" or "approved" or "active"))
                return CommitRejectedPomApproval(txn, command, "pom_approval_phase_invalid");
            if (string.IsNullOrWhiteSpace(frozenPom))
                return CommitRejectedPomApproval(txn, command, "pom_approval_frozen_snapshot_missing");

            var recomputedModelDigest = PomExporter.ComputeDigest(
                expectedPharmacyId,
                command.SessionId,
                frozenPom);
            if (!string.Equals(
                    recomputedModelDigest,
                    command.ApprovedModelDigest,
                    StringComparison.Ordinal))
            {
                return CommitRejectedPomApproval(txn, command, "pom_approval_model_digest_mismatch");
            }

            if (!TryReadFrozenBinding(frozenPom, out var frozenBinding) ||
                !string.Equals(frozenBinding.SessionId, command.SessionId, StringComparison.Ordinal) ||
                !string.Equals(frozenBinding.PharmacyId, expectedPharmacyId, StringComparison.Ordinal))
            {
                return CommitRejectedPomApproval(txn, command, "pom_approval_frozen_binding_invalid");
            }
            if (!string.Equals(
                    frozenBinding.TemplateDigest,
                    command.ApprovedTemplateDigest,
                    StringComparison.Ordinal))
            {
                return CommitRejectedPomApproval(txn, command, "pom_approval_template_digest_mismatch");
            }
            if (phase is "approved" or "active" &&
                storedDigest is not null &&
                !string.Equals(storedDigest, command.ApprovedModelDigest, StringComparison.Ordinal))
            {
                return CommitRejectedPomApproval(txn, command, "pom_approval_existing_binding_conflict");
            }

            var appliedAt = DateTimeOffset.UtcNow.ToString("o");
            using (var update = CreateCommand(txn, """
                UPDATE learning_session
                   SET approved_model_digest = @model,
                       approved_by = @approved_by,
                       approved_at = @approved_at,
                       phase = CASE WHEN phase = 'model' THEN 'approved' ELSE phase END,
                       phase_changed_at = CASE WHEN phase = 'model' THEN @approved_at ELSE phase_changed_at END,
                       mode = CASE WHEN phase = 'model' AND mode = 'observer' THEN 'supervised' ELSE mode END
                 WHERE id = @session
                   AND pharmacy_id = @pharmacy
                   AND pom_snapshot = @pom
                   AND phase IN ('model', 'approved', 'active')
                """))
            {
                update.Parameters.AddWithValue("@model", command.ApprovedModelDigest);
                update.Parameters.AddWithValue("@approved_by", command.ApprovedBy);
                update.Parameters.AddWithValue("@approved_at", appliedAt);
                update.Parameters.AddWithValue("@session", command.SessionId);
                update.Parameters.AddWithValue("@pharmacy", expectedPharmacyId);
                update.Parameters.AddWithValue("@pom", frozenPom);
                if (update.ExecuteNonQuery() != 1)
                    return CommitRejectedPomApproval(txn, command, "pom_approval_concurrent_change");
            }

            InsertPomApprovalLedger(txn, command, "applying", appliedAt, completed: false);
            txn.Commit();
            return new(PomApprovalLedgerKind.Apply, false, "applying");
        }
    }

    internal PomApprovalLedgerResult CompletePomApproval(
        PomApprovalCommand command,
        bool succeeded,
        string outcomeCode)
    {
        if (!PomApprovalCommandContract.IsSafeResultCode(outcomeCode) ||
            succeeded != IsPomApprovalSuccess(outcomeCode))
            throw new ArgumentException("Invalid POM approval outcome", nameof(outcomeCode));

        lock (_connLock)
        {
            using var txn = _conn.BeginTransaction();
            var existing = ReadPomApprovalLedger(txn, command.CommandId);
            if (existing is null)
            {
                InsertPomApprovalLedger(
                    txn,
                    command,
                    outcomeCode,
                    DateTimeOffset.UtcNow.ToString("o"),
                    completed: true);
                txn.Commit();
                return new(PomApprovalLedgerKind.Terminal, succeeded, outcomeCode);
            }
            if (!string.Equals(existing.PayloadDigest, command.PayloadDigest, StringComparison.Ordinal))
            {
                txn.Commit();
                return new(PomApprovalLedgerKind.Conflict, false, "command_payload_conflict");
            }
            if (existing.ResultCode != "applying")
            {
                txn.Commit();
                return new(
                    PomApprovalLedgerKind.Terminal,
                    IsPomApprovalSuccess(existing.ResultCode),
                    existing.ResultCode);
            }

            using var update = CreateCommand(txn, """
                UPDATE pom_approval_commands
                   SET result_code = @result, completed_at = @completed
                 WHERE command_id = @command
                   AND payload_digest = @digest
                   AND result_code = 'applying'
                """);
            update.Parameters.AddWithValue("@result", outcomeCode);
            update.Parameters.AddWithValue("@completed", DateTimeOffset.UtcNow.ToString("o"));
            update.Parameters.AddWithValue("@command", command.CommandId);
            update.Parameters.AddWithValue("@digest", command.PayloadDigest);
            if (update.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("POM approval ledger completion race");
            txn.Commit();
            return new(PomApprovalLedgerKind.Terminal, succeeded, outcomeCode);
        }
    }

    internal PomApprovalLedgerResult RecordMalformedPomApproval(
        string commandId,
        string payloadDigest,
        string outcomeCode)
    {
        if (!PomApprovalCommandContract.IsSafeResultCode(outcomeCode) ||
            IsPomApprovalSuccess(outcomeCode))
            throw new ArgumentException("Invalid malformed-command outcome", nameof(outcomeCode));

        lock (_connLock)
        {
            using var txn = _conn.BeginTransaction();
            var existing = ReadPomApprovalLedger(txn, commandId);
            if (existing is not null)
            {
                txn.Commit();
                if (!string.Equals(existing.PayloadDigest, payloadDigest, StringComparison.Ordinal))
                    return new(PomApprovalLedgerKind.Conflict, false, "command_payload_conflict");
                return new(
                    PomApprovalLedgerKind.Terminal,
                    IsPomApprovalSuccess(existing.ResultCode),
                    existing.ResultCode);
            }

            var now = DateTimeOffset.UtcNow.ToString("o");
            using var insert = CreateCommand(txn, """
                INSERT INTO pom_approval_commands
                    (command_id, payload_digest, result_code, applied_at, completed_at)
                VALUES (@command, @digest, @result, @at, @at)
                """);
            insert.Parameters.AddWithValue("@command", commandId);
            insert.Parameters.AddWithValue("@digest", payloadDigest);
            insert.Parameters.AddWithValue("@result", outcomeCode);
            insert.Parameters.AddWithValue("@at", now);
            insert.ExecuteNonQuery();
            txn.Commit();
            return new(PomApprovalLedgerKind.Terminal, false, outcomeCode);
        }
    }

    internal PomApprovalLedgerRow? GetPomApprovalLedger(string commandId)
    {
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                SELECT command_id, payload_digest, pom_id, session_id, model_digest,
                       template_digest, approved_by, result_code, applied_at, completed_at
                  FROM pom_approval_commands
                 WHERE command_id = @command
                """;
            command.Parameters.AddWithValue("@command", commandId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadPomApprovalLedgerRow(reader) : null;
        }
    }

    private PomApprovalLedgerResult CommitRejectedPomApproval(
        SqliteTransaction txn,
        PomApprovalCommand command,
        string resultCode)
    {
        var now = DateTimeOffset.UtcNow.ToString("o");
        InsertPomApprovalLedger(txn, command, resultCode, now, completed: true);
        txn.Commit();
        return new(PomApprovalLedgerKind.Terminal, false, resultCode);
    }

    private void InsertPomApprovalLedger(
        SqliteTransaction txn,
        PomApprovalCommand command,
        string resultCode,
        string appliedAt,
        bool completed)
    {
        using var insert = CreateCommand(txn, """
            INSERT INTO pom_approval_commands
                (command_id, payload_digest, pom_id, session_id, model_digest,
                 template_digest, approved_by, result_code, applied_at, completed_at)
            VALUES
                (@command, @payload, @pom, @session, @model,
                 @template, @approved_by, @result, @applied, @completed)
            """);
        insert.Parameters.AddWithValue("@command", command.CommandId);
        insert.Parameters.AddWithValue("@payload", command.PayloadDigest);
        insert.Parameters.AddWithValue("@pom", command.PomId);
        insert.Parameters.AddWithValue("@session", command.SessionId);
        insert.Parameters.AddWithValue("@model", command.ApprovedModelDigest);
        insert.Parameters.AddWithValue("@template", command.ApprovedTemplateDigest);
        insert.Parameters.AddWithValue("@approved_by", command.ApprovedBy);
        insert.Parameters.AddWithValue("@result", resultCode);
        insert.Parameters.AddWithValue("@applied", appliedAt);
        insert.Parameters.AddWithValue("@completed", completed ? appliedAt : DBNull.Value);
        insert.ExecuteNonQuery();
    }

    private PomApprovalLedgerRow? ReadPomApprovalLedger(
        SqliteTransaction txn,
        string commandId)
    {
        using var command = CreateCommand(txn, """
            SELECT command_id, payload_digest, pom_id, session_id, model_digest,
                   template_digest, approved_by, result_code, applied_at, completed_at
              FROM pom_approval_commands
             WHERE command_id = @command
            """);
        command.Parameters.AddWithValue("@command", commandId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadPomApprovalLedgerRow(reader) : null;
    }

    private static PomApprovalLedgerRow ReadPomApprovalLedgerRow(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.GetString(7),
        reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9));

    private static bool IsPomApprovalSuccess(string code) =>
        code is "pom_approval_activated" or "pom_approval_already_active";

    private static bool TryReadFrozenBinding(
        string pomJson,
        out (string SessionId, string PharmacyId, string TemplateDigest) binding)
    {
        binding = default;
        try
        {
            using var document = JsonDocument.Parse(pomJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("sessionId", out var session) ||
                session.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("pharmacyId", out var pharmacy) ||
                pharmacy.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("learnedAdapterTemplate", out var template) ||
                template.ValueKind != JsonValueKind.Object ||
                !template.TryGetProperty("sessionId", out var templateSession) ||
                templateSession.ValueKind != JsonValueKind.String ||
                !template.TryGetProperty("templateDigest", out var templateDigest) ||
                templateDigest.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var sessionId = session.GetString() ?? "";
            var pharmacyId = pharmacy.GetString() ?? "";
            var nestedSessionId = templateSession.GetString() ?? "";
            var digest = templateDigest.GetString() ?? "";
            if (!string.Equals(sessionId, nestedSessionId, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(sessionId) ||
                string.IsNullOrWhiteSpace(pharmacyId) ||
                digest.Length != 64 ||
                digest.Any(ch => ch is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            {
                return false;
            }

            binding = (sessionId, pharmacyId, digest);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
