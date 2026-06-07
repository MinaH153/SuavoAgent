using Microsoft.Extensions.DependencyInjection;
using SuavoAgent.Contracts.Ipc;

namespace SuavoAgent.Core.ActionGrammarV1.Verbs.Actuation;

/// <summary>
/// Read a UIA element's value and assert it matches an expected string — the workflow-level
/// VERIFICATION keystone. Every other actuation verb's postcondition is a near-no-op (the Helper
/// returns Ok once a click resolves or a type is sent), so a workflow could "complete" without ever
/// reaching the intended END STATE. assert_element closes that: it reads the element back and FAILS
/// the step (→ the run fails) when reality doesn't match, so "Completed" means the screen actually
/// shows what we intended (Calc reads "12", the note we typed is present).
///
/// READ-ONLY (IsMutation=false): it injects no input, so it is the one actuation verb with zero
/// blast radius. PHI-safe: the Helper compares Helper-side and NEVER returns the raw read value — a
/// mismatch carries only a length/mode hint. In a dry-run the Helper short-circuits to a pass
/// (asserting live state after dry-run actuation that never happened is meaningless).
/// </summary>
public sealed class AssertElementVerb : IVerb
{
    public const string VerbName = "assert_element";
    public const string VerbVersion = "1.0.0";

    public VerbMetadata Metadata { get; } = new(
        Name: VerbName,
        Version: VerbVersion,
        Description: "Read a UIA element's value and assert it matches expected (read-only verification)",
        RiskTier: VerbRiskTier.Low,
        BaaScope: new VerbBaaScope.None(),
        IsMutation: false,
        IsDestructive: false,
        MaxExecutionTime: TimeSpan.FromSeconds(15),
        Params: new VerbParameterSchema(new[]
        {
            new VerbParameterSpec("process_name", typeof(string), Required: true,
                ValidationHint: "must be in the actuation allowlist"),
            new VerbParameterSpec("expected", typeof(string), Required: true,
                ValidationHint: "the value the element should read"),
            new VerbParameterSpec("automation_id", typeof(string), Required: false,
                ValidationHint: "locate by AutomationId (preferred)"),
            new VerbParameterSpec("name", typeof(string), Required: false,
                ValidationHint: "locate by accessible Name"),
            new VerbParameterSpec("control_type", typeof(string), Required: false,
                ValidationHint: "filter by control type (e.g. Edit, Text, Document)"),
            new VerbParameterSpec("match_mode", typeof(string), Required: false,
                ValidationHint: "normalized (default) | exact | contains_ci"),
            new VerbParameterSpec("timeout_ms", typeof(int), Required: false,
                ValidationHint: "1..60000 — UIA value can lag, so the read retries until match or timeout"),
        }),
        Output: new VerbOutputSchema(new[]
        {
            new VerbOutputSpec("dry_run", typeof(bool)),
            new VerbOutputSpec("duration_ms", typeof(long)),
        }),
        BlastRadius: new VerbBlastRadius(
            ExpectedDollarsImpact: 0m,
            PhiRecordsExposed: 0,
            DowntimeSeconds: 0,
            RecoverableWithinSeconds: 0,
            Justification: "Read-only UIA value compare; injects no input; raw value never leaves the box")
    );

    public Task<VerbPreconditionResult> CheckPreconditionsAsync(VerbContext ctx, CancellationToken ct)
    {
        if (!ctx.Parameters.TryGetValue("process_name", out var pn) || pn is not string ps || string.IsNullOrWhiteSpace(ps))
        {
            return Task.FromResult(VerbPreconditionResult.Fail("process_name_non_empty", "process_name must be a non-empty string"));
        }
        if (!ctx.Parameters.TryGetValue("expected", out var ex) || ex is not string es || es.Length == 0)
        {
            return Task.FromResult(VerbPreconditionResult.Fail("expected_non_empty", "expected must be a non-empty string"));
        }
        // At least one locator — without one the Helper has nothing to find.
        var hasLocator =
            (ctx.Parameters.TryGetValue("automation_id", out var a) && a is string asv && !string.IsNullOrWhiteSpace(asv)) ||
            (ctx.Parameters.TryGetValue("name", out var n) && n is string nsv && !string.IsNullOrWhiteSpace(nsv)) ||
            (ctx.Parameters.TryGetValue("control_type", out var c) && c is string csv && !string.IsNullOrWhiteSpace(csv));
        if (!hasLocator)
        {
            return Task.FromResult(VerbPreconditionResult.Fail(
                "locator_required", "one of automation_id, name, or control_type is required"));
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
        var processName = (string)ctx.Parameters["process_name"]!;
        var expected = (string)ctx.Parameters["expected"]!;
        var automationId = ctx.Parameters.TryGetValue("automation_id", out var a) && a is string asv && !string.IsNullOrWhiteSpace(asv) ? asv : null;
        var name = ctx.Parameters.TryGetValue("name", out var n) && n is string nsv && !string.IsNullOrWhiteSpace(nsv) ? nsv : null;
        var controlType = ctx.Parameters.TryGetValue("control_type", out var c) && c is string csv && !string.IsNullOrWhiteSpace(csv) ? csv : null;
        var matchMode = ctx.Parameters.TryGetValue("match_mode", out var mm) && mm is string mms ? mms : "normalized";
        var timeoutMs = ctx.Parameters.TryGetValue("timeout_ms", out var tm) && tm is int tmi ? tmi : 8000;

        var req = new AssertElementRequest(
            ProcessName: processName,
            Expected: expected,
            MatchMode: matchMode,
            TimeoutMs: timeoutMs,
            AutomationId: automationId,
            Name: name,
            ControlType: controlType,
            DryRun: ctx.DryRun);
        var result = await gateway.AssertElementAsync(req, ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            return VerbExecutionResult.Fail(
                $"{result.RejectionCode}: {result.RejectionReason}",
                new Dictionary<string, object?> { ["dry_run"] = result.DryRun });
        }
        return VerbExecutionResult.Ok(new Dictionary<string, object?>
        {
            ["dry_run"] = result.DryRun,
            ["duration_ms"] = result.DurationMs,
        });
    }

    public Task<VerbPostconditionResult> VerifyPostconditionsAsync(
        VerbContext ctx,
        VerbExecutionResult executionResult,
        CancellationToken ct) => Task.FromResult(VerbPostconditionResult.Ok());
}
