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
        Assert.True(_db.RecordPricingCloudAuthorityHeartbeat(
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            out _));
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
    public async Task RunAsync_ExactGoogleExport_NormalizesLocallyAndPreservesSource()
    {
        var xlsx = GooglePricingWorkbookFixture.Create(_tempDir);
        var originalDigest = System.Security.Cryptography.SHA256.HashData(
            File.ReadAllBytes(xlsx));
        var lookup = new FakeLookup(new Dictionary<string, (string supplier, decimal cost)>
        {
            ["60505082901"] = ("Supplier A", 0.10m),
            ["59651000205"] = ("Supplier B", 0.20m),
        });
        var spec = new PricingJobSpec(
            Guid.NewGuid().ToString("N"), xlsx, "NDC", "Supplier", "Cost (per unit)");

        var progress = await NewRunner(lookup).RunAsync(spec, CancellationToken.None);

        Assert.Equal(PricingJobStatus.Completed, progress.Status);
        Assert.Equal(2, progress.TotalItems);
        Assert.Equal(2, lookup.CallCount);
        Assert.Equal(originalDigest, System.Security.Cryptography.SHA256.HashData(
            File.ReadAllBytes(xlsx)));
        var output = Assert.Single(Directory.GetFiles(
            _tempDir,
            $"{Path.GetFileNameWithoutExtension(xlsx)}-priced-*.xlsx"));
        PricingWorkbookContentPolicy.Validate(output);
        AssertCellEquals(output, "Supplier", 2, "Supplier A");
    }

    [Fact]
    public async Task RunAsync_PreambleWorkbook_WritesHeadersOnDetectedHeaderRow()
    {
        var xlsx = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var sourceSheet = workbook.AddWorksheet("Top 500");
            sourceSheet.Cell(1, 1).Value = "Top 500 Most Dispensed Rx Items";
            sourceSheet.Cell(5, 1).Value = "Drug";
            sourceSheet.Cell(5, 2).Value = "NDC";
            sourceSheet.Cell(6, 1).Value = "Drug A";
            sourceSheet.Cell(6, 2).Value = "60505082901";
            workbook.SaveAs(xlsx);
        }
        var lookup = new FakeLookup(new Dictionary<string, (string supplier, decimal cost)>
        {
            ["60505082901"] = ("Supplier A", 0.10m),
        });
        var spec = new PricingJobSpec(
            Guid.NewGuid().ToString("N"), xlsx, "NDC", "Supplier", "Best Cost");

        var progress = await NewRunner(lookup).RunAsync(spec, CancellationToken.None);

        Assert.Equal(PricingJobStatus.Completed, progress.Status);
        var output = Assert.Single(Directory.GetFiles(
            _tempDir,
            $"{Path.GetFileNameWithoutExtension(xlsx)}-priced-*.xlsx"));
        using var priced = new XLWorkbook(output);
        var sheet = priced.Worksheet(1);
        Assert.Equal("Supplier", sheet.Cell(5, 3).GetString());
        Assert.Equal(PricingJobDefaults.CostColumn, sheet.Cell(5, 4).GetString());
        Assert.Equal("Price Lookup Status", sheet.Cell(5, 5).GetString());
        Assert.NotEqual("Supplier", sheet.Cell(1, 3).GetString());
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

        Assert.Equal(PricingJobStatus.Failed, progress.Status);
        Assert.Equal("pricing_job_failed", progress.HaltReason);
        Assert.Equal(3, progress.TotalItems);
        Assert.Equal(1, progress.CompletedItems); // only the valid row
        Assert.Equal(2, progress.FailedItems);
        Assert.Equal(1, lookup.CallCount);

        // Row-level failure truth must not discard the useful review artifact.
        Assert.Single(Directory.GetFiles(_tempDir, "*-priced-*.xlsx"));

        var all = _db.GetPricingResults(spec.JobId);
        var invalid = all.Where(result => !result.Found).ToArray();
        Assert.Equal(2, invalid.Length);
        Assert.All(invalid, result =>
        {
            Assert.Equal(PricingResultContentPolicy.InvalidNdcStorageValue, result.Ndc);
            Assert.Equal(PricingResultContentPolicy.InvalidNdcReasonCode, result.ErrorMessage);
            Assert.Null(result.SupplierName);
            Assert.Null(result.CostPerUnit);
            Assert.Null(result.Observations);
        });
        var persisted = System.Text.Json.JsonSerializer.Serialize(all);
        Assert.DoesNotContain("bad-ndc", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("5024204121", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_RejectsUnapprovedWorkbookSchemaBeforeLookup()
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.AddWorksheet("Pricing");
            sheet.Cell(1, 1).Value = "NDC";
            sheet.Cell(1, 2).Value = "Patient Name";
            sheet.Cell(2, 1).Value = "55111064501";
            sheet.Cell(2, 2).Value = "Jane Doe";
            workbook.SaveAs(path);
        }
        var lookup = new FakeLookup(new Dictionary<string, (string, decimal)>());
        var spec = new PricingJobSpec(
            Guid.NewGuid().ToString("N"), path, "NDC", "Supplier", "Cost");

        var progress = await NewRunner(lookup).RunAsync(spec, CancellationToken.None);

        Assert.Equal(PricingJobStatus.Failed, progress.Status);
        Assert.Equal("pricing_workbook_validation_failed", progress.HaltReason);
        Assert.Equal(0, lookup.CallCount);
        Assert.Empty(_db.GetPricingResults(spec.JobId));
    }

    [Fact]
    public async Task RunAsync_OversizeRequiredPayloadFailsBeforeAnySqlLookup()
    {
        var ndcs = Enumerable.Range(
                0, PricingResultPayloadBudget.MaximumRequiredRows + 1)
            .Select(index => $"{50000 + index:D5}-{1000 + index:D4}-01")
            .ToArray();
        var xlsx = CreateExcel(ndcs);
        var lookup = new FakeLookup(new Dictionary<string, (string, decimal)>());
        var spec = new PricingJobSpec(
            Guid.NewGuid().ToString("N"), xlsx, "NDC", "Supplier", "Cost");

        var progress = await NewRunner(lookup).RunAsync(spec, CancellationToken.None);

        Assert.Equal(PricingJobStatus.Failed, progress.Status);
        Assert.Equal("pricing_result_payload_too_large", progress.HaltReason);
        Assert.Equal(0, lookup.CallCount);
        Assert.Empty(_db.GetPricingResults(spec.JobId));
    }

    [Fact]
    public async Task RunAsync_InvalidHeavyMetricsFailBeforeAnySqlLookup()
    {
        var values = Enumerable.Range(
                0, PricingResultPayloadBudget.MaximumSerializedMetric + 1)
            .Select(index => $"invalid-{index:D4}")
            .ToArray();
        var xlsx = CreateExcel(values);
        var lookup = new FakeLookup(new Dictionary<string, (string, decimal)>());
        var spec = new PricingJobSpec(
            Guid.NewGuid().ToString("N"), xlsx, "NDC", "Supplier", "Cost");

        var progress = await NewRunner(lookup).RunAsync(spec, CancellationToken.None);

        Assert.Equal(PricingJobStatus.Failed, progress.Status);
        Assert.Equal("pricing_result_payload_too_large", progress.HaltReason);
        Assert.Equal(PricingResultPayloadBudget.MaximumSerializedMetric + 1, progress.TotalItems);
        Assert.Equal(0, lookup.CallCount);
        Assert.Empty(_db.GetPricingResults(spec.JobId));
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
        BindInputIdentity(spec);
        _db.SavePricingResult(new SupplierPriceResult(
            spec.JobId, 2, "55111064501", true, "Prior McKesson", 0.009m, null));

        var runner = NewRunner(lookup);
        await runner.RunAsync(spec, CancellationToken.None);

        Assert.Equal(1, lookup.CallCount); // only the NOT-yet-completed NDC ran
        var all = _db.GetPricingResults(spec.JobId);
        Assert.Contains(all, r => r.SupplierName == "Prior McKesson"); // retained
    }

    [Fact]
    public async Task RunAsync_RejectsChangedWorkbookWhenJobIdAlreadyBound()
    {
        var xlsx = CreateExcel(new[] { "55111-0645-01" });
        var spec = new PricingJobSpec(
            Guid.NewGuid().ToString("N"), xlsx, "NDC", "Supplier", "Cost (per unit)");
        var firstLookup = new FakeLookup(new Dictionary<string, (string, decimal)>
        {
            ["55111064501"] = ("McKesson", 0.01m),
        });
        var first = await NewRunner(firstLookup).RunAsync(spec, CancellationToken.None);
        Assert.Equal(PricingJobStatus.Completed, first.Status);

        using (var workbook = new XLWorkbook(xlsx))
        {
            workbook.Worksheet(1).Cell(2, 1).SetValue("00093-5124-01");
            workbook.Save();
        }
        var secondLookup = new FakeLookup(new Dictionary<string, (string, decimal)>
        {
            ["00093512401"] = ("Anda", 0.02m),
        });

        var second = await NewRunner(secondLookup).RunAsync(spec, CancellationToken.None);

        Assert.Equal(PricingJobStatus.Failed, second.Status);
        Assert.Equal("pricing_observation_contract_conflict", second.HaltReason);
        Assert.Equal(0, secondLookup.CallCount);
    }

    [Fact]
    public async Task RunAsync_RejectsLookupResultWhoseNdcDoesNotMatchRequestedRow()
    {
        var xlsx = CreateExcel(new[] { "55111-0645-01" });
        var spec = new PricingJobSpec(
            Guid.NewGuid().ToString("N"), xlsx, "NDC", "Supplier", "Cost (per unit)");
        var lookup = new DelegateLookup((jobId, rowIndex, _) =>
            new SupplierPriceResult(
                jobId, rowIndex, "00093512401", true, "McKesson", 0.01m, null));

        var progress = await NewRunner(lookup).RunAsync(spec, CancellationToken.None);

        Assert.Equal(PricingJobStatus.Failed, progress.Status);
        Assert.Equal("pricing_result_integrity_failed", progress.HaltReason);
        Assert.Empty(_db.GetPricingResults(spec.JobId));
        Assert.Empty(Directory.GetFiles(
            _tempDir, $"{Path.GetFileNameWithoutExtension(xlsx)}-priced-*.xlsx"));
    }

    [Fact]
    public async Task RunAsync_RejectsOverCapacityUnitCostBeforePersistence()
    {
        var xlsx = CreateExcel(new[] { "55111-0645-01" });
        var spec = new PricingJobSpec(
            Guid.NewGuid().ToString("N"), xlsx, "NDC", "Supplier", "Cost (per unit)");
        var lookup = new DelegateLookup((jobId, rowIndex, ndc) =>
            new SupplierPriceResult(
                jobId,
                rowIndex,
                ndc,
                true,
                "McKesson",
                PricingResultContentPolicy.MaximumUnitCost + 0.0001m,
                null));

        var progress = await NewRunner(lookup).RunAsync(spec, CancellationToken.None);

        Assert.Equal(PricingJobStatus.Failed, progress.Status);
        Assert.Equal("pricing_result_integrity_failed", progress.HaltReason);
        Assert.Empty(_db.GetPricingResults(spec.JobId));
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

    [Fact]
    public async Task SqlFirstExecutor_LookupFactoryUnavailable_FailsClosedWithoutWritingOutput()
    {
        var xlsx = CreateExcel(new[] { "55111-0645-01" });
        var executor = NewExecutor(new FakeLookupFactory(
            null, "pricing schema unavailable", _db));
        var spec = new PricingJobSpec(
            Guid.NewGuid().ToString("N"), xlsx, "NDC", "Supplier", "Cost (per unit)");

        var result = await executor.RunAsync(spec, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("sql", result.Mode);
        Assert.Equal(PricingJobStatus.Failed, result.Progress.Status);
        Assert.Contains("pricing schema unavailable", result.Error);
        Assert.Empty(Directory.GetFiles(_tempDir, "*-priced-*.xlsx"));
    }

    [Fact]
    public async Task SqlFirstExecutor_LookupFactoryAvailable_UsesSqlLookupAndWritesOutput()
    {
        var xlsx = CreateExcel(new[] { "55111-0645-01" });
        var lookup = new FakeLookup(new Dictionary<string, (string, decimal)>
        {
            ["55111064501"] = ("McKesson", 0.0316m),
        });
        var executor = NewExecutor(new FakeLookupFactory(lookup, null, _db));
        var spec = new PricingJobSpec(
            Guid.NewGuid().ToString("N"), xlsx, "NDC", "Supplier", "Cost (per unit)");

        var result = await executor.RunAsync(spec, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("sql", result.Mode);
        Assert.Equal(PricingJobStatus.Completed, result.Progress.Status);
        Assert.Equal(1, lookup.CallCount);
        var output = Assert.Single(Directory.GetFiles(_tempDir, "*-priced-*.xlsx"));
        AssertCellEquals(output, "Supplier", 2, "McKesson");
    }

    [Fact]
    public async Task SqlFirstExecutor_NoMatch_WritesReviewOutputButReturnsFailed()
    {
        var xlsx = CreateExcel(new[] { "55111-0645-01" });
        var lookup = new FakeLookup(new Dictionary<string, (string, decimal)>());
        var executor = NewExecutor(new FakeLookupFactory(lookup, null, _db));
        var spec = new PricingJobSpec(
            Guid.NewGuid().ToString("N"), xlsx, "NDC", "Supplier", "Cost (per unit)");

        var result = await executor.RunAsync(spec, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(PricingJobStatus.Failed, result.Progress.Status);
        Assert.Equal("pricing_job_failed", result.Progress.HaltReason);
        Assert.Equal(0, result.Progress.CompletedItems);
        Assert.Equal(1, result.Progress.FailedItems);
        var output = Assert.Single(Directory.GetFiles(_tempDir, "*-priced-*.xlsx"));
        AssertCellEquals(output, "Price Lookup Status", 2, StatusMarkers.NoSupplierRows);
    }

    [Fact]
    public async Task RunAsync_WithBaselineVolumeProvider_EnrichesFoundResultsForSavings()
    {
        var xlsx = CreateExcel(new[] { "55111-0645-01" });
        var lookup = new FakeLookup(new Dictionary<string, (string, decimal)>
        {
            ["55111064501"] = ("McKesson", 0.0316m),
        });
        var provider = new FakeBaselineVolume(_ => new PharmacyBaselineVolume(0.0500m, 1200m));
        var runner = NewRunner(lookup, provider);
        var spec = new PricingJobSpec(
            Guid.NewGuid().ToString("N"), xlsx, "NDC", "Supplier", "Cost (per unit)");

        await runner.RunAsync(spec, CancellationToken.None);

        var r = Assert.Single(_db.GetPricingResults(spec.JobId));
        Assert.Equal(0.0316m, r.CostPerUnit);            // sourced (cheapest) retained
        Assert.Equal(0.0500m, r.BaselineCostPerUnit);    // baseline enriched
        Assert.Equal(1200m, r.Quantity);                 // quantity enriched
    }

    [Fact]
    public async Task RunAsync_NoProvider_LeavesBaselineAndQuantityNull()
    {
        var xlsx = CreateExcel(new[] { "55111-0645-01" });
        var lookup = new FakeLookup(new Dictionary<string, (string, decimal)>
        {
            ["55111064501"] = ("McKesson", 0.0316m),
        });
        var runner = NewRunner(lookup); // no enrichment provider — today's behavior
        var spec = new PricingJobSpec(
            Guid.NewGuid().ToString("N"), xlsx, "NDC", "Supplier", "Cost (per unit)");

        await runner.RunAsync(spec, CancellationToken.None);

        var r = Assert.Single(_db.GetPricingResults(spec.JobId));
        Assert.Null(r.BaselineCostPerUnit);
        Assert.Null(r.Quantity);
    }

    [Fact]
    public async Task RunAsync_ProviderThrows_FailsSoftAndCompletesWithNullSavings()
    {
        var xlsx = CreateExcel(new[] { "55111-0645-01" });
        var lookup = new FakeLookup(new Dictionary<string, (string, decimal)>
        {
            ["55111064501"] = ("McKesson", 0.0316m),
        });
        var provider = new FakeBaselineVolume(_ => throw new InvalidOperationException("schema not resolved"));
        var runner = NewRunner(lookup, provider);
        var spec = new PricingJobSpec(
            Guid.NewGuid().ToString("N"), xlsx, "NDC", "Supplier", "Cost (per unit)");

        var progress = await runner.RunAsync(spec, CancellationToken.None);

        Assert.Equal(PricingJobStatus.Completed, progress.Status); // run not derailed
        var r = Assert.Single(_db.GetPricingResults(spec.JobId));
        Assert.Equal(0.0316m, r.CostPerUnit);   // cheapest cost retained
        Assert.Null(r.BaselineCostPerUnit);      // fail-soft → null, never a wrong number
        Assert.Null(r.Quantity);
    }

    [Fact]
    public async Task RunAsync_WithExcelBaselineAndQuantityColumns_EnrichesFromWorkbook()
    {
        // The pharmacist's own workbook carries his current cost + volume — the most honest
        // baseline, needing no PMS query or Vision.
        var xlsx = CreateExcelWithBaselineQuantity(
            "55111-0645-01", baseline: 0.0500m, quantity: 1200m,
            baselineHeader: "Current Cost", quantityHeader: "Monthly Qty");
        var lookup = new FakeLookup(new Dictionary<string, (string, decimal)>
        {
            ["55111064501"] = ("McKesson", 0.0316m),
        });
        var runner = NewRunner(lookup, provider: null, baselineHint: "Current Cost", quantityHint: "Monthly Qty");
        var spec = new PricingJobSpec(
            Guid.NewGuid().ToString("N"), xlsx, "NDC", "Supplier", "Cost (per unit)");

        await runner.RunAsync(spec, CancellationToken.None);

        var r = Assert.Single(_db.GetPricingResults(spec.JobId));
        Assert.Equal(0.0316m, r.CostPerUnit);
        Assert.Equal(0.0500m, r.BaselineCostPerUnit);
        Assert.Equal(1200m, r.Quantity);
    }

    [Fact]
    public async Task RunAsync_ExcelBaselineTakesPrecedence_ProviderFillsMissingQuantity()
    {
        // Excel supplies baseline only (quantity blank); the provider fills the quantity gap.
        var xlsx = CreateExcelWithBaselineQuantity(
            "55111-0645-01", baseline: 0.0500m, quantity: null,
            baselineHeader: "Current Cost", quantityHeader: "Monthly Qty");
        var lookup = new FakeLookup(new Dictionary<string, (string, decimal)>
        {
            ["55111064501"] = ("McKesson", 0.0316m),
        });
        var provider = new FakeBaselineVolume(_ => new PharmacyBaselineVolume(0.0999m, 800m));
        var runner = NewRunner(lookup, provider, baselineHint: "Current Cost", quantityHint: "Monthly Qty");
        var spec = new PricingJobSpec(
            Guid.NewGuid().ToString("N"), xlsx, "NDC", "Supplier", "Cost (per unit)");

        await runner.RunAsync(spec, CancellationToken.None);

        var r = Assert.Single(_db.GetPricingResults(spec.JobId));
        Assert.Equal(0.0500m, r.BaselineCostPerUnit); // Excel wins (not the provider's 0.0999)
        Assert.Equal(800m, r.Quantity);               // provider filled the blank
    }

    // Generous caps so realistic test values pass the plausibility guard untouched.
    private static PricingSavingsOptions Savings(string? baselineHint, string? quantityHint) =>
        new(baselineHint, quantityHint, MaxUnitCost: 1_000_000m, MaxQuantity: 100_000_000m, SuspiciousSavingsFraction: 0.9m);

    private SqlPricingJobRunner NewRunner(ISupplierPriceLookup lookup)
    {
        var contract = PricingTestAuthority.Contract();
        var authority = PricingTestAuthority.InstallAuthority(_db, contract);
        return new(
            new ExcelPricingReader(NullLogger<ExcelPricingReader>.Instance),
            new ExcelPricingWriter(NullLogger<ExcelPricingWriter>.Instance),
            _db, lookup, NullLogger<SqlPricingJobRunner>.Instance,
            contract,
            authority,
            baselineVolume: null,
            savings: null,
            trustedApprovalKeys: PricingTestAuthority.TrustedPublicKeys);
    }

    private SqlPricingJobRunner NewRunner(ISupplierPriceLookup lookup, IPharmacyBaselineVolumeProvider? provider) =>
        NewRunner(lookup, provider, null, null);

    private SqlPricingJobRunner NewRunner(
        ISupplierPriceLookup lookup,
        IPharmacyBaselineVolumeProvider? provider,
        string? baselineHint,
        string? quantityHint)
    {
        var contract = PricingTestAuthority.Contract();
        var authority = PricingTestAuthority.InstallAuthority(_db, contract);
        return new(
            new ExcelPricingReader(NullLogger<ExcelPricingReader>.Instance),
            new ExcelPricingWriter(NullLogger<ExcelPricingWriter>.Instance),
            _db,
            lookup,
            NullLogger<SqlPricingJobRunner>.Instance,
            contract,
            authority,
            provider,
            Savings(baselineHint, quantityHint),
            trustedApprovalKeys: PricingTestAuthority.TrustedPublicKeys);
    }

    private string CreateExcelWithBaselineQuantity(
        string ndc, decimal baseline, decimal? quantity, string baselineHeader, string quantityHeader)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.xlsx");
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Sheet1");
        ws.Cell(1, 1).Value = "NDC";
        ws.Cell(1, 2).Value = baselineHeader;
        ws.Cell(1, 3).Value = quantityHeader;
        ws.Cell(2, 1).Value = ndc;
        ws.Cell(2, 2).Value = baseline;
        if (quantity is not null) ws.Cell(2, 3).Value = quantity.Value;
        wb.SaveAs(path);
        return path;
    }

    private SqlFirstPricingJobExecutor NewExecutor(IPricingLookupFactory lookupFactory) =>
        NewExecutor(lookupFactory, new SuavoAgent.Core.Config.AgentOptions());

    private SqlFirstPricingJobExecutor NewExecutor(
        IPricingLookupFactory lookupFactory, SuavoAgent.Core.Config.AgentOptions options) =>
        new(
            new ExcelPricingReader(NullLogger<ExcelPricingReader>.Instance),
            new ExcelPricingWriter(NullLogger<ExcelPricingWriter>.Instance),
            _db,
            lookupFactory,
            NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(options),
            PricingTestAuthority.TrustedPublicKeys);

    [Theory]
    [InlineData(true)]   // per-unit basis confirmed → Excel baseline applied
    [InlineData(false)]  // sourced cost is per-pack → enrichment suppressed (unit-unsafe)
    public async Task SqlFirstExecutor_UnitSafetyGatesExcelSavingsEnrichment(bool unitSafe)
    {
        var xlsx = CreateExcelWithBaselineQuantity(
            "55111-0645-01", baseline: 0.0500m, quantity: 1200m,
            baselineHeader: "Current Cost", quantityHeader: "Monthly Qty");
        var lookup = new FakeLookup(new Dictionary<string, (string, decimal)>
        {
            ["55111064501"] = ("McKesson", 0.0316m),
        });
        var options = new SuavoAgent.Core.Config.AgentOptions
        {
            EnablePricingSavingsEnrichment = true,
            PricingBaselineCostColumn = "Current Cost",
            PricingQuantityColumn = "Monthly Qty",
        };
        var executor = NewExecutor(new FakeLookupFactory(
            lookup, null, _db, savingsUnitSafe: unitSafe), options);
        var spec = new PricingJobSpec(
            Guid.NewGuid().ToString("N"), xlsx, "NDC", "Supplier", "Cost (per unit)");

        await executor.RunAsync(spec, CancellationToken.None);

        var r = Assert.Single(_db.GetPricingResults(spec.JobId));
        if (unitSafe)
        {
            Assert.Equal(0.0500m, r.BaselineCostPerUnit);
            Assert.Equal(1200m, r.Quantity);
        }
        else
        {
            Assert.Null(r.BaselineCostPerUnit); // suppressed — never a unit-mixed savings
            Assert.Null(r.Quantity);
        }
    }

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

    private void BindInputIdentity(PricingJobSpec spec)
    {
        Assert.True(PricingWorkbookContentPolicy.TryPrepareForExecution(
            spec.ExcelPath, out var lease, out var admissionCode), admissionCode);
        using var execution = lease!;
        var read = new ExcelPricingReader(
            NullLogger<ExcelPricingReader>.Instance).Read(
                execution.WorkbookPath, spec.NdcColumn);
        Assert.True(read.Success, read.Error);
        Assert.True(PricingRunIntegrity.TryCreateManifest(read, out var manifest));
        var contract = PricingTestAuthority.Contract();
        var authority = PricingTestAuthority.InstallAuthority(_db, contract);
        Assert.True(_db.TryBindPricingInputIdentity(
            spec.JobId,
            execution.SourceSha256,
            manifest.RowFingerprint,
            contract,
            authority,
            DateTimeOffset.UtcNow,
            out var identityCode), identityCode);
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

    private sealed class DelegateLookup : ISupplierPriceLookup
    {
        private readonly Func<string, int, string, SupplierPriceResult> _result;

        internal DelegateLookup(
            Func<string, int, string, SupplierPriceResult> result) =>
            _result = result;

        public Task<SupplierPriceResult> FindCheapestSupplierAsync(
            string jobId, int rowIndex, string ndc11, CancellationToken ct) =>
            Task.FromResult(_result(jobId, rowIndex, ndc11));
    }

    private sealed class FakeLookupFactory : IPricingLookupFactory
    {
        private readonly ISupplierPriceLookup? _lookup;
        private readonly string? _error;
        private readonly bool _savingsUnitSafe;

        private readonly AgentStateDb _db;

        public FakeLookupFactory(
            ISupplierPriceLookup? lookup,
            string? error,
            AgentStateDb db,
            bool savingsUnitSafe = false)
        {
            _lookup = lookup;
            _error = error;
            _db = db;
            _savingsUnitSafe = savingsUnitSafe;
        }

        public Task<PricingLookupFactoryResult> TryCreateAsync(CancellationToken ct)
        {
            if (_lookup is null)
                return Task.FromResult(PricingLookupFactoryResult.Fail(_error ?? "unavailable"));
            var contract = PricingTestAuthority.Contract();
            var authority = PricingTestAuthority.InstallAuthority(_db, contract);
            return Task.FromResult(PricingLookupFactoryResult.Success(
                _lookup,
                "sql",
                null,
                provider: null,
                savingsUnitSafe: _savingsUnitSafe,
                observationContract: contract,
                authority: authority));
        }
    }

    private sealed class FakeBaselineVolume : IPharmacyBaselineVolumeProvider
    {
        private readonly Func<string, PharmacyBaselineVolume> _fn;
        public FakeBaselineVolume(Func<string, PharmacyBaselineVolume> fn) => _fn = fn;
        public Task<PharmacyBaselineVolume> GetAsync(string ndc11, CancellationToken ct) =>
            Task.FromResult(_fn(ndc11));
    }
}
