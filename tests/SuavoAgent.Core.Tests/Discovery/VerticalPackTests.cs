using SuavoAgent.Contracts.Discovery;
using SuavoAgent.Core.Verticals.Pharmacy;
using Xunit;

namespace SuavoAgent.Core.Tests.Discovery;

/// <summary>
/// VerticalPack is the abstraction that keeps the universal file-discovery
/// foundation vertical-neutral while letting narrower "packs" specialize
/// it. These tests prove the pharmacy pack materializes correctly AND
/// that the mechanism generalizes to other verticals — because the whole
/// point is that adding a new sector (accounting, legal, laundromat-group)
/// is data, not code.
/// </summary>
public class VerticalPackTests
{
    [Fact]
    public void NdcPricingList_SpecCarriesPharmacyPackAttribution()
    {
        var spec = PharmacyPresets.NdcPricingList();

        Assert.NotNull(spec.SourcePack);
        Assert.Equal("pharmacy_rx", spec.SourcePack!.Id);
        Assert.Equal("1.0.0", spec.SourcePack.Version);
    }

    [Fact]
    public void NdcPricingList_MaterializesPackPriorsIntoSpecFields()
    {
        var spec = PharmacyPresets.NdcPricingList();

        Assert.NotNull(spec.NameHints);
        Assert.Contains("generic", spec.NameHints!);
        Assert.Contains("ndc", spec.NameHints);

        Assert.NotNull(spec.Extensions);
        Assert.Contains(".xlsx", spec.Extensions!);

        var tab = Assert.IsType<TabularExpectation>(spec.Shape);
        Assert.NotNull(tab.PrimaryKeyPattern);
        Assert.Equal("NDC", tab.PrimaryKeyPattern!.Name);
    }

    [Fact]
    public void FromPack_AdditionalHintsMergeWithoutDuplication()
    {
        var extraHints = new[] { "ndc", "rxlist", "custom-keyword" };
        var spec = PharmacyPresets.FromPack(
            PharmacyRxPack.Instance,
            description: "test spec",
            additionalNameHints: extraHints);

        // Pack hints + operator additions, each appearing once.
        Assert.Contains("generic", spec.NameHints!);      // from pack
        Assert.Contains("custom-keyword", spec.NameHints); // operator addition
        Assert.Equal(
            spec.NameHints!.Count,
            spec.NameHints.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void FromPack_GeneralizesToNewVerticals_WithoutCodeChanges()
    {
        // Proof-of-concept: a brand-new vertical (construction load ticket
        // lookup) ships entirely as a pack. If this test goes green, the
        // mechanism works for laundromat, auto-shop, field-service,
        // accounting, legal — anything — with zero core changes.
        var constructionPack = new VerticalPack(
            Id: "construction_load_tickets",
            Version: "0.1.0-draft",
            DisplayName: "Construction (Load Tickets)",
            NameHintPriors: new[] { "load", "ticket", "dispatch", "job", "haul" },
            CommonPrimaryKeyPatterns: new[]
            {
                new ExpectedColumnPattern(
                    Name: "LoadNumber",
                    Regex: @"^[A-Z]{2,3}-\d{5,7}$",
                    MinSampleMatches: 3),
            },
            CommonExtensions: new[] { ".xlsx", ".csv", ".pdf" });

        var spec = PharmacyPresets.FromPack(
            constructionPack,
            description: "Today's load tickets for dispatch reconciliation.",
            minRows: 10,
            maxRows: 500);

        Assert.Equal("construction_load_tickets", spec.SourcePack!.Id);
        Assert.Contains("haul", spec.NameHints!);
        Assert.Contains(".pdf", spec.Extensions!);
        var tab = Assert.IsType<TabularExpectation>(spec.Shape);
        Assert.Equal("LoadNumber", tab.PrimaryKeyPattern!.Name);
    }

    [Fact]
    public void PharmacyRxPack_NdcRegex_AcceptsRealWorldFormats()
    {
        var regex = new System.Text.RegularExpressions.Regex(PharmacyRxPack.NdcRegex);

        // PioneerRx display format (5-4-2 hyphenated) — image(31) showed this.
        Assert.Matches(regex, "55111-0645-01");
        // 4-4-2 hyphenated — many legacy labelers use this.
        Assert.Matches(regex, "0781-5180-01");
        // 5-3-2 hyphenated — some supplier catalogs use this.
        Assert.Matches(regex, "59762-323-01");
        // Unhyphenated 11-digit (billing format).
        Assert.Matches(regex, "55111064501");
        // Unhyphenated 10-digit (legacy).
        Assert.Matches(regex, "5511106450");
    }

    [Fact]
    public void PharmacyRxPack_NdcRegex_RejectsInvalidFormats()
    {
        var regex = new System.Text.RegularExpressions.Regex(PharmacyRxPack.NdcRegex);

        // 9 digits — rejected (was a bug in the original scaffold).
        Assert.DoesNotMatch(regex, "123456789");
        Assert.DoesNotMatch(regex, "1234-567-89");
        // Letters anywhere.
        Assert.DoesNotMatch(regex, "5511A-0645-01");
        // Wrong grouping (3-4-3).
        Assert.DoesNotMatch(regex, "555-1110-645");
        // Empty.
        Assert.DoesNotMatch(regex, "");
    }
}
