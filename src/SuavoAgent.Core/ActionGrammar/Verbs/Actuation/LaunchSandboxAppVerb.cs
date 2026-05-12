using Microsoft.Extensions.DependencyInjection;
using SuavoAgent.Contracts.Ipc;

namespace SuavoAgent.Core.ActionGrammarV1.Verbs.Actuation;

/// <summary>
/// Launch an allowlisted sandbox application (Notepad / Calculator only)
/// — Phase-5.2 demo workflow scaffolding. LOW risk because the allowlist
/// is enforced both here AND in <c>SendInputDriver.LaunchSandboxAppAsync</c>
/// (defense in depth), and neither app touches PHI in this phase.
///
/// PioneerRx and other PMS launches are deliberately excluded — they ship
/// in Phase 5.3 behind their own verb (<c>pioneerrx_launch</c>) gated on
/// patent + BAA addendum + N=2/3 pilots.
/// </summary>
public sealed class LaunchSandboxAppVerb : IVerb
{
    public const string VerbName = "launch_sandbox_app";
    public const string VerbVersion = "1.0.0";

    public VerbMetadata Metadata { get; } = new(
        Name: VerbName,
        Version: VerbVersion,
        Description: "Launch an allowlisted sandbox application (Notepad / Calculator)",
        RiskTier: VerbRiskTier.Low,
        BaaScope: new VerbBaaScope.None(),
        IsMutation: true,
        IsDestructive: false,
        MaxExecutionTime: TimeSpan.FromSeconds(10),
        Params: new VerbParameterSchema(new[]
        {
            new VerbParameterSpec("app_key", typeof(string), Required: true,
                ValidationHint: "must be one of: notepad, calculator"),
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
            Justification: "Sandbox process; user can close window manually if rollback fails")
    );

    public Task<VerbPreconditionResult> CheckPreconditionsAsync(VerbContext ctx, CancellationToken ct)
    {
        if (!ctx.Parameters.TryGetValue("app_key", out var raw)
            || raw is not string s
            || string.IsNullOrWhiteSpace(s))
        {
            return Task.FromResult(VerbPreconditionResult.Fail(
                "app_key_non_empty", "app_key parameter must be a non-empty string"));
        }
        if (!ActuationAllowlistedSandboxApps.ProcessNames.ContainsKey(s))
        {
            return Task.FromResult(VerbPreconditionResult.Fail(
                "app_key_not_allowlisted", $"app_key '{s}' is not allowlisted"));
        }
        if (ctx.Services.GetService<IActuationGateway>() is null)
        {
            return Task.FromResult(VerbPreconditionResult.Fail(
                "gateway_missing", "IActuationGateway not registered"));
        }
        return Task.FromResult(VerbPreconditionResult.Ok());
    }

    public Task<VerbRollbackEnvelope> CaptureRollbackAsync(VerbContext ctx, CancellationToken ct) =>
        Task.FromResult(VerbRollbackEnvelope.None(ctx.InvocationId));

    public async Task<VerbExecutionResult> ExecuteAsync(VerbContext ctx, CancellationToken ct)
    {
        var gateway = ctx.Services.GetRequiredService<IActuationGateway>();
        var appKey = (string)ctx.Parameters["app_key"]!;
        var result = await gateway.LaunchSandboxAppAsync(new LaunchSandboxAppRequest(appKey, ctx.DryRun), ct).ConfigureAwait(false);
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
        CancellationToken ct)
    {
        // Phase 5.2: process-existence verification is deferred. The cloud
        // audit row + dry-run evidence hash is the operator's trust signal.
        // Phase 5.3 promotes this to a real probe that opens UIA against
        // the launched window.
        return Task.FromResult(VerbPostconditionResult.Ok());
    }
}
