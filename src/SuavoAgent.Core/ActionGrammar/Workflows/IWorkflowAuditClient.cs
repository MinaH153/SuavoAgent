namespace SuavoAgent.Core.ActionGrammarV1.Workflows;

/// <summary>
/// Per-step audit posting + run-completion posting against the cloud.
/// Concrete implementation lives in
/// <see cref="SuavoAgent.Core.Cloud.WorkflowAuditCloudClient"/>;
/// the interface keeps WorkflowExecutor unit-testable.
/// </summary>
public interface IWorkflowAuditClient
{
    Task PostStepAuditAsync(WorkflowStepAuditEntry entry, CancellationToken ct);

    Task PostRunCompletedAsync(string workflowRunId, WorkflowRunOutcome outcome, string? abortReason, CancellationToken ct);
}

public sealed record WorkflowStepAuditEntry(
    Guid EventId,
    string WorkflowRunId,
    int StepIndex,
    string VerbName,
    string VerbVersion,
    bool RequestedDryRun,
    string Outcome,
    long? ExecDurationMs,
    string? ErrorKind,
    int ParamsFieldCount,
    int? BeforeStateFieldCount,
    int? AfterStateFieldCount,
    // For actuating verbs the executor snapshots the local gate before
    // dispatch and records request.DryRun || gate.DryRun even when dispatch
    // fails before the Helper can return an output envelope. Read-only or
    // unresolved verbs may legitimately leave this null.
    bool? EffectiveDryRun = null)
{
    // Compatibility name retained for existing callers/tests. It is a
    // boolean alias only; no raw workflow parameter or state value is ever
    // carried by this audit contract.
    public bool DryRun => RequestedDryRun;
}

public enum WorkflowRunOutcome
{
    Completed,
    Failed,
    Aborted,
}
