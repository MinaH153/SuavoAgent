using Microsoft.Extensions.DependencyInjection;
using SuavoAgent.Contracts.Ipc;

namespace SuavoAgent.Core.ActionGrammarV1.Verbs.Actuation;

/// <summary>
/// Type plain text into the focused field via Unicode SendInput. MED-tier
/// because typing into the wrong window or a field that captures PHI is a
/// HIPAA-relevant failure mode. Mitigations:
///
///   - PhiPatternGuard runs Helper-side BEFORE any keystroke is injected.
///     If the input matches SSN / structured field / address / email /
///     phone-or-NDC, the request is rejected with "phi_pattern_detected".
///   - clear_first prepends Ctrl+A + Delete so workflows can guarantee
///     they're typing into an empty field — no accidental concatenation
///     with a stale value.
///   - Per-key delay (default 25ms) so the agent never out-paces UIA
///     focus transitions.
/// </summary>
public sealed class TypeIntoFieldVerb : IVerb
{
    public const string VerbName = "type_into_field";
    public const string VerbVersion = "1.0.0";

    public VerbMetadata Metadata { get; } = new(
        Name: VerbName,
        Version: VerbVersion,
        Description: "Type plain Unicode text into the focused field; rejects PHI patterns Helper-side",
        RiskTier: VerbRiskTier.Medium,
        BaaScope: new VerbBaaScope.None(),
        IsMutation: true,
        IsDestructive: false,
        MaxExecutionTime: TimeSpan.FromSeconds(60),
        Params: new VerbParameterSchema(new[]
        {
            new VerbParameterSpec("text", typeof(string), Required: true,
                ValidationHint: "must not match PHI patterns (SSN, address, email, phone, NDC)"),
            new VerbParameterSpec("clear_first", typeof(bool), Required: false,
                ValidationHint: "prepend Ctrl+A then Delete"),
            new VerbParameterSpec("per_key_delay_ms", typeof(int), Required: false,
                ValidationHint: "0..500"),
        }),
        Output: new VerbOutputSchema(new[]
        {
            new VerbOutputSpec("dry_run", typeof(bool)),
            new VerbOutputSpec("evidence_hash", typeof(string)),
            new VerbOutputSpec("duration_ms", typeof(long)),
        }),
        BlastRadius: new VerbBlastRadius(
            ExpectedDollarsImpact: 0m,
            PhiRecordsExposed: 0,
            DowntimeSeconds: 0,
            RecoverableWithinSeconds: 5,
            Justification: "Typed text only; PhiPatternGuard rejects PHI shapes before injection")
    );

    public Task<VerbPreconditionResult> CheckPreconditionsAsync(VerbContext ctx, CancellationToken ct)
    {
        if (!ctx.Parameters.TryGetValue("text", out var raw) || raw is not string s)
        {
            return Task.FromResult(VerbPreconditionResult.Fail("text_required", "text parameter must be a string"));
        }
        if (s.Length > 10_000)
        {
            return Task.FromResult(VerbPreconditionResult.Fail("text_too_long", "text exceeds 10k characters"));
        }
        if (ctx.Services.GetService<IActuationGateway>() is null)
        {
            return Task.FromResult(VerbPreconditionResult.Fail("gateway_missing", "IActuationGateway not registered"));
        }
        return Task.FromResult(VerbPreconditionResult.Ok());
    }

    public Task<VerbRollbackEnvelope> CaptureRollbackAsync(VerbContext ctx, CancellationToken ct) =>
        Task.FromResult(VerbRollbackEnvelope.None(ctx.InvocationId));

    public async Task<VerbExecutionResult> ExecuteAsync(VerbContext ctx, CancellationToken ct)
    {
        var gateway = ctx.Services.GetRequiredService<IActuationGateway>();
        var text = (string)ctx.Parameters["text"]!;
        var clearFirst = ctx.Parameters.TryGetValue("clear_first", out var c) && c is bool cb && cb;
        var perKey = ctx.Parameters.TryGetValue("per_key_delay_ms", out var pk) && pk is int pkInt ? pkInt : 25;

        var req = new TypeTextRequest(text, clearFirst, perKey, ctx.DryRun);
        var result = await gateway.TypeTextAsync(req, ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            return VerbExecutionResult.Fail($"{result.RejectionCode}: {result.RejectionReason}");
        }
        return VerbExecutionResult.Ok(new Dictionary<string, object?>
        {
            ["dry_run"] = result.DryRun,
            ["evidence_hash"] = result.EvidenceHash,
            ["duration_ms"] = result.DurationMs,
        });
    }

    public Task<VerbPostconditionResult> VerifyPostconditionsAsync(
        VerbContext ctx,
        VerbExecutionResult executionResult,
        CancellationToken ct) => Task.FromResult(VerbPostconditionResult.Ok());
}
