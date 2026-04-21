namespace SuavoAgent.Contracts.Discovery;

/// <summary>
/// Per-candidate output from the ranker pipeline. Every candidate — best
/// and alternatives — gets one. Carries the raw sample (for operator
/// display), the final confidence + human-readable reason, which tier
/// produced the decision, and the optional per-signal breakdown from the
/// heuristic scorer.
/// </summary>
/// <param name="Candidate">Raw sample; contains operator-visible path + name.</param>
/// <param name="Confidence">[0,1]; scorer or ranker's confidence this candidate is the right one.</param>
/// <param name="Reason">One-line human-readable explanation for the portal/log.</param>
/// <param name="Tier">Which tier produced this ranking.</param>
/// <param name="SignalBreakdown">
/// Per-signal heuristic contributions when <see cref="Tier"/> is
/// <see cref="RankerTier.Heuristic"/> or the heuristic was consulted
/// alongside an LLM. Null when the LLM tier decided without heuristic
/// input surfaced.
/// </param>
public sealed record CandidateRanking(
    FileCandidateSample Candidate,
    double Confidence,
    string Reason,
    RankerTier Tier,
    ScoreDetail? SignalBreakdown = null);
