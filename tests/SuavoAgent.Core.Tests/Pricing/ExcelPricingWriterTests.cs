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
    public void Write_PackageCost_CreatesExactSixColumnReviewWorkbook()
    {
        var path = CreatePackageExcel();
        var results = new[]
        {
            new SupplierPriceResult(
                "job1", 2, "00093505698", true, "ParMed", null, null,
                PackageCost: 2.6000m,
                CostBasis: PricingApprovalContract.PackageCostBasis),
            new SupplierPriceResult(
                "job1", 3, "55111064501", false, null, null,
                "No eligible package-cost supplier rows",
                CostBasis: PricingApprovalContract.PackageCostBasis),
        };

        var result = _writer.Write(
            path,
            results,
            costBasis: PricingApprovalContract.PackageCostBasis);

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.OkRows);
        Assert.Equal(1, result.FailRows);
        using var workbook = new XLWorkbook(result.OutputPath!);
        var sheet = workbook.Worksheet(1);
        Assert.Equal(
            new[] { "Rank", "Drug", "Strength", "NDC", "Cheapest Supplier", "Cost" },
            Enumerable.Range(1, 6).Select(column => sheet.Cell(1, column).GetString()));
        Assert.Equal(6, sheet.LastColumnUsed()!.ColumnNumber());
        Assert.Equal(1, sheet.Cell(2, 1).GetValue<int>());
        Assert.Equal(2, sheet.Cell(3, 1).GetValue<int>());
        Assert.Equal(XLDataType.Number, sheet.Cell(2, 1).DataType);
        Assert.Equal("00093505698", sheet.Cell(2, 4).GetString());
        Assert.Equal("ParMed", sheet.Cell(2, 5).GetString());
        Assert.Equal(2.6000m, sheet.Cell(2, 6).GetValue<decimal>());
        Assert.Equal("Needs review", sheet.Cell(3, 5).GetString());
        Assert.True(sheet.Cell(3, 6).IsEmpty());
        Assert.True(sheet.AutoFilter.IsEnabled);
        Assert.Equal(XLColor.FromHtml("#1D4ED8"), sheet.Cell(1, 1).Style.Fill.BackgroundColor);
        Assert.Equal(XLColor.White, sheet.Cell(1, 1).Style.Font.FontColor);
        Assert.True(sheet.Cell(1, 1).Style.Font.Bold);
        Assert.Equal(XLColor.FromHtml("#FFF7ED"), sheet.Cell(3, 1).Style.Fill.BackgroundColor);
        Assert.Equal("$0.0000", sheet.Cell(2, 6).Style.NumberFormat.Format);
    }

    [Fact]
    public void Write_PackageCost_PreservesExactSchemaForFiveHundredRows()
    {
        var path = CreatePackageExcel(500);
        var results = Enumerable.Range(0, 500)
            .Select(index => new SupplierPriceResult(
                "job-500",
                index + 2,
                index switch
                {
                    0 => "00093505698",
                    1 => "55111064501",
                    _ => (10_000_000_000L + index).ToString("D11"),
                },
                true,
                "Eligible Supplier",
                null,
                null,
                PackageCost: 3.16m,
                CostBasis: PricingApprovalContract.PackageCostBasis))
            .ToArray();

        var result = _writer.Write(
            path,
            results,
            costBasis: PricingApprovalContract.PackageCostBasis);

        Assert.True(result.Success, result.Error);
        using var workbook = new XLWorkbook(result.OutputPath!);
        var sheet = workbook.Worksheet(1);
        Assert.Equal(501, sheet.LastRowUsed()!.RowNumber());
        Assert.Equal(6, sheet.LastColumnUsed()!.ColumnNumber());
        Assert.Equal(500, sheet.Cell(501, 1).GetValue<int>());
        Assert.Equal(XLDataType.Number, sheet.Cell(501, 1).DataType);
        Assert.Equal(XLDataType.Text, sheet.Cell(501, 4).DataType);
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
        SupplierPriceResult[] firstRows =
        [
            new("job1", 2, "55111064501", true, "McKesson", 0.0316m, null),
        ];
        SupplierPriceResult[] secondRows =
        [
            new("job2", 2, "55111064501", true, "Cardinal", 0.0412m, null),
        ];

        for (var iteration = 0; iteration < 8; iteration++)
        {
            var destination = Path.Combine(
                _tempDir,
                $"fixed-priced-identity-{iteration}.xlsx");
            using var publicationBarrier = new Barrier(participantCount: 2);
            void Rendezvous()
            {
                if (!publicationBarrier.SignalAndWait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Concurrent publication rendezvous timed out.");
            }

            var first = new ExcelPricingWriter(
                NullLogger<ExcelPricingWriter>.Instance,
                _ => destination,
                Rendezvous);
            var second = new ExcelPricingWriter(
                NullLogger<ExcelPricingWriter>.Instance,
                _ => destination,
                Rendezvous);

            var attempts = await Task.WhenAll(
                Task.Run(() => first.Write(source, firstRows)),
                Task.Run(() => second.Write(source, secondRows)));

            Assert.Single(attempts, result => result.Success);
            var rejected = Assert.Single(attempts, result => !result.Success);
            Assert.Equal("pricing_output_collision", rejected.Error);
            var expectedSupplier = attempts[0].Success ? "McKesson" : "Cardinal";
            using var published = new XLWorkbook(destination);
            Assert.Equal(expectedSupplier, published.Worksheet(1).Cell(2, 3).GetString());
        }

        Assert.Equal(sourceBytes, File.ReadAllBytes(source));
        Assert.Empty(Directory.EnumerateFiles(_tempDir, ".suavo-priced-*"));
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

    private string CreatePackageExcel(int rowCount = 2)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.xlsx");
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Top 500");
        var headers = new[] { "Rank", "Drug", "Strength", "NDC", "Report Junk" };
        for (var index = 0; index < headers.Length; index++)
            sheet.Cell(1, index + 1).Value = headers[index];
        for (var index = 0; index < rowCount; index++)
        {
            var row = index + 2;
            sheet.Cell(row, 1).SetValue((index + 1).ToString());
            sheet.Cell(row, 2).Value = index == 1 ? "Omeprazole" : $"Example Drug {index + 1}";
            sheet.Cell(row, 3).Value = index == 1 ? "40 mg" : "10 mg";
            sheet.Cell(row, 4).SetValue(index switch
            {
                0 => "00093505698",
                1 => "55111064501",
                _ => (10_000_000_000L + index).ToString("D11"),
            });
        }
        workbook.SaveAs(path);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
