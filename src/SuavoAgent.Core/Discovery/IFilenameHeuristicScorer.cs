using SuavoAgent.Contracts.Discovery;

namespace SuavoAgent.Core.Discovery;

/// <summary>
/// Deterministic cheap scoring of a candidate on {filename, recency, size,
/// extension, path bucket, MRU freshness}. Runs before the content sampler
/// touches files — lets the locator prioritize which candidates are worth
/// opening. Output is a <see cref="ScoreDetail"/> carrying per-signal
/// values plus the weighted total, so operators/audit logs can see which
/// signal drove a decision.
/// </summary>
public interface IFilenameHeuristicScorer
{
    /// <summary>
    /// Returns the per-signal breakdown + total score in [0,1]. Pure function
    /// over (spec, candidate, nowUtc) — identical inputs always yield
    /// identical output, which is why this tier is separate from the
    /// LLM ranker (non-determinism lives there).
    /// </summary>
    ScoreDetail Score(FileDiscoverySpec spec, FileCandidate candidate, DateTimeOffset nowUtc);
}
