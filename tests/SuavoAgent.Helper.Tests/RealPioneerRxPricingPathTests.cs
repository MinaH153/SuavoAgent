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

    private static TextRegion Cell(string text, int x, int y, int width = 160, double conf = 0.85) =>
        new() { Text = text, Bounds = new Rect(x, y, width, 30), Confidence = conf };

    // Cell-bounded OCR reconstruction of image (31).png: pricing panel (no id — must drop out) +
    // the supplier rows. A line-wide OCR representation is intentionally rejected because it cannot
    // prove which decimal belongs to Cost Per Unit rather than Cost/Rebate/AWP/MAC.
    // The 500-ct McKesson pack ($4.95 → 0.0099/unit) is the cheapest per unit but sits at row 8; the
    // top priced row (Real Value Rx, $3.16 → 0.0316) is dearer per unit. The grid is sorted by package
    // Cost, so "read the top row" would pick the wrong supplier — the trap the sighted read must beat.
    private static List<TextRegion> RealGridRegions() => new()
    {
        Line("AWP Source: Highest AWP 7.3956", 100),
        Line("NADAC: (per EA) 0.0504", 140),
        Line("Average Received Cost: (per EA) 0.0118", 180),
        Cell("Cost Per Unit", 2100, 220),
        Cell("55111-0645-01", 700, 300), Cell("7555111064501", 880, 300),
        Cell("Real Value Rx", 1080, 300), Cell("3.1600", 1900, 300), Cell("0.0316", 2100, 300), Cell("Available", 3280, 300),
        Cell("55111-0645-01", 700, 340), Cell("35511164501", 880, 340),
        Cell("keysource", 1080, 340), Cell("3.2800", 1900, 340), Cell("0.0328", 2100, 340), Cell("Available", 3280, 340),
        Cell("55111-0645-01", 700, 380), Cell("355111645016", 880, 380),
        Cell("Anda", 1080, 380), Cell("3.5400", 1900, 380), Cell("0.0354", 2100, 380), Cell("Available", 3280, 380),
        Cell("55111-0645-01", 700, 420), Cell("35511164501", 880, 420),
        Cell("McKesson", 1080, 420), Cell("4.9500", 1900, 420), Cell("0.0099", 2100, 420), Cell("Available", 3280, 420),
        Cell("55111-0645-01", 700, 460), Cell("35511164501", 880, 460),
        Cell("Mckesson Geri", 1080, 460), Cell("13.8900", 1900, 460), Cell("0.1389", 2100, 460), Cell("Available", 3280, 460),
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
        var regions = new List<TextRegion>
        {
            Cell("Cost Per Unit", 2140, 240, 90),
            // Real Value Rx row (top priced row — the trap)
            Cell("Yes", 60, 300, 90), Cell("Rx", 210, 300, 90), Cell("OMEPRAZOLE DR CP 40...", 410, 300, 90),
            Cell("55111-0645-01", 710, 300, 90), Cell("7555111064501", 890, 300, 90), Cell("Real Value Rx", 1090, 300, 90),
            Cell("3.1600", 1990, 300, 90), Cell("0.0316", 2140, 300, 90), Cell("Available", 3280, 300, 90),
            // McKesson 500-ct row (the real cheapest per unit)
            Cell("Yes", 60, 340, 90), Cell("Rx", 210, 340, 90), Cell("OMEPRAZOLE DR CP 40...", 410, 340, 90),
            Cell("55111-0645-01", 710, 340, 90), Cell("35511164501", 890, 340, 90), Cell("McKesson", 1090, 340, 90),
            Cell("4.9500", 1990, 340, 90), Cell("0.0099", 2140, 340, 90), Cell("Available", 3280, 340, 90),
        };

        var reading = VisionSupplierGridParser.ReadCheapest(regions);
        Assert.NotNull(reading);
        Assert.Equal("McKesson", reading!.Supplier);
        Assert.Equal(0.0099m, reading.CostPerUnit);
    }
}
