using SuavoAgent.Core.Audit;
using SuavoAgent.Core.Mission;

namespace SuavoAgent.Core.ActionGrammarV1;

/// <summary>
/// Runtime envelope passed to every step of a verb invocation. Immutable —
/// the context the planner hands to the dispatcher is the exact context the
/// verb receives. No hidden mutable state.
/// </summary>
public sealed record VerbContext(
    string PharmacyId,
    MissionCharter Charter,
    AuditChain Audit,
    string InvocationId,
    string Actor,
    IReadOnlyDictionary<string, object?> Parameters,
    IServiceProvider Services,
    DateTimeOffset DeadlineUtc
);

public sealed record VerbPreconditionResult(bool Satisfied, string? FailedPreconditionId, string? Reason)
{
    public static VerbPreconditionResult Ok() => new(true, null, null);
    public static VerbPreconditionResult Fail(string id, string reason) => new(false, id, reason);
}

public sealed record VerbExecutionResult(
    bool Succeeded,
    IReadOnlyDictionary<string, object?> Output,
    string? FailureReason
)
{
    public static VerbExecutionResult Ok(IReadOnlyDictionary<string, object?> output) =>
        new(true, output, null);
    public static VerbExecutionResult Fail(string reason) =>
        new(false, new Dictionary<string, object?>(), reason);
}

public sealed record VerbPostconditionResult(bool Satisfied, string? FailedPostconditionId, string? Reason)
{
    public static VerbPostconditionResult Ok() => new(true, null, null);
    public static VerbPostconditionResult Fail(string id, string reason) => new(false, id, reason);
}

public sealed record VerbRollbackEnvelope(
    string InvocationId,
    string InverseActionType,
    IReadOnlyDictionary<string, object?> PreState,
    Func<VerbContext, CancellationToken, Task>? InverseFn,
    TimeSpan MaxInverseDuration,
    string Evidence
)
{
    public static VerbRollbackEnvelope None(string invocationId) => new(
        InvocationId: invocationId,
        InverseActionType: "none",
        PreState: new Dictionary<string, object?>(),
        InverseFn: null,
        MaxInverseDuration: TimeSpan.Zero,
        Evidence: "read-only");

    public bool IsNoOp => InverseFn is null;
}
