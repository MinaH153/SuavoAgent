using ClosedXML.Excel;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Core.Pricing;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

public class ExcelPricingReaderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"suavo_price_test_{Guid.NewGuid():N}");
    private readonly ExcelPricingReader _reader;

    public ExcelPricingReaderTests()
    {
        Directory.CreateDirectory(_tempDir);
        _reader = new ExcelPricingReader(NullLogger<ExcelPricingReader>.Instance);
    }

    [Fact]
    public void Read_ValidFile_ReturnsNdcRows()
    {
        var path = CreateExcel(new[]
        {
            ("NDC", "Drug Name"),
            ("55111-0645-01", "Omeprazole 40mg"),
            ("00093-5124-01", "Metformin 500mg"),
            ("16714-0234-01", "Lisinopril 10mg"),
        });

        var result = _reader.Read(path, "NDC");

        Assert.True(result.Success);
        Assert.Equal(3, result.Rows.Count);
        // NDC is normalized: hyphens stripped, padded to 11 digits
        Assert.Equal("55111064501", result.Rows[0].NdcNormalized);
        Assert.Equal("00093512401", result.Rows[1].NdcNormalized);
        Assert.Equal(2, result.Rows[0].RowIndex); // row 1 = header
    }

    [Fact]
    public void Read_RejectsPartialIdentityHeaderMatch()
    {
        var path = CreateExcel(new[]
        {
            ("ndc_number", "Item"),
            ("12345-6789-01", "Drug A"), // 5-4-2 real shape
        });

        var result = _reader.Read(path, "ndc");
        Assert.False(result.Success);
        Assert.Equal("ndc_identity_column_missing", result.Error);
    }

    [Fact]
    public void Read_AcceptsExactNormalizedCaseInsensitiveIdentityHeader()
    {
        var path = CreateExcel(new[]
        {
            ("  ndc  ", "Item"),
            ("12345-6789-01", "Drug A"),
        });

        var result = _reader.Read(path, "NDC");

        Assert.True(result.Success, result.Error);
        Assert.Equal("12345678901", Assert.Single(result.Rows).NdcNormalized);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_RejectsNdcAndNdc11RegardlessOfColumnOrder(bool swap)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.AddWorksheet("Sheet1");
            sheet.Cell(1, 1).Value = swap ? "NDC11" : "NDC";
            sheet.Cell(1, 2).Value = swap ? "NDC" : "NDC11";
            sheet.Cell(1, 3).Value = "Drug Name";
            sheet.Cell(2, 1).Value = "55111064501";
            sheet.Cell(2, 2).Value = "00093512401";
            sheet.Cell(2, 3).Value = "Conflicting identity";
            workbook.SaveAs(path);
        }

        var result = _reader.Read(path, "NDC");

        Assert.False(result.Success);
        Assert.Equal("ndc_identity_columns_ambiguous", result.Error);
    }

    [Fact]
    public void Read_PopulatedRowWithBlankNdcRequiresManualReview()
    {
        var path = CreateExcel(new[]
        {
            ("NDC", "Name"),
            ("55111-0645-01", "Drug A"),
            ("", "Missing NDC"),
            ("00093-5124-01", "Drug B"),
        });

        var result = _reader.Read(path, "NDC");
        Assert.True(result.Success);
        Assert.Equal(2, result.Rows.Count);
        var invalid = Assert.Single(result.Invalid);
        Assert.Equal(3, invalid.RowIndex);
        Assert.Equal("blank_ndc_on_populated_row", invalid.Reason);
        Assert.Empty(invalid.NdcRaw);
    }

    [Fact]
    public void Read_TrulyBlankRowsAreSkipped()
    {
        var path = CreateExcel(new[]
        {
            ("NDC", "Name"),
            ("55111-0645-01", "Drug A"),
            ("", ""),
            ("00093-5124-01", "Drug B"),
        });

        var result = _reader.Read(path, "NDC");

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.Rows.Count);
        Assert.Empty(result.Invalid);
    }

    [Fact]
    public void Read_MissingFile_ReturnsFail()
    {
        var result = _reader.Read(Path.Combine(_tempDir, "Patient Jane Doe Top500.xlsx"));
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.DoesNotContain("Jane Doe", result.Error);
        Assert.DoesNotContain("Top500.xlsx", result.Error);
    }

    [Fact]
    public void Read_MissingNdcColumn_ReturnsFail()
    {
        var path = CreateExcel(new[]
        {
            ("DrugName", "Qty"),
            ("Omeprazole", "100"),
        });

        var result = _reader.Read(path, "NDC");
        Assert.False(result.Success);
    }

    [Fact]
    public void Read_NdcNormalization_ExpandsBySegment()
    {
        var path = CreateExcel(new[]
        {
            ("NDC", "Name"),
            ("0006-0734-60", "Drug 4-4-2"),       // → prepend '0' to labeler: 00006073460
            ("50242-041-21", "Drug 5-3-2"),       // → pad product: 50242004121
            ("55111-0645-01", "Drug 5-4-2"),      // pass-through: 55111064501
            ("50242004121", "Drug 11-digit"),     // pass-through
        });

        var result = _reader.Read(path, "NDC");
        Assert.True(result.Success);
        Assert.Equal(4, result.Rows.Count);
        Assert.Equal("00006073460", result.Rows[0].NdcNormalized);
        Assert.Equal("50242004121", result.Rows[1].NdcNormalized);
        Assert.Equal("55111064501", result.Rows[2].NdcNormalized);
        Assert.Equal("50242004121", result.Rows[3].NdcNormalized);
    }

    [Fact]
    public void Read_InvalidNdcs_LandInInvalidListNotRows()
    {
        var path = CreateExcel(new[]
        {
            ("NDC", "Name"),
            ("55111-0645-01", "Valid"),
            ("not-an-ndc", "Bad shape"),
            ("5024204121", "Ambiguous 10-digit"),
            ("12-34-56", "Wrong segment lengths"),
        });

        var result = _reader.Read(path, "NDC");
        Assert.True(result.Success);
        Assert.Single(result.Rows);
        Assert.Equal(3, result.Invalid.Count);
        Assert.All(result.Invalid, i => Assert.NotEmpty(i.Reason));
    }

    [Fact]
    public void Read_NadimRealTop500Layout_FindsHeaderPastPreamble()
    {
        // Ground truth: Nadim's actual "top 500 generics jan 1 to may 30.xlsx" PioneerRx export.
        // The data header is row 8, with spacer columns between several fields. Acquisition Cost
        // is a stacked header on row 6 over the same data column, while Total Dispensed and NDC
        // live on row 8. Looking only on the NDC header row silently drops the savings baseline.
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.Worksheets.Add("Sheet1");
            ws.Cell(1, 1).Value = "Top 500 Most Dispensed Rx Items";
            ws.Cell(5, 1).Value = "Dispensed Item Brand/Generic: Generic\nDispensed Item Dea Schedule: No Schedule";
            ws.Cell(6, 17).Value = "Acquisition\nCost";
            ws.Cell(8, 1).Value = "#";
            ws.Cell(8, 3).Value = "Drug";
            ws.Cell(8, 4).Value = "Strength";
            ws.Cell(8, 6).Value = "NDC";
            ws.Cell(8, 7).Value = "Total Dispensed";
            ws.Cell(8, 19).Value = "Price";
            string[,] data =
            {
                { "1", "Drug A", "10 mg", "60505082901", "1523", "0.1625" },
                { "2", "Drug B", "20 mg", "59651000205", "1405", "0.0344" },
                { "3", "Drug C", "40 mg", "60505258008", "1300", "0.0500" },
            };
            for (int i = 0; i < data.GetLength(0); i++)
            {
                var row = 9 + i;
                ws.Cell(row, 1).Value = data[i, 0];
                ws.Cell(row, 3).Value = data[i, 1];
                ws.Cell(row, 4).Value = data[i, 2];
                ws.Cell(row, 6).Value = data[i, 3];
                ws.Cell(row, 7).Value = data[i, 4];
                ws.Cell(row, 17).Value = data[i, 5];
            }
            wb.SaveAs(path);
        }

        var result = _reader.Read(path, "NDC", baselineCostColumnHint: "Acquisition Cost", quantityColumnHint: "Total Dispensed");

        Assert.True(result.Success, result.Error);
        Assert.Equal(6, result.NdcColumnIndex);              // header detected at row 8, NDC is col F
        Assert.Equal(8, result.HeaderRowIndex);
        Assert.Equal(3, result.Rows.Count);
        Assert.Equal("60505082901", result.Rows[0].NdcNormalized);
        Assert.Equal("59651000205", result.Rows[1].NdcNormalized);
        Assert.Equal("60505258008", result.Rows[2].NdcNormalized);
        Assert.Equal(9, result.Rows[0].RowIndex);
        Assert.Null(result.Rows[0].BaselineCostPerUnit);            // aggregate report spend is not per-unit cost
        Assert.Equal(1523m, result.Rows[0].Quantity);                // Total Dispensed mapped
    }

    [Fact]
    public void Read_RepeatedPageHeadersAreNotInvalidNdcs()
    {
        var path = CreateExcel(new[]
        {
            ("NDC", "Drug Name"),
            ("55111-0645-01", "Drug A"),
            ("NDC", "Drug Name"),
            ("00093-5124-01", "Drug B"),
        });

        var result = _reader.Read(path, "NDC");

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.Rows.Count);
        Assert.Empty(result.Invalid);
    }

    [Fact]
    public void Read_NdcTextIsSkippedOnlyForAnExactRepeatedHeader()
    {
        var path = CreateExcel(new[]
        {
            ("NDC", "Drug Name"),
            ("55111-0645-01", "Drug A"),
            ("NDC", "Not the report header"),
        });

        var result = _reader.Read(path, "NDC");

        Assert.True(result.Success, result.Error);
        Assert.Single(result.Rows);
        var invalid = Assert.Single(result.Invalid);
        Assert.Equal(3, invalid.RowIndex);
    }

    [Fact]
    public void Read_AmbiguousCostHintCannotMatchAggregateAcquisitionCost()
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.AddWorksheet("Sheet1");
            sheet.Cell(1, 2).Value = "Acquisition Cost";
            sheet.Cell(2, 1).Value = "NDC";
            sheet.Cell(3, 1).Value = "60505082901";
            sheet.Cell(3, 2).Value = 8297.11m;
            workbook.SaveAs(path);
        }

        var result = _reader.Read(path, "NDC", baselineCostColumnHint: "Cost");

        Assert.True(result.Success, result.Error);
        Assert.Null(Assert.Single(result.Rows).BaselineCostPerUnit);
    }

    private string CreateExcel(IEnumerable<(string col1, string col2)> rows)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.xlsx");
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Sheet1");
        int r = 1;
        foreach (var (a, b) in rows)
        {
            ws.Cell(r, 1).Value = a;
            ws.Cell(r, 2).Value = b;
            r++;
        }
        wb.SaveAs(path);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
