using Microsoft.Extensions.Logging.Abstractions;
using OfficeOpenXml;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Pricing;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

public class ExcelPricingWriterTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"suavo_price_write_{Guid.NewGuid():N}");
    private readonly ExcelPricingWriter _writer;

    public ExcelPricingWriterTests()
    {
        Directory.CreateDirectory(_tempDir);
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        _writer = new ExcelPricingWriter(NullLogger<ExcelPricingWriter>.Instance);
    }

    [Fact]
    public void Write_CreatesSupplierAndCostColumns()
    {
        var path = CreateExcel();

        var results = new List<SupplierPriceResult>
        {
            new("job1", 2, "55111064501", true, "McKesson", 0.0316m, null),
            new("job1", 3, "00093512401", true, "Anda", 0.0120m, null),
        };

        var ok = _writer.Write(path, results);

        Assert.True(ok);

        using var pkg = new ExcelPackage(new FileInfo(path));
        var ws = pkg.Workbook.Worksheets[0];

        // Find Supplier and Cost columns
        var headers = Enumerable.Range(1, ws.Dimension.End.Column)
            .ToDictionary(c => ws.Cells[1, c].Text, c => c);

        Assert.True(headers.ContainsKey("Supplier"));
        Assert.True(headers.ContainsKey("Cost (per unit)"));

        var supplierCol = headers["Supplier"];
        var costCol = headers["Cost (per unit)"];

        Assert.Equal("McKesson", ws.Cells[2, supplierCol].Text);
        Assert.Equal("Anda", ws.Cells[3, supplierCol].Text);
        Assert.Equal(0.0316m, (decimal)(double)ws.Cells[2, costCol].Value);
    }

    [Fact]
    public void Write_SkipsNotFoundRows()
    {
        var path = CreateExcel();

        var results = new List<SupplierPriceResult>
        {
            new("job1", 2, "55111064501", true, "McKesson", 0.0316m, null),
            new("job1", 3, "00093512401", false, null, null, "Not found"),
        };

        var ok = _writer.Write(path, results);
        Assert.True(ok);

        using var pkg = new ExcelPackage(new FileInfo(path));
        var ws = pkg.Workbook.Worksheets[0];
        var headers = Enumerable.Range(1, ws.Dimension.End.Column)
            .ToDictionary(c => ws.Cells[1, c].Text, c => c);
        var supplierCol = headers["Supplier"];

        Assert.Equal("McKesson", ws.Cells[2, supplierCol].Text);
        Assert.Equal("", ws.Cells[3, supplierCol].Text);
    }

    [Fact]
    public void Write_UpdatesExistingColumns()
    {
        // Pre-populate Supplier column in the file
        var path = CreateExcel(includeSupplierCol: true);

        var results = new List<SupplierPriceResult>
        {
            new("job1", 2, "55111064501", true, "Real Value Rx", 0.0316m, null),
        };

        var ok = _writer.Write(path, results, "Supplier", "Cost (per unit)");
        Assert.True(ok);

        using var pkg = new ExcelPackage(new FileInfo(path));
        var ws = pkg.Workbook.Worksheets[0];
        var headers = Enumerable.Range(1, ws.Dimension.End.Column)
            .ToDictionary(c => ws.Cells[1, c].Text, c => c);

        // Should not create duplicate column
        Assert.Equal(1, headers.Keys.Count(k => k == "Supplier"));
        Assert.Equal("Real Value Rx", ws.Cells[2, headers["Supplier"]].Text);
    }

    [Fact]
    public void Write_MissingFile_ReturnsFalse()
    {
        var ok = _writer.Write("/nonexistent/path.xlsx", []);
        Assert.False(ok);
    }

    private string CreateExcel(bool includeSupplierCol = false)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.xlsx");
        using var pkg = new ExcelPackage();
        var ws = pkg.Workbook.Worksheets.Add("Sheet1");
        ws.Cells[1, 1].Value = "NDC";
        ws.Cells[1, 2].Value = "Drug Name";
        if (includeSupplierCol)
        {
            ws.Cells[1, 3].Value = "Supplier";
            ws.Cells[1, 4].Value = "Cost (per unit)";
        }
        ws.Cells[2, 1].Value = "55111-0645-01";
        ws.Cells[2, 2].Value = "Omeprazole DR 40mg";
        ws.Cells[3, 1].Value = "00093-5124-01";
        ws.Cells[3, 2].Value = "Metformin 500mg";
        pkg.SaveAs(new FileInfo(path));
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
