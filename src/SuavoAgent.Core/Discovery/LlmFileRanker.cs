using SuavoAgent.Contracts.Discovery;

namespace SuavoAgent.Core.Discovery;

/// <summary>
/// Composite ranker that tries an ordered chain of
/// <see cref="IFileRankingInference"/> tiers (local SLM first, cloud
/// Claude second) and falls back to the deterministic
/// <see cref="HeuristicOnlyRanker"/> when every tier is unavailable or
/// declines. This is the "tiered brain" shape from v3.11 specialized for
/// ranking output.
///
/// <para>
/// Flow for each discovery:
/// <list type="number">
///   <item>Run <c>HeuristicOnlyRanker</c> to get a deterministic baseline
///     for every candidate (so even when the LLM picks one, every other
///     candidate still has a reason + confidence for the portal).</item>
///   <item>If the heuristic top candidate already clears the auto-use
///     threshold, short-circuit — no LLM cost for easy calls.</item>
///   <item>Otherwise consult each inference tier in order. First tier to
///     return a <see cref="FileRankingJudgment"/> wins; its pick becomes
///     the top verdict, with the heuristic ranker still feeding alternatives.</item>
///   <item>If no tier produces a judgment, return the heuristic-only result
///     verbatim.</item>
/// </list>
/// </para>
///
/// <para><b>Privacy:</b> the inference tiers receive only
/// <see cref="FileCandidateForRanker"/>, never raw filenames or paths.
/// Header scrubbing is the caller's responsibility (the
/// <see cref="FileCandidateProjection"/> factory handles it when a
/// <c>PhiScrubber</c> is injected).</para>
/// </summary>
public sealed class LlmFileRanker : IFileRanker
{
    private readonly HeuristicOnlyRanker _heuristic;
    private readonly IReadOnlyList<IFileRankingInference> _tiers;
    private readonly double _heuristicShortCircuitThreshold;
    private readonly ILogger<LlmFileRanker>? _logger;

    public LlmFileRanker(
        HeuristicOnlyRanker heuristic,
        IReadOnlyList<IFileRankingInference> tiers,
        double heuristicShortCircuitThreshold = 0.92,
        ILogger<LlmFileRanker>? logger = null)
    {
        _heuristic = heuristic;
        _tiers = tiers;
        _heuristicShortCircuitThreshold = heuristicShortCircuitThreshold;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RankerVerdict>> RankAsync(
        FileDiscoverySpec spec,
        IReadOnlyList<FileCandidateForRanker> candidates,
        CancellationToken ct)
    {
        // Baseline: always compute the deterministic heuristic ranking.
        // Every non-winning candidate in the final result keeps its
        // heuristic-tier verdict so the portal can show a full audit.
        var heuristicVerdicts = await _heuristic.RankAsync(spec, candidates, ct);
        if (heuristicVerdicts.Count == 0) return heuristicVerdicts;

        // Short-circuit: the heuristic already has a confident winner.
        if (heuristicVerdicts[0].Confidence >= _heuristicShortCircuitThreshold)
        {
            _logger?.LogDebug(
                "LlmFileRanker: heuristic short-circuit at {Conf:F2} — skipping LLM tiers",
                heuristicVerdicts[0].Confidence);
            return heuristicVerdicts;
        }

        // Ambiguous: try each inference tier in order.
        foreach (var tier in _tiers)
        {
            if (!tier.IsReady) continue;
            ct.ThrowIfCancellationRequested();

            FileRankingJudgment? judgment = null;
            try
            {
                judgment = await tier.RankAsync(spec, candidates, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "LlmFileRanker: tier {Tier}/{Model} threw — falling through",
                    tier.Tier, tier.ModelId);
                continue;
            }

            if (judgment is null)
            {
                _logger?.LogDebug(
                    "LlmFileRanker: tier {Tier}/{Model} declined — falling through",
                    tier.Tier, tier.ModelId);
                continue;
            }

            // Validate the judgment references a real candidate. A hallucinated
            // CandidateId means the LLM didn't follow the grammar — discard
            // and fall through.
            if (!candidates.Any(c => c.CandidateId == judgment.PickedCandidateId))
            {
                _logger?.LogWarning(
                    "LlmFileRanker: tier {Tier}/{Model} returned unknown CandidateId '{Id}' — discarding",
                    tier.Tier, tier.ModelId, judgment.PickedCandidateId);
                continue;
            }

            return BuildFinal(candidates, heuristicVerdicts, tier, judgment);
        }

        // No tier produced a judgment — fall through to heuristic-only.
        return heuristicVerdicts;
    }

    private static IReadOnlyList<RankerVerdict> BuildFinal(
        IReadOnlyList<FileCandidateForRanker> candidates,
        IReadOnlyList<RankerVerdict> heuristicVerdicts,
        IFileRankingInference pickerTier,
        FileRankingJudgment judgment)
    {
        // LLM pick becomes #1 with its own confidence/reason/tier.
        // Heuristic verdicts for every other candidate keep their original
        // reason + attribution so the portal shows "why the other files
        // didn't win" via the heuristic breakdown.
        var pickerHeuristic = heuristicVerdicts.FirstOrDefault(
            v => v.CandidateId == judgment.PickedCandidateId);
        var pickerVerdict = new RankerVerdict(
            CandidateId: judgment.PickedCandidateId,
            Confidence: Math.Clamp(judgment.Confidence, 0.0, 1.0),
            Reason: $"[{pickerTier.Tier}/{pickerTier.ModelId}] {judgment.Reason}",
            Tier: pickerTier.Tier,
            SignalBreakdown: pickerHeuristic?.SignalBreakdown);

        var alternatives = heuristicVerdicts
            .Where(v => v.CandidateId != judgment.PickedCandidateId)
            .OrderByDescending(v => v.Confidence);

        return new[] { pickerVerdict }.Concat(alternatives).ToList();
    }
}
