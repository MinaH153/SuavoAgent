using Microsoft.Extensions.Logging.Abstractions;
using OfficeOpenXml;
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
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
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
            ("12345-678-90", "Drug A"),
        });

        var result = _reader.Read(path, "ndc");
        Assert.True(result.Success);
        Assert.Single(result.Rows);
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
    public void Read_NdcNormalization_StripsDashesAndPads()
    {
        var path = CreateExcel(new[]
        {
            ("NDC", "Name"),
            ("1234-567-89", "Drug A"),    // 10 chars → pad to 11
            ("55111-0645-01", "Drug B"),  // already 11 after strip
        });

        var result = _reader.Read(path, "NDC");
        Assert.True(result.Success);
        Assert.Equal("00123456789", result.Rows[0].NdcNormalized); // "1234-567-89" → strip → "123456789" (9) → pad → "00123456789"
        Assert.Equal("55111064501", result.Rows[1].NdcNormalized);
    }

    private string CreateExcel(IEnumerable<(string col1, string col2)> rows)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.xlsx");
        using var pkg = new ExcelPackage();
        var ws = pkg.Workbook.Worksheets.Add("Sheet1");
        int r = 1;
        foreach (var (a, b) in rows)
        {
            ws.Cells[r, 1].Value = a;
            ws.Cells[r, 2].Value = b;
            r++;
        }
        pkg.SaveAs(new FileInfo(path));
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
