using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

/// <summary>
/// M2a selector-observation capture: the GREEN-tier learning signal must round-trip through
/// SQLite and reach the cloud payload carrying ONLY structural atoms (ControlType / AutomationId
/// / ClassName) + enums — never an element Name/Value, a free-text error, an NDC-bearing string,
/// or anything PHI. The uploader payload is the compliance gate.
/// </summary>
public class SelectorObservationCaptureTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"suavo_m2a_{Guid.NewGuid():N}");
    private readonly AgentStateDb _db;

    public SelectorObservationCaptureTests()
    {
        Directory.CreateDirectory(_tempDir);
        _db = new AgentStateDb(Path.Combine(_tempDir, "state.db"));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    // pricing_results has a FK to pricing_jobs(job_id) — seed the parent row first.
    private void SeedJob(string jobId) =>
        _db.UpsertPricingJob(
            new PricingJobSpec(jobId, "C:\\book.xlsx", "NDC", "Best Supplier", "Best Cost"),
            PricingJobStatus.Running, 0, 0, 0);

    [Fact]
    public void Observations_RoundTripThroughSqlite()
    {
        var observations = new List<SelectorObservation>
        {
            new(SelectorStepId.QuickSearchField, SelectorResolvedVia.Builtin, SelectorOutcome.Failed,
                SelectorFailureKind.ElementNotFound, null,
                new List<ObservedElement> { new("Edit", "ndcSearchBox", "PioneerRxEdit") }),
        };

        SeedJob("job1");
        _db.SavePricingResult(new SupplierPriceResult(
            "job1", 0, "00071015523", false, null, null, "not found", observations));

        var got = _db.GetPricingResults("job1");
        Assert.Single(got);
        Assert.NotNull(got[0].Observations);
        var o = Assert.Single(got[0].Observations!);
        Assert.Equal(SelectorStepId.QuickSearchField, o.StepId);
        Assert.Equal(SelectorOutcome.Failed, o.Outcome);
        Assert.Equal(SelectorFailureKind.ElementNotFound, o.FailureKind);
        var cand = Assert.Single(o.ObservedCandidates);
        Assert.Equal("Edit", cand.ControlType);
        Assert.Equal("ndcSearchBox", cand.AutomationId);
        Assert.Equal("PioneerRxEdit", cand.ClassName);
    }

    [Fact]
    public void NoObservations_PersistsAsNull()
    {
        // SQL path drives no selectors; Observations defaults null and must stay null.
        SeedJob("job2");
        _db.SavePricingResult(new SupplierPriceResult("job2", 0, "123", true, "Acme", 1.5m, null));
        var got = _db.GetPricingResults("job2");
        Assert.Null(Assert.Single(got).Observations);
    }

    [Fact]
    public async Task UploadPayload_CarriesGreenTierObservations_AndNoPhi()
    {
        var signer = new CapturingPostSigner();
        var uploader = new PricingJobCloudUploader(signer, _db, NullLogger<PricingJobCloudUploader>.Instance);

        // A failed row whose (pre-existing) error string contains an email — must be scrubbed —
        // plus a GREEN-tier observation that must survive intact.
        SeedJob("job3");
        _db.SavePricingResult(new SupplierPriceResult(
            "job3", 0, "00071015523", false, null, null,
            "could not reach patient jane.doe@example.com",
            new List<SelectorObservation>
            {
                new(SelectorStepId.PricingTab, SelectorResolvedVia.Builtin, SelectorOutcome.Failed,
                    SelectorFailureKind.ElementNotFound, null,
                    new List<ObservedElement> { new("TabItem", null, "WpfTabItem") }),
            }));

        var spec = new PricingJobSpec("job3", "C:\\book.xlsx", "NDC", "Best Supplier", "Best Cost");
        var execution = new PricingJobExecutionResult(
            new PricingJobProgress("job3", 1, 0, 1, PricingJobStatus.Completed), "uia", true, null);

        await uploader.UploadAsync(spec, execution, "cmd1", default);

        Assert.NotNull(signer.LastPayload);
        Assert.Equal("/api/agent/pricing-jobs/job3/results", signer.LastPath);
        var json = JsonSerializer.Serialize(signer.LastPayload);

        // GREEN-tier observation reached the payload intact.
        Assert.Contains("selectorObservations", json);
        Assert.Contains("PricingTab", json);
        Assert.Contains("ElementNotFound", json);
        Assert.Contains("TabItem", json);
        Assert.Contains("WpfTabItem", json);

        // Compliance gate: PHI never reaches the cloud.
        Assert.DoesNotContain("jane.doe@example.com", json);
        Assert.Contains("pricing_lookup_failed", json); // the email-bearing warning was suppressed
        // The observation object exposes only ControlType/AutomationId/ClassName — no element name.
        Assert.DoesNotContain("\"name\"", json.ToLowerInvariant());
    }

    private sealed class CapturingPostSigner : IPostSigner
    {
        public object? LastPayload { get; private set; }
        public string? LastPath { get; private set; }

        public Task<JsonElement?> PostSignedAsync(string path, object payload, CancellationToken ct)
        {
            LastPath = path;
            LastPayload = payload;
            return Task.FromResult<JsonElement?>(null);
        }

        public Task<JsonElement?> PostSignedVerifiedAsync(
            string path, object payload, string publicKeyDer, CancellationToken ct) =>
            Task.FromResult<JsonElement?>(null);
    }
}
