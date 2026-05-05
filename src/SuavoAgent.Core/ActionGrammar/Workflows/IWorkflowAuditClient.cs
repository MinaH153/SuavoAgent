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
    string WorkflowRunId,
    int StepIndex,
    string VerbName,
    string VerbVersion,
    bool DryRun,
    string Outcome,
    long? ExecDurationMs,
    string? ErrorKind,
    string? ErrorDetail,
    IReadOnlyDictionary<string, object?> Params,
    IReadOnlyDictionary<string, object?>? BeforeState,
    IReadOnlyDictionary<string, object?>? AfterState
);

public enum WorkflowRunOutcome
{
    Completed,
    Failed,
    Aborted,
}
