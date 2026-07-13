using System.Collections.Generic;
using System.Linq;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Helper.Workflows;
using Xunit;

namespace SuavoAgent.Helper.Tests;

/// <summary>
/// The vision grid parser reads the PioneerRx Pricing/Supplier-Catalog grid from OCR regions and
/// ranks the cheapest supplier under the engine's current dedicated COST PER UNIT contract.
/// Covers both OCR layouts: one region per line, and one region per cell on a shared Y.
/// </summary>
public class VisionSupplierGridParserTests
{
    private static TextRegion Line(string text, int y, double conf = 0.9) =>
        new() { Text = text, Bounds = new Rect(0, y, 400, 16), Confidence = conf };

    private static TextRegion Cell(string text, int x, int y, double conf = 0.9) =>
        new() { Text = text, Bounds = new Rect(x, y, 90, 16), Confidence = conf };

    private static TextRegion Word(
        string text,
        int x,
        int y,
        int width,
        double conf = 0.9) =>
        new() { Text = text, Bounds = new Rect(x, y, width, 16), Confidence = conf };

    private static TextRegion CostHeader(int x = 220) => Cell("Cost Per Unit", x, 0);

    [Fact]
    public void Ranks_cheapest_by_cost_per_unit_not_pack_cost()
    {
        // McKesson wins under the current per-unit contract despite the largest pack cost. The
        // competing pack-cost cells are deliberately outside the proven Cost Per Unit x-band.
        var regions = new List<TextRegion>
        {
            CostHeader(),
            Cell("Mckesson", 0, 20), Cell("8.42", 120, 20), Cell("0.0099", 220, 20), Cell("Available", 320, 20),
            Cell("Parmed", 0, 40), Cell("3.16", 120, 40), Cell("0.0316", 220, 40), Cell("Available", 320, 40),
            Cell("Real Value Rx", 0, 60), Cell("3.16", 120, 60), Cell("3.16", 220, 60), Cell("Available", 320, 60),
        };

        var reading = VisionSupplierGridParser.ReadCheapest(regions);

        Assert.NotNull(reading);
        Assert.Contains("Mckesson", reading!.Supplier, System.StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0.0099m, reading.CostPerUnit);
    }

    [Fact]
    public void Handles_per_cell_regions_on_a_shared_row()
    {
        // Same visual row split into separate OCR cells (different X, same Y) must group into one row.
        var regions = new List<TextRegion>
        {
            CostHeader(),
            Cell("Oak Drugs", 0, 40), Cell("2.99", 120, 40), Cell("2.99", 220, 40), Cell("Available", 320, 40),
            Cell("Parmed", 0, 60), Cell("6.60", 120, 60), Cell("6.60", 220, 60), Cell("Available", 320, 60),
        };

        var reading = VisionSupplierGridParser.ReadCheapest(regions);

        Assert.NotNull(reading);
        Assert.Contains("Oak Drugs", reading!.Supplier, System.StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2.99m, reading.CostPerUnit);
    }

    [Fact]
    public void Excludes_discontinued_rows()
    {
        var regions = new List<TextRegion>
        {
            CostHeader(),
            Cell("Cardinal", 0, 20), Cell("1.00", 120, 20), Cell("0.50", 220, 20), Cell("Discontinued", 320, 20),
            Cell("Mckesson", 0, 40), Cell("4.00", 120, 40), Cell("2.00", 220, 40), Cell("Available", 320, 40),
        };

        var reading = VisionSupplierGridParser.ReadCheapest(regions);

        Assert.NotNull(reading);
        Assert.Contains("Mckesson", reading!.Supplier, System.StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2.00m, reading.CostPerUnit);
    }

    [Fact]
    public void Accepts_explicit_active_status()
    {
        var reading = VisionSupplierGridParser.ReadCheapest(new List<TextRegion>
        {
            CostHeader(),
            Cell("McKesson", 0, 20), Cell("4.00", 120, 20), Cell("2.00", 220, 20), Cell("Active", 320, 20),
        });

        Assert.NotNull(reading);
        Assert.Equal("McKesson", reading!.Supplier);
    }

    [Theory]
    [InlineData("Not Available")]
    [InlineData("NOT-AVAILABLE")]
    [InlineData("Unavailable")]
    [InlineData("Inactive")]
    [InlineData("Not Active")]
    [InlineData("Discontinued")]
    [InlineData("Available Soon")]
    [InlineData("Do Not Use Available")]
    public void Rejects_negated_or_non_exact_status_phrases(string status)
    {
        var reading = VisionSupplierGridParser.ReadCheapest(new List<TextRegion>
        {
            CostHeader(),
            Cell("McKesson", 0, 20), Cell("4.00", 120, 20), Cell("2.00", 220, 20), Cell(status, 320, 20),
        });

        Assert.Null(reading);
    }

