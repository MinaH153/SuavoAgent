using Microsoft.Data.Sqlite;

namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private WorkflowAuditEventOutboxEntry? ReadWorkflowAuditEventById(
        Guid eventId,
        SqliteTransaction transaction)
    {
        using var command = _conn.CreateCommand();
        command.Transaction = transaction;
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
             WHERE e.event_id = @event
             LIMIT 1
            """;
        command.Parameters.AddWithValue("@event", eventId.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadWorkflowAuditEvent(reader) : null;
    }

    private WorkflowCompletionIntentEntry? ReadWorkflowCompletionIntent(
        string workflowRunId,
        SqliteTransaction transaction)
    {
        using var command = _conn.CreateCommand();
        command.Transaction = transaction;
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

    private WorkflowCompletionOutboxEntry? ReadWorkflowCompletionOutbox(
        Guid completionId,
        SqliteTransaction transaction)
    {
        using var command = _conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT o.completion_id, o.workflow_run_id, i.outcome,
                   i.reason_code, i.audit_event_count, i.final_event_id,
                   o.audit_chain_digest, o.payload_json, o.payload_sha256,
                   (SELECT COUNT(*) FROM workflow_completion_attempts c
                     WHERE c.completion_id = o.completion_id)
              FROM workflow_completion_outbox o
              JOIN workflow_completion_intents i
                ON i.completion_id = o.completion_id
             WHERE o.completion_id = @completion
             LIMIT 1
            """;
        command.Parameters.AddWithValue(
            "@completion", completionId.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadWorkflowCompletionOutbox(reader) : null;
    }

    private static WorkflowAuditEventOutboxEntry ReadWorkflowAuditEvent(
        SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetInt32(2),
        reader.GetInt32(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetInt32(6) == 1,
        reader.IsDBNull(7) ? null : reader.GetInt32(7) == 1,
        reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetInt32(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.GetInt32(11),
        reader.IsDBNull(12) ? null : reader.GetInt32(12),
        reader.IsDBNull(13) ? null : reader.GetInt32(13),
        reader.GetString(14),
        reader.GetString(15),
        reader.GetInt32(16));

    private static WorkflowCompletionIntentEntry ReadCompletionIntent(
        SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.GetInt32(4),
        reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)));

    private static WorkflowCompletionOutboxEntry ReadWorkflowCompletionOutbox(
        SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.GetInt32(4),
        reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)),
        reader.GetString(6),
        reader.GetString(7),
        reader.GetString(8),
        reader.GetInt32(9));
}
