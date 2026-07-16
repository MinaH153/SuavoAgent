using ClosedXML.Excel;
using PioneerRxSim;
using SuavoAgent.Core.Pricing;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

public sealed class PioneerRxTop500ExportWorkbookValidatorTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"top500-semantic-{Guid.NewGuid():N}");

    public PioneerRxTop500ExportWorkbookValidatorTests() =>
        Directory.CreateDirectory(_root);

    [Fact]
    public void ExactFieldShape_IsAccepted_AndStaleDateIsRejected()
    {
        var path = SyntheticTop500XlsxWriter.Write(_root, Now);

        Assert.True(PioneerRxTop500ExportWorkbookValidator.IsExact(
            path,
            new DateOnly(2026, 7, 15)));
        Assert.False(PioneerRxTop500ExportWorkbookValidator.IsExact(
            path,
            new DateOnly(2026, 7, 14)));
    }

    [Fact]
    public void FiveHundredFirstBusinessRow_IsRejected()
    {
        var source = SyntheticTop500XlsxWriter.Write(_root, Now);
        var tampered = Path.Combine(_root, "rank-501.xlsx");
        using (var workbook = new XLWorkbook(source))
        {
            var sheet = workbook.Worksheets.Single();
            sheet.Row(662).InsertRowsAbove(1);
            sheet.Cell(662, 1).Value = 501;
            sheet.Cell(662, 3).Value = "Unexpected Drug";
            sheet.Cell(662, 4).Value = "1 mg";
            sheet.Cell(662, 6).SetValue("10000000501");
            sheet.Cell(662, 7).Value = 1;
            workbook.SaveAs(tampered);
        }

        Assert.False(PioneerRxTop500ExportWorkbookValidator.IsExact(
            tampered,
            new DateOnly(2026, 7, 15)));
    }

    [Fact]
    public void UnexpectedMeaningfulFurnitureRow_IsRejected()
    {
        var source = SyntheticTop500XlsxWriter.Write(_root, Now);
        var tampered = Path.Combine(_root, "mixed-report.xlsx");
        using (var workbook = new XLWorkbook(source))
        {
            var sheet = workbook.Worksheets.Single();
            sheet.Cell(38, 2).Value = "Unexpected mixed report content";
            workbook.SaveAs(tampered);
        }

        Assert.False(PioneerRxTop500ExportWorkbookValidator.IsExact(
            tampered,
            new DateOnly(2026, 7, 15)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
