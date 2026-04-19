using SuavoAgent.Contracts.Reasoning;

namespace SuavoAgent.Core.Reasoning;

/// <summary>
/// The "no Tier 3" fallback. Used when cloud reasoning is disabled, the agent
/// has no ApiKey, or the operator has explicitly opted out. TieredBrain treats
/// a null result as "escalate to operator" so this cleanly disables Tier 3
/// without breaking the tier chain.
/// </summary>
public sealed class NullCloudReasoning : ICloudReasoning
{
    public bool IsEnabled => false;

    public Task<InferenceProposal?> ProposeAsync(
        InferenceRequest request,
        string tier2EscalationReason,
        CancellationToken ct) =>
        Task.FromResult<InferenceProposal?>(null);
}
