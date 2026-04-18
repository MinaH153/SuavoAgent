using SuavoAgent.Contracts.Reasoning;

namespace SuavoAgent.Core.Reasoning;

/// <summary>
/// Tier 2 decision service. Called only when the RuleEngine returns NoMatch.
/// Implementations must produce output CONSTRAINED to the RuleActionSpec schema
/// via GBNF grammar (or equivalent) — free-text output is never acceptable.
///
/// Every implementation is expected to be async + cancellable because the
/// underlying model inference can take 100–2000 ms on CPU.
/// </summary>
public interface ILocalInference
{
    /// <summary>
    /// Model id for audit trails — e.g. "llama-3.2-1b-q4_k_m".
    /// Mock implementations return "mock".
    /// </summary>
    string ModelId { get; }

    /// <summary>
    /// Returns true if the local model is loaded and ready to serve proposals.
    /// False during startup or model download.
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// Proposes a single RuleActionSpec for the given context. Returns null if
    /// the model is not ready, the request times out, or the grammar-constrained
    /// output could not satisfy the schema. NEVER throws for inference failures —
    /// those are reported as null so the caller can cleanly escalate.
    /// </summary>
    Task<InferenceProposal?> ProposeAsync(InferenceRequest request, CancellationToken ct);
}
