using System.Collections.Generic;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Helper.Workflows;
using Xunit;

namespace SuavoAgent.Helper.Tests;

/// <summary>
/// The vision grid parser reads the PioneerRx Pricing/Supplier-Catalog grid from OCR regions and
/// ranks the cheapest supplier by COST PER UNIT (Nadim's rule; the Graphify McKesson $0.0099 example).
/// Covers both OCR layouts: one region per line, and one region per cell on a shared Y.
/// </summary>
public class VisionSupplierGridParserTests
{
    private static TextRegion Line(string text, int y, double conf = 0.9) =>
        new() { Text = text, Bounds = new Rect(0, y, 400, 16), Confidence = conf };

    private static TextRegion Cell(string text, int x, int y, double conf = 0.9) =>
        new() { Text = text, Bounds = new Rect(x, y, 90, 16), Confidence = conf };

    [Fact]
    public void Ranks_cheapest_by_cost_per_unit_not_pack_cost()
    {
        // Each row: "supplier packCost perUnitCost status". Per-unit is the SMALLEST decimal on the row.
        // McKesson wins on per-unit (0.0099) despite the largest pack cost (8.42) — the Graphify rule.
        var regions = new List<TextRegion>
        {
            Line("Supplier Cost Per Unit Status", 0),          // header — no cost token
            Line("Mckesson 8.42 0.0099 Available", 20),
            Line("Parmed 3.16 0.0316 Available", 40),
            Line("Real Value Rx 3.16 3.16 Available", 60),
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
            Line("Cardinal 1.00 0.50 Discontinued", 20),   // cheapest but discontinued → skip
            Line("Mckesson 4.00 2.00 Available", 40),
        };

        var reading = VisionSupplierGridParser.ReadCheapest(regions);

        Assert.NotNull(reading);
        Assert.Contains("Mckesson", reading!.Supplier, System.StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2.00m, reading.CostPerUnit);
    }

    [Fact]
    public void Does_not_treat_an_ndc_or_quantity_as_a_cost()
    {
        // NDC ("60505-0829-01", hyphens no dot) and quantity ("500", integer) must not be read as costs.
        var regions = new List<TextRegion>
        {
            Line("Mckesson 60505-0829-01 500 3.28 Available", 20),
        };

        var rows = VisionSupplierGridParser.ParseRows(regions);
        var row = Assert.Single(rows);
        Assert.Equal(3.28m, row.CostPerUnit); // only the true decimal, not 60505 / 500
        Assert.Contains("Mckesson", row.Supplier, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reads_supplier_and_cheapest_from_the_real_wide_pioneerrx_grid()
    {
        // Verbatim line-OCR of Nadim's real PioneerRx Supplier-Catalog (image (31).png, Omeprazole DR
        // 40mg). Each row is ONE TextLine — Linked/Group/Name/NDC/UPC precede the Supplier column, and
        // Cost/CostPerUnit/Rebate/BOH/AWP/MAC follow it — so a naive "leading alpha" read would return
        // "Yes Rx OMEPRAZOLE ... Stock Package with" and the top-of-window pricing panel would pollute
        // the ranking. The supplier must anchor on the item id (NDC), and the panel rows (no id) drop out.
        // The 500-count McKesson pack ($4.95 → 0.0099/unit) wins on per-unit despite sitting at row 8,
        // NOT the top row (Real Value Rx $3.16 / 0.0316) — the exact trap the sighted read exists to beat.
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

        var reading = VisionSupplierGridParser.ReadCheapest(regions);

        Assert.NotNull(reading);
        Assert.Equal("McKesson", reading!.Supplier);            // clean name — not the row prefix junk
        Assert.Equal(0.0099m, reading.CostPerUnit);             // the 500-ct pack, not the top row

        // The Supplier column is read cleanly on the priced grid rows, and the pricing panel is excluded.
        var priced = VisionSupplierGridParser.ParseRows(regions).FindAll(r => r.CostPerUnit is > 0m && r.HasId);
        Assert.Contains(priced, r => r.Supplier == "Real Value Rx");
        Assert.Contains(priced, r => r.Supplier == "keysource");
        Assert.Contains(priced, r => r.Supplier == "Anda");
        Assert.DoesNotContain(priced, r => r.Supplier.Contains("OMEPRAZOLE"));   // Name never leaks in
        Assert.DoesNotContain(priced, r => r.Supplier.Contains("DR.REDDY"));     // Manufacturer never leaks in
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
            Line("Oak Drugs 2.99 2.99 Available", 20, conf: 0.30), // fuzzy — below floor
            Line("Mckesson 4.00 4.00 Available", 40, conf: 0.95),
        };

        var reading = VisionSupplierGridParser.ReadCheapest(regions, minRowConfidence: 0.5);
        Assert.NotNull(reading);
        Assert.Contains("Mckesson", reading!.Supplier, System.StringComparison.OrdinalIgnoreCase);
    }
}
