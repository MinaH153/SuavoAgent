using SuavoAgent.Contracts.Discovery;

namespace SuavoAgent.Core.Discovery;

/// <summary>
/// No-op inference for pharmacies where Tier-2/3 are disabled. Always
/// reports not-ready so <see cref="LlmFileRanker"/> delegates straight
/// to its heuristic fallback.
/// </summary>
public sealed class NullFileRankingInference : IFileRankingInference
{
    public string ModelId => "none";
    public bool IsReady => false;
    public RankerTier Tier => RankerTier.Heuristic;

    public Task<FileRankingJudgment?> RankAsync(
        FileDiscoverySpec spec,
        IReadOnlyList<FileCandidateForRanker> candidates,
        CancellationToken ct)
        => Task.FromResult<FileRankingJudgment?>(null);
}
