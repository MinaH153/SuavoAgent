using SuavoAgent.Helper.Workflows;
using Xunit;
using Row = SuavoAgent.Helper.Workflows.PricingGridReader.SupplierRow;
using PackageRow = SuavoAgent.Helper.Workflows.PricingGridReader.PackageSupplierRow;

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
    public void SelectCheapest_rejects_blank_or_unknown_status()
    {
        var rows = new[]
        {
            new Row("BlankStatus", 1.00m, ""),
            new Row("UnknownStatus", 2.00m, "Backordered"),
            new Row("VerifiedStatus", 4.20m, "Active"),
        };

        Assert.Equal("VerifiedStatus", PricingGridReader.SelectCheapest(rows)!.Value.supplier);
    }

    [Theory]
    [InlineData("3.28", true, 3.28)]
    [InlineData("13.89", true, 13.89)]
    [InlineData("1,234.50", true, 1234.50)]
    [InlineData("", false, 0)]
    [InlineData("n/a", false, 0)]
    // Currency-formatted cells (DevExpress currency columns render "$3.28" / "$0.0099"). Invariant's
    // currency symbol is "¤" not "$", so NumberStyles.Any rejected these → whole supplier batch parsed
    // to nothing → false "no supplier rows". Must now strip the symbol and parse.
    [InlineData("$3.28", true, 3.28)]
    [InlineData("$0.0099", true, 0.0099)]
    [InlineData("$1,234.50", true, 1234.50)]
    [InlineData("$", false, 0)]
    public void TryParseCost_parses_plain_numbers(string text, bool ok, double expected)
    {
        var parsed = PricingGridReader.TryParseCost(text, out var cost);
        Assert.Equal(ok, parsed);
        if (ok) Assert.Equal((decimal)expected, cost);
    }

    [Theory]
    [InlineData("Available", true)]
    [InlineData("available", true)]
    [InlineData(" Active ", true)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("AVAIL", false)]
    [InlineData("A", false)]
    [InlineData("Available Soon", false)]
    [InlineData("Backordered", false)]
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
    public void SelectCheapest_CurrentPerUnitContract_ranks_by_cost_per_unit_not_pack_cost()
    {
        // Ground truth: Nadim's live Supplier Catalog for Omeprazole Dr 40 Mg (NDC 55111-0645-01).
        // The row values below exercise the current engine's dedicated COST PER UNIT contract; the
        // pharmacy PIC's final cost-basis decision remains open. Two McKesson rows carry a blank/0
        // per-unit cost and render on top; the engine excludes them fail-closed. The 500-count pack has the
        // LOWEST per-unit ($0.0099) even though its pack cost ($4.95) is far from the lowest — so
        // the admitted per-unit implementation picks McKesson, while a pack-cost implementation would
        // pick Real Value Rx. This test locks the current behavior without deciding the PIC's policy.
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

    [Fact]
    public void Package_cost_lane_uses_exact_cost_and_keeps_per_unit_lane_unchanged()
    {
        var perUnit = PricingGridReader.SelectCheapest(new[]
        {
            new Row("Real Value Rx", 0.0316m, "Available"),
            new Row("McKesson", 0.0099m, "Available"),
        });
        var package = PricingGridReader.SelectCheapestPackage(new[]
        {
            new PackageRow("Real Value Rx", 3.16m, "Available", true, "Rx", false),
            new PackageRow("McKesson", 4.95m, "Available", true, "Rx", false),
        });

        Assert.Equal("McKesson", perUnit!.Value.supplier);
        Assert.Equal(0.0099m, perUnit.Value.costPerUnit);
        Assert.Equal("Real Value Rx", package!.Value.supplier);
        Assert.Equal(3.16m, package.Value.packageCost);
    }

    [Fact]
    public void Package_cost_lane_rejects_unlinked_non_rx_and_discontinued_rows()
    {
        var result = PricingGridReader.SelectCheapestPackage(new[]
        {
            new PackageRow("Unlinked", 1.00m, "Available", false, "Rx", false),
            new PackageRow("Front Shop", 1.10m, "Available", true, "OTC", false),
            new PackageRow("Discontinued", 1.20m, "Available", true, "Rx", true),
            new PackageRow("Unavailable", 1.30m, "Unavailable", true, "Rx", false),
            new PackageRow("Eligible", 2.60m, "Active", true, "Rx", false),
        });

        Assert.Equal("Eligible", result!.Value.supplier);
        Assert.Equal(2.60m, result.Value.packageCost);
    }
}
