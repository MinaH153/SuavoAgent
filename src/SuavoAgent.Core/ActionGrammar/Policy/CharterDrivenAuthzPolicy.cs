using SuavoAgent.Core.Mission;

namespace SuavoAgent.Core.ActionGrammarV1.Policy;

/// <summary>
/// Phase-1 authz policy. Rules:
///
/// 1. HIGH-risk verb is always denied until Cedar + operator approval gates
///    are wired (Phase A item A1). Fail closed.
/// 2. Verb's BaaScope must be compatible with the charter — a
///    <c>BaaAmendment(id)</c> scope requires the charter's constraint set to
///    contain a matching constraint id.
/// 3. Blast-radius PhiRecordsExposed must be zero for any verb whose
///    BaaScope is <see cref="VerbBaaScope.None"/>. PHI-adjacent side-effects
///    demand BAA scope structurally.
/// 4. A verb whose BaaScope is <see cref="VerbBaaScope.Forbidden"/> is never
///    allowed.
///
/// Cedar-backed enforcement replaces this class once the policy store is
/// populated. The public surface (<see cref="IAuthzPolicy"/>) stays stable.
/// </summary>
public sealed class CharterDrivenAuthzPolicy : IAuthzPolicy
{
    public const string PolicyId = "charter-driven-phase1";

    public AuthzDecision Evaluate(VerbContext ctx, IVerb verb)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(verb);

        var meta = verb.Metadata;

        if (meta.BaaScope is VerbBaaScope.Forbidden)
        {
            return AuthzDecision.Deny(
                PolicyId,
                $"verb '{meta.Name}' declared Forbidden BaaScope");
        }

        if (meta.RiskTier == VerbRiskTier.High)
        {
            return AuthzDecision.Deny(
                PolicyId,
                $"verb '{meta.Name}' RiskTier=HIGH requires operator approval + Cedar policy (Phase A item A1); denied in Phase 1");
        }

        if (meta.RiskTier == VerbRiskTier.Unknown)
        {
            return AuthzDecision.Deny(
                PolicyId,
                $"verb '{meta.Name}' RiskTier=Unknown — structurally rejected per action-grammar-v1.md §Risk tiers");
        }

        if (meta.BaaScope is VerbBaaScope.BaaAmendment amendment)
        {
            var hasConstraint = ctx.Charter.Constraints.Any(c =>
                string.Equals(c.Id, $"baa-amendment:{amendment.AmendmentId}", StringComparison.Ordinal));
            if (!hasConstraint)
            {
                return AuthzDecision.Deny(
                    PolicyId,
                    $"verb '{meta.Name}' requires BAA amendment '{amendment.AmendmentId}' which is not declared in the charter");
            }
        }

        if (meta.BaaScope is VerbBaaScope.None && meta.BlastRadius.PhiRecordsExposed > 0)
        {
            return AuthzDecision.Deny(
                PolicyId,
                $"verb '{meta.Name}' declared BaaScope=None but blast radius exposes {meta.BlastRadius.PhiRecordsExposed} PHI records — BAA scope required");
        }

        if (meta.BlastRadius.DowntimeSeconds > ctx.Charter.Tolerance.MaxDowntimeSecondsPerShift)
        {
            return AuthzDecision.Deny(
                PolicyId,
                $"verb '{meta.Name}' blast radius DowntimeSeconds={meta.BlastRadius.DowntimeSeconds} exceeds charter tolerance {ctx.Charter.Tolerance.MaxDowntimeSecondsPerShift}");
        }

        return AuthzDecision.Allow(
            PolicyId,
            $"verb '{meta.Name}' cleared charter-driven policy (risk={meta.RiskTier}, baa={meta.BaaScope.GetType().Name})");
    }
}
