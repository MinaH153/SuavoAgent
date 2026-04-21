using SuavoAgent.Contracts.Discovery;
using SuavoAgent.Core.Discovery;
using SuavoAgent.Core.Verticals.Pharmacy;
using Xunit;

namespace SuavoAgent.Core.Tests.Discovery;

public class LlmFileRankerTests
{
    private readonly HeuristicOnlyRanker _heuristic = new();

    [Fact]
    public async Task Rank_HeuristicShortCircuit_SkipsLlmTiers()
    {
        var inputs = new[]
        {
            Input("clear_winner", heuristicScore: 0.95, hasPk: true, hintsMatch: true), // → 0.95 + 0.08 + 0.04 = 1.0 clamped, >0.92 threshold
            Input("also_ran", heuristicScore: 0.3, hasPk: false),
        };
        var mockTier = new MockRankingTier(isReady: true, picks: "also_ran", confidence: 0.99);
        var ranker = new LlmFileRanker(_heuristic, new[] { (IFileRankingInference)mockTier });

        var verdicts = await ranker.RankAsync(PharmacyPresets.NdcPricingList(), inputs, CancellationToken.None);

        // Heuristic winner stays on top — LLM tier was never consulted.
        Assert.Equal("clear_winner", verdicts[0].CandidateId);
        Assert.Equal(RankerTier.Heuristic, verdicts[0].Tier);
        Assert.False(mockTier.WasCalled);
    }

    [Fact]
    public async Task Rank_AmbiguousCase_ConsultsLlmTier()
    {
        var inputs = new[]
        {
            Input("a", heuristicScore: 0.75, hasPk: false),
            Input("b", heuristicScore: 0.74, hasPk: false),
        };
        var mockTier = new MockRankingTier(isReady: true, picks: "b", confidence: 0.88, tier: RankerTier.LocalInference);
        var ranker = new LlmFileRanker(_heuristic, new[] { (IFileRankingInference)mockTier });

        var verdicts = await ranker.RankAsync(PharmacyPresets.NdcPricingList(), inputs, CancellationToken.None);

        Assert.True(mockTier.WasCalled);
        Assert.Equal("b", verdicts[0].CandidateId);
        Assert.Equal(RankerTier.LocalInference, verdicts[0].Tier);
        Assert.Equal(0.88, verdicts[0].Confidence, precision: 4);
        Assert.Contains("LocalInference", verdicts[0].Reason);
    }

    [Fact]
    public async Task Rank_LlmReturnsUnknownId_FallsThrough()
    {
        var inputs = new[]
        {
            Input("real1", heuristicScore: 0.6, hasPk: false),
            Input("real2", heuristicScore: 0.5, hasPk: false),
        };
        var badTier = new MockRankingTier(isReady: true, picks: "HALLUCINATED", confidence: 0.99);
        var goodTier = new MockRankingTier(isReady: true, picks: "real2", confidence: 0.8, tier: RankerTier.CloudInference);
        var ranker = new LlmFileRanker(_heuristic, new IFileRankingInference[] { badTier, goodTier });

        var verdicts = await ranker.RankAsync(PharmacyPresets.NdcPricingList(), inputs, CancellationToken.None);

        // Bad tier's hallucinated pick is discarded; cloud tier provides the answer.
        Assert.Equal("real2", verdicts[0].CandidateId);
        Assert.Equal(RankerTier.CloudInference, verdicts[0].Tier);
    }

    [Fact]
    public async Task Rank_NoTierReady_UsesHeuristicVerdicts()
    {
        var inputs = new[]
        {
            Input("a", heuristicScore: 0.6, hasPk: false),
            Input("b", heuristicScore: 0.55, hasPk: false),
        };
        var off = new NullFileRankingInference();
        var ranker = new LlmFileRanker(_heuristic, new IFileRankingInference[] { off });

        var verdicts = await ranker.RankAsync(PharmacyPresets.NdcPricingList(), inputs, CancellationToken.None);

        Assert.Equal("a", verdicts[0].CandidateId);
        Assert.Equal(RankerTier.Heuristic, verdicts[0].Tier);
    }

    [Fact]
    public async Task Rank_TierThrows_FallsThroughToNext()
    {
        var inputs = new[]
        {
            Input("a", heuristicScore: 0.6, hasPk: false),
            Input("b", heuristicScore: 0.55, hasPk: false),
        };
        var throwing = new MockRankingTier(isReady: true, picks: "b", confidence: 0.9, throws: true);
        var working = new MockRankingTier(isReady: true, picks: "a", confidence: 0.85, tier: RankerTier.CloudInference);
        var ranker = new LlmFileRanker(_heuristic, new IFileRankingInference[] { throwing, working });

        var verdicts = await ranker.RankAsync(PharmacyPresets.NdcPricingList(), inputs, CancellationToken.None);

        Assert.Equal("a", verdicts[0].CandidateId);
        Assert.Equal(RankerTier.CloudInference, verdicts[0].Tier);
    }

    [Fact]
    public async Task Rank_Cancellation_Propagates()
    {
        var inputs = new[] { Input("a", heuristicScore: 0.5, hasPk: false) };
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancellingTier = new MockRankingTier(isReady: true, picks: "a", confidence: 0.9, honorCancellation: true);
        var ranker = new LlmFileRanker(_heuristic, new IFileRankingInference[] { cancellingTier });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ranker.RankAsync(PharmacyPresets.NdcPricingList(), inputs, cts.Token));
    }

    // ---------------------------------------------------------------------

    private static FileCandidateForRanker Input(string id, double heuristicScore, bool hasPk, bool hintsMatch = false)
    {
        return new FileCandidateForRanker(
            CandidateId: id,
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
            SampleOutcome: SampleOutcome.Sampled);
    }

    private sealed class MockRankingTier : IFileRankingInference
    {
        private readonly string _picks;
        private readonly double _confidence;
        private readonly bool _throws;
        private readonly bool _honorCancellation;

        public MockRankingTier(
            bool isReady,
            string picks,
            double confidence,
            RankerTier tier = RankerTier.LocalInference,
            bool throws = false,
            bool honorCancellation = false)
        {
            IsReady = isReady;
            Tier = tier;
            _picks = picks;
            _confidence = confidence;
            _throws = throws;
            _honorCancellation = honorCancellation;
        }

        public string ModelId => "mock";
        public bool IsReady { get; }
        public RankerTier Tier { get; }
        public bool WasCalled { get; private set; }

        public Task<FileRankingJudgment?> RankAsync(
            FileDiscoverySpec spec,
            IReadOnlyList<FileCandidateForRanker> candidates,
            CancellationToken ct)
        {
            WasCalled = true;
            if (_honorCancellation) ct.ThrowIfCancellationRequested();
            if (_throws) throw new InvalidOperationException("mock throw");
            return Task.FromResult<FileRankingJudgment?>(
                new FileRankingJudgment(_picks, _confidence, "mock picked " + _picks));
        }
    }
}
