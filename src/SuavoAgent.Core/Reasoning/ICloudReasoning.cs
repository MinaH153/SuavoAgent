using SuavoAgent.Contracts.Reasoning;

namespace SuavoAgent.Core.Reasoning;

/// <summary>
/// Tier-3 decision service. Called only when Tier-1 (RuleEngine) returns
/// NoMatch AND Tier-2 (ILocalInference) either returned null or produced a
/// proposal with confidence below the cloud-escalation threshold.
///
/// Implementations POST a scrubbed structured context to the cloud reasoning
/// endpoint and receive back an <see cref="InferenceProposal"/> shaped the
/// same way as Tier-2 so TieredBrain doesn't care where the proposal came
/// from.
///
/// HIPAA rules:
///   - Input MUST be pre-scrubbed — no raw PHI crosses the boundary.
///   - Anthropic BAA must be in place for the hosting account.
///   - Every call is audit-logged on the cloud side (<c>agent_reasoning_log</c>).
///
/// Like <see cref="ILocalInference"/>, implementations must not throw on
/// failure — they return null so TieredBrain cleanly escalates to the
/// operator approval queue.
/// </summary>
public interface ICloudReasoning
{
    /// <summary>
    /// True when the agent is configured for cloud reasoning (ApiKey + endpoint
    /// reachable in the most recent heartbeat window). False during offline or
    /// opt-out periods, in which case TieredBrain skips Tier-3.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Proposes a single RuleActionSpec by asking the cloud reasoner. Returns
    /// null on any failure (network, timeout, rate limit, parse error) — NEVER
    /// throws. The caller treats null as "escalate to operator".
    /// </summary>
    Task<InferenceProposal?> ProposeAsync(
        InferenceRequest request,
        string tier2EscalationReason,
        CancellationToken ct);
}
