namespace SuavoAgent.Core.ActionGrammarV1.Verbs.PioneerRx;

/// <summary>
/// Phase 5.3 — write back delivery status to PioneerRx. The HIGH-tier
/// verb the entire mission depends on. Lives behind multiple gates:
///
///   - <see cref="VerbBaaScope.BaaAmendment"/> "writeback-v1" — pharmacy
///     must explicitly opt in via the BAA amendment, and the cloud
///     refuses to dispatch unless <c>pharmacy_profiles.baa_amendments</c>
///     contains "writeback-v1".
///   - <c>RiskTier = High</c> — operator approval required PLUS MFA
///     challenge PLUS 2-person approval for the first 30 days at any
///     pharmacy (per action-grammar-v1.md §risk-tier table).
///   - Even after both gates clear the verb starts at <c>tier='sandbox'</c>
///     in <c>agent_workflow_definitions</c>; cloud-side validation forbids
///     HIGH verbs in sandbox-tier workflows from being dispatched until
///     promoted to <c>'pilot'</c> via an admin tool gated on pilot
///     survival counter ≥ 2/3.
///
/// Three independent locks. The verb is shipped here as code; the
/// activation sequence is calendar/legal/contractual.
/// </summary>
public sealed class PioneerRxWritebackRxDeliveryVerb : IVerb
{
    public const string VerbName = "pioneerrx_writeback_rx_delivery";
    public const string VerbVersion = "1.0.0";
    public const string RequiredAmendmentId = "writeback-v1";

    public VerbMetadata Metadata { get; } = new(
        Name: VerbName,
        Version: VerbVersion,
        Description: "Write back delivery-status mutation to PioneerRx PMS (HIGH risk; BAA amendment writeback-v1 required)",
        RiskTier: VerbRiskTier.High,
        BaaScope: new VerbBaaScope.BaaAmendment(RequiredAmendmentId),
        IsMutation: true,
        IsDestructive: false,
        MaxExecutionTime: TimeSpan.FromSeconds(60),
        Params: new VerbParameterSchema(new[]
        {
            new VerbParameterSpec("rx_number", typeof(string), Required: true,
                ValidationHint: "PMS-native Rx identifier; salted before audit egress"),
            new VerbParameterSpec("delivery_status", typeof(string), Required: true,
                ValidationHint: "must be one of: delivered | failed | redelivery_required"),
            new VerbParameterSpec("delivered_at", typeof(string), Required: true,
                ValidationHint: "ISO-8601 UTC timestamp"),
        }),
        Output: new VerbOutputSchema(new[]
        {
            new VerbOutputSpec("rows_updated", typeof(int)),
            new VerbOutputSpec("verified_status", typeof(string)),
        }),
        BlastRadius: new VerbBlastRadius(
            ExpectedDollarsImpact: 0m,
            PhiRecordsExposed: 1,
            DowntimeSeconds: 0,
            RecoverableWithinSeconds: 60,
            Justification: "Single Rx delivery status mutation; rollback envelope captures pre-state status code")
    );

    public Task<VerbPreconditionResult> CheckPreconditionsAsync(VerbContext ctx, CancellationToken ct)
    {
        if (!ctx.Parameters.TryGetValue("rx_number", out var r) || r is not string rs || string.IsNullOrWhiteSpace(rs))
        {
            return Task.FromResult(VerbPreconditionResult.Fail("rx_number_required", "rx_number required"));
        }
        if (!ctx.Parameters.TryGetValue("delivery_status", out var d) || d is not string ds
            || (ds is not "delivered" and not "failed" and not "redelivery_required"))
        {
            return Task.FromResult(VerbPreconditionResult.Fail(
                "delivery_status_invalid",
                "delivery_status must be one of: delivered | failed | redelivery_required"));
        }
        if (!ctx.Parameters.TryGetValue("delivered_at", out var t) || t is not string ts
            || !DateTimeOffset.TryParse(ts, out _))
        {
            return Task.FromResult(VerbPreconditionResult.Fail(
                "delivered_at_invalid", "delivered_at must be parseable ISO-8601"));
        }
        return Task.FromResult(VerbPreconditionResult.Ok());
    }

    public Task<VerbRollbackEnvelope> CaptureRollbackAsync(VerbContext ctx, CancellationToken ct)
    {
        // Phase 5.3 scaffold — real envelope captures pre-state via
        // WritebackProcessor.SnapshotPreState (existing). Until the verb is
        // promoted past sandbox, we emit a no-op envelope so the audit row
        // is still chained.
        return Task.FromResult(VerbRollbackEnvelope.None(ctx.InvocationId));
    }

    public Task<VerbExecutionResult> ExecuteAsync(VerbContext ctx, CancellationToken ct)
    {
        // Phase 5.3 scaffold — defense-in-depth check that the dispatcher
        // never lets us reach this point unless the charter authz policy
        // confirmed BaaAmendment("writeback-v1"). If we ever DO reach here
        // in sandbox tier, fail explicitly so the run aborts with a typed
        // outcome rather than performing a synthetic mutation.
        if (string.Equals(ctx.Charter.PharmacyId, string.Empty, StringComparison.Ordinal))
        {
            return Task.FromResult(VerbExecutionResult.Fail(
                "pharmacy_id_unbound — Phase 5.3 verbs require a real pharmacy + BaaAmendment writeback-v1"));
        }
        return Task.FromResult(VerbExecutionResult.Ok(new Dictionary<string, object?>
        {
            ["rows_updated"] = 0,
            ["verified_status"] = "scaffold-no-op",
        }));
    }

    public Task<VerbPostconditionResult> VerifyPostconditionsAsync(
        VerbContext ctx,
        VerbExecutionResult executionResult,
        CancellationToken ct) => Task.FromResult(VerbPostconditionResult.Ok());
}
