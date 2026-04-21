namespace SuavoAgent.Contracts.Discovery;

/// <summary>
/// Which tier produced a candidate's final ranking. Carried in
/// <see cref="CandidateRanking.Tier"/> so the portal can attribute the
/// decision (e.g., "ranked by local SLM in 240ms") and compliance audits
/// can trace every decision back to its source.
/// </summary>
public enum RankerTier
{
    /// <summary>Deterministic filename heuristic only — no LLM consulted.</summary>
    Heuristic,

    /// <summary>On-device small language model (LLama/Phi/Qwen via GGUF).</summary>
    LocalInference,

    /// <summary>Cloud Claude via signed <c>/api/agent/reason</c>.</summary>
    CloudInference,

    /// <summary>No tier was confident enough — operator pick required.</summary>
    OperatorFallback,
}
