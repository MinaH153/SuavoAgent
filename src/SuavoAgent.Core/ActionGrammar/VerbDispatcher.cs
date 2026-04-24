using SuavoAgent.Core.ActionGrammarV1.Policy;
using SuavoAgent.Core.Audit;

namespace SuavoAgent.Core.ActionGrammarV1;

/// <summary>
/// Phase-1 verb dispatcher. Executes the subset of the nine-step
/// <c>action-grammar-v1.md §VerbDispatcher</c> flow that applies to
/// in-process Mission Loop invocations:
///
/// <list type="number">
///   <item>Parameter schema validation (typed, per-verb)</item>
///   <item>Authz policy evaluation (charter-driven; Cedar-ready)</item>
///   <item>Precondition check</item>
///   <item>Rollback capture + audit entry <c>verb.rollback_captured</c></item>
///   <item>Execute</item>
///   <item>Postcondition verification (runs inverse on failure)</item>
///   <item>Audit <c>verb.executed</c> or <c>verb.rollback_executed</c></item>
/// </list>
///
/// Steps deferred to Phase D (cross-process signed verb bundles):
/// schema-hash version check, HMAC signature verification, fence-ID check.
/// Those live on the cloud→agent boundary, not in-process.
///
/// Fail-closed on every gate. No step is optional. No step runs without the
/// prior step succeeding. This is the CrowdStrike lesson applied.
/// </summary>
public sealed class VerbDispatcher
{
    private readonly IAuthzPolicy _policy;

