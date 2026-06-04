using ClosedXML.Excel;
using SuavoAgent.Core.Pricing;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

public class PricingWorkbookInspectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"suavo_inspect_{Guid.NewGuid():N}");

    public PricingWorkbookInspectorTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void Describe_ReportsHeadersAndNumericRanges_ButNeverTextValues()
    {
        var path = Path.Combine(_dir, "wb.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.Worksheets.Add("S");
            ws.Cell(1, 1).Value = "NDC";
            ws.Cell(1, 2).Value = "Current Cost";
            ws.Cell(1, 3).Value = "Qty";
            ws.Cell(1, 4).Value = "Patient";
            ws.Cell(2, 1).Value = "55111-0645-01"; ws.Cell(2, 2).Value = 0.05; ws.Cell(2, 3).Value = 1200; ws.Cell(2, 4).Value = "Jane Doe";
            ws.Cell(3, 1).Value = "00093-5124-01"; ws.Cell(3, 2).Value = 0.12; ws.Cell(3, 3).Value = 800; ws.Cell(3, 4).Value = "John Smith";
            wb.SaveAs(path);
        }

        var cols = PricingWorkbookInspector.Describe(path);
        var summary = PricingWorkbookInspector.Summarize(cols);

        Assert.Equal(4, cols.Count);
        var cost = cols.Single(c => c.Header == "Current Cost");
        Assert.Equal("numeric", cost.Kind);
        Assert.Equal(2, cost.NumericCount);
        Assert.Equal(0.05m, cost.Min);
        Assert.Equal(0.12m, cost.Max);
        Assert.Equal("numeric", cols.Single(c => c.Header == "Qty").Kind);
        Assert.Equal("text", cols.Single(c => c.Header == "Patient").Kind);

        // PHI-safe: column TITLES surface (operational), but text cell VALUES never do.
        Assert.Contains("Patient", summary);
        Assert.DoesNotContain("Jane Doe", summary);
        Assert.DoesNotContain("John Smith", summary);
        Assert.DoesNotContain("55111-0645-01", summary); // NDC values aren't echoed either
    }

    [Fact]
    public void Describe_MissingFile_ReturnsEmpty()
        => Assert.Empty(PricingWorkbookInspector.Describe(Path.Combine(_dir, "nope.xlsx")));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
