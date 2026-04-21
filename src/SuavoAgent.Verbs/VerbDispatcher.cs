using Microsoft.Extensions.Logging;

namespace SuavoAgent.Verbs;

/// <summary>
/// The verb execution pipeline. Pure logic over injected collaborators —
/// unit-testable without a Windows service or network. See
/// <c>docs/self-healing/action-grammar-v1.md §Enforcement on the agent side</c>
/// for the canonical order of checks.
/// </summary>
public sealed class VerbDispatcher : IVerbDispatcher
{
    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    private readonly IVerbRegistry _registry;
    private readonly ISignatureVerifier _signatureVerifier;
    private readonly IFenceProvider _fenceProvider;
    private readonly IServiceProvider _services;
    private readonly ILogger<VerbDispatcher> _logger;

    public VerbDispatcher(
        IVerbRegistry registry,
        ISignatureVerifier signatureVerifier,
        IFenceProvider fenceProvider,
        IServiceProvider services,
        ILogger<VerbDispatcher> logger)
    {
        _registry = registry;
        _signatureVerifier = signatureVerifier;
        _fenceProvider = fenceProvider;
        _services = services;
        _logger = logger;
    }

    public async Task<VerbDispatchResult> DispatchAsync(SignedVerbInvocation invocation, CancellationToken cancellationToken)
    {
        // 1. Schema version check FIRST — fail closed on mismatch (CrowdStrike lesson)
        var expectedSchemaHash = _registry.SchemaHash(invocation.VerbName, invocation.VerbVersion);
        if (string.IsNullOrEmpty(expectedSchemaHash))
        {
            return VerbDispatchResult.Reject(VerbDispatchStatus.UnknownVerb,
                $"no verb registered for {invocation.VerbName}@{invocation.VerbVersion}");
        }
        if (expectedSchemaHash != invocation.SchemaHash)
        {
            _logger.LogCritical(
                "grammar.version_mismatch: expected {Expected} got {Got} for {Verb}@{Version}",
                expectedSchemaHash, invocation.SchemaHash, invocation.VerbName, invocation.VerbVersion);
            return VerbDispatchResult.Reject(VerbDispatchStatus.SchemaVersionMismatch,
                $"schema hash mismatch: expected {expectedSchemaHash}, got {invocation.SchemaHash}");
        }

        // 2. Signature
        if (!_signatureVerifier.Verify(invocation))
        {
            return VerbDispatchResult.Reject(VerbDispatchStatus.SignatureInvalid, "HMAC signature verification failed");
        }

        // 3. Timestamp skew (replay defense)
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now - invocation.SignedAt) > MaxClockSkew.TotalSeconds)
        {
            return VerbDispatchResult.Reject(VerbDispatchStatus.TimestampSkew,
                $"timestamp skew {Math.Abs(now - invocation.SignedAt)}s exceeds {MaxClockSkew.TotalSeconds}s");
        }

        // 4. Fence ID
        if (invocation.FenceId != _fenceProvider.CurrentFenceId)
        {
            return VerbDispatchResult.Reject(VerbDispatchStatus.FenceMismatch,
                $"fence id {invocation.FenceId} is not current (kill-switch in effect)");
        }

        var verb = _registry.Resolve(invocation.VerbName, invocation.VerbVersion);
        if (verb is null)
        {
            // Can't hit this given the schema-hash check above, but belt-and-suspenders.
            return VerbDispatchResult.Reject(VerbDispatchStatus.UnknownVerb,
                $"verb resolved null despite non-empty schema hash: {invocation.VerbName}@{invocation.VerbVersion}");
        }

        // 5. Parameter validation
        var paramError = ValidateParameters(verb.Metadata.Parameters, invocation.Parameters);
        if (paramError is not null)
        {
            return VerbDispatchResult.Reject(VerbDispatchStatus.ParameterValidationFailed, paramError);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(verb.Metadata.MaxExecutionTime);

        var ctx = new VerbContext(
            InvocationId: invocation.InvocationId,
            VerbName: invocation.VerbName,
            VerbVersion: invocation.VerbVersion,
            Parameters: invocation.Parameters,
            ReceivedAt: DateTimeOffset.UtcNow,
            CancellationToken: cts.Token,
            Services: _services);

        // 6. Preconditions
        var pre = await verb.CheckPreconditionsAsync(ctx).ConfigureAwait(false);
        if (!pre.Satisfied)
        {
            return VerbDispatchResult.Reject(VerbDispatchStatus.PreconditionFailed,
                pre.Reason ?? "precondition_failed");
        }

        // 7. Capture rollback envelope BEFORE execution
        var envelope = await verb.CaptureRollbackAsync(ctx).ConfigureAwait(false);

        // 8. Execute
        VerbExecutionResult execResult;
        try
        {
            execResult = await verb.ExecuteAsync(ctx).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "verb.execute threw for {Verb}@{Version}", invocation.VerbName, invocation.VerbVersion);
            await InvokeRollbackAsync(verb, ctx, envelope).ConfigureAwait(false);
            return VerbDispatchResult.RolledBack(null, envelope, $"execute threw: {ex.GetType().Name}: {ex.Message}");
        }

        if (!execResult.Success)
        {
            await InvokeRollbackAsync(verb, ctx, envelope).ConfigureAwait(false);
            return VerbDispatchResult.RolledBack(execResult, envelope, execResult.Error ?? "execute_failed");
        }

        // 9. Postconditions
        var post = await verb.VerifyPostconditionsAsync(ctx).ConfigureAwait(false);
        if (!post.Satisfied)
        {
            await InvokeRollbackAsync(verb, ctx, envelope).ConfigureAwait(false);
            return VerbDispatchResult.RolledBack(execResult, envelope,
                post.Reason ?? "postcondition_failed");
        }

        return VerbDispatchResult.Executed(execResult, envelope);
    }

    private async Task InvokeRollbackAsync(IVerb verb, VerbContext ctx, VerbRollbackEnvelope envelope)
    {
        using var cts = new CancellationTokenSource(envelope.MaxInverseDuration);
        try
        {
            var rollbackCtx = ctx with { CancellationToken = cts.Token };
            await envelope.InverseFn(rollbackCtx).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex,
                "ROLLBACK FAILED for {Verb}@{Version} invocation {InvocationId} — escalating to operator",
                verb.Metadata.Name, verb.Metadata.Version, envelope.VerbInvocationId);
            // Rollback failure is an invariant violation; upstream handlers must escalate.
        }
    }

    internal static string? ValidateParameters(
        VerbParameterSchema schema,
        IReadOnlyDictionary<string, object?> parameters)
    {
        foreach (var def in schema.Required)
        {
            if (!parameters.TryGetValue(def.Name, out var value))
                return $"missing required parameter: {def.Name}";

            if (value is null)
                return $"parameter {def.Name} is null";

            var actualType = value.GetType();
            if (!def.ClrType.IsAssignableFrom(actualType) && !TryCoerce(def.ClrType, value))
                return $"parameter {def.Name} has wrong type: expected {def.ClrType.Name}, got {actualType.Name}";

            if (!string.IsNullOrEmpty(def.ValidationHint) && !ValidateHint(def.ValidationHint, value))
                return $"parameter {def.Name}={value} failed validation hint: {def.ValidationHint}";
        }
        return null;
    }

    private static bool TryCoerce(Type target, object value)
    {
        try
        {
            _ = Convert.ChangeType(value, target);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool ValidateHint(string hint, object value)
    {
        var s = value.ToString() ?? "";
        if (hint.StartsWith("enum:"))
        {
            var allowed = hint["enum:".Length..].Split('|', StringSplitOptions.RemoveEmptyEntries);
            return allowed.Contains(s, StringComparer.OrdinalIgnoreCase);
        }
        if (hint.StartsWith("regex:"))
        {
            var pattern = hint["regex:".Length..];
            return System.Text.RegularExpressions.Regex.IsMatch(s, pattern);
        }
        return true;
    }
}