    public VerbDispatcher(IAuthzPolicy policy)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public async Task<VerbDispatchResult> DispatchAsync(
        IVerb verb,
        VerbContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(verb);
        ArgumentNullException.ThrowIfNull(ctx);

        var meta = verb.Metadata;

        var paramCheck = ValidateParameters(meta.Params, ctx.Parameters);
        if (paramCheck is not null)
        {
            return Reject(ctx, meta, "parameter_validation_failed", paramCheck, rollback: VerbRollbackEnvelope.None(ctx.InvocationId));
        }

        var authz = _policy.Evaluate(ctx, verb);
        if (!authz.Allowed)
        {
            return Reject(ctx, meta, "authz_denied", $"{authz.PolicyId}: {authz.Reason}", rollback: VerbRollbackEnvelope.None(ctx.InvocationId));
        }

        VerbPreconditionResult precond;
        try
        {
            precond = await verb.CheckPreconditionsAsync(ctx, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Reject(ctx, meta, "precondition_exception", ex.Message, rollback: VerbRollbackEnvelope.None(ctx.InvocationId));
        }

        if (!precond.Satisfied)
        {
            return Reject(
                ctx,
                meta,
                "precondition_failed",
                $"{precond.FailedPreconditionId}: {precond.Reason}",
                rollback: VerbRollbackEnvelope.None(ctx.InvocationId));
        }

        VerbRollbackEnvelope rollback;
        try
        {
            rollback = await verb.CaptureRollbackAsync(ctx, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Reject(ctx, meta, "rollback_capture_exception", ex.Message, rollback: VerbRollbackEnvelope.None(ctx.InvocationId));
        }

        ctx.Audit.Append(
            eventType: "verb.rollback_captured",
            actor: ctx.Actor,
            subjectType: "verb",
            subjectId: ctx.InvocationId,
            metadata: new Dictionary<string, object?>
            {
                ["verb"] = meta.Name,
                ["version"] = meta.Version,
                ["inverse_action"] = rollback.InverseActionType,
                ["evidence"] = rollback.Evidence,
                ["max_inverse_duration_ms"] = (long)rollback.MaxInverseDuration.TotalMilliseconds,
            });

        using var execCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        execCts.CancelAfter(meta.MaxExecutionTime);

        VerbExecutionResult exec;
        try
        {
            exec = await verb.ExecuteAsync(ctx, execCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (execCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            await TryRunInverseAsync(rollback, ctx, CancellationToken.None).ConfigureAwait(false);
            return FailWithAudit(ctx, meta, rollback, "execution_timeout", $"verb exceeded MaxExecutionTime={meta.MaxExecutionTime}");
        }
        catch (Exception ex)
        {
            await TryRunInverseAsync(rollback, ctx, ct).ConfigureAwait(false);
            return FailWithAudit(ctx, meta, rollback, "execution_exception", ex.Message);
        }

        if (!exec.Succeeded)
        {
            await TryRunInverseAsync(rollback, ctx, ct).ConfigureAwait(false);
            return FailWithAudit(ctx, meta, rollback, "execution_failed", exec.FailureReason ?? "unspecified");
        }

        VerbPostconditionResult postcond;
        try
        {
            postcond = await verb.VerifyPostconditionsAsync(ctx, exec, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await TryRunInverseAsync(rollback, ctx, ct).ConfigureAwait(false);
            return FailWithAudit(ctx, meta, rollback, "postcondition_exception", ex.Message);
        }

        if (!postcond.Satisfied)
        {
            await TryRunInverseAsync(rollback, ctx, ct).ConfigureAwait(false);
            return FailWithAudit(
                ctx,
                meta,
                rollback,
                "postcondition_failed",
                $"{postcond.FailedPostconditionId}: {postcond.Reason}");
        }

        ctx.Audit.Append(
            eventType: "verb.executed",
            actor: ctx.Actor,
            subjectType: "verb",
            subjectId: ctx.InvocationId,
            metadata: new Dictionary<string, object?>
            {
                ["verb"] = meta.Name,
                ["version"] = meta.Version,
                ["output_fields"] = exec.Output.Count,
            });

        return new VerbDispatchResult(
            InvocationId: ctx.InvocationId,
            Verb: meta.Name,
            Version: meta.Version,
            Outcome: VerbDispatchOutcome.Success,
            Authz: authz,
            RollbackEnvelope: rollback,
            Output: exec.Output,
            FailureReason: null);
    }

    private static string? ValidateParameters(
        VerbParameterSchema schema,
        IReadOnlyDictionary<string, object?> actual)
    {
        foreach (var spec in schema.Parameters)
        {
            if (spec.Required && !actual.ContainsKey(spec.Name))
            {
                return $"missing required parameter '{spec.Name}'";
            }

            if (actual.TryGetValue(spec.Name, out var value))
            {
                if (value is null)
                {
                    if (spec.Required)
                    {
                        return $"parameter '{spec.Name}' is required but null";
                    }
                    continue;
                }

                if (!spec.ClrType.IsInstanceOfType(value))
                {
                    return $"parameter '{spec.Name}' expected {spec.ClrType.Name}, got {value.GetType().Name}";
                }
            }
        }
        return null;
    }

    private VerbDispatchResult Reject(
        VerbContext ctx,
        VerbMetadata meta,
        string rejectionCode,
        string reason,
        VerbRollbackEnvelope rollback)
    {
        ctx.Audit.Append(
            eventType: "verb.rejected",
            actor: ctx.Actor,
            subjectType: "verb",
            subjectId: ctx.InvocationId,
            metadata: new Dictionary<string, object?>
            {
                ["verb"] = meta.Name,
                ["version"] = meta.Version,
                ["code"] = rejectionCode,
                ["reason"] = reason,
            });

        return new VerbDispatchResult(
            InvocationId: ctx.InvocationId,
            Verb: meta.Name,
            Version: meta.Version,
            Outcome: VerbDispatchOutcome.Rejected,
            Authz: null,
            RollbackEnvelope: rollback,
            Output: new Dictionary<string, object?>(),
            FailureReason: $"{rejectionCode}: {reason}");
    }

    private static async Task TryRunInverseAsync(
        VerbRollbackEnvelope envelope,
        VerbContext ctx,
        CancellationToken ct)
    {
        if (envelope.IsNoOp)
        {
            return;
        }

        using var inverseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        inverseCts.CancelAfter(envelope.MaxInverseDuration);
        try
        {
            await envelope.InverseFn!(ctx, inverseCts.Token).ConfigureAwait(false);
            ctx.Audit.Append(
                eventType: "verb.rollback_executed",
                actor: ctx.Actor,
                subjectType: "verb",
                subjectId: ctx.InvocationId,
                metadata: new Dictionary<string, object?>
                {
                    ["inverse_action"] = envelope.InverseActionType,
                    ["evidence"] = envelope.Evidence,
                });
        }
        catch (Exception ex)
        {
            ctx.Audit.Append(
                eventType: "verb.rollback_failed",
                actor: ctx.Actor,
                subjectType: "verb",
                subjectId: ctx.InvocationId,
                metadata: new Dictionary<string, object?>
                {
                    ["inverse_action"] = envelope.InverseActionType,
                    ["error"] = ex.Message,
                });
        }
    }

    private static VerbDispatchResult FailWithAudit(
        VerbContext ctx,
        VerbMetadata meta,
        VerbRollbackEnvelope rollback,
        string failureCode,
        string reason)
    {
        ctx.Audit.Append(
            eventType: "verb.failed",
            actor: ctx.Actor,
            subjectType: "verb",
            subjectId: ctx.InvocationId,
            metadata: new Dictionary<string, object?>
            {
                ["verb"] = meta.Name,
                ["version"] = meta.Version,
                ["code"] = failureCode,
                ["reason"] = reason,
            });

        return new VerbDispatchResult(
            InvocationId: ctx.InvocationId,
            Verb: meta.Name,
            Version: meta.Version,
            Outcome: VerbDispatchOutcome.Failed,
            Authz: null,
            RollbackEnvelope: rollback,
            Output: new Dictionary<string, object?>(),
            FailureReason: $"{failureCode}: {reason}");
    }
}

public enum VerbDispatchOutcome
{
    Rejected = 0,
    Failed = 1,
    Success = 2,
}

public sealed record VerbDispatchResult(
    string InvocationId,
    string Verb,
    string Version,
    VerbDispatchOutcome Outcome,
    AuthzDecision? Authz,
    VerbRollbackEnvelope RollbackEnvelope,
    IReadOnlyDictionary<string, object?> Output,
    string? FailureReason
);
