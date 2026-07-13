using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Config;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public sealed partial class PricingJobCloudUploaderTests
{
    public static IEnumerable<object[]> ApprovedSupplierCatalog() =>
        new[]
        {
            "Cardinal Health", "Real Value Rx", "McKesson",
            "McKesson Pharmaceutical", "Cencora", "Morris & Dickson", "Anda",
            "Smith Drug Company", "Rochester Drug Cooperative", "Dakota Drug",
            "Value Drug Company", "Masters Pharmaceutical", "KeySource",
        }.Select(value => new object[] { value });

    [Theory]
    [MemberData(nameof(ApprovedSupplierCatalog))]
    public void BuildPersistedPayload_PreservesApprovedSupplierCatalogNames(
        string supplierName)
    {
        var payload = PricingJobCloudUploader.BuildPersistedPayloadEnvelope(
            "approved-supplier-job",
            "approved-supplier-command",
            PricingJobStatus.Completed,
            "sql",
            1,
            1,
            0,
            [new SupplierPriceResult(
                "approved-supplier-job", 2, "55111064501", true,
                supplierName, 1.25m, null)]);

        using var document = JsonDocument.Parse(payload.Json);
        Assert.Equal(
            supplierName,
            document.RootElement.GetProperty("items")[0]
                .GetProperty("supplierName").GetString());
    }

    [Theory]
    [InlineData("AmerisourceBergen", "Cencora")]
    [InlineData("Cardinal", null)]
    [InlineData("Kinray", null)]
    public void BuildPersistedPayload_CanonicalizesOnlyApprovedSupplierAliases(
        string input,
        string? canonical)
    {
        var payload = PricingJobCloudUploader.BuildPersistedPayloadEnvelope(
            "supplier-alias", null, PricingJobStatus.Completed, "sql",
            1, 1, 0,
            [new SupplierPriceResult(
                "supplier-alias", 2, "55111064501", true,
                input, 1.25m, null)]);
        using var document = JsonDocument.Parse(payload.Json);
        var output = document.RootElement.GetProperty("items")[0]
            .GetProperty("supplierName").GetString();

        if (canonical is null)
        {
            Assert.Matches("^supplier:[a-f0-9]{64}$", output!);
            Assert.DoesNotContain(input, payload.Json, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Equal(canonical, output);
        }
    }

    [Fact]
    public void BuildPersistedPayload_UsesExplicitAwayFromZeroNumericRounding()
    {
        var payload = PricingJobCloudUploader.BuildPersistedPayloadEnvelope(
            "numeric-rounding", null, PricingJobStatus.Completed, "sql",
            1, 1, 0,
            [new SupplierPriceResult(
                "numeric-rounding", 2, "55111064501", true,
                "McKesson", 1.23445m, null,
                BaselineCostPerUnit: 2.34565m,
                Quantity: 1.2345m)]);
        using var document = JsonDocument.Parse(payload.Json);
        var item = document.RootElement.GetProperty("items")[0];

        Assert.Equal(1.2345m, item.GetProperty("costPerUnit").GetDecimal());
        Assert.Equal(2.3457m, item.GetProperty("baselineCostPerUnit").GetDecimal());
        Assert.Equal(1.235m, item.GetProperty("quantity").GetDecimal());
    }

    [Fact]
    public void BuildPersistedPayload_UnknownSupplierIsDeterministicOpaqueCode()
    {
        const string rawUnknownSupplier = "John Doe Specialty Supplier";
        var first = PricingJobCloudUploader.BuildPersistedPayloadEnvelope(
            "opaque-supplier-job", null, PricingJobStatus.Completed, "sql",
            1, 1, 0,
            [new SupplierPriceResult(
                "opaque-supplier-job", 2, "55111064501", true,
                rawUnknownSupplier, 1.25m, null)]);
        var second = PricingJobCloudUploader.BuildPersistedPayloadEnvelope(
            "opaque-supplier-job", null, PricingJobStatus.Completed, "sql",
            1, 1, 0,
            [new SupplierPriceResult(
                "opaque-supplier-job", 2, "55111064501", true,
                "  john doe specialty supplier  ", 1.25m, null)]);

        using var firstDocument = JsonDocument.Parse(first.Json);
        using var secondDocument = JsonDocument.Parse(second.Json);
        var firstCode = firstDocument.RootElement.GetProperty("items")[0]
            .GetProperty("supplierName").GetString();
        var secondCode = secondDocument.RootElement.GetProperty("items")[0]
            .GetProperty("supplierName").GetString();
        Assert.Matches("^supplier:[a-f0-9]{64}$", firstCode!);
        Assert.Equal(firstCode, secondCode);
        Assert.DoesNotContain(rawUnknownSupplier, first.Json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildPersistedPayload_Top500RequiredRowsStayBelowSharedCeiling()
    {
        var results = Enumerable.Range(0, 500)
            .Select(index => new SupplierPriceResult(
                "top500-budget-job",
                index + 2,
                $"{50000 + index:D5}{1000 + index:D4}01",
                true,
                index % 2 == 0 ? "Cardinal Health" : "Real Value Rx",
                1.25m,
                null))
            .ToArray();

        var payload = PricingJobCloudUploader.BuildPersistedPayloadEnvelope(
            "top500-budget-job", null, PricingJobStatus.Completed, "sql",
            500, 500, 0, results);

        Assert.Equal(500, payload.ItemCount);
        Assert.True(
            PricingResultPayloadBudget.SerializedSize(payload.Json) <=
            PricingResultPayloadBudget.MaximumSerializedBytes);
    }

    [Fact]
    public void BuildPersistedPayload_MaximumAdmittedWorstCaseRowsFitSharedCeiling()
    {
        var jobId = new string('j', 128);
        var results = Enumerable.Range(
                0, PricingResultPayloadBudget.MaximumRequiredRows)
            .Select(index => new SupplierPriceResult(
                jobId,
                index + 2,
                $"{index % 100_000_000_000L:D11}",
                false,
                new string('S', 128),
                1_000_000m,
                new string('w', 240),
                Observations: null,
                BaselineCostPerUnit: 1_000_000m,
                Quantity: 1_000_000_000m))
            .ToArray();

        var payload = PricingJobCloudUploader.BuildPersistedPayloadEnvelope(
            jobId, new string('c', 200), PricingJobStatus.Completed, "vision",
            results.Length, 0, results.Length, results);

        Assert.Equal(results.Length, payload.ItemCount);
        Assert.True(
            PricingResultPayloadBudget.SerializedSize(payload.Json) <=
            PricingResultPayloadBudget.MaximumSerializedBytes);
    }

    [Fact]
    public void OutboxCommit_RejectsPayloadAboveSharedServerCeiling()
    {
        var oversized = new string(
            'x', PricingResultPayloadBudget.MaximumSerializedBytes + 1);

        var error = Assert.Throws<InvalidOperationException>(() =>
            _db.StagePricingResultPayload(
                "oversized-payload", null, null, oversized, 0, true));

        Assert.Equal("Pricing result outbox payload is invalid.", error.Message);
        Assert.Null(_db.GetPricingResultOutbox("oversized-payload"));
    }

    [Fact]
    public void BuildPersistedPayload_DropsOptionalObservationsBeforeByteCeiling()
    {
        var candidates = Enumerable.Range(0, 128)
            .Select(index => new ObservedElement(
                "Edit", $"candidate_{index:D3}", "WindowsForms10Edit"))
            .ToArray();
        var observations = Enumerable.Range(0, 250)
            .Select(_ => new SelectorObservation(
                SelectorStepId.QuickSearchField,
                SelectorResolvedVia.Builtin,
                SelectorOutcome.Resolved,
                SelectorFailureKind.None,
                candidates[0],
                candidates))
            .ToArray();
        var result = new SupplierPriceResult(
            "observation-budget-job", 2, "55111064501", true,
            "McKesson", 1.25m, null, observations);

        var payload = PricingJobCloudUploader.BuildPersistedPayloadEnvelope(
            result.JobId, null, PricingJobStatus.Completed, "uia",
            1, 1, 0, [result]);

        Assert.True(
            PricingResultPayloadBudget.SerializedSize(payload.Json) <=
            PricingResultPayloadBudget.MaximumSerializedBytes);
        using var document = JsonDocument.Parse(payload.Json);
        Assert.Equal(
            JsonValueKind.Null,
            document.RootElement.GetProperty("items")[0]
                .GetProperty("selectorObservations").ValueKind);
        Assert.Equal(
            observations.Length,
            document.RootElement.GetProperty("omittedSelectorObservations").GetInt32());
    }

    [Fact]
    public void BuildPersistedPayload_CapsSelectorEvidenceAndCountsEveryOmission()
    {
        var observation = new SelectorObservation(
            SelectorStepId.QuickSearchField,
            SelectorResolvedVia.Builtin,
            SelectorOutcome.Resolved,
            SelectorFailureKind.None,
            null,
            []);
        var first = Enumerable.Repeat(observation, 4_000).ToArray();
        var second = Enumerable.Repeat(observation, 4_000).ToArray();
        var results = new[]
        {
            new SupplierPriceResult(
                "selector-cap", 2, "55111064501", true,
                "McKesson", 1.25m, null, first),
            new SupplierPriceResult(
                "selector-cap", 3, "00093015001", true,
                "Cencora", 1.35m, null, second),
        };

        var payload = PricingJobCloudUploader.BuildPersistedPayloadEnvelope(
            "selector-cap", null, PricingJobStatus.Completed, "uia",
            2, 2, 0, results);
        using var document = JsonDocument.Parse(payload.Json);
        var root = document.RootElement;

        Assert.Equal(2_000, root.GetProperty("omittedSelectorObservations").GetInt32());
        Assert.Equal(4_000, root.GetProperty("items")[0]
            .GetProperty("selectorObservations").GetArrayLength());
        Assert.Equal(2_000, root.GetProperty("items")[1]
            .GetProperty("selectorObservations").GetArrayLength());
    }

    [Fact]
    public async Task UploadAsync_CarriesBaselineCostAndQuantity_ForSavingsComputation()
    {
        // M1: the cloud computes savings_total = (baselineCostPerUnit - costPerUnit) * quantity,
        // only when all three are present + found. Pin the EXACT field names the route reads.
        var spec = AuthorizePricingJob(_db, new PricingJobSpec(
            "dddddddddddddddddddddddddddddddd",
            @"C:\Users\queen\Desktop\Top500.xlsx",
            "NDC",
            "Supplier",
            "Cost (per unit)"));
        const string commandId = "44444444-4444-4444-8444-444444444444";
        PreparePricingCommandDelivery(_db, commandId, spec);
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
        _db.UpsertPricingJob(spec, PricingJobStatus.Completed, 1, 1, 0);
        var signer = new RecordingPostSigner();
        var uploader = CreateUploader(signer, _db);

        await uploader.UploadAsync(
            spec,
            new PricingJobExecutionResult(
                new PricingJobProgress(spec.JobId, 1, 1, 0, PricingJobStatus.Completed),
                "sql",
                true,
                null),
            commandId,
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
        var spec = AuthorizePricingJob(_db, new PricingJobSpec(
            "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
            @"C:\Users\queen\Desktop\Top500.xlsx",
            "NDC",
            "Supplier",
            "Cost (per unit)"));
        const string commandId = "55555555-5555-4555-8555-555555555555";
        PreparePricingCommandDelivery(_db, commandId, spec);
        _db.SavePricingResult(new SupplierPriceResult(
            spec.JobId, 5, "55111064501", true, "Cardinal", 0.042m, null));
        _db.UpsertPricingJob(spec, PricingJobStatus.Completed, 1, 1, 0);
        var signer = new RecordingPostSigner();
        var uploader = CreateUploader(signer, _db);

        await uploader.UploadAsync(
            spec,
            new PricingJobExecutionResult(
                new PricingJobProgress(spec.JobId, 1, 1, 0, PricingJobStatus.Completed),
                "sql",
                true,
                null),
            commandId,
            CancellationToken.None);

        var item = JsonSerializer.SerializeToElement(signer.Payload)
            .GetProperty("items")[0];
        Assert.Equal(JsonValueKind.Null, item.GetProperty("baselineCostPerUnit").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("quantity").ValueKind);
    }

    [Fact]
    public async Task UploadAsync_OmitsWholeNonStructuralObservationAndCountsIt()
    {
        var spec = AuthorizePricingJob(_db, new PricingJobSpec(
            "ffffffffffffffffffffffffffffffff",
            @"C:\Users\queen\Desktop\Top500.xlsx",
            "NDC",
            "Supplier",
            "Cost (per unit)"), modality: "uia");
        const string commandId = "66666666-6666-4666-8666-666666666666";
        PreparePricingCommandDelivery(_db, commandId, spec, sourceMode: "uia");
        var observations = new[]
        {
            new SelectorObservation(
                SelectorStepId.QuickSearchField,
                SelectorResolvedVia.Builtin,
                SelectorOutcome.Failed,
                SelectorFailureKind.ElementNotFound,
                new ObservedElement("Edit", "Jane Doe 555-123-4567", "TextBox"),
                new[]
                {
                    new ObservedElement("Edit", "ndcSearchBox", "TextBox"),
                    new ObservedElement("Edit", "patient@example.com", "TextBox"),
                    new ObservedElement("Edit", "patient_123456789", "TextBox"),
                })
        };
        _db.SavePricingResult(new SupplierPriceResult(
            spec.JobId, 6, "55111064501", false, null, null,
            "pricing_lookup_failed", observations));
        _db.UpsertPricingJob(spec, PricingJobStatus.Completed, 1, 0, 1);
        var stored = Assert.Single(_db.GetPricingResults(spec.JobId));
        var localJson = JsonSerializer.Serialize(stored.Observations);
        Assert.DoesNotContain("Jane Doe", localJson, StringComparison.Ordinal);
        Assert.DoesNotContain("patient@example.com", localJson, StringComparison.Ordinal);
        Assert.DoesNotContain("patient_123456789", localJson, StringComparison.Ordinal);
        Assert.Null(stored.Observations);
        Assert.Equal(1, stored.OmittedSelectorObservations);
        var signer = new RecordingPostSigner();
        var uploader = CreateUploader(signer, _db);

        await uploader.UploadAsync(
            spec,
            new PricingJobExecutionResult(
                new PricingJobProgress(spec.JobId, 1, 0, 1, PricingJobStatus.Completed),
                "uia",
                true,
                null),
            commandId,
            CancellationToken.None);

        var json = JsonSerializer.Serialize(signer.Payload);
        var payload = JsonSerializer.SerializeToElement(signer.Payload);
        Assert.Equal(
            JsonValueKind.Null,
            payload.GetProperty("items")[0]
                .GetProperty("selectorObservations").ValueKind);
        Assert.Equal(1, payload.GetProperty("omittedSelectorObservations").GetInt32());
        Assert.DoesNotContain("Jane Doe", json, StringComparison.Ordinal);
        Assert.DoesNotContain("555-123-4567", json, StringComparison.Ordinal);
        Assert.DoesNotContain("patient@example.com", json, StringComparison.Ordinal);
        Assert.DoesNotContain("patient_123456789", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("attempted")]
    [InlineData("candidate")]
    [InlineData("candidate_overflow")]
    [InlineData("enum")]
    public void BuildPersistedPayload_OmitsWholeInvalidObservation(string defect)
    {
        var safeCandidate = new ObservedElement("Edit", "ndcSearchBox", "TextBox");
        var observation = defect switch
        {
            "attempted" => new SelectorObservation(
                SelectorStepId.QuickSearchField,
                SelectorResolvedVia.Builtin,
                SelectorOutcome.Resolved,
                SelectorFailureKind.None,
                new ObservedElement("Edit", "patient@example.com", "TextBox"),
                [safeCandidate]),
            "candidate" => new SelectorObservation(
                SelectorStepId.QuickSearchField,
                SelectorResolvedVia.Builtin,
                SelectorOutcome.Resolved,
                SelectorFailureKind.None,
                null,
                [safeCandidate, new ObservedElement(
                    "Edit", "patient@example.com", "TextBox")]),
            "candidate_overflow" => new SelectorObservation(
                SelectorStepId.QuickSearchField,
                SelectorResolvedVia.Builtin,
                SelectorOutcome.Resolved,
                SelectorFailureKind.None,
                null,
                Enumerable.Repeat(safeCandidate, 129).ToArray()),
            "enum" => new SelectorObservation(
                (SelectorStepId)999,
                SelectorResolvedVia.Builtin,
                SelectorOutcome.Resolved,
                SelectorFailureKind.None,
                null,
                [safeCandidate]),
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };
        var payload = PricingJobCloudUploader.BuildPersistedPayloadEnvelope(
            $"invalid-observation-{defect}",
            null,
            PricingJobStatus.Completed,
            "uia",
            1,
            1,
            0,
            [new SupplierPriceResult(
                $"invalid-observation-{defect}",
                2,
                "55111064501",
                true,
                "McKesson",
                1.25m,
                null,
                [observation])]);
        using var document = JsonDocument.Parse(payload.Json);
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("omittedSelectorObservations").GetInt32());
        Assert.Equal(
            JsonValueKind.Null,
            root.GetProperty("items")[0]
                .GetProperty("selectorObservations").ValueKind);
    }
}
