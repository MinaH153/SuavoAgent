using SuavoAgent.Contracts.Discovery;
using SuavoAgent.Core.Discovery;
using SuavoAgent.Core.Verticals.Pharmacy;
using Xunit;

namespace SuavoAgent.Core.Tests.Discovery;

public class HeuristicOnlyRankerTests
{
    private readonly HeuristicOnlyRanker _ranker = new();

    [Fact]
    public async Task Rank_SortsByConfidenceDescending()
    {
        var inputs = new[]
        {
            Input("low", heuristicScore: 0.2, hasPk: false),
            Input("high", heuristicScore: 0.95, hasPk: true),
            Input("mid", heuristicScore: 0.7, hasPk: false),
        };

        var verdicts = await _ranker.RankAsync(
            PharmacyPresets.NdcPricingList(), inputs, CancellationToken.None);

        Assert.Equal("high", verdicts[0].CandidateId);
        Assert.Equal("mid", verdicts[1].CandidateId);
        Assert.Equal("low", verdicts[2].CandidateId);
    }

    [Fact]
    public async Task Rank_PrimaryKeyMatch_BoostsConfidenceAboveHeuristic()
    {
        var withPk = Input("with_pk", heuristicScore: 0.85, hasPk: true);
        var withoutPk = Input("no_pk", heuristicScore: 0.85, hasPk: false);

        var verdicts = await _ranker.RankAsync(
            PharmacyPresets.NdcPricingList(),
            new[] { withPk, withoutPk },
            CancellationToken.None);

        var byPk = verdicts.First(v => v.CandidateId == "with_pk");
        var byNoPk = verdicts.First(v => v.CandidateId == "no_pk");
        Assert.True(byPk.Confidence > byNoPk.Confidence);
    }

    [Fact]
    public async Task Rank_CarriesTierAttribution()
    {
        var input = Input("x", heuristicScore: 0.9, hasPk: true);

        var verdicts = await _ranker.RankAsync(
            PharmacyPresets.NdcPricingList(),
            new[] { input },
            CancellationToken.None);

        Assert.Equal(RankerTier.Heuristic, verdicts[0].Tier);
        Assert.Contains("heuristic", verdicts[0].Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rank_ClampsConfidenceAtOne()
    {
        var input = Input("perfect", heuristicScore: 0.99, hasPk: true, hintsMatch: true);

        var verdicts = await _ranker.RankAsync(
            PharmacyPresets.NdcPricingList(),
            new[] { input },
            CancellationToken.None);

        Assert.InRange(verdicts[0].Confidence, 0.99, 1.0);
    }

    // ---------------------------------------------------------------------
    // Sampler-error safety — the ship-blocker Codex identified.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Rank_SampleFailed_CapsConfidenceBelowAutoUse()
    {
        // A file with a perfect heuristic score (filename matches, Excel-MRU,
        // recent, correct extension) but whose sampler failed MUST land below
        // the default AutoUse threshold (0.92). Otherwise a corrupted xlsx
        // with a good filename gets silently selected without operator review.
        var failed = Input(
            "failed",
            heuristicScore: 1.0,
            hasPk: false,
            hintsMatch: false,
            outcome: SampleOutcome.SampleFailed);

        var verdicts = await _ranker.RankAsync(
            PharmacyPresets.NdcPricingList(),
            new[] { failed },
            CancellationToken.None);

        Assert.True(verdicts[0].Confidence <= HeuristicOnlyRanker.SampleFailedConfidenceCap,
            $"expected ≤ {HeuristicOnlyRanker.SampleFailedConfidenceCap}, got {verdicts[0].Confidence:F3}");
        Assert.True(verdicts[0].Confidence < 0.92,
            "must land below AutoUseConfidence to force operator confirmation");
        Assert.Contains("sampler failed", verdicts[0].Reason);
    }

    [Fact]
    public async Task Rank_NotSampled_KeepsHeuristicBaseline()
    {
        // NotSampled is NOT the same as SampleFailed — these are candidates
        // below the sampling top-K, not broken files. They should keep their
        // heuristic score without the failure cap.
        var notSampled = Input(
            "ns",
            heuristicScore: 0.88,
            hasPk: false,
            hintsMatch: false,
            outcome: SampleOutcome.NotSampled);

        var verdicts = await _ranker.RankAsync(
            PharmacyPresets.NdcPricingList(),
            new[] { notSampled },
            CancellationToken.None);

        // No boosts (we didn't sample, we don't know), but no penalty either.
        Assert.InRange(verdicts[0].Confidence, 0.88, 0.88 + 1e-6);
        Assert.Contains("not sampled", verdicts[0].Reason);
    }

    // ---------------------------------------------------------------------

    private static FileCandidateForRanker Input(
        string candidateId,
        double heuristicScore,
        bool hasPk,
        bool hintsMatch = false,
        SampleOutcome outcome = SampleOutcome.Sampled)
    {
        return new FileCandidateForRanker(
            CandidateId: candidateId,
            DirectoryDepth: 1,
            Size: SizeBand.Small,
            Recency: RecencyBand.ThisWeek,
            Extension: ".xlsx",
            Bucket: FileLocationBucket.Desktop,
            ColumnHeaderCount: 3,
            ScrubbedColumnHeaders: new[] { "Generic", "NDC", "Qty" },
            RowCount: 500,
            HasPrimaryKeyShape: hasPk,
            StructureMatchesHints: hintsMatch,
            HeuristicScore: heuristicScore,
            SampleOutcome: outcome);
    }
}
