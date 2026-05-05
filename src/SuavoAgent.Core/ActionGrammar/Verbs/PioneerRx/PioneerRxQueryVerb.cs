using Microsoft.Extensions.DependencyInjection;
using SuavoAgent.Core.Adapters;

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

    public Task<VerbPreconditionResult> CheckPreconditionsAsync(VerbContext ctx, CancellationToken ct)
    {
        if (!ctx.Parameters.TryGetValue("query_kind", out var qk) || qk is not string qks
            || (qks is not "ndc" and not "rx_number" and not "patient_identifier"))
        {
            return Task.FromResult(VerbPreconditionResult.Fail(
                "query_kind_invalid",
                "query_kind must be one of: ndc | rx_number | patient_identifier"));
        }
        if (!ctx.Parameters.TryGetValue("query_arg", out var qa) || qa is not string qas || string.IsNullOrWhiteSpace(qas))
        {
            return Task.FromResult(VerbPreconditionResult.Fail(
                "query_arg_non_empty", "query_arg must be a non-empty string"));
        }
        if (ctx.Services.GetService<IPharmacyReadAdapter>() is null)
        {
            return Task.FromResult(VerbPreconditionResult.Fail(
                "adapter_missing", "IPharmacyReadAdapter not registered"));
        }
        return Task.FromResult(VerbPreconditionResult.Ok());
    }

    public Task<VerbRollbackEnvelope> CaptureRollbackAsync(VerbContext ctx, CancellationToken ct) =>
        Task.FromResult(VerbRollbackEnvelope.None(ctx.InvocationId));

    public Task<VerbExecutionResult> ExecuteAsync(VerbContext ctx, CancellationToken ct)
    {
        // Phase 5.3 scaffold — wires verb metadata + dispatcher + audit chain
        // end-to-end. Live PMS query path lands when the writeback BAA
        // amendment is signed at Better Life and the verb is promoted from
        // tier='sandbox' (current) to tier='pilot' in the catalog.
        //
        // The verb succeeds with a synthetic result so dry-run dispatches
        // exercise the full audit/preconcition/postcondition chain — but no
        // PHI is ever queried.
        return Task.FromResult(VerbExecutionResult.Ok(new Dictionary<string, object?>
        {
            ["rows_returned"] = 0,
            ["schema_signature"] = "scaffold-no-op",
        }));
    }

    public Task<VerbPostconditionResult> VerifyPostconditionsAsync(
        VerbContext ctx,
        VerbExecutionResult executionResult,
        CancellationToken ct) => Task.FromResult(VerbPostconditionResult.Ok());
}
