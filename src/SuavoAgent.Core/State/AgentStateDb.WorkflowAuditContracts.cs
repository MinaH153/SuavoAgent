namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    internal sealed record WorkflowAuditPayloadBytes(string Json, string Sha256);

    internal sealed record WorkflowAuditEventOutboxEntry(
        Guid EventId,
        string WorkflowRunId,
        int ExecutionOrdinal,
        int StepIndex,
        string VerbName,
        string VerbVersion,
        bool RequestedDryRun,
        bool? EffectiveDryRun,
        string Outcome,
        int? ExecDurationMs,
        string? ErrorKind,
        int ParamsFieldCount,
        int? BeforeStateFieldCount,
        int? AfterStateFieldCount,
        string PayloadJson,
        string PayloadSha256,
        int AttemptCount);

    internal sealed record WorkflowCompletionIntentEntry(
        Guid CompletionId,
        string WorkflowRunId,
        string Outcome,
        string? ReasonCode,
        int AuditEventCount,
        Guid? FinalEventId);

    internal sealed record WorkflowCompletionMaterialization(
        WorkflowCompletionIntentEntry Intent,
        IReadOnlyList<string> AcceptedReceiptDigests);

    internal sealed record WorkflowCompletionOutboxEntry(
        Guid CompletionId,
        string WorkflowRunId,
        string Outcome,
        string? ReasonCode,
        int AuditEventCount,
        Guid? FinalEventId,
        string AuditChainDigest,
        string PayloadJson,
        string PayloadSha256,
        int AttemptCount);
}
