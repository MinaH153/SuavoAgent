using ClosedXML.Excel;
using Microsoft.Extensions.Logging.Abstractions;
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
        _writer = new ExcelPricingWriter(NullLogger<ExcelPricingWriter>.Instance);
    }

    [Fact]
    public void Write_Sibling_ProducesTimestampedOutputLeavesSourceUntouched()
    {
        var path = CreateExcel();
        var originalBytes = File.ReadAllBytes(path);

        var results = new List<SupplierPriceResult>
        {
            new("job1", 2, "55111064501", true, "McKesson", 0.0316m, null),
            new("job1", 3, "00093512401", true, "Anda", 0.0120m, null),
        };

        var result = _writer.Write(path, results, mode: WriteMode.Sibling);

        Assert.True(result.Success, $"Write failed: {result.Error}");
        Assert.NotNull(result.OutputPath);
        Assert.NotEqual(path, result.OutputPath);
        Assert.Contains("-priced-", result.OutputPath!);
        Assert.True(File.Exists(result.OutputPath!));
        Assert.Equal(originalBytes, File.ReadAllBytes(path));
        Assert.Equal(2, result.OkRows);
        Assert.Equal(0, result.FailRows);

        using var wb = new XLWorkbook(result.OutputPath!);
        var ws = wb.Worksheet(1);
        var headers = GetHeaders(ws);

        Assert.True(headers.ContainsKey(PricingJobDefaults.SupplierColumn));
        Assert.True(headers.ContainsKey(PricingJobDefaults.CostColumn));
        Assert.True(headers.ContainsKey(ExcelPricingWriter.DefaultStatusHeader));

        Assert.Equal("McKesson", ws.Cell(2, headers[PricingJobDefaults.SupplierColumn]).GetString());
        Assert.Equal(0.0316, ws.Cell(2, headers[PricingJobDefaults.CostColumn]).GetDouble(), 4);
        Assert.Equal(StatusMarkers.Ok, ws.Cell(2, headers[ExcelPricingWriter.DefaultStatusHeader]).GetString());
    }

    [Fact]
    public void Write_MarksNotFoundWithExplicitStatus()
    {
        var path = CreateExcel();
        var results = new List<SupplierPriceResult>
        {
            new("job1", 2, "55111064501", true, "McKesson", 0.0316m, null),
            new("job1", 3, "00093512401", false, null, null, "No supplier rows found in Pricing tab"),
        };

        var result = _writer.Write(path, results);
        Assert.True(result.Success);
        Assert.Equal(1, result.OkRows);
        Assert.Equal(1, result.FailRows);

        using var wb = new XLWorkbook(result.OutputPath!);
        var ws = wb.Worksheet(1);
        var headers = GetHeaders(ws);

        Assert.Equal("McKesson", ws.Cell(2, headers[PricingJobDefaults.SupplierColumn]).GetString());
        Assert.Equal("", ws.Cell(3, headers[PricingJobDefaults.SupplierColumn]).GetString());
        Assert.Equal(StatusMarkers.NoSupplierRows, ws.Cell(3, headers[ExcelPricingWriter.DefaultStatusHeader]).GetString());
    }

    [Fact]
    public void Write_GenericErrorSurfacesAsErrorPrefix()
    {
        var path = CreateExcel();
        var results = new List<SupplierPriceResult>
        {
            new("job1", 2, "55111064501", false, null, null, "UIA timeout talking to PioneerRx"),
        };

        var result = _writer.Write(path, results);
        Assert.True(result.Success);

        using var wb = new XLWorkbook(result.OutputPath!);
        var ws = wb.Worksheet(1);
        var headers = GetHeaders(ws);

        Assert.StartsWith("ERROR:", ws.Cell(2, headers[ExcelPricingWriter.DefaultStatusHeader]).GetString());
    }

    [Fact]
    public void Write_UpdatesExistingColumnsNoDuplicates()
    {
        var path = CreateExcel();

        var results = new List<SupplierPriceResult>
        {
            new("job1", 2, "55111064501", true, "Real Value Rx", 0.0316m, null),
        };

        var result = _writer.Write(path, results);
        Assert.True(result.Success);

        using var wb = new XLWorkbook(result.OutputPath!);
        var ws = wb.Worksheet(1);
        var headers = GetHeaders(ws);

        Assert.Equal(1, headers.Keys.Count(k => k == PricingJobDefaults.SupplierColumn));
        Assert.Equal("Real Value Rx", ws.Cell(2, headers[PricingJobDefaults.SupplierColumn]).GetString());
    }

    [Fact]
    public void Write_ExplicitLegacyHeadersStillHonoredForExistingWorkflows()
    {
        var path = CreateExcel(includeLegacySupplierCol: true);

        var result = _writer.Write(
            path,
            [new("job1", 2, "55111064501", true, "McKesson", 0.0316m, null)],
            supplierColumnHeader: PricingJobDefaults.LegacySupplierColumn,
            costColumnHeader: PricingJobDefaults.LegacyCostColumn);

        Assert.True(result.Success);

        using var wb = new XLWorkbook(result.OutputPath!);
        var ws = wb.Worksheet(1);
        var headers = GetHeaders(ws);

        Assert.Equal(1, headers.Keys.Count(k => k == PricingJobDefaults.LegacySupplierColumn));
        Assert.Equal("McKesson", ws.Cell(2, headers[PricingJobDefaults.LegacySupplierColumn]).GetString());
        Assert.Equal(0.0316, ws.Cell(2, headers[PricingJobDefaults.LegacyCostColumn]).GetDouble(), 4);
    }

    [Fact]
    public void Write_AmbiguousLegacyCostHeaderIsUpgradedToPerUnit()
    {
        var path = CreateExcel();

        var result = _writer.Write(
            path,
            [new("job1", 2, "55111064501", true, "McKesson", 0.0316m, null)],
            costColumnHeader: PricingJobDefaults.AmbiguousLegacyCostColumn);

        Assert.True(result.Success, result.Error);
        using var workbook = new XLWorkbook(result.OutputPath!);
        var headers = GetHeaders(workbook.Worksheet(1));
        Assert.Contains(PricingJobDefaults.CostColumn, headers.Keys);
        Assert.DoesNotContain(PricingJobDefaults.AmbiguousLegacyCostColumn, headers.Keys);
        Assert.Equal(
            0.0316,
            workbook.Worksheet(1).Cell(2, headers[PricingJobDefaults.CostColumn]).GetDouble(),
            4);
    }

    [Fact]
    public void Write_MissingFile_Fails()
    {
        var result = _writer.Write(Path.Combine(_tempDir, "Patient Jane Doe Top500.xlsx"), []);
        Assert.False(result.Success);
        Assert.DoesNotContain("Jane Doe", result.Error ?? "");
        Assert.DoesNotContain("Top500.xlsx", result.Error ?? "");
    }

    [Fact]
    public void Write_InPlace_RefusesWhenLocked()
    {
        var path = CreateExcel();

        using var fs = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = _writer.Write(
            path,
            [new("job1", 2, "55111064501", true, "McKesson", 0.1m, null)],
            mode: WriteMode.InPlace);

        Assert.False(result.Success);
        Assert.Contains("locked", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Write_InPlace_OverwritesSourceWhenUnlocked()
    {
        var path = CreateExcel();
        var result = _writer.Write(
            path,
            [new("job1", 2, "55111064501", true, "McKesson", 0.1m, null)],
            mode: WriteMode.InPlace);

        Assert.True(result.Success);
        Assert.Equal(path, result.OutputPath);

        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheet(1);
        var headers = GetHeaders(ws);
        Assert.Equal("McKesson", ws.Cell(2, headers[PricingJobDefaults.SupplierColumn]).GetString());
    }

    [Fact]
    public async Task Write_Sibling_ConcurrentIdentityCollision_PublishesExactlyOnceWithoutOverwrite()
    {
        var source = CreateExcel();
        var sourceBytes = File.ReadAllBytes(source);
        var destination = Path.Combine(_tempDir, "fixed-priced-identity.xlsx");
        var first = new ExcelPricingWriter(
            NullLogger<ExcelPricingWriter>.Instance,
            _ => destination);
        var second = new ExcelPricingWriter(
            NullLogger<ExcelPricingWriter>.Instance,
            _ => destination);
        SupplierPriceResult[] rows =
        [
            new("job1", 2, "55111064501", true, "McKesson", 0.0316m, null),
        ];

        var attempts = await Task.WhenAll(
            Task.Run(() => first.Write(source, rows)),
            Task.Run(() => second.Write(source, rows)));

        Assert.Single(attempts, result => result.Success);
        var rejected = Assert.Single(attempts, result => !result.Success);
        Assert.Equal("pricing_output_collision", rejected.Error);
        Assert.Equal(sourceBytes, File.ReadAllBytes(source));
        using var published = new XLWorkbook(destination);
        Assert.Equal("McKesson", published.Worksheet(1).Cell(2, 3).GetString());
    }

    private static Dictionary<string, int> GetHeaders(IXLWorksheet ws)
    {
        var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
        var result = new Dictionary<string, int>();
        for (int c = 1; c <= lastCol; c++)
        {
            var h = ws.Cell(1, c).GetString();
            if (!string.IsNullOrEmpty(h)) result[h] = c;
        }
        return result;
    }

    private string CreateExcel(bool includeLegacySupplierCol = false)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.xlsx");
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Sheet1");
        ws.Cell(1, 1).Value = "NDC";
        ws.Cell(1, 2).Value = "Drug Name";
        if (includeLegacySupplierCol)
        {
            ws.Cell(1, 3).Value = PricingJobDefaults.LegacySupplierColumn;
            ws.Cell(1, 4).Value = PricingJobDefaults.LegacyCostColumn;
        }
        else
        {
            ws.Cell(1, 3).Value = PricingJobDefaults.SupplierColumn;
            ws.Cell(1, 4).Value = PricingJobDefaults.CostColumn;
        }
        ws.Cell(2, 1).Value = "55111-0645-01";
        ws.Cell(2, 2).Value = "Omeprazole DR 40mg";
        ws.Cell(3, 1).Value = "00093-5124-01";
        ws.Cell(3, 2).Value = "Metformin 500mg";
        wb.SaveAs(path);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
