using SuavoAgent.Contracts.Reasoning;

namespace SuavoAgent.Core.Reasoning;

/// <summary>
/// The "no Tier 2" fallback. Used when ReasoningOptions.Enabled is false or
/// no model file passes IModelManager.Verify. Every ProposeAsync returns null
/// so TieredBrain cleanly escalates to the operator.
///
/// This lets the agent run usefully in rules-only mode while the operator
/// decides whether/when to enable local inference. No degradation of Tier 1.
/// </summary>
public sealed class NullLocalInference : ILocalInference
{
    public string ModelId => "none";
    public bool IsReady => false;

    public Task<InferenceProposal?> ProposeAsync(InferenceRequest request, CancellationToken ct) =>
        Task.FromResult<InferenceProposal?>(null);
}
