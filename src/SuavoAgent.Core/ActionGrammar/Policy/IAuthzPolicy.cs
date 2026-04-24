namespace SuavoAgent.Core.ActionGrammarV1.Policy;

/// <summary>
/// Policy-engine contract. Cedar-backed implementation lands in Phase A item
/// A1; Phase 1 ships with a conservative charter-driven policy that enforces
/// only what the loaded MissionCharter expresses.
///
/// The contract is intentionally minimal so Cedar can slot in without the
/// caller code changing.
/// </summary>
public interface IAuthzPolicy
{
    AuthzDecision Evaluate(VerbContext ctx, IVerb verb);
}

public sealed record AuthzDecision(
    bool Allowed,
    string PolicyId,
    string Reason
)
{
    public static AuthzDecision Allow(string policyId, string reason) => new(true, policyId, reason);
    public static AuthzDecision Deny(string policyId, string reason) => new(false, policyId, reason);
}