    [Fact]
    public void PerCellStatus_requires_exact_whole_cell_value()
    {
        var rejected = VisionSupplierGridParser.ReadCheapest(new List<TextRegion>
        {
            CostHeader(),
            Cell("McKesson", 0, 40),
            Cell("4.00", 120, 40),
            Cell("2.00", 220, 40),
            Cell("Not Available", 320, 40),
        });

        Assert.Null(rejected);
    }

    [Fact]
    public void Does_not_treat_an_ndc_or_quantity_as_a_cost()
    {
        // NDC ("60505-0829-01", hyphens no dot) and quantity ("500", integer) must not be read as costs.
        var regions = new List<TextRegion>
        {
            CostHeader(),
            Cell("Mckesson", 0, 20), Cell("60505-0829-01", 80, 20), Cell("500", 140, 20),
            Cell("3.28", 220, 20), Cell("Available", 320, 20),
        };

        var rows = VisionSupplierGridParser.ParseRows(regions);
        var row = Assert.Single(rows, candidate => candidate.CostPerUnit is not null);
        Assert.Equal(3.28m, row.CostPerUnit); // only the true decimal, not 60505 / 500
        Assert.Contains("Mckesson", row.Supplier, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_line_wide_real_grid_when_cost_column_identity_is_unresolved()
    {
        // Verbatim line-OCR of Nadim's real PioneerRx Supplier-Catalog (image (31).png, Omeprazole DR
        // 40mg). Each row is ONE TextLine — Linked/Group/Name/NDC/UPC precede the Supplier column, and
        // Cost/CostPerUnit/Rebate/BOH/AWP/MAC follow it — so a naive "leading alpha" read would return
        // "Yes Rx OMEPRAZOLE ... Stock Package with" and the top-of-window pricing panel would pollute
        // the ranking. The supplier must anchor on the item id (NDC), and the panel rows (no id) drop out.
        // Under the current per-unit engine contract, the 500-count McKesson pack ($4.95 → 0.0099/unit)
        // wins despite sitting below Real Value Rx; the final pharmacy cost-basis policy remains open.
        var regions = new List<TextRegion>
        {
            // top-of-window pricing panel — decimal-bearing but no id; must not be ranked as suppliers
            Line("AWP Source: Highest AWP 7.3956", 100),
            Line("NADAC: (per EA) 0.0504", 140),
            Line("Average Received Cost: (per EA) 0.0118", 180),
            // the Supplier Catalog grid
            Line("Yes Rx OMEPRAZOLE DR CP 40... 55111-0645-01 35511164501 Mckesson 869640 1583772 1 Stock Package with 100.000... 7858.000000 0.000000 739.5600 7.3956 0.0000 0.0000 Available", 300),
            Line("Yes Rx OMEPRAZOLE DR CP 40... 55111-0645-01 7555111064501 Real Value Rx 755511106... 1 Stock Package with 100.000... 3.1600 0.0316 3.1600 0.0316 7858.000000 0.000000 739.5600 7.3956 0.0000 0.0000 Available", 340),
            Line("Yes Rx Omeprazole DR 40mg Ca... 55111-0645-01 keysource 117387 DR.REDDY'S LAB 1 Stock Package with 100.000... 3.2800 0.0328 3.2800 0.0328 7858.000000 0.000000 739.5600 7.3956 0.0000 0.0000 Available", 380),
            Line("Yes Rx OMEPRAZOLE DR 40MG 55111-0645-01 355111645016 Anda 322642 DR.REDDY'S LAB 1 Stock Package with 100.000... 3.5400 0.0354 3.5400 0.0354 7858.000000 0.000000 739.5600 7.3956 0.0000 0.0000 Available", 420),
            Line("Yes Rx OMEPRAZOLE DR CP 40... 55111-0645-01 35511164501 McKesson 1583772 1 Stock Package with 500.000... 4.9500 0.0099 4.9500 0.0099 7858.000000 0.000000 739.5600 1.4791 0.0000 0.0000 Available", 460),
            Line("Yes Rx OMEPRAZOLE DR CP 40... 55111-0645-01 35511164501 Mckesson Geri... 1583772 1 Stock Package with 100.000... 13.8900 0.1389 13.8900 0.1389 7858.000000 0.000000 739.5600 7.3956 0.0000 0.0000 Available", 500),
        };

        Assert.Null(VisionSupplierGridParser.ReadCheapest(regions));
        Assert.All(
            VisionSupplierGridParser.ParseRows(regions),
            row => Assert.Null(row.CostPerUnit));
    }

    [Fact]
    public void Reads_only_exact_cost_per_unit_column_among_competing_money_columns()
    {
        var regions = new List<TextRegion>
        {
            Cell("Cost", 120, 0), CostHeader(220), Cell("Rebate", 320, 0),
            Cell("AWP", 420, 0), Cell("MAC", 520, 0),
            Cell("McKesson", 0, 20), Cell("4.95", 120, 20), Cell("0.0099", 220, 20),
            Cell("0.0001", 320, 20), Cell("7.3956", 420, 20), Cell("0.0002", 520, 20),
            Cell("Available", 620, 20),
            Cell("Parmed", 0, 40), Cell("3.16", 120, 40), Cell("0.0316", 220, 40),
            Cell("0.0000", 320, 40), Cell("6.00", 420, 40), Cell("0.0001", 520, 40),
            Cell("Available", 620, 40),
        };

        var reading = VisionSupplierGridParser.ReadCheapest(regions);

        Assert.NotNull(reading);
        Assert.Equal("McKesson", reading!.Supplier);
        Assert.Equal(0.0099m, reading.CostPerUnit);
    }

    [Fact]
    public void Production_word_geometry_reconstructs_exact_cost_per_unit_header()
    {
        var regions = new List<TextRegion>
        {
            Word("Cost", 200, 0, 35),
            Word("Cost", 300, 0, 35), Word("Per", 341, 0, 25), Word("Unit", 372, 0, 32),
            Word("Rebate", 440, 0, 50), Word("AWP", 510, 0, 30), Word("MAC", 570, 0, 30),

            Word("55111-0645-01", 0, 30, 105), Word("McKesson", 115, 30, 70),
            Word("4.95", 205, 30, 35), Word("0.0099", 315, 30, 55),
            Word("0.0001", 440, 30, 55), Word("7.3956", 510, 30, 50),
            Word("0.0002", 570, 30, 55), Word("Available", 650, 30, 70),

            Word("55111-0645-01", 0, 55, 105), Word("Parmed", 115, 55, 55),
            Word("3.16", 205, 55, 35), Word("0.0316", 315, 55, 55),
            Word("0.0000", 440, 55, 55), Word("6.0000", 510, 55, 50),
            Word("0.0001", 570, 55, 55), Word("Available", 650, 55, 70),
        };

        var reading = VisionSupplierGridParser.ReadCheapest(regions);

        Assert.NotNull(reading);
        Assert.Equal("McKesson", reading!.Supplier);
        Assert.Equal(0.0099m, reading.CostPerUnit);
    }

    [Fact]
    public void Production_word_geometry_rejects_split_not_available_status()
    {
        var regions = new List<TextRegion>
        {
            Word("Cost", 300, 0, 35), Word("Per", 341, 0, 25), Word("Unit", 372, 0, 32),
            Word("55111-0645-01", 0, 30, 105), Word("McKesson", 115, 30, 70),
            Word("0.0099", 315, 30, 55), Word("Not", 620, 30, 25),
            Word("Available", 650, 30, 70),
        };

        Assert.Null(VisionSupplierGridParser.ReadCheapest(regions));
    }

    [Fact]
    public void Rejects_missing_or_duplicate_cost_per_unit_header()
    {
        var row = new[]
        {
            Cell("McKesson", 0, 20), Cell("2.00", 220, 20), Cell("Available", 320, 20),
        };
        Assert.Null(VisionSupplierGridParser.ReadCheapest(row));
        Assert.Null(VisionSupplierGridParser.ReadCheapest(new[]
        {
            CostHeader(120), CostHeader(220),
            Cell("McKesson", 0, 20), Cell("2.00", 220, 20), Cell("Available", 320, 20),
        }));
    }

    [Fact]
    public void Returns_null_on_empty_or_headers_only()
    {
        Assert.Null(VisionSupplierGridParser.ReadCheapest(new List<TextRegion>()));
        Assert.Null(VisionSupplierGridParser.ReadCheapest(new List<TextRegion>
        {
            Line("Supplier Cost Per Unit Status", 0), // no priced rows
        }));
    }

    [Fact]
    public void Drops_rows_below_the_confidence_floor()
    {
        var regions = new List<TextRegion>
        {
            CostHeader(),
            Cell("Oak Drugs", 0, 20, 0.30), Cell("2.99", 220, 20, 0.30), Cell("Available", 320, 20, 0.30),
            Cell("Mckesson", 0, 40, 0.95), Cell("4.00", 220, 40, 0.95), Cell("Available", 320, 40, 0.95),
        };

        var reading = VisionSupplierGridParser.ReadCheapest(regions, minRowConfidence: 0.5);
        Assert.NotNull(reading);
        Assert.Contains("Mckesson", reading!.Supplier, System.StringComparison.OrdinalIgnoreCase);
    }
}
