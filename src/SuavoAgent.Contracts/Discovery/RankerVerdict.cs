namespace SuavoAgent.Contracts.Discovery;

/// <summary>
/// Output of an <c>IFileRanker</c>. Carries only ranking metadata —
/// confidence, reason, tier attribution, optional signal breakdown — plus
/// the opaque <see cref="CandidateId"/> that lets the locator translate
/// a verdict back to its full raw <c>FileCandidateSample</c> for the
/// operator-facing result.
///
/// <para>
/// By keeping the ranker's output separate from <c>CandidateRanking</c>
/// (which carries the raw sample), the type system enforces the privacy
/// boundary: no ranker can leak raw filenames or paths back into the
/// audit log or the LLM prompt, because it never saw them in the first
/// place.
/// </para>
/// </summary>
public sealed record RankerVerdict(
    string CandidateId,
    double Confidence,
    string Reason,
    RankerTier Tier,
    ScoreDetail? SignalBreakdown = null);
