using System.Globalization;
using Microsoft.Data.Sqlite;

namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private readonly object _workflowAuditSync = new();

    internal WorkflowAuditEventOutboxEntry StageWorkflowAuditEvent(
        Guid eventId,
        string workflowRunId,
        int stepIndex,
        string verbName,
        string verbVersion,
        bool requestedDryRun,
        bool? effectiveDryRun,
        string outcome,
        int? execDurationMs,
        string? errorKind,
        int paramsFieldCount,
        int? beforeStateFieldCount,
        int? afterStateFieldCount,
        Func<int, WorkflowAuditPayloadBytes> payloadFactory)
    {
        ArgumentNullException.ThrowIfNull(payloadFactory);
        lock (_workflowAuditSync)
        {
            using var transaction = _conn.BeginTransaction();
            var existing = ReadWorkflowAuditEventById(eventId, transaction);
            if (existing is not null)
            {
                var existingPayload = payloadFactory(existing.ExecutionOrdinal);
                if (existing.WorkflowRunId != workflowRunId ||
                    existing.StepIndex != stepIndex ||
                    existing.VerbName != verbName ||
                    existing.VerbVersion != verbVersion ||
                    existing.RequestedDryRun != requestedDryRun ||
                    existing.EffectiveDryRun != effectiveDryRun ||
                    existing.Outcome != outcome ||
                    existing.ExecDurationMs != execDurationMs ||
                    existing.ErrorKind != errorKind ||
                    existing.ParamsFieldCount != paramsFieldCount ||
                    existing.BeforeStateFieldCount != beforeStateFieldCount ||
                    existing.AfterStateFieldCount != afterStateFieldCount ||
                    existing.PayloadJson != existingPayload.Json ||
                    existing.PayloadSha256 != existingPayload.Sha256)
                    throw new InvalidOperationException(
                        "Workflow audit event conflicts with durable evidence.");
                transaction.Commit();
                return existing;
            }

            using (var terminalCheck = _conn.CreateCommand())
            {
                terminalCheck.Transaction = transaction;
                terminalCheck.CommandText = """
                    SELECT 1 FROM workflow_completion_intents
                     WHERE workflow_run_id = @run
                     LIMIT 1
                    """;
                terminalCheck.Parameters.AddWithValue("@run", workflowRunId);
                if (terminalCheck.ExecuteScalar() is not null)
                    throw new InvalidOperationException(
                        "Workflow audit event cannot be staged after completion.");
            }

            int ordinal;
            using (var ordinalCommand = _conn.CreateCommand())
            {
                ordinalCommand.Transaction = transaction;
                ordinalCommand.CommandText = """
                    SELECT COALESCE(MAX(execution_ordinal) + 1, 0)
                      FROM workflow_audit_event_outbox
                     WHERE workflow_run_id = @run
                    """;
                ordinalCommand.Parameters.AddWithValue("@run", workflowRunId);
                ordinal = Convert.ToInt32(
                    ordinalCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            if (ordinal is < 0 or > 99999)
                throw new InvalidOperationException(
                    "Workflow audit execution ordinal is out of range.");

            var payload = payloadFactory(ordinal);
            using (var insert = _conn.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO workflow_audit_event_outbox (
                        event_id, workflow_run_id, execution_ordinal, step_index,
                        verb_name, verb_version, requested_dry_run,
                        effective_dry_run, outcome, exec_duration_ms, error_kind,
                        params_field_count, before_state_field_count,
                        after_state_field_count, payload_json, payload_sha256,
                        created_at
                    ) VALUES (
                        @event, @run, @ordinal, @step, @verb, @version,
                        @requested, @effective, @outcome, @duration, @error,
                        @params_count, @before_count, @after_count, @payload,
                        @digest, @created
                    )
                    """;
                insert.Parameters.AddWithValue("@event", eventId.ToString("D"));
                insert.Parameters.AddWithValue("@run", workflowRunId);
                insert.Parameters.AddWithValue("@ordinal", ordinal);
                insert.Parameters.AddWithValue("@step", stepIndex);
                insert.Parameters.AddWithValue("@verb", verbName);
                insert.Parameters.AddWithValue("@version", verbVersion);
                insert.Parameters.AddWithValue("@requested", requestedDryRun ? 1 : 0);
                insert.Parameters.AddWithValue(
                    "@effective", effectiveDryRun is null
                        ? DBNull.Value
                        : effectiveDryRun.Value ? 1 : 0);
                insert.Parameters.AddWithValue("@outcome", outcome);
                insert.Parameters.AddWithValue(
                    "@duration", execDurationMs is null ? DBNull.Value : execDurationMs.Value);
                insert.Parameters.AddWithValue(
                    "@error", errorKind is null ? DBNull.Value : errorKind);
                insert.Parameters.AddWithValue("@params_count", paramsFieldCount);
                insert.Parameters.AddWithValue(
                    "@before_count", beforeStateFieldCount is null
                        ? DBNull.Value
                        : beforeStateFieldCount.Value);
                insert.Parameters.AddWithValue(
                    "@after_count", afterStateFieldCount is null
                        ? DBNull.Value
                        : afterStateFieldCount.Value);
                insert.Parameters.AddWithValue("@payload", payload.Json);
                insert.Parameters.AddWithValue("@digest", payload.Sha256);
                insert.Parameters.AddWithValue(
                    "@created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
            return new WorkflowAuditEventOutboxEntry(
                eventId, workflowRunId, ordinal, stepIndex, verbName, verbVersion,
                requestedDryRun, effectiveDryRun, outcome, execDurationMs,
                errorKind, paramsFieldCount, beforeStateFieldCount,
                afterStateFieldCount, payload.Json, payload.Sha256, 0);
        }
    }

    internal WorkflowCompletionIntentEntry StageWorkflowCompletionIntent(
        Guid completionId,
        string workflowRunId,
        string outcome,
        string? reasonCode)
    {
        lock (_workflowAuditSync)
        {
            using var transaction = _conn.BeginTransaction();
            var existing = ReadWorkflowCompletionIntent(workflowRunId, transaction);
            if (existing is not null)
            {
                if (existing.Outcome != outcome || existing.ReasonCode != reasonCode)
                    throw new InvalidOperationException(
                        "Workflow completion intent conflicts with durable evidence.");
                transaction.Commit();
                return existing;
            }

            int count;
            int maxOrdinal;
            Guid? finalEventId = null;
            using (var summary = _conn.CreateCommand())
            {
                summary.Transaction = transaction;
                summary.CommandText = """
                    SELECT COUNT(*), COALESCE(MAX(execution_ordinal), -1)
                      FROM workflow_audit_event_outbox
                     WHERE workflow_run_id = @run
                    """;
                summary.Parameters.AddWithValue("@run", workflowRunId);
                using var reader = summary.ExecuteReader();
                if (!reader.Read())
                    throw new InvalidOperationException(
                        "Workflow audit summary is unavailable.");
                count = reader.GetInt32(0);
                maxOrdinal = reader.GetInt32(1);
            }
            if (count != maxOrdinal + 1)
                throw new InvalidOperationException(
                    "Workflow audit execution ordinals are not contiguous.");
            if (outcome == "completed" && count == 0)
                throw new InvalidOperationException(
                    "Completed workflow does not have an audit chain.");
            if (count > 0)
            {
                using var finalEvent = _conn.CreateCommand();
                finalEvent.Transaction = transaction;
                finalEvent.CommandText = """
                    SELECT event_id FROM workflow_audit_event_outbox
                     WHERE workflow_run_id = @run
                     ORDER BY execution_ordinal DESC
                     LIMIT 1
                    """;
                finalEvent.Parameters.AddWithValue("@run", workflowRunId);
                finalEventId = Guid.Parse((string)finalEvent.ExecuteScalar()!);
            }

            using (var insert = _conn.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO workflow_completion_intents (
                        completion_id, workflow_run_id, outcome, reason_code,
                        audit_event_count, final_event_id, created_at
                    ) VALUES (
                        @completion, @run, @outcome, @reason, @count, @final,
                        @created
                    )
                    """;
                insert.Parameters.AddWithValue(
                    "@completion", completionId.ToString("D"));
                insert.Parameters.AddWithValue("@run", workflowRunId);
                insert.Parameters.AddWithValue("@outcome", outcome);
                insert.Parameters.AddWithValue(
                    "@reason", reasonCode is null ? DBNull.Value : reasonCode);
                insert.Parameters.AddWithValue("@count", count);
                insert.Parameters.AddWithValue(
                    "@final", finalEventId is null
                        ? DBNull.Value
                        : finalEventId.Value.ToString("D"));
                insert.Parameters.AddWithValue(
                    "@created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
            return new WorkflowCompletionIntentEntry(
                completionId, workflowRunId, outcome, reasonCode, count,
                finalEventId);
        }
    }

    internal WorkflowCompletionMaterialization?
        GetNextWorkflowCompletionToMaterialize()
    {
        lock (_workflowAuditSync)
        {
            using var intentCommand = _conn.CreateCommand();
            intentCommand.CommandText = """
                SELECT i.completion_id, i.workflow_run_id, i.outcome,
                       i.reason_code, i.audit_event_count, i.final_event_id
                  FROM workflow_completion_intents i
                 WHERE NOT EXISTS (
                           SELECT 1 FROM workflow_completion_outbox o
                            WHERE o.completion_id = i.completion_id)
                   AND i.audit_event_count = (
                           SELECT COUNT(*) FROM workflow_audit_event_outbox e
                            WHERE e.workflow_run_id = i.workflow_run_id)
                   AND i.audit_event_count = (
                           SELECT COUNT(*)
                             FROM workflow_audit_event_outbox e
                            WHERE e.workflow_run_id = i.workflow_run_id
                              AND EXISTS (
                                  SELECT 1 FROM workflow_audit_event_attempts a
                                   WHERE a.event_id = e.event_id
                                     AND a.outcome_code = 'accepted'))
                 ORDER BY i.created_at, i.completion_id
                 LIMIT 1
                """;
            using var reader = intentCommand.ExecuteReader();
            if (!reader.Read()) return null;
            var intent = ReadCompletionIntent(reader);
            reader.Close();

            var digests = new List<string>(intent.AuditEventCount);
            using var digestCommand = _conn.CreateCommand();
            digestCommand.CommandText = """
                SELECT a.receipt_digest
                  FROM workflow_audit_event_outbox e
                  JOIN workflow_audit_event_attempts a
                    ON a.event_id = e.event_id
                   AND a.outcome_code = 'accepted'
                 WHERE e.workflow_run_id = @run
                 ORDER BY e.execution_ordinal
                """;
            digestCommand.Parameters.AddWithValue("@run", intent.WorkflowRunId);
            using var digestReader = digestCommand.ExecuteReader();
            while (digestReader.Read()) digests.Add(digestReader.GetString(0));
            if (digests.Count != intent.AuditEventCount)
                throw new InvalidOperationException(
                    "Workflow audit acceptance chain changed during materialization.");
            return new WorkflowCompletionMaterialization(intent, digests);
        }
    }

    internal WorkflowCompletionOutboxEntry StageWorkflowCompletionPayload(
        WorkflowCompletionIntentEntry intent,
        string auditChainDigest,
        string payloadJson,
        string payloadSha256)
    {
        lock (_workflowAuditSync)
        {
            using var transaction = _conn.BeginTransaction();
            var existing = ReadWorkflowCompletionOutbox(
                intent.CompletionId, transaction);
            if (existing is not null)
            {
                if (existing.AuditChainDigest != auditChainDigest ||
                    existing.PayloadJson != payloadJson ||
                    existing.PayloadSha256 != payloadSha256)
                    throw new InvalidOperationException(
                        "Workflow completion payload conflicts with durable evidence.");
                transaction.Commit();
                return existing;
            }

            using var insert = _conn.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO workflow_completion_outbox (
                    completion_id, workflow_run_id, audit_chain_digest,
                    payload_json, payload_sha256, created_at
                ) VALUES (
                    @completion, @run, @chain, @payload, @digest, @created
                )
                """;
            insert.Parameters.AddWithValue(
                "@completion", intent.CompletionId.ToString("D"));
            insert.Parameters.AddWithValue("@run", intent.WorkflowRunId);
            insert.Parameters.AddWithValue("@chain", auditChainDigest);
            insert.Parameters.AddWithValue("@payload", payloadJson);
            insert.Parameters.AddWithValue("@digest", payloadSha256);
            insert.Parameters.AddWithValue(
                "@created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            insert.ExecuteNonQuery();
            transaction.Commit();
            return new WorkflowCompletionOutboxEntry(
                intent.CompletionId, intent.WorkflowRunId, intent.Outcome,
                intent.ReasonCode, intent.AuditEventCount, intent.FinalEventId,
                auditChainDigest, payloadJson, payloadSha256, 0);
        }
    }

    internal WorkflowAuditEventOutboxEntry? GetNextDueWorkflowAuditEvent(
        DateTimeOffset now)
    {
        lock (_workflowAuditSync)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                SELECT e.event_id, e.workflow_run_id, e.execution_ordinal,
                       e.step_index, e.verb_name, e.verb_version,
                       e.requested_dry_run, e.effective_dry_run, e.outcome,
                       e.exec_duration_ms, e.error_kind, e.params_field_count,
                       e.before_state_field_count, e.after_state_field_count,
                       e.payload_json, e.payload_sha256,
                       (SELECT COUNT(*) FROM workflow_audit_event_attempts c
                         WHERE c.event_id = e.event_id)
                  FROM workflow_audit_event_outbox e
                 WHERE NOT EXISTS (
                           SELECT 1 FROM workflow_audit_event_attempts done
                            WHERE done.event_id = e.event_id
                              AND done.outcome_code IN (
                                  'accepted', 'terminal_rejection'))
                   AND COALESCE((
                           SELECT latest.next_attempt_at
                             FROM workflow_audit_event_attempts latest
                            WHERE latest.event_id = e.event_id
                            ORDER BY latest.attempt_number DESC
                            LIMIT 1), e.created_at) <= @now
                   AND NOT EXISTS (
                           SELECT 1 FROM workflow_audit_event_outbox prior
                            WHERE prior.workflow_run_id = e.workflow_run_id
                              AND prior.execution_ordinal < e.execution_ordinal
                              AND NOT EXISTS (
                                  SELECT 1 FROM workflow_audit_event_attempts accepted
                                   WHERE accepted.event_id = prior.event_id
                                     AND accepted.outcome_code = 'accepted'))
                 ORDER BY e.created_at, e.workflow_run_id, e.execution_ordinal
                 LIMIT 1
                """;
            command.Parameters.AddWithValue(
                "@now", now.ToString("O", CultureInfo.InvariantCulture));
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadWorkflowAuditEvent(reader) : null;
        }
    }

    internal WorkflowCompletionOutboxEntry? GetNextDueWorkflowCompletion(
        DateTimeOffset now)
    {
        lock (_workflowAuditSync)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                SELECT o.completion_id, o.workflow_run_id, i.outcome,
                       i.reason_code, i.audit_event_count, i.final_event_id,
                       o.audit_chain_digest, o.payload_json, o.payload_sha256,
                       (SELECT COUNT(*) FROM workflow_completion_attempts c
                         WHERE c.completion_id = o.completion_id)
                  FROM workflow_completion_outbox o
                  JOIN workflow_completion_intents i
                    ON i.completion_id = o.completion_id
                 WHERE NOT EXISTS (
                           SELECT 1 FROM workflow_completion_attempts done
                            WHERE done.completion_id = o.completion_id
                              AND done.outcome_code IN (
                                  'accepted', 'terminal_rejection'))
                   AND COALESCE((
                           SELECT latest.next_attempt_at
                             FROM workflow_completion_attempts latest
                            WHERE latest.completion_id = o.completion_id
                            ORDER BY latest.attempt_number DESC
                            LIMIT 1), o.created_at) <= @now
                 ORDER BY o.created_at, o.completion_id
                 LIMIT 1
                """;
            command.Parameters.AddWithValue(
                "@now", now.ToString("O", CultureInfo.InvariantCulture));
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadWorkflowCompletionOutbox(reader) : null;
        }
    }

    internal void RecordWorkflowAuditRetry(
        Guid eventId,
        string outcomeCode,
        int? httpStatus,
        DateTimeOffset attemptedAt,
        DateTimeOffset nextAttemptAt) =>
        RecordWorkflowAuditAttempt(
            eventId, outcomeCode, httpStatus, attemptedAt, nextAttemptAt,
            null, null, null, null, null, null);

    internal void RecordWorkflowAuditSignedAttempt(
        Guid eventId,
        string outcomeCode,
        int httpStatus,
        DateTimeOffset attemptedAt,
        DateTimeOffset? nextAttemptAt,
        string? receiptDigest,
        string? rejectionCode,
        string responseJson,
        string responseSha256,
        string responseKeyId,
        string responseSignature) =>
        RecordWorkflowAuditAttempt(
            eventId, outcomeCode, httpStatus, attemptedAt, nextAttemptAt,
            receiptDigest, rejectionCode, responseJson, responseSha256,
            responseKeyId, responseSignature);

    internal void RecordWorkflowCompletionRetry(
        Guid completionId,
        string outcomeCode,
        int? httpStatus,
        DateTimeOffset attemptedAt,
        DateTimeOffset nextAttemptAt) =>
        RecordWorkflowCompletionAttempt(
            completionId, outcomeCode, httpStatus, attemptedAt, nextAttemptAt,
            null, null, null, null, null, null);

    internal void RecordWorkflowCompletionSignedAttempt(
        Guid completionId,
        string outcomeCode,
        int httpStatus,
        DateTimeOffset attemptedAt,
        DateTimeOffset? nextAttemptAt,
        string? completionReceiptDigest,
        string? rejectionCode,
        string responseJson,
        string responseSha256,
        string responseKeyId,
        string responseSignature) =>
        RecordWorkflowCompletionAttempt(
            completionId, outcomeCode, httpStatus, attemptedAt, nextAttemptAt,
            completionReceiptDigest, rejectionCode, responseJson,
            responseSha256, responseKeyId, responseSignature);

    internal IReadOnlyList<WorkflowAuditEventOutboxEntry>
        GetWorkflowAuditEvents(string workflowRunId)
    {
        lock (_workflowAuditSync)
        {
            var entries = new List<WorkflowAuditEventOutboxEntry>();
            using var command = _conn.CreateCommand();
            command.CommandText = """
                SELECT e.event_id, e.workflow_run_id, e.execution_ordinal,
                       e.step_index, e.verb_name, e.verb_version,
                       e.requested_dry_run, e.effective_dry_run, e.outcome,
                       e.exec_duration_ms, e.error_kind, e.params_field_count,
                       e.before_state_field_count, e.after_state_field_count,
                       e.payload_json, e.payload_sha256,
                       (SELECT COUNT(*) FROM workflow_audit_event_attempts c
                         WHERE c.event_id = e.event_id)
                  FROM workflow_audit_event_outbox e
                 WHERE e.workflow_run_id = @run
                 ORDER BY e.execution_ordinal
                """;
            command.Parameters.AddWithValue("@run", workflowRunId);
            using var reader = command.ExecuteReader();
            while (reader.Read()) entries.Add(ReadWorkflowAuditEvent(reader));
            return entries;
        }
    }

    internal WorkflowCompletionIntentEntry? GetWorkflowCompletionIntent(
        string workflowRunId)
    {
        lock (_workflowAuditSync)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                SELECT completion_id, workflow_run_id, outcome, reason_code,
                       audit_event_count, final_event_id
                  FROM workflow_completion_intents
                 WHERE workflow_run_id = @run
                 LIMIT 1
                """;
            command.Parameters.AddWithValue("@run", workflowRunId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadCompletionIntent(reader) : null;
        }
    }

    internal IReadOnlyList<string> GetWorkflowAuditAttemptOutcomes(Guid eventId) =>
        ReadAttemptOutcomes(
            "workflow_audit_event_attempts", "event_id", eventId.ToString("D"));

    internal IReadOnlyList<string> GetWorkflowCompletionAttemptOutcomes(
        Guid completionId) =>
        ReadAttemptOutcomes(
            "workflow_completion_attempts", "completion_id",
            completionId.ToString("D"));

    internal string? GetAcceptedWorkflowCompletionReceiptDigest(
        Guid completionId)
    {
        lock (_workflowAuditSync)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                SELECT completion_receipt_digest
                  FROM workflow_completion_attempts
                 WHERE completion_id = @id
                   AND outcome_code = 'accepted'
                 LIMIT 1
                """;
            command.Parameters.AddWithValue("@id", completionId.ToString("D"));
            return command.ExecuteScalar() as string;
        }
    }

    private void RecordWorkflowAuditAttempt(
        Guid eventId,
        string outcomeCode,
        int? httpStatus,
        DateTimeOffset attemptedAt,
        DateTimeOffset? nextAttemptAt,
        string? receiptDigest,
        string? rejectionCode,
        string? responseJson,
        string? responseSha256,
        string? responseKeyId,
        string? responseSignature)
    {
        lock (_workflowAuditSync)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                INSERT INTO workflow_audit_event_attempts (
                    event_id, attempt_number, outcome_code, attempted_at,
                    next_attempt_at, http_status, receipt_digest,
                    rejection_code, response_json, response_sha256,
                    response_key_id, response_signature
                ) VALUES (
                    @id,
                    (SELECT COALESCE(MAX(attempt_number), 0) + 1
                       FROM workflow_audit_event_attempts WHERE event_id = @id),
                    @outcome, @attempted, @next, @status, @receipt,
                    @rejection, @response, @response_digest, @key, @signature
                )
                """;
            AddAttemptParameters(
                command, eventId.ToString("D"), outcomeCode, httpStatus,
                attemptedAt, nextAttemptAt, rejectionCode, responseJson,
                responseSha256, responseKeyId, responseSignature);
            command.Parameters.AddWithValue(
                "@receipt", receiptDigest is null ? DBNull.Value : receiptDigest);
            command.ExecuteNonQuery();
        }
    }

    private void RecordWorkflowCompletionAttempt(
        Guid completionId,
        string outcomeCode,
        int? httpStatus,
        DateTimeOffset attemptedAt,
        DateTimeOffset? nextAttemptAt,
        string? completionReceiptDigest,
        string? rejectionCode,
        string? responseJson,
        string? responseSha256,
        string? responseKeyId,
        string? responseSignature)
    {
        lock (_workflowAuditSync)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                INSERT INTO workflow_completion_attempts (
                    completion_id, attempt_number, outcome_code, attempted_at,
                    next_attempt_at, http_status, completion_receipt_digest,
                    rejection_code, response_json, response_sha256,
                    response_key_id, response_signature
                ) VALUES (
                    @id,
                    (SELECT COALESCE(MAX(attempt_number), 0) + 1
                       FROM workflow_completion_attempts
                      WHERE completion_id = @id),
                    @outcome, @attempted, @next, @status,
                    @completion_receipt, @rejection,
                    @response, @response_digest, @key, @signature
                )
                """;
            AddAttemptParameters(
                command, completionId.ToString("D"), outcomeCode, httpStatus,
                attemptedAt, nextAttemptAt, rejectionCode, responseJson,
                responseSha256, responseKeyId, responseSignature);
            command.Parameters.AddWithValue(
                "@completion_receipt",
                completionReceiptDigest is null
                    ? DBNull.Value
                    : completionReceiptDigest);
            command.ExecuteNonQuery();
        }
    }

    private static void AddAttemptParameters(
        SqliteCommand command,
        string identity,
        string outcomeCode,
        int? httpStatus,
        DateTimeOffset attemptedAt,
        DateTimeOffset? nextAttemptAt,
        string? rejectionCode,
        string? responseJson,
        string? responseSha256,
        string? responseKeyId,
        string? responseSignature)
    {
        command.Parameters.AddWithValue("@id", identity);
        command.Parameters.AddWithValue("@outcome", outcomeCode);
        command.Parameters.AddWithValue(
            "@status", httpStatus is null ? DBNull.Value : httpStatus.Value);
        command.Parameters.AddWithValue(
            "@attempted", attemptedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "@next", nextAttemptAt is null
                ? DBNull.Value
                : nextAttemptAt.Value.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "@rejection", rejectionCode is null ? DBNull.Value : rejectionCode);
        command.Parameters.AddWithValue(
            "@response", responseJson is null ? DBNull.Value : responseJson);
        command.Parameters.AddWithValue(
            "@response_digest", responseSha256 is null
                ? DBNull.Value
                : responseSha256);
        command.Parameters.AddWithValue(
            "@key", responseKeyId is null ? DBNull.Value : responseKeyId);
        command.Parameters.AddWithValue(
            "@signature", responseSignature is null
                ? DBNull.Value
                : responseSignature);
    }

    private IReadOnlyList<string> ReadAttemptOutcomes(
        string table,
        string identityColumn,
        string identity)
    {
        // Both identifiers are compile-time constants supplied only by the two
        // wrappers above; values remain parameterized. Keeping this helper
        // private prevents caller-controlled SQL identifiers.
        var sql = (table, identityColumn) switch
        {
            ("workflow_audit_event_attempts", "event_id") =>
                "SELECT outcome_code FROM workflow_audit_event_attempts WHERE event_id = @id ORDER BY attempt_number",
            ("workflow_completion_attempts", "completion_id") =>
                "SELECT outcome_code FROM workflow_completion_attempts WHERE completion_id = @id ORDER BY attempt_number",
            _ => throw new ArgumentOutOfRangeException(nameof(table)),
        };
        lock (_workflowAuditSync)
        {
            var outcomes = new List<string>();
            using var command = _conn.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@id", identity);
            using var reader = command.ExecuteReader();
            while (reader.Read()) outcomes.Add(reader.GetString(0));
            return outcomes;
        }
    }

}
