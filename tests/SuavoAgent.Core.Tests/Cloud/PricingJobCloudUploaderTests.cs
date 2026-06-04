using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public sealed class PricingJobCloudUploaderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"suavo_pricing_upload_{Guid.NewGuid():N}");
    private readonly AgentStateDb _db;

    public PricingJobCloudUploaderTests()
    {
        Directory.CreateDirectory(_tempDir);
        _db = new AgentStateDb(Path.Combine(_tempDir, "state.db"));
    }

    [Fact]
    public async Task UploadAsync_PostsJobResultsWithoutWorkbookPath()
    {
        var spec = new PricingJobSpec(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            @"C:\Users\queen\Desktop\Top500.xlsx",
            "NDC",
            "Supplier",
            "Cost (per unit)");
        _db.UpsertPricingJob(spec, PricingJobStatus.Completed, 1, 1, 0);
        _db.SavePricingResult(new SupplierPriceResult(
            spec.JobId,
            2,
            "55111064501",
            true,
            "McKesson",
            0.0316m,
            null));
        var signer = new RecordingPostSigner();
        var uploader = new PricingJobCloudUploader(
            signer,
            _db,
            NullLogger<PricingJobCloudUploader>.Instance);

        await uploader.UploadAsync(
            spec,
            new PricingJobExecutionResult(
                new PricingJobProgress(spec.JobId, 1, 1, 0, PricingJobStatus.Completed),
                "sql",
                true,
                null),
            "11111111-1111-1111-1111-111111111111",
            CancellationToken.None);

        Assert.Equal($"/api/agent/pricing-jobs/{spec.JobId}/results", signer.Path);
        var json = JsonSerializer.Serialize(signer.Payload);
        Assert.Contains("55111064501", json);
        Assert.Contains("McKesson", json);
        Assert.DoesNotContain("Top500.xlsx", json);
        Assert.DoesNotContain("Desktop", json);
        Assert.DoesNotContain("ExcelPath", json);
    }

    [Fact]
    public async Task UploadAsync_SuppressesSensitivePricingWarnings()
    {
        var spec = new PricingJobSpec(
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            @"C:\Users\queen\Desktop\Top500.xlsx",
            "NDC",
            "Supplier",
            "Cost (per unit)");
        _db.UpsertPricingJob(spec, PricingJobStatus.Completed, 1, 0, 1);
        _db.SavePricingResult(new SupplierPriceResult(
            spec.JobId,
            7,
            "55111064501",
            false,
            null,
            null,
            @"Patient Jane Doe phone 555-123-4567 failed from C:\Users\queen\Desktop\Top500.xlsx"));
        var signer = new RecordingPostSigner();
        var uploader = new PricingJobCloudUploader(
            signer,
            _db,
            NullLogger<PricingJobCloudUploader>.Instance);

        await uploader.UploadAsync(
            spec,
            new PricingJobExecutionResult(
                new PricingJobProgress(spec.JobId, 1, 0, 1, PricingJobStatus.Completed),
                "sql",
                true,
                null),
            "22222222-2222-2222-2222-222222222222",
            CancellationToken.None);

        var json = JsonSerializer.Serialize(signer.Payload);
        Assert.Contains("pricing_lookup_failed", json);
        Assert.DoesNotContain("Jane Doe", json);
        Assert.DoesNotContain("555-123-4567", json);
        Assert.DoesNotContain("Top500.xlsx", json);
        Assert.DoesNotContain("Desktop", json);
    }

    [Fact]
    public async Task UploadAsync_NormalizesPricingSourceForCloudContract()
    {
        var spec = new PricingJobSpec(
            "cccccccccccccccccccccccccccccccc",
            @"C:\Users\queen\Desktop\Top500.xlsx",
            "NDC",
            "Supplier",
            "Cost (per unit)");
        _db.UpsertPricingJob(spec, PricingJobStatus.Completed, 1, 1, 0);
        _db.SavePricingResult(new SupplierPriceResult(
            spec.JobId,
            3,
            "55111064501",
            true,
            "Cardinal",
            0.042m,
            null));
        var signer = new RecordingPostSigner();
        var uploader = new PricingJobCloudUploader(
            signer,
            _db,
            NullLogger<PricingJobCloudUploader>.Instance);

        await uploader.UploadAsync(
            spec,
            new PricingJobExecutionResult(
                new PricingJobProgress(spec.JobId, 1, 1, 0, PricingJobStatus.Completed),
                "SqlFirst",
                true,
                null),
            "33333333-3333-3333-3333-333333333333",
            CancellationToken.None);

        var json = JsonSerializer.Serialize(signer.Payload);
        Assert.Contains("\"mode\":\"sql\"", json);
        Assert.Contains("\"source\":\"sql\"", json);
        Assert.DoesNotContain("SqlFirst", json);
    }

    [Fact]
    public async Task UploadAsync_CarriesBaselineCostAndQuantity_ForSavingsComputation()
    {
        // M1: the cloud computes savings_total = (baselineCostPerUnit - costPerUnit) * quantity,
        // only when all three are present + found. Pin the EXACT field names the route reads.
        var spec = new PricingJobSpec(
            "dddddddddddddddddddddddddddddddd",
            @"C:\Users\queen\Desktop\Top500.xlsx",
            "NDC",
            "Supplier",
            "Cost (per unit)");
        _db.UpsertPricingJob(spec, PricingJobStatus.Completed, 1, 1, 0);
        _db.SavePricingResult(new SupplierPriceResult(
            spec.JobId,
            4,
            "55111064501",
            Found: true,
            SupplierName: "McKesson",
            CostPerUnit: 0.0316m,
            ErrorMessage: null,
            Observations: null,
            BaselineCostPerUnit: 0.0500m,
            Quantity: 1200m));
        var signer = new RecordingPostSigner();
        var uploader = new PricingJobCloudUploader(signer, _db, NullLogger<PricingJobCloudUploader>.Instance);

        await uploader.UploadAsync(
            spec,
            new PricingJobExecutionResult(
                new PricingJobProgress(spec.JobId, 1, 1, 0, PricingJobStatus.Completed),
                "sql",
                true,
                null),
            "44444444-4444-4444-4444-444444444444",
            CancellationToken.None);

        var item = JsonSerializer.SerializeToElement(signer.Payload)
            .GetProperty("items")[0];
        Assert.Equal(0.0500m, item.GetProperty("baselineCostPerUnit").GetDecimal());
        Assert.Equal(1200m, item.GetProperty("quantity").GetDecimal());
        Assert.Equal(0.0316m, item.GetProperty("costPerUnit").GetDecimal());
    }

    [Fact]
    public async Task UploadAsync_CostOnlyRun_LeavesBaselineAndQuantityNull()
    {
        // A run that captured only the cheapest cost (no baseline/quantity) must upload cleanly
        // with both null — the cloud then stores savings_total = NULL (never a wrong number).
        var spec = new PricingJobSpec(
            "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
            @"C:\Users\queen\Desktop\Top500.xlsx",
            "NDC",
            "Supplier",
            "Cost (per unit)");
        _db.UpsertPricingJob(spec, PricingJobStatus.Completed, 1, 1, 0);
        _db.SavePricingResult(new SupplierPriceResult(
            spec.JobId, 5, "55111064501", true, "Cardinal", 0.042m, null));
        var signer = new RecordingPostSigner();
        var uploader = new PricingJobCloudUploader(signer, _db, NullLogger<PricingJobCloudUploader>.Instance);

        await uploader.UploadAsync(
            spec,
            new PricingJobExecutionResult(
                new PricingJobProgress(spec.JobId, 1, 1, 0, PricingJobStatus.Completed),
                "sql",
                true,
                null),
            "55555555-5555-5555-5555-555555555555",
            CancellationToken.None);

        var item = JsonSerializer.SerializeToElement(signer.Payload)
            .GetProperty("items")[0];
        Assert.Equal(JsonValueKind.Null, item.GetProperty("baselineCostPerUnit").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("quantity").ValueKind);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private sealed class RecordingPostSigner : IPostSigner
    {
        public string? Path { get; private set; }
        public object? Payload { get; private set; }

        public Task<JsonElement?> PostSignedAsync(string path, object payload, CancellationToken ct)
        {
            Path = path;
            Payload = payload;
            return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new { success = true }));
        }

        public Task<JsonElement?> PostSignedVerifiedAsync(
            string path,
            object payload,
            string publicKeyDer,
            CancellationToken ct) =>
            PostSignedAsync(path, payload, ct);
    }
}
