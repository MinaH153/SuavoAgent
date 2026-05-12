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
    IReadOnlyDictionary<string, object?>? AfterState,
    // Bug 21 (MinaH153/SuavoAgent#63): the actual dry-run state the Helper
    // enforced — `request.DryRun || gate.IsDryRun`. <c>DryRun</c> above is
    // the REQUESTED state (workflow definition); <c>EffectiveDryRun</c> is
    // the TRUTH. Null when the verb did not emit a "dry_run" output key
    // (read-only verbs, manifest-resolution failures, etc.). Operators
    // join the workflow row's requested with this column to reconstruct
    // chain of custody.
    bool? EffectiveDryRun = null
);

public enum WorkflowRunOutcome
{
    Completed,
    Failed,
    Aborted,
}
