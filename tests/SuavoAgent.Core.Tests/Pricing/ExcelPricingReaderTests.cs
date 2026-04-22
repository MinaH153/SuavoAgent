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
    public void Read_CaseInsensitiveColumnMatch()
    {
        var path = CreateExcel(new[]
        {
            ("ndc_number", "Item"),
            ("12345-6789-01", "Drug A"), // 5-4-2 real shape
        });

        var result = _reader.Read(path, "ndc");
        Assert.True(result.Success);
        Assert.Single(result.Rows);
        Assert.Equal("12345678901", result.Rows[0].NdcNormalized);
    }

    [Fact]
    public void Read_SkipsEmptyNdcRows()
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
    }

    [Fact]
    public void Read_MissingFile_ReturnsFail()
    {
        var result = _reader.Read("/nonexistent/path/file.xlsx");
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
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
