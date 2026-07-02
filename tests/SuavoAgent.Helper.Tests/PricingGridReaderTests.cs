using SuavoAgent.Helper.Workflows;
using Xunit;
using Row = SuavoAgent.Helper.Workflows.PricingGridReader.SupplierRow;

namespace SuavoAgent.Helper.Tests;

/// <summary>
/// Pure-logic coverage for the Pricing grid interpretation extracted from
/// PricingWorkflow — the parts that decide which supplier/cost get written back.
/// </summary>
public sealed class PricingGridReaderTests
{
    [Fact]
    public void SelectCheapest_picks_min_cost_not_row_one()
    {
        // Grid sort is user-toggleable, so the cheapest may not be first.
        var rows = new[]
        {
            new Row("McKesson", 13.89m, "Available"),
            new Row("keysource", 3.16m, "Available"),
            new Row("Anda", 4.95m, "Available"),
        };

        var result = PricingGridReader.SelectCheapest(rows);

        Assert.NotNull(result);
        Assert.Equal("keysource", result!.Value.supplier);
        Assert.Equal(3.16m, result.Value.costPerUnit);
    }

    [Fact]
    public void SelectCheapest_skips_discontinued_even_if_cheaper()
    {
        var rows = new[]
        {
            new Row("GhostSupplier", 1.00m, "Discontinued"),
            new Row("RealValueRx", 3.44m, "Available"),
            new Row("DeadStock", 0.50m, "Unavailable"),
        };

        var result = PricingGridReader.SelectCheapest(rows);

        Assert.Equal("RealValueRx", result!.Value.supplier);
        Assert.Equal(3.44m, result.Value.costPerUnit);
    }

    [Fact]
    public void SelectCheapest_skips_blank_supplier_and_non_positive_cost()
    {
        var rows = new[]
        {
            new Row("", 1.00m, "Available"),
            new Row("Anda", 0m, "Available"),
            new Row("Anda", -2m, "Available"),
            new Row("McKesson", 5.00m, "Available"),
        };

        var result = PricingGridReader.SelectCheapest(rows);

        Assert.Equal("McKesson", result!.Value.supplier);
    }

    [Fact]
    public void SelectCheapest_returns_null_when_nothing_qualifies()
    {
        var rows = new[]
        {
            new Row("X", 0m, "Available"),
            new Row("Y", 9m, "Discontinued"),
        };

        Assert.Null(PricingGridReader.SelectCheapest(rows));
    }

    [Fact]
    public void SelectCheapest_treats_blank_status_as_usable()
    {
        // No Status column resolved → empty status → don't over-filter.
        var rows = new[] { new Row("Anda", 4.20m, "") };
        Assert.Equal("Anda", PricingGridReader.SelectCheapest(rows)!.Value.supplier);
    }

    [Theory]
    [InlineData("3.28", true, 3.28)]
    [InlineData("13.89", true, 13.89)]
    [InlineData("1,234.50", true, 1234.50)]
    [InlineData("", false, 0)]
    [InlineData("n/a", false, 0)]
    public void TryParseCost_parses_plain_numbers(string text, bool ok, double expected)
    {
        var parsed = PricingGridReader.TryParseCost(text, out var cost);
        Assert.Equal(ok, parsed);
        if (ok) Assert.Equal((decimal)expected, cost);
    }

    [Theory]
    [InlineData("Available", true)]
    [InlineData("", true)]
    [InlineData("Discontinued", false)]
    [InlineData("UNAVAILABLE", false)]
    [InlineData("inactive", false)]
    public void IsUsableStatus_classifies(string status, bool usable)
        => Assert.Equal(usable, PricingGridReader.IsUsableStatus(status));

    [Theory]
    [InlineData("OMEPRAZOLE DR 40MG (Do Not Use)", true)]
    [InlineData("OMEPRAZOLE DR 40MG", false)]
    [InlineData("DONOTUSE - legacy", true)]
    [InlineData("", false)]
    public void LooksLikeDoNotUse_detects_marker(string text, bool expected)
        => Assert.Equal(expected, PricingGridReader.LooksLikeDoNotUse(text));

    [Fact]
    public void SelectCheapest_NadimOmeprazole_ranks_by_cost_per_unit_not_pack_cost()
    {
        // Ground truth: Nadim's live Supplier Catalog for Omeprazole Dr 40 Mg (NDC 55111-0645-01).
        // The row values below are COST PER UNIT (the ranking basis — Graphify rule "argmin over
        // Cost Per Unit, NOT Cost"). Two McKesson rows carry a blank/0 per-unit cost and render on
        // top; the engine excludes them fail-closed. Crucially the 500-count McKesson pack has the
        // LOWEST per-unit ($0.0099) even though its pack cost ($4.95) is far from the lowest — so
        // ranking by per-unit (correct) picks McKesson, while ranking by raw pack cost (WRONG) would
        // have picked Real Value Rx. This test locks in the per-unit rule.
        var rows = new[]
        {
            new Row("Mckesson 869640", 0m, "Available"),        // blank per-unit, sorts on top -> excluded
            new Row("Mckesson 340b", 0m, "Available"),          // blank -> excluded
            new Row("Real Value Rx", 0.0316m, "Available"),     // cheapest by PACK cost ($3.16/100) — NOT the winner
            new Row("keysource", 0.0328m, "Available"),
            new Row("Prescription Supply", 0.0344m, "Available"),
            new Row("Anda", 0.0354m, "Available"),
            new Row("McKesson", 0.0099m, "Available"),          // 500-ct pack: cheapest PER UNIT -> the winner
            new Row("Mckesson Geri", 0.1389m, "Available"),
        };

        var result = PricingGridReader.SelectCheapest(rows);

        Assert.NotNull(result);
        Assert.Equal("McKesson", result!.Value.supplier);       // per-unit winner, not the pack-cost winner
        Assert.Equal(0.0099m, result.Value.costPerUnit);
    }
}
