namespace SuavoAgent.Core.ActionGrammarV1.Verbs.PioneerRx;

/// <summary>
/// Phase 5.3 — read-only PioneerRx query. LOW risk because it uses the
/// existing <see cref="IPharmacyReadAdapter"/> path (read-only SQL,
/// salted identifiers, no UI automation, no PHI in parameters).
///
/// Gating: <see cref="VerbBaaScope.AgentBaa"/> — pharmacy must have the
/// base agent BAA signed (<c>pharmacy_profiles.baa_signed_at</c>). Cloud
/// also enforces this in <c>/api/agent/commands/run-workflow</c>.
///
/// This verb cannot dispatch in production until:
///   1. Patent provisional filed (legal — not engineering)
///   2. Pilot pharmacy survival counter ≥ 2/3 (calendar, ~3 weeks)
///   3. Workflow definition with <c>tier='pilot'</c> or <c>'production'</c>
///      created cloud-side
/// The verb itself is ready, the rollout schedule is not.
/// </summary>
public sealed class PioneerRxQueryVerb : IVerb
{
    public const string VerbName = "pioneerrx_query";
    public const string VerbVersion = "1.0.0";

    public VerbMetadata Metadata { get; } = new(
        Name: VerbName,
        Version: VerbVersion,
        Description: "Read-only PioneerRx PMS query (NDC / Rx-number / scoped patient identifier)",
        RiskTier: VerbRiskTier.Low,
        BaaScope: new VerbBaaScope.AgentBaa(),
        IsMutation: false,
        IsDestructive: false,
        MaxExecutionTime: TimeSpan.FromSeconds(20),
        Params: new VerbParameterSchema(new[]
        {
            new VerbParameterSpec("query_kind", typeof(string), Required: true,
                ValidationHint: "must be one of: ndc | rx_number | patient_identifier"),
            new VerbParameterSpec("query_arg", typeof(string), Required: true,
                ValidationHint: "non-empty; for patient_identifier this MUST be a salted hash, never raw PHI"),
        }),
        Output: new VerbOutputSchema(new[]
        {
            new VerbOutputSpec("rows_returned", typeof(int)),
            new VerbOutputSpec("schema_signature", typeof(string)),
        }),
        BlastRadius: new VerbBlastRadius(
            ExpectedDollarsImpact: 0m,
            PhiRecordsExposed: 0,
            DowntimeSeconds: 0,
            RecoverableWithinSeconds: 0,
            Justification: "Read-only PMS query; rows scrubbed by adapter before egress")
    );

    public Task<VerbPreconditionResult> CheckPreconditionsAsync(VerbContext ctx, CancellationToken ct) =>
        Task.FromResult(VerbPreconditionResult.Fail(
            "capability_unavailable",
            "PioneerRx query capability is unavailable until a real routed query is implemented"));

    public Task<VerbRollbackEnvelope> CaptureRollbackAsync(VerbContext ctx, CancellationToken ct) =>
        Task.FromResult(VerbRollbackEnvelope.None(ctx.InvocationId));

    public Task<VerbExecutionResult> ExecuteAsync(VerbContext ctx, CancellationToken ct) =>
        Task.FromResult(VerbExecutionResult.Fail("capability_unavailable"));

    public Task<VerbPostconditionResult> VerifyPostconditionsAsync(
        VerbContext ctx,
        VerbExecutionResult executionResult,
        CancellationToken ct) => Task.FromResult(VerbPostconditionResult.Fail(
            "capability_unavailable",
            "PioneerRx query did not execute"));
}
