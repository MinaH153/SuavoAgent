using Microsoft.Extensions.DependencyInjection;
using SuavoAgent.Contracts.Ipc;

namespace SuavoAgent.Core.ActionGrammarV1.Verbs.Actuation;

/// <summary>
/// Find a UIA element by accessible-name label inside an allowlisted
/// process and click its centroid via SendInput. LOW risk for the
/// Phase-5.2 sandbox set because the process allowlist is enforced
/// Helper-side and the click is a single LMB at a deterministic point.
/// </summary>
public sealed class ClickByLabelVerb : IVerb
{
    public const string VerbName = "click_by_label";
    public const string VerbVersion = "1.0.0";

    public VerbMetadata Metadata { get; } = new(
        Name: VerbName,
        Version: VerbVersion,
        Description: "Locate UIA element by accessible-name label and left-click its centroid",
        RiskTier: VerbRiskTier.Low,
        BaaScope: new VerbBaaScope.None(),
        IsMutation: true,
        IsDestructive: false,
        MaxExecutionTime: TimeSpan.FromSeconds(15),
        Params: new VerbParameterSchema(new[]
        {
            new VerbParameterSpec("label", typeof(string), Required: true,
                ValidationHint: "non-empty UI label (e.g. 'File', 'Save')"),
            new VerbParameterSpec("process_name", typeof(string), Required: true,
                ValidationHint: "must be in actuation allowlist (notepad / notepad.exe / calc / calc.exe)"),
            new VerbParameterSpec("match_mode", typeof(string), Required: false,
                ValidationHint: "exact (default) | contains_ci"),
            new VerbParameterSpec("timeout_ms", typeof(int), Required: false,
                ValidationHint: "1..60000"),
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
            Justification: "Single click at resolved screen point; sandbox-only allowlist")
    );

    public Task<VerbPreconditionResult> CheckPreconditionsAsync(VerbContext ctx, CancellationToken ct)
    {
        if (!ctx.Parameters.TryGetValue("label", out var label) || label is not string ls || string.IsNullOrWhiteSpace(ls))
        {
            return Task.FromResult(VerbPreconditionResult.Fail("label_non_empty", "label must be a non-empty string"));
        }
        if (!ctx.Parameters.TryGetValue("process_name", out var pn) || pn is not string ps || string.IsNullOrWhiteSpace(ps))
        {
            return Task.FromResult(VerbPreconditionResult.Fail("process_name_non_empty", "process_name must be a non-empty string"));
        }
        var allowed = ActuationAllowlistedSandboxApps.ProcessNames.Values
            .Concat(ActuationAllowlistedSandboxApps.ProcessNames.Keys)
            .Any(allowedName => string.Equals(allowedName, ps, StringComparison.OrdinalIgnoreCase));
        if (!allowed)
        {
            return Task.FromResult(VerbPreconditionResult.Fail(
                "process_not_allowlisted",
                $"process_name '{ps}' is not in the actuation allowlist"));
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
        var label = (string)ctx.Parameters["label"]!;
        var processName = (string)ctx.Parameters["process_name"]!;
        var matchMode = ctx.Parameters.TryGetValue("match_mode", out var mm) && mm is string mms ? mms : "exact";
        var timeoutMs = ctx.Parameters.TryGetValue("timeout_ms", out var tm) && tm is int tmi ? tmi : 8000;

        var req = new ClickByLabelRequest(label, processName, matchMode, timeoutMs, ctx.DryRun);
        var result = await gateway.ClickByLabelAsync(req, ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            // Preserve effective dry-run state for audit chain (Bug 21 / Codex HIGH-2).
            return VerbExecutionResult.Fail(
                $"{result.RejectionCode}: {result.RejectionReason}",
                new Dictionary<string, object?> { ["dry_run"] = result.DryRun });
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
