using SuavoAgent.Contracts.Discovery;

namespace SuavoAgent.Core.Discovery;

/// <summary>
/// Default ranker — derives confidence from the deterministic heuristic
/// score plus small structural boosts when the sampler confirmed the
/// spec's primary-key shape and header hints. No LLM involvement.
///
/// <para><b>Sampler-error safety:</b> when
/// <see cref="FileCandidateForRanker.SampleOutcome"/> is
/// <see cref="SampleOutcome.SampleFailed"/>, confidence is capped below
/// the <c>AutoUseConfidence</c> band so the locator forces operator
/// confirmation instead of silently auto-using an unreadable file. This
/// closes the wrong-file selection path Codex flagged in the session 2
/// review.</para>
/// </summary>
public sealed class HeuristicOnlyRanker : IFileRanker
{
    private const double PrimaryKeyBoost = 0.08;
    private const double HintsBoost = 0.04;

    /// <summary>
    /// Maximum confidence for a candidate whose sampler failed. Chosen so
    /// even a heuristic-perfect (1.0) file lands strictly below the
    /// default <c>AutoUseConfidence = 0.92</c> → always lands in
    /// <c>RequireConfirm</c> or <c>Inconclusive</c>, never <c>AutoUse</c>.
    /// </summary>
    internal const double SampleFailedConfidenceCap = 0.85;

    public Task<IReadOnlyList<RankerVerdict>> RankAsync(
        FileDiscoverySpec spec,
        IReadOnlyList<FileCandidateForRanker> candidates,
        CancellationToken ct)
    {
        var verdicts = new List<RankerVerdict>(candidates.Count);
        foreach (var c in candidates)
        {
            ct.ThrowIfCancellationRequested();

            double pkBoost = c.HasPrimaryKeyShape ? PrimaryKeyBoost : 0.0;
            double hintsBoost = c.StructureMatchesHints ? HintsBoost : 0.0;

            double baseConfidence = Math.Clamp(
                c.HeuristicScore + pkBoost + hintsBoost, 0.0, 1.0);

            // Sampler-failure cap: never auto-use a file we couldn't read.
            double finalConfidence = c.SampleOutcome == SampleOutcome.SampleFailed
                ? Math.Min(baseConfidence, SampleFailedConfidenceCap)
                : baseConfidence;

            verdicts.Add(new RankerVerdict(
                CandidateId: c.CandidateId,
                Confidence: finalConfidence,
                Reason: BuildReason(c, pkBoost, hintsBoost, baseConfidence, finalConfidence),
                Tier: RankerTier.Heuristic,
                SignalBreakdown: null));
        }

        // Sort by confidence desc, stable on CandidateId for deterministic
        // ordering on ties (the locator maps IDs back to raw samples).
        verdicts.Sort((a, b) =>
        {
            var cmp = b.Confidence.CompareTo(a.Confidence);
            if (cmp != 0) return cmp;
            return string.CompareOrdinal(a.CandidateId, b.CandidateId);
        });

        return Task.FromResult<IReadOnlyList<RankerVerdict>>(verdicts);
    }

    private static string BuildReason(
        FileCandidateForRanker c,
        double pkBoost,
        double hintsBoost,
        double baseConfidence,
        double finalConfidence)
    {
        var parts = new List<string>
        {
            $"heuristic {c.HeuristicScore:F2}",
            $"bucket {c.Bucket}",
            $"recency {c.Recency}",
        };
        if (pkBoost > 0) parts.Add("primary-key shape matched");
        if (hintsBoost > 0) parts.Add("headers match hints");
        if (c.SampleOutcome == SampleOutcome.SampleFailed)
            parts.Add($"sampler failed — confidence capped at {SampleFailedConfidenceCap:F2}");
        else if (c.SampleOutcome == SampleOutcome.NotSampled)
            parts.Add("content not sampled (below top-K threshold)");
        return string.Join("; ", parts);
    }
}
