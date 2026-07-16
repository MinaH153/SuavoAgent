using Microsoft.Extensions.DependencyInjection;
using SuavoAgent.Contracts.Ipc;

namespace SuavoAgent.Core.ActionGrammarV1.Verbs.Actuation;

/// <summary>
/// Click an element matched by GREEN-tier structural signature (ControlType + AutomationId
/// [+ ClassName]) inside an allowlisted process. This is the actuation primitive learned-template
/// REPLAY needs: templates store structural signatures (never PHI names), so replay cannot use the
/// name-based <see cref="ClickByLabelVerb"/>. The Helper resolves the signature via its UIA walker
/// and clicks the centroid through SendInputDriver — so the kill switch / pause / dry-run gate apply
/// uniformly, identical to click_by_label.
/// </summary>
public sealed class ClickBySignatureVerb : IVerb
{
    public const string VerbName = "click_by_signature";
    public const string VerbVersion = "1.0.0";

    public VerbMetadata Metadata { get; } = new(
        Name: VerbName,
        Version: VerbVersion,
        Description: "Locate UIA element by structural signature (ControlType + AutomationId) and left-click its centroid",
        RiskTier: VerbRiskTier.Low,
        BaaScope: new VerbBaaScope.None(),
        IsMutation: true,
        IsDestructive: false,
        MaxExecutionTime: TimeSpan.FromSeconds(15),
        Params: new VerbParameterSchema(new[]
        {
            new VerbParameterSpec("automationId", typeof(string), Required: true,
                ValidationHint: "non-empty UIA AutomationId"),
            new VerbParameterSpec("controlType", typeof(string), Required: true,
                ValidationHint: "UIA ControlType string (e.g. 'Button', 'Edit')"),
            new VerbParameterSpec("className", typeof(string), Required: false,
                ValidationHint: "optional UIA ClassName"),
            new VerbParameterSpec("process_name", typeof(string), Required: true,
                ValidationHint: "must be in actuation allowlist (notepad / notepad.exe / calc / calc.exe)"),
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
            Justification: "Single click at signature-resolved screen point; sandbox-only allowlist")
    );

    public Task<VerbPreconditionResult> CheckPreconditionsAsync(VerbContext ctx, CancellationToken ct)
    {
        if (!ctx.Parameters.TryGetValue("automationId", out var aid) || aid is not string ais || string.IsNullOrWhiteSpace(ais))
            return Task.FromResult(VerbPreconditionResult.Fail("automation_id_non_empty", "automationId must be a non-empty string"));
        if (!ctx.Parameters.TryGetValue("controlType", out var ctl) || ctl is not string ctls || string.IsNullOrWhiteSpace(ctls))
            return Task.FromResult(VerbPreconditionResult.Fail("control_type_non_empty", "controlType must be a non-empty string"));
        if (!ctx.Parameters.TryGetValue("process_name", out var pn) || pn is not string ps || string.IsNullOrWhiteSpace(ps))
            return Task.FromResult(VerbPreconditionResult.Fail("process_name_non_empty", "process_name must be a non-empty string"));

        if (!ActuationAllowlistedSandboxApps.IsDeclaredSandboxProcess(ps))
            return Task.FromResult(VerbPreconditionResult.Fail("process_not_allowlisted", $"process_name '{ps}' is not in the actuation allowlist"));

        if (ctx.Services.GetService<IActuationGateway>() is null)
            return Task.FromResult(VerbPreconditionResult.Fail("gateway_missing", "IActuationGateway not registered"));

        return Task.FromResult(VerbPreconditionResult.Ok());
    }

    public Task<VerbRollbackEnvelope> CaptureRollbackAsync(VerbContext ctx, CancellationToken ct) =>
        Task.FromResult(VerbRollbackEnvelope.None(ctx.InvocationId));

    public async Task<VerbExecutionResult> ExecuteAsync(VerbContext ctx, CancellationToken ct)
    {
        var gateway = ctx.Services.GetRequiredService<IActuationGateway>();
        var automationId = (string)ctx.Parameters["automationId"]!;
        var controlType = (string)ctx.Parameters["controlType"]!;
        var className = ctx.Parameters.TryGetValue("className", out var cn) && cn is string cns && !string.IsNullOrWhiteSpace(cns) ? cns : null;
        var processName = (string)ctx.Parameters["process_name"]!;
        var timeoutMs = ctx.Parameters.TryGetValue("timeout_ms", out var tm) && tm is int tmi ? tmi : 8000;

        var req = new ClickBySignatureRequest(controlType, automationId, className, processName, timeoutMs, ctx.DryRun);
        var result = await gateway.ClickBySignatureAsync(req, ct).ConfigureAwait(false);
        if (!result.Ok)
        {
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
        VerbContext ctx, VerbExecutionResult executionResult, CancellationToken ct) =>
        Task.FromResult(VerbPostconditionResult.Ok());
}
