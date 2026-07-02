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
        Assert.Equal(3.16m, result.Value.cost);
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
        Assert.Equal(3.44m, result.Value.cost);
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
    public void SelectCheapest_NadimOmeprazole_excludes_blank_cost_rows_that_sort_on_top()
    {
        // Ground truth: Nadim's live Supplier Catalog for Omeprazole Dr 40 Mg (NDC 55111-0645-01),
        // read off his box. Two McKesson rows carry BLANK cost and render at the top of the grid;
        // Nadim's spoken heuristic ("cheapest = the one on top") would wrongly pick one of them.
        // The engine must exclude blank/zero costs (fail-closed) and compute the true argmin —
        // which is Real Value Rx at $3.16 (NOT McKesson $4.95, which is a 500-count package).
        var rows = new[]
        {
            new Row("Mckesson 869640", 0m, "Available"),      // blank cost, sorts on top -> excluded
            new Row("Mckesson 340b", 0m, "Available"),        // blank cost -> excluded
            new Row("Real Value Rx", 3.16m, "Available"),     // true cheapest by Cost
            new Row("keysource", 3.28m, "Available"),
            new Row("Prescription Supply", 3.44m, "Available"),
            new Row("Anda", 3.54m, "Available"),
            new Row("McKesson", 4.95m, "Available"),          // 500-ct package (low per-unit, high Cost)
            new Row("Mckesson Geri", 13.89m, "Available"),
        };

        var result = PricingGridReader.SelectCheapest(rows);

        Assert.NotNull(result);
        Assert.Equal("Real Value Rx", result!.Value.supplier);
        Assert.Equal(3.16m, result.Value.cost);
    }
}
