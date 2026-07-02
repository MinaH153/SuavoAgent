using System.Collections.Generic;
using System.Linq;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Helper.Workflows;
using Xunit;

namespace SuavoAgent.Helper.Tests;

/// <summary>
/// End-to-end money path on Nadim's REAL PioneerRx Supplier-Catalog (image (31).png, Omeprazole DR
/// 40mg): the OCR regions → <see cref="VisionSupplierGridParser"/> (what SuavoAgent SEES) →
/// <see cref="VisionExactReconciler"/> (the write gate). Uses the verbatim line-OCR of the real grid,
/// so this is the actual data the box would produce — not a synthetic sim. Proves the sighted read
/// finds the right supplier AND that the reconciler only writes when the exact source confirms it.
/// </summary>
public class RealPioneerRxPricingPathTests
{
    private static TextRegion Line(string text, int y, double conf = 0.85) =>
        new() { Text = text, Bounds = new Rect(52, y, 3314, 34), Confidence = conf };

    // Verbatim line-OCR of image (31).png: pricing panel (no id — must drop out) + the 9-row grid.
    // The 500-ct McKesson pack ($4.95 → 0.0099/unit) is the cheapest per unit but sits at row 8; the
    // top priced row (Real Value Rx, $3.16 → 0.0316) is dearer per unit. The grid is sorted by package
    // Cost, so "read the top row" would pick the wrong supplier — the trap the sighted read must beat.
    private static List<TextRegion> RealGridRegions() => new()
    {
        Line("AWP Source: Highest AWP 7.3956", 100),
        Line("NADAC: (per EA) 0.0504", 140),
        Line("Average Received Cost: (per EA) 0.0118", 180),
        Line("Yes Rx OMEPRAZOLE DR CP 40... 55111-0645-01 35511164501 Mckesson 869640 1583772 1 Stock Package with 100.000... 7858.000000 0.000000 739.5600 7.3956 0.0000 0.0000 Available", 300),
        Line("Yes Rx OMEPRAZOLE DR CP 40... 55111-0645-01 7555111064501 Real Value Rx 755511106... 1 Stock Package with 100.000... 3.1600 0.0316 3.1600 0.0316 7858.000000 0.000000 739.5600 7.3956 0.0000 0.0000 Available", 340),
        Line("Yes Rx Omeprazole DR 40mg Ca... 55111-0645-01 keysource 117387 DR.REDDY'S LAB 1 Stock Package with 100.000... 3.2800 0.0328 3.2800 0.0328 7858.000000 0.000000 739.5600 7.3956 0.0000 0.0000 Available", 380),
        Line("Yes Rx OMEPRAZOLE DR 40MG 55111-0645-01 355111645016 Anda 322642 DR.REDDY'S LAB 1 Stock Package with 100.000... 3.5400 0.0354 3.5400 0.0354 7858.000000 0.000000 739.5600 7.3956 0.0000 0.0000 Available", 420),
        Line("Yes Rx OMEPRAZOLE DR CP 40... 55111-0645-01 35511164501 McKesson 1583772 1 Stock Package with 500.000... 4.9500 0.0099 4.9500 0.0099 7858.000000 0.000000 739.5600 1.4791 0.0000 0.0000 Available", 460),
        Line("Yes Rx OMEPRAZOLE DR CP 40... 55111-0645-01 35511164501 Mckesson Geri... 1583772 1 Stock Package with 100.000... 13.8900 0.1389 13.8900 0.1389 7858.000000 0.000000 739.5600 7.3956 0.0000 0.0000 Available", 500),
    };

    [Fact]
    public void Vision_read_plus_confirming_exact_writes_the_cheapest_real_supplier()
    {
        var vision = VisionSupplierGridParser.ReadCheapest(RealGridRegions());
        Assert.NotNull(vision);
        Assert.Equal("McKesson", vision!.Supplier);
        Assert.Equal(0.0099m, vision.CostPerUnit);

        // UIA reads the same cell exactly (PioneerRx exposes the supplier as "Mckesson <item#>").
        var decision = VisionExactReconciler.Reconcile(
            new VisionExactReconciler.Reading(vision.Supplier, vision.CostPerUnit, vision.Confidence),
            new VisionExactReconciler.Reading("Mckesson 1583772", 0.0099m, 1.0));

        Assert.True(decision.Accept);
        Assert.Equal("vision+exact", decision.Source);
        Assert.Equal(0.0099m, decision.CostPerUnit);        // writes the EXACT value, not the OCR value
    }

    [Fact]
    public void Vision_misread_that_the_exact_source_contradicts_fails_closed()
    {
        var vision = VisionSupplierGridParser.ReadCheapest(RealGridRegions());
        Assert.NotNull(vision);

        // Exact source says a different supplier is the row (i.e. the OCR located the wrong one) →
        // never write a price that the authoritative source doesn't back.
        var decision = VisionExactReconciler.Reconcile(
            new VisionExactReconciler.Reading(vision!.Supplier, vision.CostPerUnit, vision.Confidence),
            new VisionExactReconciler.Reading("Real Value Rx", 0.0316m, 1.0));

        Assert.False(decision.Accept);
        Assert.Contains("mismatch", decision.RejectReason);
    }

    [Fact]
    public void Grid_delivered_as_per_cell_regions_reads_the_same_cheapest()
    {
        // Production's other OCR layout: a row split into per-cell regions on a shared Y (different X).
        // Clustering must rebuild the row and the id-anchored read must still find McKesson @ 0.0099.
        TextRegion Cell(string t, int x, int y) => new() { Text = t, Bounds = new Rect(x, y, 90, 30), Confidence = 0.85 };
        var regions = new List<TextRegion>
        {
            // Real Value Rx row (top priced row — the trap)
            Cell("Yes", 60, 300), Cell("Rx", 210, 300), Cell("OMEPRAZOLE DR CP 40...", 410, 300),
            Cell("55111-0645-01", 710, 300), Cell("7555111064501", 890, 300), Cell("Real Value Rx", 1090, 300),
            Cell("3.1600", 1990, 300), Cell("0.0316", 2140, 300), Cell("Available", 3280, 300),
            // McKesson 500-ct row (the real cheapest per unit)
            Cell("Yes", 60, 340), Cell("Rx", 210, 340), Cell("OMEPRAZOLE DR CP 40...", 410, 340),
            Cell("55111-0645-01", 710, 340), Cell("35511164501", 890, 340), Cell("McKesson", 1090, 340),
            Cell("4.9500", 1990, 340), Cell("0.0099", 2140, 340), Cell("Available", 3280, 340),
        };

        var reading = VisionSupplierGridParser.ReadCheapest(regions);
        Assert.NotNull(reading);
        Assert.Equal("McKesson", reading!.Supplier);
        Assert.Equal(0.0099m, reading.CostPerUnit);
    }
}
