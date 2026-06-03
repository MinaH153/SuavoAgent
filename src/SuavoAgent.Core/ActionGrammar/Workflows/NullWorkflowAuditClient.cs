namespace SuavoAgent.Core.ActionGrammarV1.Workflows;

/// <summary>
/// Null-object <see cref="IWorkflowAuditClient"/> used when the cloud
/// client isn't available. Logs locally only — for unit tests and
/// developer smoke runs. Production wiring goes through
/// <see cref="SuavoAgent.Core.Cloud.WorkflowAuditCloudClient"/>.
/// </summary>
internal sealed class NullWorkflowAuditClient : IWorkflowAuditClient
{
    public Task PostStepAuditAsync(WorkflowStepAuditEntry entry, CancellationToken ct) => Task.CompletedTask;
    public Task PostRunCompletedAsync(string workflowRunId, WorkflowRunOutcome outcome, string? abortReason, CancellationToken ct) => Task.CompletedTask;
}
