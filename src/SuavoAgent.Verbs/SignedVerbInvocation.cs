namespace SuavoAgent.Verbs;

/// <summary>
/// Inbound message from the cloud dispatcher describing a verb to execute.
/// The agent verifies (1) HMAC signature, (2) schema hash matches registry,
/// (3) fence ID is current, (4) parameter shape is valid — in that order.
/// Any failure is a hard reject with an audit event.
/// </summary>
/// <param name="InvocationId">Unique per invocation. Becomes <see cref="VerbContext.InvocationId"/>.</param>
/// <param name="VerbName">e.g., "restart_service"</param>
/// <param name="VerbVersion">Strict semver, must match registry exactly.</param>
/// <param name="SchemaHash">Cloud-computed schema hash. Agent re-computes and compares.</param>
/// <param name="Parameters">Dictionary matching the verb's <see cref="VerbParameterSchema"/>.</param>
/// <param name="FenceId">Current kill-switch fence ID. Invalid → reject.</param>
/// <param name="PharmacyId">Salted pharmacy identifier.</param>
/// <param name="SignedAt">Unix seconds — used for replay defense (5-min skew).</param>
/// <param name="Signature">HMAC-SHA256 hex over (invocationId|verbName|verbVersion|parametersJson|fenceId|pharmacyId|signedAt).</param>
public sealed record SignedVerbInvocation(
    Guid InvocationId,
    string VerbName,
    string VerbVersion,
    string SchemaHash,
    IReadOnlyDictionary<string, object?> Parameters,
    Guid FenceId,
    string PharmacyId,
    long SignedAt,
    string Signature);

/// <summary>
/// Immutable result of attempting to dispatch a signed verb invocation.
/// One of the <see cref="DispatchStatus"/> values plus evidence.
/// </summary>
public sealed record VerbDispatchResult(
    VerbDispatchStatus Status,
    string? Reason,
    VerbExecutionResult? ExecutionResult,
    VerbRollbackEnvelope? RollbackEnvelope)
{
    public static VerbDispatchResult Reject(VerbDispatchStatus status, string reason) =>
        new(status, reason, null, null);

    public static VerbDispatchResult Executed(VerbExecutionResult result, VerbRollbackEnvelope envelope) =>
        new(VerbDispatchStatus.Success, null, result, envelope);

    public static VerbDispatchResult RolledBack(VerbExecutionResult? result, VerbRollbackEnvelope envelope, string reason) =>
        new(VerbDispatchStatus.RolledBack, reason, result, envelope);
}

public enum VerbDispatchStatus
{
    Success,
    RolledBack,
    SignatureInvalid,
    SchemaVersionMismatch,
    FenceMismatch,
    UnknownVerb,
    TimestampSkew,
    ParameterValidationFailed,
    PreconditionFailed,
    ExecutionFailed,
    PostconditionFailed
}
