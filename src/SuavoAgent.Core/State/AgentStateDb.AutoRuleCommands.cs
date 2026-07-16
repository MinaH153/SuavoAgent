using Microsoft.Data.Sqlite;
using SuavoAgent.Core.Workers;

namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    public sealed record ActiveAutoRuleBinding(
        string ApprovalId,
        string RuleId,
        string TemplateId,
        string YamlSha256,
        string ActivatedByCommandId,
        string ActivatedAt);

    internal sealed record AutoRuleTransitionApplyResult(
        bool Succeeded,
        bool Replay,
        bool AlreadyAtTarget,
        string ResultCode);

    internal enum AutoRuleRunBeginKind
    {
        Start,
        InProgress,
        Terminal,
        Conflict,
    }

    internal sealed record AutoRuleRunBeginResult(
        AutoRuleRunBeginKind Kind,
        bool Succeeded,
        string OutcomeCode,
        int StepsCompleted = 0,
        int? FailedOrdinal = null);

    internal AutoRuleTransitionApplyResult ApplyAutoRuleTransition(
        AutoRuleTransitionCommand command,
        bool exactRuleValidated)
    {
        lock (_connLock)
        {
            using var txn = _conn.BeginTransaction();

            using (var replay = CreateCommand(txn, """
                SELECT payload_digest, result_code
                  FROM auto_rule_transition_commands
                 WHERE command_id = @command
                """))
            {
                replay.Parameters.AddWithValue("@command", command.CommandId);
                using var reader = replay.ExecuteReader();
                if (reader.Read())
                {
                    var digest = reader.GetString(0);
                    var ledgerResultCode = reader.GetString(1);
                    txn.Commit();
                    if (!string.Equals(digest, command.PayloadDigest, StringComparison.Ordinal))
                        return new(false, true, false, "command_payload_conflict");
                    var success = ledgerResultCode is "applied" or "already_at_target";
                    return new(success, true, ledgerResultCode == "already_at_target", ledgerResultCode);
                }
            }

            AutoRuleApprovalRow? approval = null;
            using (var current = CreateCommand(txn, """
                SELECT rule_id, template_id, yaml_sha256, has_writeback, status,
                       shadow_runs, shadow_matches, shadow_mismatches,
                       approved_by, approved_at, rejected_reason, approval_id
                  FROM auto_rule_approvals
                 WHERE rule_id = @rule
                """))
            {
                current.Parameters.AddWithValue("@rule", command.RuleId);
                using var reader = current.ExecuteReader();
                if (reader.Read()) approval = ReadAutoRuleApproval(reader);
            }

            var rejection = ValidateTransitionBinding(txn, command, approval, exactRuleValidated);
            if (rejection is not null)
                return CommitRejectedTransition(txn, command, rejection);

            var alreadyAtTarget = approval!.Status == command.ToStatus;
            using (var update = CreateCommand(txn, """
                UPDATE auto_rule_approvals
                   SET approval_id = @approval,
                       status = @status,
                       approved_by = CASE WHEN @status = 'Approved' THEN @approved_by ELSE NULL END,
                       approved_at = CASE WHEN @status = 'Approved' THEN @approved_at ELSE NULL END,
                       rejected_reason = CASE WHEN @status = 'Rejected' THEN @reason ELSE NULL END
                 WHERE rule_id = @rule
                   AND template_id = @template
                   AND yaml_sha256 = @yaml
                   AND status IN (@from_status, @to_status)
                   AND (approval_id IS NULL OR approval_id = @approval)
                """))
            {
                update.Parameters.AddWithValue("@approval", command.ApprovalId);
                update.Parameters.AddWithValue("@status", command.ToStatus.ToString());
                update.Parameters.AddWithValue("@approved_by", (object?)command.ApprovedBy ?? DBNull.Value);
                update.Parameters.AddWithValue("@approved_at", (object?)command.ApprovedAt ?? DBNull.Value);
                update.Parameters.AddWithValue("@reason", command.ReasonCode);
                update.Parameters.AddWithValue("@rule", command.RuleId);
                update.Parameters.AddWithValue("@template", command.TemplateId);
                update.Parameters.AddWithValue("@yaml", command.YamlSha256);
                update.Parameters.AddWithValue("@from_status", command.FromStatus.ToString());
                update.Parameters.AddWithValue("@to_status", command.ToStatus.ToString());
                if (update.ExecuteNonQuery() != 1)
                    return CommitRejectedTransition(txn, command, "transition_concurrent");
            }

            using (var remove = CreateCommand(txn,
                       "DELETE FROM active_auto_rule_registry WHERE rule_id = @rule"))
            {
                remove.Parameters.AddWithValue("@rule", command.RuleId);
                remove.ExecuteNonQuery();
            }

            if (command.ToStatus == AutoRuleStatus.Approved)
            {
                using var admit = CreateCommand(txn, """
                    INSERT INTO active_auto_rule_registry
                        (approval_id, rule_id, template_id, yaml_sha256,
                         activated_by_command_id, activated_at)
                    VALUES (@approval, @rule, @template, @yaml, @command, @at)
                    """);
                admit.Parameters.AddWithValue("@approval", command.ApprovalId);
                admit.Parameters.AddWithValue("@rule", command.RuleId);
                admit.Parameters.AddWithValue("@template", command.TemplateId);
                admit.Parameters.AddWithValue("@yaml", command.YamlSha256);
                admit.Parameters.AddWithValue("@command", command.CommandId);
                admit.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("o"));
                admit.ExecuteNonQuery();
            }

            var resultCode = alreadyAtTarget ? "already_at_target" : "applied";
            InsertTransitionLedger(txn, command, resultCode);
            txn.Commit();
            return new(true, false, alreadyAtTarget, resultCode);
        }
    }

    internal AutoRuleRunBeginResult BeginAutoRuleRun(
        AutoRuleRunCommand command,
        string ownerInstanceId,
        bool runtimeRegistryExact)
    {
        lock (_connLock)
        {
            using var txn = _conn.BeginTransaction();
            using (var existing = CreateCommand(txn, """
                SELECT payload_digest, state, owner_instance_id, outcome_code,
                       COALESCE(steps_completed, 0), failed_ordinal
                  FROM auto_rule_run_commands
                 WHERE command_id = @command
                """))
            {
                existing.Parameters.AddWithValue("@command", command.CommandId);
                using var reader = existing.ExecuteReader();
                if (reader.Read())
                {
                    var digest = reader.GetString(0);
                    var state = reader.GetString(1);
                    var owner = reader.GetString(2);
                    var outcome = reader.IsDBNull(3) ? "running" : reader.GetString(3);
                    var steps = reader.GetInt32(4);
                    int? ordinal = reader.IsDBNull(5) ? null : reader.GetInt32(5);
                    reader.Close();

                    if (!string.Equals(digest, command.PayloadDigest, StringComparison.Ordinal))
                    {
                        txn.Commit();
                        return new(AutoRuleRunBeginKind.Conflict, false, "command_payload_conflict");
                    }

                    if (state != "running")
                    {
                        txn.Commit();
                        return new(AutoRuleRunBeginKind.Terminal, state == "succeeded", outcome, steps, ordinal);
                    }

                    if (string.Equals(owner, ownerInstanceId, StringComparison.Ordinal))
                    {
                        txn.Commit();
                        return new(AutoRuleRunBeginKind.InProgress, false, "run_in_progress");
                    }

                    // A different owner means the process restarted after actuation began. Whether a click
                    // occurred is unknowable, so terminally fail and NEVER replay the template.
                    using var interrupt = CreateCommand(txn, """
                        UPDATE auto_rule_run_commands
                           SET state = 'failed', outcome_code = 'interrupted_no_replay',
                               completed_at = @at
                         WHERE command_id = @command AND state = 'running'
                        """);
                    interrupt.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("o"));
                    interrupt.Parameters.AddWithValue("@command", command.CommandId);
                    interrupt.ExecuteNonQuery();
                    txn.Commit();
                    return new(AutoRuleRunBeginKind.Terminal, false, "interrupted_no_replay");
                }
            }

            using (var duplicateRun = CreateCommand(txn,
                       "SELECT 1 FROM auto_rule_run_commands WHERE run_id = @run LIMIT 1"))
            {
                duplicateRun.Parameters.AddWithValue("@run", command.RunId);
                if (duplicateRun.ExecuteScalar() is not null)
                {
                    txn.Commit();
                    return new(AutoRuleRunBeginKind.Conflict, false, "run_id_conflict");
                }
            }

            var rejection = ValidateRunBinding(txn, command, runtimeRegistryExact);
            if (rejection is not null)
            {
                InsertRunLedger(txn, command, ownerInstanceId, "failed", rejection, 0, null, completed: true);
                txn.Commit();
                return new(AutoRuleRunBeginKind.Terminal, false, rejection);
            }

            InsertRunLedger(txn, command, ownerInstanceId, "running", null, null, null, completed: false);
            txn.Commit();
            return new(AutoRuleRunBeginKind.Start, false, "running");
        }
    }

    internal bool CompleteAutoRuleRun(
        string commandId,
        string ownerInstanceId,
        bool succeeded,
        string outcomeCode,
        int stepsCompleted,
        int? failedOrdinal)
    {
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                UPDATE auto_rule_run_commands
                   SET state = @state, outcome_code = @outcome,
                       steps_completed = @steps, failed_ordinal = @ordinal,
                       completed_at = @at
                 WHERE command_id = @command
                   AND owner_instance_id = @owner
                   AND state = 'running'
                """;
            command.Parameters.AddWithValue("@state", succeeded ? "succeeded" : "failed");
            command.Parameters.AddWithValue("@outcome", outcomeCode);
            command.Parameters.AddWithValue("@steps", stepsCompleted);
            command.Parameters.AddWithValue("@ordinal", (object?)failedOrdinal ?? DBNull.Value);
            command.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("o"));
            command.Parameters.AddWithValue("@command", commandId);
            command.Parameters.AddWithValue("@owner", ownerInstanceId);
            return command.ExecuteNonQuery() == 1;
        }
    }

    public IReadOnlyList<ActiveAutoRuleBinding> GetActiveAutoRuleBindings()
    {
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                SELECT approval_id, rule_id, template_id, yaml_sha256,
                       activated_by_command_id, activated_at
                  FROM active_auto_rule_registry
                 ORDER BY rule_id
                """;
            var rows = new List<ActiveAutoRuleBinding>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add(new ActiveAutoRuleBinding(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetString(5)));
            return rows;
        }
    }

    public bool IsActiveAutoRuleBinding(ActiveAutoRuleBinding binding)
    {
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                SELECT 1
                  FROM active_auto_rule_registry registry
                  JOIN auto_rule_approvals approval
                    ON approval.approval_id = registry.approval_id
                   AND approval.rule_id = registry.rule_id
                   AND approval.template_id = registry.template_id
                   AND approval.yaml_sha256 = registry.yaml_sha256
                   AND approval.status = 'Approved'
                  JOIN workflow_templates template
                    ON template.template_id = registry.template_id
                   AND template.retired_at IS NULL
                   AND template.capture_only = 0
                 WHERE registry.approval_id = @approval
                   AND registry.rule_id = @rule
                   AND registry.template_id = @template
                   AND registry.yaml_sha256 = @yaml
                 LIMIT 1
                """;
            command.Parameters.AddWithValue("@approval", binding.ApprovalId);
            command.Parameters.AddWithValue("@rule", binding.RuleId);
            command.Parameters.AddWithValue("@template", binding.TemplateId);
            command.Parameters.AddWithValue("@yaml", binding.YamlSha256);
            return command.ExecuteScalar() is not null;
        }
    }

    private string? ValidateTransitionBinding(
        SqliteTransaction txn,
        AutoRuleTransitionCommand command,
        AutoRuleApprovalRow? approval,
        bool exactRuleValidated)
    {
        if (approval is null) return "approval_not_found";
        if (!string.Equals(approval.TemplateId, command.TemplateId, StringComparison.Ordinal) ||
            !string.Equals(approval.YamlSha256, command.YamlSha256, StringComparison.Ordinal))
            return "approval_binding_mismatch";
        if (approval.ApprovalId is not null &&
            !string.Equals(approval.ApprovalId, command.ApprovalId, StringComparison.Ordinal))
            return "approval_id_mismatch";
        if (approval.Status != command.FromStatus && approval.Status != command.ToStatus)
            return "transition_state_mismatch";
        if (!AutoRuleCommandContracts.IsLegalTransition(command.FromStatus, command.ToStatus))
            return "transition_illegal";
        if (command.ToStatus == AutoRuleStatus.Approved && !exactRuleValidated)
            return "rule_validation_failed";

        using var template = CreateCommand(txn, """
            SELECT 1 FROM workflow_templates
             WHERE template_id = @template AND retired_at IS NULL AND capture_only = 0
             LIMIT 1
            """);
        template.Parameters.AddWithValue("@template", command.TemplateId);
        return template.ExecuteScalar() is null ? "template_not_active" : null;
    }

    private string? ValidateRunBinding(
        SqliteTransaction txn,
        AutoRuleRunCommand command,
        bool runtimeRegistryExact)
    {
        if (!runtimeRegistryExact) return "runtime_registry_mismatch";

        using var approval = CreateCommand(txn, """
            SELECT 1 FROM auto_rule_approvals
             WHERE approval_id = @approval
               AND rule_id = @rule
               AND template_id = @template
               AND yaml_sha256 = @yaml
               AND status = 'Approved'
               AND approved_by IS NOT NULL
               AND approved_at IS NOT NULL
             LIMIT 1
            """);
        approval.Parameters.AddWithValue("@approval", command.ApprovalId);
        approval.Parameters.AddWithValue("@rule", command.RuleId);
        approval.Parameters.AddWithValue("@template", command.TemplateId);
        approval.Parameters.AddWithValue("@yaml", command.YamlSha256);
        if (approval.ExecuteScalar() is null) return "approval_binding_mismatch";

        using var registry = CreateCommand(txn, """
            SELECT 1
              FROM active_auto_rule_registry registry
              JOIN workflow_templates template
                ON template.template_id = registry.template_id
               AND template.retired_at IS NULL
               AND template.capture_only = 0
             WHERE registry.approval_id = @approval
               AND registry.rule_id = @rule
               AND registry.template_id = @template
               AND registry.yaml_sha256 = @yaml
             LIMIT 1
            """);
        registry.Parameters.AddWithValue("@approval", command.ApprovalId);
        registry.Parameters.AddWithValue("@rule", command.RuleId);
        registry.Parameters.AddWithValue("@template", command.TemplateId);
        registry.Parameters.AddWithValue("@yaml", command.YamlSha256);
        return registry.ExecuteScalar() is null ? "active_registry_mismatch" : null;
    }

    private AutoRuleTransitionApplyResult CommitRejectedTransition(
        SqliteTransaction txn,
        AutoRuleTransitionCommand command,
        string resultCode)
    {
        InsertTransitionLedger(txn, command, resultCode);
        txn.Commit();
        return new(false, false, false, resultCode);
    }

    private void InsertTransitionLedger(
        SqliteTransaction txn,
        AutoRuleTransitionCommand command,
        string resultCode)
    {
        using var insert = CreateCommand(txn, """
            INSERT INTO auto_rule_transition_commands
                (command_id, payload_digest, approval_id, rule_id, template_id, yaml_sha256,
                 from_status, to_status, approved_by, approved_at, reason_code,
                 result_code, applied_at)
            VALUES
                (@command, @digest, @approval, @rule, @template, @yaml,
                 @from_status, @to_status, @approved_by, @approved_at, @reason,
                 @result, @at)
            """);
        insert.Parameters.AddWithValue("@command", command.CommandId);
        insert.Parameters.AddWithValue("@digest", command.PayloadDigest);
        insert.Parameters.AddWithValue("@approval", command.ApprovalId);
        insert.Parameters.AddWithValue("@rule", command.RuleId);
        insert.Parameters.AddWithValue("@template", command.TemplateId);
        insert.Parameters.AddWithValue("@yaml", command.YamlSha256);
        insert.Parameters.AddWithValue("@from_status", command.FromStatus.ToString());
        insert.Parameters.AddWithValue("@to_status", command.ToStatus.ToString());
        insert.Parameters.AddWithValue("@approved_by", (object?)command.ApprovedBy ?? DBNull.Value);
        insert.Parameters.AddWithValue("@approved_at", (object?)command.ApprovedAt ?? DBNull.Value);
        insert.Parameters.AddWithValue("@reason", command.ReasonCode);
        insert.Parameters.AddWithValue("@result", resultCode);
        insert.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("o"));
        insert.ExecuteNonQuery();
    }

    private void InsertRunLedger(
        SqliteTransaction txn,
        AutoRuleRunCommand command,
        string ownerInstanceId,
        string state,
        string? outcome,
        int? steps,
        int? failedOrdinal,
        bool completed)
    {
        using var insert = CreateCommand(txn, """
            INSERT INTO auto_rule_run_commands
                (command_id, payload_digest, approval_id, rule_id, template_id, yaml_sha256,
                 run_id, deadline_seconds, state, owner_instance_id, outcome_code,
                 steps_completed, failed_ordinal, started_at, completed_at)
            VALUES
                (@command, @digest, @approval, @rule, @template, @yaml,
                 @run, @deadline, @state, @owner, @outcome,
                 @steps, @ordinal, @started, @completed)
            """);
        insert.Parameters.AddWithValue("@command", command.CommandId);
        insert.Parameters.AddWithValue("@digest", command.PayloadDigest);
        insert.Parameters.AddWithValue("@approval", command.ApprovalId);
        insert.Parameters.AddWithValue("@rule", command.RuleId);
        insert.Parameters.AddWithValue("@template", command.TemplateId);
        insert.Parameters.AddWithValue("@yaml", command.YamlSha256);
        insert.Parameters.AddWithValue("@run", command.RunId);
        insert.Parameters.AddWithValue("@deadline", command.DeadlineSeconds);
        insert.Parameters.AddWithValue("@state", state);
        insert.Parameters.AddWithValue("@owner", ownerInstanceId);
        insert.Parameters.AddWithValue("@outcome", (object?)outcome ?? DBNull.Value);
        insert.Parameters.AddWithValue("@steps", (object?)steps ?? DBNull.Value);
        insert.Parameters.AddWithValue("@ordinal", (object?)failedOrdinal ?? DBNull.Value);
        var now = DateTimeOffset.UtcNow.ToString("o");
        insert.Parameters.AddWithValue("@started", now);
        insert.Parameters.AddWithValue("@completed", completed ? now : DBNull.Value);
        insert.ExecuteNonQuery();
    }

    private SqliteCommand CreateCommand(SqliteTransaction txn, string sql)
    {
        var command = _conn.CreateCommand();
        command.Transaction = txn;
        command.CommandText = sql;
        return command;
    }
}
