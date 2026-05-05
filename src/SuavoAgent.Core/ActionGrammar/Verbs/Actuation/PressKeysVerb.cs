using Microsoft.Extensions.DependencyInjection;
using SuavoAgent.Contracts.Ipc;

namespace SuavoAgent.Core.ActionGrammarV1.Verbs.Actuation;

/// <summary>
/// Press a sequence of virtual-key chords (e.g. "Ctrl+S", "Enter").
/// LOW-tier: each chord is parsed and validated Helper-side; an
/// unrecognised chord token returns "chord_parse_failure" without ever
/// touching SendInput.
/// </summary>
public sealed class PressKeysVerb : IVerb
{
    public const string VerbName = "press_keys";
    public const string VerbVersion = "1.0.0";

    public VerbMetadata Metadata { get; } = new(
        Name: VerbName,
        Version: VerbVersion,
        Description: "Press a sequence of virtual-key chords",
        RiskTier: VerbRiskTier.Low,
        BaaScope: new VerbBaaScope.None(),
        IsMutation: true,
        IsDestructive: false,
        MaxExecutionTime: TimeSpan.FromSeconds(30),
        Params: new VerbParameterSchema(new[]
        {
            new VerbParameterSpec("chords", typeof(System.Collections.IList), Required: true,
                ValidationHint: "non-empty list of chord strings (e.g. ['Ctrl+S', 'Enter'])"),
            new VerbParameterSpec("inter_chord_delay_ms", typeof(int), Required: false,
                ValidationHint: "0..1000"),
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
            Justification: "Synthetic key chords; chord parser rejects unknown tokens")
    );

    public Task<VerbPreconditionResult> CheckPreconditionsAsync(VerbContext ctx, CancellationToken ct)
    {
        if (!ctx.Parameters.TryGetValue("chords", out var raw) || raw is not System.Collections.IEnumerable enumerable)
        {
            return Task.FromResult(VerbPreconditionResult.Fail("chords_required", "chords parameter must be enumerable"));
        }
        var list = enumerable.Cast<object?>().ToList();
        if (list.Count == 0)
        {
            return Task.FromResult(VerbPreconditionResult.Fail("chords_empty", "chords list must contain at least one chord"));
        }
        if (list.Count > 64)
        {
            return Task.FromResult(VerbPreconditionResult.Fail("chords_too_many", "chords list exceeds limit (64)"));
        }
        if (list.Any(o => o is not string s || string.IsNullOrWhiteSpace(s)))
        {
            return Task.FromResult(VerbPreconditionResult.Fail("chords_invalid", "chords must be non-empty strings"));
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
        var enumerable = (System.Collections.IEnumerable)ctx.Parameters["chords"]!;
        var chords = enumerable.Cast<object?>().Select(o => (string)o!).ToList();
        var interDelay = ctx.Parameters.TryGetValue("inter_chord_delay_ms", out var d) && d is int di ? di : 80;

        var req = new PressKeysRequest(chords, interDelay);
        var result = await gateway.PressKeysAsync(req, ct).ConfigureAwait(false);
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
