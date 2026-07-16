using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Pricing;

/// <summary>
/// Production authority boundary for pricing. Configuration values never grant
/// access: only the verified append-only local ledger can admit a run.
/// </summary>
internal static class PricingApprovalAuthorityResolver
{
    internal static PricingCostBasisAuthority? ResolveOrStageProposal(
        AgentStateDb db,
        string pharmacyId,
        string agentId,
        string machineFingerprint,
        PricingObservationContract observation,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string> trustedPublicKeys,
        out string code) => ResolveOrStageProposal(
            db,
            pharmacyId,
            agentId,
            machineFingerprint,
            observation,
            now,
            trustedPublicKeys,
            expectedApprovalId: null,
            expectedGrantDigest: null,
            out code);

    internal static PricingCostBasisAuthority? ResolveOrStageProposal(
        AgentStateDb db,
        string pharmacyId,
        string agentId,
        string machineFingerprint,
        PricingObservationContract observation,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string> trustedPublicKeys,
        string? expectedApprovalId,
        string? expectedGrantDigest,
        out string code)
    {
        if (!db.TryAdmitPricingCloudAuthority(now, out code))
            return null;

        if (db.TryGetInstalledPricingAuthority(
                pharmacyId,
                agentId,
                machineFingerprint,
                observation,
                now,
                trustedPublicKeys,
                expectedGrantDigest,
                out var authority,
                out code))
        {
            if (expectedApprovalId is null && expectedGrantDigest is null)
                return authority;
            if (authority is not null &&
                string.Equals(
                    authority.ApprovalId,
                    expectedApprovalId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    authority.ApprovalDigest,
                    expectedGrantDigest,
                    StringComparison.Ordinal))
                return authority;
            code = "pricing_job_authority_binding_invalid";
            return null;
        }

        if (expectedApprovalId is not null || expectedGrantDigest is not null)
        {
            if (code == "pricing_cost_basis_approval_required")
                code = "pricing_job_authority_binding_missing";
            return null;
        }

        if (code == "pricing_cost_basis_approval_invalid")
            return null;

        var proposal = db.StagePricingApprovalProposal(
            pharmacyId,
            agentId,
            machineFingerprint,
            observation,
            now,
            out var proposalCode);
        if (proposal is null)
        {
            code = proposalCode;
            return null;
        }

        code = "pricing_cost_basis_approval_pending";
        return null;
    }
}
