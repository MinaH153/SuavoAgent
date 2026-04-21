using SuavoAgent.Contracts.Discovery;
using SuavoAgent.Core.Discovery;
using SuavoAgent.Core.Verticals.Pharmacy;
using Xunit;

namespace SuavoAgent.Core.Tests.Discovery;

public class FilenameHeuristicScorerTests
{
    private readonly FilenameHeuristicScorer _scorer = new();
    private static readonly DateTimeOffset Now = new(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------------
    // Happy-path ranking — the cases the scorer must get right for Saturday
    // ---------------------------------------------------------------------

    [Fact]
    public void Score_PerfectNadimCase_ScoresAboveNinety()
    {
        var spec = PharmacyPresets.NdcPricingList();
        var candidate = Candidate("generics_top500.xlsx", bucket: FileLocationBucket.Desktop);

        var detail = _scorer.Score(spec, candidate, Now);

        Assert.True(detail.Total >= 0.90, $"expected ≥ 0.90 for the ideal case, got {detail.Total:F3}");
    }

    [Fact]
    public void Score_OldAuditFileDeepDocuments_ScoresLow()
    {
        var spec = PharmacyPresets.NdcPricingList();
        var candidate = Candidate(
            "rx_audit_final.xlsx",
            bucket: FileLocationBucket.Documents,
            ageDays: 400,
            size: 1_200_000);

        var detail = _scorer.Score(spec, candidate, Now);

        Assert.True(detail.Total < 0.5, $"old audit should score below 0.5, got {detail.Total:F3}");
    }

    [Fact]
    public void Score_WrongExtension_ZerosExtensionComponent()
    {
        var spec = PharmacyPresets.NdcPricingList();
        var candidate = Candidate("generics_top500.txt", bucket: FileLocationBucket.Desktop);

        var detail = _scorer.Score(spec, candidate, Now);

        Assert.Equal(0.0, detail.ExtensionScore);
        Assert.True(detail.Total < 0.85, $"wrong extension should cap the total, got {detail.Total:F3}");
    }

    [Fact]
    public void Score_MultipleNameHintsMatch_ScoresHigherThanSingle()
    {
        var spec = PharmacyPresets.NdcPricingList();
        var single = Candidate("generic_list.xlsx");
        var triple = Candidate("top_generic_ndc.xlsx");

        Assert.True(
            _scorer.Score(spec, triple, Now).Total > _scorer.Score(spec, single, Now).Total,
            "three hits must exceed one hit");
    }

    [Fact]
    public void Score_ExcelMruBucket_BeatsOtherBucket_WhenFresh()
    {
        var spec = PharmacyPresets.NdcPricingList();
        var mru = Candidate(
            "generics.xlsx",
            bucket: FileLocationBucket.ExcelMru,
            lastOpenedAgeDays: 2);
        var other = Candidate("generics.xlsx", bucket: FileLocationBucket.Other);

        Assert.True(_scorer.Score(spec, mru, Now).Total > _scorer.Score(spec, other, Now).Total);
    }

    [Fact]
    public void Score_SizeFarOutOfBand_ReducesSizeComponent()
    {
        var spec = PharmacyPresets.NdcPricingList();
        var tiny = Candidate("generics.xlsx", size: 500);
        var normal = Candidate("generics.xlsx", size: 250_000);

        Assert.True(_scorer.Score(spec, normal, Now).SizeScore >= 1.0);
        Assert.True(_scorer.Score(spec, tiny, Now).SizeScore < 0.5);
    }

    [Fact]
    public void Score_NonTabularShape_SizeComponentIsNeutral()
    {
        var spec = new FileDiscoverySpec(
            Description: "compliance manual PDF",
            NameHints: new[] { "manual", "policy" },
            Extensions: new[] { ".pdf" },
            Shape: new DocumentExpectation(MinPages: 5));
        var candidate = Candidate(
            "compliance_manual.pdf",
            bucket: FileLocationBucket.Documents,
            size: 5_000_000);

        var detail = _scorer.Score(spec, candidate, Now);

        Assert.Equal(0.5, detail.SizeScore);
        Assert.True(detail.Total > 0.5, $"doc-shaped spec should score well when name/ext match, got {detail.Total:F3}");
    }

    [Fact]
    public void Score_IsAlwaysInUnitInterval()
    {
        var spec = PharmacyPresets.NdcPricingList();
        var worstCase = new FileCandidate(
            AbsolutePath: @"C:\junk\nothing_relevant.bin",
            FileName: "nothing_relevant.bin",
            SizeBytes: 1,
            LastModifiedUtc: Now.AddYears(-5),
            Bucket: FileLocationBucket.Other,
            HeuristicScore: 0.0);

        var detail = _scorer.Score(spec, worstCase, Now);
        Assert.InRange(detail.Total, 0.0, 1.0);
    }

    // ---------------------------------------------------------------------
    // ScoreDetail auditability — per-signal values must match the total
    // ---------------------------------------------------------------------

    [Fact]
    public void Score_SignalBreakdownMatchesTotal()
    {
        var spec = PharmacyPresets.NdcPricingList();
        var detail = _scorer.Score(spec, Candidate("generics.xlsx"), Now);

        // Weights must match the class constants.
        var recomputed =
            0.45 * detail.NameScore +
            0.20 * detail.RecencyScore +
            0.15 * detail.ExtensionScore +
            0.15 * detail.BucketScore +
            0.05 * detail.SizeScore;

        Assert.Equal(Math.Clamp(recomputed, 0.0, 1.0), detail.Total, precision: 6);
    }

    // ---------------------------------------------------------------------
    // Normalization — Codex fix (b3): whitespace/invalid spec inputs
    // ---------------------------------------------------------------------

    [Fact]
    public void Score_WhitespaceOnlyHints_TreatedAsNoHints()
    {
        var spec = new FileDiscoverySpec(
            Description: "any",
            NameHints: new[] { "", "   ", "\t" },
            Extensions: new[] { ".xlsx" });

        var detail = _scorer.Score(spec, Candidate("random.xlsx"), Now);

        // Whitespace hints must drop out → behaves as "no hints" → neutral 0.5.
        Assert.Equal(0.5, detail.NameScore);
    }

    [Fact]
    public void Score_ShortHintsBelowMinLength_Ignored()
    {
        // "rx" (2 chars) would substring-match "box.xlsx" and
        // "desktop_export.csv" nonsense. Enforcing min length 3 drops it.
        var spec = new FileDiscoverySpec(
            Description: "any",
            NameHints: new[] { "rx", "nd", "xl" });

        var shouldNotMatch = Candidate("boxrx_exportnd.xlsx");
        var detail = _scorer.Score(spec, shouldNotMatch, Now);

        // All three hints dropped → treated as no hints → neutral 0.5.
        Assert.Equal(0.5, detail.NameScore);
    }

    [Fact]
    public void Score_HintsGivenButNoneMatch_ScoresLow()
    {
        // Distinct from "no hints given" (0.5). Hints were provided, none
        // matched → strong negative signal 0.1.
        var spec = new FileDiscoverySpec(
            Description: "any",
            NameHints: new[] { "invoice", "receipt", "vendor" });

        var detail = _scorer.Score(spec, Candidate("generics.xlsx"), Now);
        Assert.Equal(0.1, detail.NameScore);
    }

    [Fact]
    public void Score_BlankExtensionsEntries_TreatedAsNoPreference()
    {
        var spec = new FileDiscoverySpec(
            Description: "any",
            NameHints: new[] { "generic" },
            Extensions: new[] { "", "   " });

        var detail = _scorer.Score(spec, Candidate("generics.xlsx"), Now);
        Assert.Equal(0.5, detail.ExtensionScore);
    }

    [Fact]
    public void Score_NegativeRowBounds_ClampedNotOverflow()
    {
        var spec = new FileDiscoverySpec(
            Description: "any",
            NameHints: new[] { "generic" },
            Extensions: new[] { ".xlsx" },
            Shape: new TabularExpectation(MinRows: -100, MaxRows: -50));

        var detail = _scorer.Score(spec, Candidate("generics.xlsx"), Now);

        // Bounds clamp to [0, 0]; 250 KB is "above" the band, so 0.3 expected.
        // Must not throw OverflowException or produce NaN.
        Assert.False(double.IsNaN(detail.Total));
        Assert.False(double.IsInfinity(detail.Total));
    }

    [Fact]
    public void Score_MaxValueRowBounds_NoOverflow()
    {
        var spec = new FileDiscoverySpec(
            Description: "any",
            NameHints: new[] { "generic" },
            Extensions: new[] { ".xlsx" },
            Shape: new TabularExpectation(MinRows: 0, MaxRows: int.MaxValue));

        var detail = _scorer.Score(spec, Candidate("generics.xlsx"), Now);

        Assert.False(double.IsNaN(detail.Total));
        Assert.InRange(detail.Total, 0.0, 1.0);
    }

    [Fact]
    public void Score_ZeroRecentDaysBoost_DoesNotDivideByZero()
    {
        var spec = new FileDiscoverySpec(
            Description: "any",
            NameHints: new[] { "generic" },
            RecentDaysBoost: 0);

        var detail = _scorer.Score(spec, Candidate("generics.xlsx"), Now);

        Assert.False(double.IsNaN(detail.RecencyScore));
        Assert.False(double.IsInfinity(detail.RecencyScore));
    }

    // ---------------------------------------------------------------------
    // Tokenization — Codex fix (b4): substring-in-token, not substring-in-string
    // ---------------------------------------------------------------------

    [Fact]
    public void Score_TokenizesOnHyphensAndUnderscores()
    {
        var spec = new FileDiscoverySpec(
            Description: "any",
            NameHints: new[] { "generic", "top" });
        var hyphenated = Candidate("top-generic-ndc-april.xlsx");
        var underscored = Candidate("top_generic_ndc_april.xlsx");

        Assert.Equal(
            _scorer.Score(spec, hyphenated, Now).NameScore,
            _scorer.Score(spec, underscored, Now).NameScore);
    }

    // ---------------------------------------------------------------------
    // MRU freshness — Codex fix (b5): stale MRU should decay
    // ---------------------------------------------------------------------

    [Fact]
    public void Score_StaleMruEntry_ScoresLowerThanFreshMru()
    {
        var spec = PharmacyPresets.NdcPricingList();
        var fresh = Candidate("generics.xlsx", bucket: FileLocationBucket.ExcelMru, lastOpenedAgeDays: 2);
        var stale = Candidate("generics.xlsx", bucket: FileLocationBucket.ExcelMru, lastOpenedAgeDays: 180);

        Assert.True(
            _scorer.Score(spec, fresh, Now).BucketScore > _scorer.Score(spec, stale, Now).BucketScore,
            "stale MRU entries must decay");
    }

    [Fact]
    public void Score_MruWithoutLastOpenedSignal_StillDecays()
    {
        var spec = PharmacyPresets.NdcPricingList();
        var withSignal = Candidate("generics.xlsx", bucket: FileLocationBucket.ExcelMru, lastOpenedAgeDays: 1);
        var noSignal = Candidate("generics.xlsx", bucket: FileLocationBucket.ExcelMru); // lastOpenedAgeDays null

        Assert.True(
            _scorer.Score(spec, withSignal, Now).BucketScore > _scorer.Score(spec, noSignal, Now).BucketScore);
    }

    // ---------------------------------------------------------------------
    // Adversarial — Codex fix (b6)
    // ---------------------------------------------------------------------

    [Fact]
    public void Score_BackupSuffix_MisleadsNoneOfTheSignals()
    {
        // generics.xlsx.bak — extension is ".bak", not ".xlsx". Shouldn't
        // ride on a good-looking stem to auto-use.
        var spec = PharmacyPresets.NdcPricingList();
        var backup = Candidate("generics_top500.xlsx.bak");

        var detail = _scorer.Score(spec, backup, Now);
        Assert.Equal(0.0, detail.ExtensionScore);
    }

    [Fact]
    public void Score_UnicodeFilename_DoesNotThrow()
    {
        var spec = PharmacyPresets.NdcPricingList();
        var arabic = Candidate("الأدوية_generic.xlsx");

        var detail = _scorer.Score(spec, arabic, Now);
        Assert.InRange(detail.Total, 0.0, 1.0);
    }

    [Fact]
    public void Score_EmptyStem_ReturnsZeroNameNotNaN()
    {
        var spec = PharmacyPresets.NdcPricingList();
        var weird = Candidate(".xlsx", size: 250_000); // just an extension

        var detail = _scorer.Score(spec, weird, Now);
        Assert.False(double.IsNaN(detail.NameScore));
        Assert.InRange(detail.Total, 0.0, 1.0);
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static FileCandidate Candidate(
        string fileName,
        FileLocationBucket bucket = FileLocationBucket.Desktop,
        long size = 250_000,
        double ageDays = 4,
        double? lastOpenedAgeDays = null)
    {
        return new FileCandidate(
            AbsolutePath: Path.Combine(@"C:\Users\Nadim\Desktop", fileName),
            FileName: fileName,
            SizeBytes: size,
            LastModifiedUtc: Now.AddDays(-ageDays),
            Bucket: bucket,
            HeuristicScore: 0.0,
            LastOpenedUtc: lastOpenedAgeDays is { } d ? Now.AddDays(-d) : null);
    }
}
