using ClosedXML.Excel;
using SuavoAgent.Core.Pricing;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

public sealed class PioneerRxPricedWorkbookSchemaValidatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"priced-schema-{Guid.NewGuid():N}");

    public PioneerRxPricedWorkbookSchemaValidatorTests() =>
        Directory.CreateDirectory(_root);

    [Fact]
    public void ExactSixColumnWorkbook_IsAccepted()
    {
        var path = WriteValid("valid.xlsx");
        Assert.True(PioneerRxPricedWorkbookSchemaValidator.IsExact(path));
    }

    [Theory]
    [InlineData("rank_text")]
    [InlineData("duplicate_ndc")]
    [InlineData("blank_drug")]
    [InlineData("blank_strength")]
    [InlineData("review_with_cost")]
    [InlineData("supplier_without_cost")]
    [InlineData("formula")]
    [InlineData("hidden_header")]
    [InlineData("hidden_row")]
    [InlineData("extra_sheet")]
    [InlineData("extra_cell")]
    public void AdversarialWorkbook_IsRejected(string mutation)
    {
        var source = WriteValid($"{mutation}-source.xlsx");
        var path = Path.Combine(_root, $"{mutation}.xlsx");
        using (var workbook = new XLWorkbook(source))
        {
            var sheet = workbook.Worksheet("Pricing");
            switch (mutation)
            {
                case "rank_text": sheet.Cell(2, 1).SetValue("1"); break;
                case "duplicate_ndc": sheet.Cell(3, 4).SetValue(sheet.Cell(2, 4).GetString()); break;
                case "blank_drug": sheet.Cell(2, 2).Clear(); break;
                case "blank_strength": sheet.Cell(2, 3).Clear(); break;
                case "review_with_cost":
                    sheet.Cell(2, 5).Value = "Needs review";
                    sheet.Cell(2, 6).Value = 1m;
                    break;
                case "supplier_without_cost": sheet.Cell(2, 6).Clear(); break;
                case "formula": sheet.Cell(2, 6).FormulaA1 = "=1+1"; break;
                case "hidden_header": sheet.Row(1).Hide(); break;
                case "hidden_row": sheet.Row(2).Hide(); break;
                case "extra_sheet": workbook.AddWorksheet("Hidden").Visibility =
                    XLWorksheetVisibility.VeryHidden; break;
                case "extra_cell": sheet.Cell(502, 1).Value = "extra"; break;
            }
            workbook.SaveAs(path);
        }

        Assert.False(PioneerRxPricedWorkbookSchemaValidator.IsExact(path));
    }

    private string WriteValid(string name)
    {
        var path = Path.Combine(_root, name);
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Pricing");
        var headers = new[]
        {
            "Rank", "Drug", "Strength", "NDC", "Cheapest Supplier", "Cost",
        };
        for (var column = 1; column <= headers.Length; column++)
            sheet.Cell(1, column).Value = headers[column - 1];
        for (var rank = 1; rank <= 500; rank++)
        {
            var row = rank + 1;
            sheet.Cell(row, 1).Value = rank;
            sheet.Cell(row, 2).Value = $"Drug {rank}";
            sheet.Cell(row, 3).Value = "1 mg";
            sheet.Cell(row, 4).SetValue((10_000_000_000L + rank).ToString());
            sheet.Cell(row, 5).Value = "Supplier";
            sheet.Cell(row, 6).Value = 1m + rank / 100m;
        }
        workbook.SaveAs(path);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
