namespace SuavoAgent.Core.ActionGrammarV1.Verbs.PioneerRx;

/// <summary>
/// Phase 5.3 — UI click against PioneerPharmacy.exe. MED tier because
/// clicking the wrong button in a PMS can change Rx state. Process
/// allowlist enforced Helper-side — this verb cannot accidentally hit
/// Notepad / Calc / any non-PMS window.
///
/// Live dispatch is gated on:
///   1. PioneerRx Click BAA amendment signed at the target pharmacy
///   2. Workflow definition tier = 'pilot' or 'production'
///   3. Pilot survival counter satisfied
///
/// The verb itself ships behind feature flag — <c>tier='sandbox'</c>
/// definitions cannot reference this verb (cloud-side validation), and
/// even after promotion the per-pharmacy BAA amendment gate fires before
/// the agent ever receives the signed command.
/// </summary>
public sealed class PioneerRxClickVerb : IVerb
{
    public const string VerbName = "pioneerrx_click";
    public const string VerbVersion = "1.0.0";

    public VerbMetadata Metadata { get; } = new(
        Name: VerbName,
        Version: VerbVersion,
        Description: "Click an accessible-name label inside PioneerRx (PioneerPharmacy.exe)",
        RiskTier: VerbRiskTier.Medium,
        BaaScope: new VerbBaaScope.AgentBaa(),
        IsMutation: true,
        IsDestructive: false,
        MaxExecutionTime: TimeSpan.FromSeconds(30),
        Params: new VerbParameterSchema(new[]
        {
            new VerbParameterSpec("label", typeof(string), Required: true,
                ValidationHint: "non-empty UIA accessible name; never a PHI substring"),
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
            Justification: "Single click in the live PMS window; UIA-resolved coordinates")
    );

    public Task<VerbPreconditionResult> CheckPreconditionsAsync(VerbContext ctx, CancellationToken ct) =>
        Task.FromResult(VerbPreconditionResult.Fail(
            "capability_unavailable",
            "PioneerRx click capability is unavailable until signed live execution is implemented"));

    public Task<VerbRollbackEnvelope> CaptureRollbackAsync(VerbContext ctx, CancellationToken ct) =>
        Task.FromResult(VerbRollbackEnvelope.None(ctx.InvocationId));

    public Task<VerbExecutionResult> ExecuteAsync(VerbContext ctx, CancellationToken ct) =>
        Task.FromResult(VerbExecutionResult.Fail("capability_unavailable"));

    public Task<VerbPostconditionResult> VerifyPostconditionsAsync(
        VerbContext ctx,
        VerbExecutionResult executionResult,
        CancellationToken ct) => Task.FromResult(VerbPostconditionResult.Fail(
            "capability_unavailable",
            "PioneerRx click did not execute"));
}
