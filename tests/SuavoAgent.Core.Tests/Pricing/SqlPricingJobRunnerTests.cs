using ClosedXML.Excel;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

public class SqlPricingJobRunnerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"suavo_sql_runner_{Guid.NewGuid():N}");
    private readonly AgentStateDb _db;

    public SqlPricingJobRunnerTests()
    {
        Directory.CreateDirectory(_tempDir);
        _db = new AgentStateDb(Path.Combine(_tempDir, "state.db"));
    }

    [Fact]
    public async Task RunAsync_HappyPath_WritesSiblingFileWithAllResults()
    {
        var xlsx = CreateExcel(new[]
        {
            "55111-0645-01",
            "00093-5124-01",
            "50242-0041-21",
        });

        var lookup = new FakeLookup(new Dictionary<string, (string supplier, decimal cost)>
        {
            ["55111064501"] = ("McKesson", 0.0316m),
            ["00093512401"] = ("Anda", 0.0120m),
            ["50242004121"] = ("Real Value Rx", 0.0180m),
        });

        var runner = NewRunner(lookup);
        var spec = new PricingJobSpec(
            JobId: Guid.NewGuid().ToString("N"),
            ExcelPath: xlsx,
            NdcColumn: "NDC",
            SupplierColumn: "Supplier",
            CostColumn: "Cost (per unit)");

        var progress = await runner.RunAsync(spec, CancellationToken.None);

        Assert.Equal(PricingJobStatus.Completed, progress.Status);
        Assert.Equal(3, progress.TotalItems);
        Assert.Equal(3, progress.CompletedItems);
        Assert.Equal(0, progress.FailedItems);

        var outputs = Directory.GetFiles(_tempDir, "*-priced-*.xlsx");
        Assert.Single(outputs);
        AssertCellEquals(outputs[0], "Supplier", 2, "McKesson");
        AssertCellEquals(outputs[0], "Price Lookup Status", 2, StatusMarkers.Ok);
    }

    [Fact]
    public async Task RunAsync_RecordsInvalidNdcsAsFailedRowsAndSkipsLookup()
    {
        var xlsx = CreateExcel(new[] { "55111-0645-01", "bad-ndc", "5024204121" });

        var lookup = new FakeLookup(new Dictionary<string, (string, decimal)>
        {
            ["55111064501"] = ("McKesson", 0.01m),
        });

        var runner = NewRunner(lookup);
        var spec = new PricingJobSpec(
            Guid.NewGuid().ToString("N"), xlsx, "NDC", "Supplier", "Cost (per unit)");

        var progress = await runner.RunAsync(spec, CancellationToken.None);

        Assert.Equal(PricingJobStatus.Completed, progress.Status);
        Assert.Equal(1, progress.CompletedItems); // only the valid row
        Assert.Equal(1, lookup.CallCount);

        var all = _db.GetPricingResults(spec.JobId);
        Assert.Contains(all, r => !r.Found && r.ErrorMessage != null && r.ErrorMessage.Contains("Invalid NDC"));
    }

    [Fact]
    public async Task RunAsync_IsCrashResumable_SkipsRowsAlreadyPersisted()
    {
        var xlsx = CreateExcel(new[] { "55111-0645-01", "00093-5124-01" });
        var lookup = new FakeLookup(new Dictionary<string, (string, decimal)>
        {
            ["55111064501"] = ("McKesson", 0.01m),
            ["00093512401"] = ("Anda", 0.02m),
        });

        var spec = new PricingJobSpec(
            Guid.NewGuid().ToString("N"), xlsx, "NDC", "Supplier", "Cost (per unit)");

        // Seed the parent pricing_jobs row (FK target) then pre-seed row 2 as already completed,
        // simulating a prior crash after row 2 succeeded.
        _db.UpsertPricingJob(spec, PricingJobStatus.Running, 2, 1, 0);
        _db.SavePricingResult(new SupplierPriceResult(
            spec.JobId, 2, "55111064501", true, "Prior McKesson", 0.009m, null));

        var runner = NewRunner(lookup);
        await runner.RunAsync(spec, CancellationToken.None);

        Assert.Equal(1, lookup.CallCount); // only the NOT-yet-completed NDC ran
        var all = _db.GetPricingResults(spec.JobId);
        Assert.Contains(all, r => r.SupplierName == "Prior McKesson"); // retained
    }

    [Fact]
    public async Task RunAsync_ExcelMissing_FailsGracefully()
    {
        var runner = NewRunner(new FakeLookup(new Dictionary<string, (string, decimal)>()));
        var spec = new PricingJobSpec(
            Guid.NewGuid().ToString("N"),
            ExcelPath: Path.Combine(_tempDir, "does-not-exist.xlsx"),
            NdcColumn: "NDC",
            SupplierColumn: "Supplier",
            CostColumn: "Cost");

        var progress = await runner.RunAsync(spec, CancellationToken.None);
        Assert.Equal(PricingJobStatus.Failed, progress.Status);
    }

    private SqlPricingJobRunner NewRunner(ISupplierPriceLookup lookup) =>
        new(
            new ExcelPricingReader(NullLogger<ExcelPricingReader>.Instance),
            new ExcelPricingWriter(NullLogger<ExcelPricingWriter>.Instance),
            _db,
            lookup,
            NullLogger<SqlPricingJobRunner>.Instance);

    private string CreateExcel(IReadOnlyList<string> ndcs)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.xlsx");
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Sheet1");
        ws.Cell(1, 1).Value = "NDC";
        ws.Cell(1, 2).Value = "Drug Name";
        for (int i = 0; i < ndcs.Count; i++)
        {
            ws.Cell(i + 2, 1).Value = ndcs[i];
            ws.Cell(i + 2, 2).Value = $"Drug {i}";
        }
        wb.SaveAs(path);
        return path;
    }

    private static void AssertCellEquals(string path, string headerName, int row, string expected)
    {
        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheet(1);
        var lastCol = ws.LastColumnUsed()!.ColumnNumber();
        for (int c = 1; c <= lastCol; c++)
        {
            if (string.Equals(ws.Cell(1, c).GetString().Trim(), headerName, StringComparison.OrdinalIgnoreCase))
            {
                Assert.Equal(expected, ws.Cell(row, c).GetString());
                return;
            }
        }
        throw new Xunit.Sdk.XunitException($"Header '{headerName}' not found");
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private sealed class FakeLookup : ISupplierPriceLookup
    {
        private readonly IReadOnlyDictionary<string, (string Supplier, decimal Cost)> _data;
        public int CallCount { get; private set; }

        public FakeLookup(IReadOnlyDictionary<string, (string, decimal)> data) => _data = data;

        public Task<SupplierPriceResult> FindCheapestSupplierAsync(
            string jobId, int rowIndex, string ndc11, CancellationToken ct)
        {
            CallCount++;
            if (_data.TryGetValue(ndc11, out var hit))
            {
                return Task.FromResult(new SupplierPriceResult(
                    jobId, rowIndex, ndc11, true, hit.Supplier, hit.Cost, null));
            }
            return Task.FromResult(new SupplierPriceResult(
                jobId, rowIndex, ndc11, false, null, null, "No supplier rows found"));
        }
    }
}
