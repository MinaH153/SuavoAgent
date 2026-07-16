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
using SuavoAgent.Core.Tests.Pricing;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public sealed partial class PricingJobCloudUploaderTests
{
    public static IEnumerable<object[]> InvalidNdcEntryPathCases()
    {
        var entryPaths = new[] { "uploaded", "direct", "discovered", "scheduled" };
        var invalidValues = new[]
        {
            "John Smith",
            "jane.patient@example.com",
            "Rx Number 123456789",
        };
        return entryPaths.SelectMany(entryPath => invalidValues.Select(value =>
            new object[] { entryPath, value }));
    }

    [Theory]
    [MemberData(nameof(InvalidNdcEntryPathCases))]
    public async Task EveryExecutionEntryPath_InvalidNdcNeverPersistsOrUploads(
        string entryPath,
        string invalidCellValue)
    {
        var spec = AuthorizePricingJob(_db, new PricingJobSpec(
            Guid.NewGuid().ToString("N"),
            @"C:\Pricing.xlsx",
            "NDC",
            "Supplier",
            "Cost"));
        var sourceUploadId = entryPath == "uploaded" ? Guid.NewGuid() : (Guid?)null;
        var commandId = Guid.NewGuid().ToString("D");
        var signer = new RecordingPostSigner();
        var uploader = CreateUploader(signer, _db);
        RegisterPricingCommandBinding(_db, commandId, spec);
        uploader.PrepareDelivery(
            spec, commandId, sourceUploadId, PricingExecutorMode.SqlFirst);

        _db.SavePricingResult(new SupplierPriceResult(
            spec.JobId,
            2,
            invalidCellValue,
            false,
            "Untrusted Supplier",
            99.99m,
            $"Invalid NDC: {invalidCellValue}"));
        _db.UpsertPricingJob(spec, PricingJobStatus.Completed, 1, 0, 1);

        var persistedResult = Assert.Single(_db.GetPricingResults(spec.JobId));
        Assert.Equal(PricingResultContentPolicy.InvalidNdcStorageValue, persistedResult.Ndc);
        Assert.Equal(PricingResultContentPolicy.InvalidNdcReasonCode, persistedResult.ErrorMessage);
        Assert.Null(persistedResult.SupplierName);
        Assert.Null(persistedResult.CostPerUnit);

        var outbox = Assert.IsType<AgentStateDb.PricingResultOutboxEntry>(
            _db.GetPricingResultOutbox(spec.JobId));
        Assert.Equal(0, outbox.ItemCount);
        Assert.DoesNotContain(
            invalidCellValue, outbox.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(sourceUploadId, outbox.SourceUploadId);

        await uploader.FlushPendingAsync(
            CancellationToken.None, includeDeferred: true);

        var sentJson = JsonSerializer.Serialize(signer.Payload);
        Assert.DoesNotContain(
            invalidCellValue, sentJson, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(JsonSerializer.SerializeToElement(signer.Payload)
            .GetProperty("items").EnumerateArray());
        var sent = JsonSerializer.SerializeToElement(signer.Payload);
        Assert.Equal(1, sent.GetProperty("omittedInvalidItems").GetInt32());
        Assert.Equal(1, sent.GetProperty("totalItems").GetInt32());
        Assert.Equal(0, sent.GetProperty("completedItems").GetInt32());
        Assert.Equal(1, sent.GetProperty("failedItems").GetInt32());
        Assert.Equal("accepted", _db.GetPricingResultOutbox(spec.JobId)!.State);
    }

    [Theory]
    [InlineData(PricingJobStatus.Failed)]
    [InlineData(PricingJobStatus.Halted)]
    public async Task NonCompletedRun_RemainsResumableAndNeverCreatesOutbox(
        string terminalStatus)
    {
        var spec = AuthorizePricingJob(_db, new PricingJobSpec(
            $"resume-{terminalStatus}", @"C:\Pricing.xlsx", "NDC", "Supplier", "Cost"));
        var signer = new RecordingPostSigner();
        var uploader = CreateUploader(signer, _db);
        var commandId = Guid.NewGuid().ToString("D");
        RegisterPricingCommandBinding(_db, commandId, spec);
        uploader.PrepareDelivery(spec, commandId, null, PricingExecutorMode.SqlFirst);
        _db.SavePricingResult(new SupplierPriceResult(
            spec.JobId, 2, "55111064501", false, null, null, "lookup_failed"));
        _db.UpsertPricingJob(spec, terminalStatus, 1, 0, 1);

        var rejected = await uploader.UploadAsync(
            spec,
            new PricingJobExecutionResult(
                new PricingJobProgress(spec.JobId, 1, 0, 1, terminalStatus),
                "sql", false, "halted"),
            commandId,
            CancellationToken.None);

        Assert.False(rejected.Accepted);
        Assert.Equal("pricing_result_not_complete", rejected.Code);
        Assert.Null(signer.Payload);
        Assert.Null(_db.GetPricingResultOutbox(spec.JobId));
        Assert.Single(_db.GetPricingResults(spec.JobId));

        // Same job resumes from the durable row. Only the later completed
        // snapshot becomes immutable delivery evidence.
        _db.UpsertPricingJob(spec, PricingJobStatus.Completed, 1, 0, 1);
        var completed = Assert.IsType<AgentStateDb.PricingResultOutboxEntry>(
            _db.GetPricingResultOutbox(spec.JobId));
        Assert.False(completed.Legacy);
        Assert.Equal(1, completed.Generation);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("legacy-command")]
    [InlineData("11111111-1111-1111-8111-111111111111")]
    [InlineData("AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA")]
    public async Task UploadAsync_WithoutCanonicalSignedCommandAuthority_NeverStagesOrTransports(
        string? commandId)
    {
        var spec = CompletedSpec($"command-ineligible-{Guid.NewGuid():N}");
        var signer = new RecordingPostSigner();
        var uploader = CreateUploader(signer, _db);

        var receipt = await uploader.UploadAsync(
            spec.Spec, spec.Execution, commandId, CancellationToken.None);

        Assert.False(receipt.Accepted);
        Assert.Equal("pricing_result_command_ineligible", receipt.Code);
        Assert.Null(signer.Payload);
        Assert.Null(_db.GetPricingResultOutbox(spec.Spec.JobId));
    }

    [Fact]
    public async Task FlushPending_LegacyNullCommand_IsTerminalizedLocallyWithoutTransport()
    {
        const string jobId = "legacy-null-command";
        const string approvalId = "11111111-1111-4111-8111-111111111111";
        var grantDigest = new string('a', 64);
        var payload = PricingJobCloudUploader.BuildPersistedPayloadEnvelope(
            jobId, null, PricingJobStatus.Completed, "sql", 0, 0, 0, [],
            approvalId, grantDigest);
        _db.StagePricingResultPayload(
            jobId, null, null, payload.Json, payload.ItemCount, true);
        var signer = new RecordingPostSigner();
        var uploader = CreateUploader(signer, _db);

        await uploader.FlushPendingAsync(
            CancellationToken.None, includeDeferred: true);

        Assert.Null(signer.Payload);
        Assert.Empty(_db.GetAllPendingPricingResultPayloads(20));
        Assert.Equal(
            "pricing_result_command_ineligible",
            _db.GetPricingResultOutboxQuarantine(jobId)?.ReasonCode);
    }

    public static IEnumerable<object[]> TerminalRejections()
    {
        yield return ["pricing_result_payload_invalid", 400,
            "Invalid pricing result payload (max 500 items)"];
        yield return ["pricing_result_payload_invalid", 422,
            "Pricing result payload is invalid"];
        yield return ["pricing_result_payload_conflict", 409,
            "Pricing result conflicts with the accepted job"];
        yield return ["pricing_result_job_agent_conflict", 409,
            "Pricing job is already bound to another workstation"];
        yield return ["pricing_result_job_not_eligible", 409,
            "Pricing job requires manual reconciliation"];
        yield return ["pricing_result_command_binding_invalid", 409,
            "Pricing command authorization is invalid"];
        yield return ["pricing_result_not_complete", 409,
            "Only completed pricing results can be receipted"];
    }

    [Theory]
    [MemberData(nameof(TerminalRejections))]
    public async Task ExactServerTerminalRejection_IsDurableAndNeverRetried(
        string code,
        int status,
        string error)
    {
        var commandId = Guid.NewGuid().ToString("D");
        var spec = CompletedSpec($"terminal-{code}", commandId);
        var signer = new TerminalRejectingSigner(code, status, error);
        var uploader = CreateUploader(signer, _db);

        var receipt = await uploader.UploadAsync(
            spec.Spec, spec.Execution, commandId, CancellationToken.None);

        Assert.False(receipt.Accepted);
        Assert.Equal(code, receipt.Code);
        Assert.Equal(1, signer.CallCount);
        var outbox = Assert.IsType<AgentStateDb.PricingResultOutboxEntry>(
            _db.GetPricingResultOutbox(spec.Spec.JobId));
        Assert.Equal(0, outbox.AttemptCount);
        var quarantine = Assert.IsType<AgentStateDb.PricingResultOutboxQuarantineEntry>(
            _db.GetPricingResultOutboxQuarantine(spec.Spec.JobId));
        Assert.Equal(code, quarantine.ReasonCode);
        Assert.Equal(status, quarantine.HttpStatus);
        Assert.Equal(signer.ResponseBody, quarantine.ResponseJson);
        Assert.Empty(_db.GetAllPendingPricingResultPayloads(20));

        await uploader.FlushPendingAsync(CancellationToken.None, includeDeferred: true);
        Assert.Equal(1, signer.CallCount);

        var repeated = await uploader.UploadAsync(
            spec.Spec, spec.Execution, commandId, CancellationToken.None);
        Assert.False(repeated.Accepted);
        Assert.Equal(code, repeated.Code);
        Assert.Equal(1, signer.CallCount);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(422)]
    public async Task SignedResponseEvidence_PreservesExactWhitespaceBoundBytes(int status)
    {
        var commandId = Guid.NewGuid().ToString("D");
        var spec = CompletedSpec($"exact-response-{status}", commandId);
        var body = status == 200
            ? " \n" + JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                kind = "pricing_result_receipt",
                accepted = true,
                commandId,
                agentInstanceId = AgentId,
                pharmacyId = PharmacyId,
                jobId = spec.Spec.JobId,
                recorded = 1,
                idempotent = false,
            }) + "\n "
            : " \n{\"accepted\":false,\"terminal\":true,\"code\":\"pricing_result_payload_invalid\",\"error\":\"Pricing result payload is invalid\"}\n ";
        var signer = new ThrowingSigner(status, body);
        var uploader = CreateUploader(signer, _db);

        var result = await uploader.UploadAsync(
            spec.Spec, spec.Execution, commandId, CancellationToken.None);

        if (status == 200)
        {
            Assert.True(result.Accepted);
            var accepted = Assert.IsType<AgentStateDb.PricingResultOutboxEntry>(
                _db.GetPricingResultOutbox(spec.Spec.JobId));
            Assert.Equal(body, accepted.AcceptedReceiptJson);
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))
                    .ToLowerInvariant(),
                accepted.AcceptedReceiptSha256);
        }
        else
        {
            Assert.Equal("pricing_result_payload_invalid", result.Code);
            var terminal = Assert.IsType<AgentStateDb.PricingResultOutboxQuarantineEntry>(
                _db.GetPricingResultOutboxQuarantine(spec.Spec.JobId));
            Assert.Equal(body, terminal.ResponseJson);
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))
                    .ToLowerInvariant(),
                terminal.ResponseSha256);
        }
    }

    [Theory]
    [InlineData(500, "{\"error\":\"Failed to store results\"}")]
    [InlineData(409, "{\"accepted\":false,\"terminal\":true,\"code\":\"pricing_result_payload_conflict\",\"error\":\"unexpected\"}")]
    public async Task TransientOrNonExactServerResponse_RemainsRetryable(
        int status,
        string responseBody)
    {
        var commandId = Guid.NewGuid().ToString("D");
        var spec = CompletedSpec(
            $"retry-{status}-{responseBody.Length}", commandId);
        var signer = new ThrowingSigner(status, responseBody);
        var uploader = CreateUploader(signer, _db);

        var receipt = await uploader.UploadAsync(
            spec.Spec, spec.Execution, commandId,
            CancellationToken.None);

        Assert.False(receipt.Accepted);
        Assert.Equal("pricing_result_upload_failed", receipt.Code);
        var pending = Assert.IsType<AgentStateDb.PricingResultOutboxEntry>(
            _db.GetPricingResultOutbox(spec.Spec.JobId));
        Assert.Equal(1, pending.AttemptCount);
        Assert.Null(_db.GetPricingResultOutboxQuarantine(spec.Spec.JobId));
        Assert.Single(_db.GetAllPendingPricingResultPayloads(20));
    }

    [Theory]
    [InlineData("unknown_field")]
    [InlineData("cross_command")]
    [InlineData("cross_agent")]
    [InlineData("cross_pharmacy")]
    [InlineData("cross_job")]
    [InlineData("wrong_recorded")]
    [InlineData("wrong_schema")]
    [InlineData("wrong_kind")]
    [InlineData("wrong_idempotent_type")]
    public async Task SignedSuccessReceipt_MustMatchExactLocalBinding(string defect)
    {
        var commandId = Guid.NewGuid().ToString("D");
        var completed = CompletedSpec($"bound-receipt-{defect}", commandId);
        var receipt = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["kind"] = "pricing_result_receipt",
            ["accepted"] = true,
            ["commandId"] = commandId,
            ["agentInstanceId"] = AgentId,
            ["pharmacyId"] = PharmacyId,
            ["jobId"] = completed.Spec.JobId,
            ["recorded"] = 1,
            ["idempotent"] = false,
        };
        switch (defect)
        {
            case "unknown_field": receipt["unexpected"] = true; break;
            case "cross_command": receipt["commandId"] = Guid.NewGuid().ToString("D"); break;
            case "cross_agent": receipt["agentInstanceId"] = Guid.NewGuid().ToString("D"); break;
            case "cross_pharmacy": receipt["pharmacyId"] = Guid.NewGuid().ToString("D"); break;
            case "cross_job": receipt["jobId"] = "different-job"; break;
            case "wrong_recorded": receipt["recorded"] = 0; break;
            case "wrong_schema": receipt["schemaVersion"] = 2; break;
            case "wrong_kind": receipt["kind"] = "pricing_result"; break;
            case "wrong_idempotent_type": receipt["idempotent"] = "false"; break;
            default: throw new ArgumentOutOfRangeException(nameof(defect));
        }
        var signer = new ThrowingSigner(200, JsonSerializer.Serialize(receipt));
        var uploader = CreateUploader(signer, _db);

        var result = await uploader.UploadAsync(
            completed.Spec,
            completed.Execution,
            commandId,
            CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("pricing_result_upload_receipt_invalid", result.Code);
        Assert.Equal(
            "pending",
            _db.GetPricingResultOutbox(completed.Spec.JobId)!.State);
    }

    [Fact]
    public void UnsafeSavingsCapacity_IsNulledWithFixedLocalReviewCode()
    {
        const string jobId = "numeric-capacity";
        var spec = AuthorizePricingJob(_db, new PricingJobSpec(
            jobId, @"C:\Pricing.xlsx", "NDC", "Supplier", "Cost"));
        _db.UpsertPricingJob(spec, PricingJobStatus.Running, 1, 0, 0);
        _db.SavePricingResult(new SupplierPriceResult(
            jobId,
            2,
            "55111064501",
            true,
            "McKesson",
            PricingResultContentPolicy.MaximumUnitCost + 0.0001m,
            null,
            BaselineCostPerUnit: PricingResultContentPolicy.MaximumUnitCost,
            Quantity: PricingResultContentPolicy.MaximumQuantity));

        var stored = Assert.Single(_db.GetPricingResults(jobId));
        Assert.Null(stored.CostPerUnit);
        Assert.Null(stored.BaselineCostPerUnit);
        Assert.Null(stored.Quantity);
        Assert.Equal(
            PricingResultContentPolicy.NumericCapacityReviewCode,
            stored.ErrorMessage);

        var payload = PricingJobCloudUploader.BuildPersistedPayloadEnvelope(
            jobId, null, PricingJobStatus.Completed, "sql", 1, 1, 0, [stored]);
        using var document = JsonDocument.Parse(payload.Json);
        var item = document.RootElement.GetProperty("items")[0];
        Assert.Equal(JsonValueKind.Null, item.GetProperty("costPerUnit").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("baselineCostPerUnit").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("quantity").ValueKind);
        Assert.False(item.TryGetProperty("warning", out _));
        Assert.DoesNotContain(
            PricingResultContentPolicy.NumericCapacityReviewCode,
            payload.Json,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlushPending_PreFixUnsafeOutboxIsBlockedBeforeTransport()
    {
        const string rawPhiLikeValue = "John Smith";
        const string jobId = "legacy-unsafe-result";
        var unsafePayload = JsonSerializer.Serialize(new
        {
            commandId = "legacy-command",
            status = PricingJobStatus.Completed,
            mode = "sql",
            totalItems = 1,
            completedItems = 0,
            failedItems = 1,
            items = new[]
            {
                new
                {
                    rowIndex = 2,
                    ndc = rawPhiLikeValue,
                    found = false,
                    warning = $"Invalid NDC: {rawPhiLikeValue}",
                },
            },
        });
        var legacyPath = Path.Combine(_tempDir, "legacy-unsafe.db");
        using (var initialized = new AgentStateDb(legacyPath)) { }
        InsertLegacyOutbox(legacyPath, jobId, unsafePayload, itemCount: 1);
        var signer = new RecordingPostSigner();
        using var legacyDb = new AgentStateDb(legacyPath);
        var uploader = CreateUploader(signer, legacyDb);

        await uploader.FlushPendingAsync(
            CancellationToken.None, includeDeferred: true);

        Assert.Null(signer.Path);
        Assert.Null(signer.Payload);
        Assert.Equal("pending", legacyDb.GetPricingResultOutbox(jobId)!.State);
        Assert.NotNull(legacyDb.GetPricingResultOutboxQuarantine(jobId));
    }

    [Theory]
    [InlineData("extra_top_level")]
    [InlineData("item_warning")]
    [InlineData("missing_row_index")]
    public void DirectStage_RejectsNonExactPayloadBeforePersistence(string mutation)
    {
        const string jobId = "direct-exact-contract";
        const string commandId = "direct-command";
        var valid = PricingJobCloudUploader.BuildPersistedPayloadEnvelope(
            jobId, commandId, PricingJobStatus.Completed, "sql",
            1, 1, 0,
            [new SupplierPriceResult(
                jobId, 2, "55111064501", true, "McKesson", 1.25m, null)]);
        var root = JsonNode.Parse(valid.Json)!.AsObject();
        var item = root["items"]!.AsArray()[0]!.AsObject();
        switch (mutation)
        {
            case "extra_top_level":
                root["patientName"] = "Jane Doe";
                break;
            case "item_warning":
                item["warning"] = "patient@example.com";
                break;
            case "missing_row_index":
                item.Remove("rowIndex");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        Assert.Throws<InvalidOperationException>(() =>
            _db.StagePricingResultPayload(
                jobId,
                commandId,
                null,
                root.ToJsonString(),
                1,
                true));
        Assert.Null(_db.GetPricingResultOutbox(jobId));
    }

    [Fact]
    public void DirectStage_RejectsCommandBindingMismatch()
    {
        const string jobId = "direct-command-binding";
        var payload = PricingJobCloudUploader.BuildPersistedPayloadEnvelope(
            jobId, "payload-command", PricingJobStatus.Completed, "sql",
            0, 0, 0, []);

        Assert.Throws<InvalidOperationException>(() =>
            _db.StagePricingResultPayload(
                jobId, "column-command", null, payload.Json, 0, true));
        Assert.Null(_db.GetPricingResultOutbox(jobId));
    }

    [Fact]
    public async Task UploadAsync_PostsJobResultsWithoutWorkbookPath()
    {
        var spec = AuthorizePricingJob(_db, new PricingJobSpec(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            @"C:\Users\queen\Desktop\Top500.xlsx",
            "NDC",
            "Supplier",
            "Cost (per unit)"));
        const string commandId = "11111111-1111-4111-8111-111111111111";
        PreparePricingCommandDelivery(_db, commandId, spec);
        _db.SavePricingResult(new SupplierPriceResult(
            spec.JobId,
            2,
            "55111064501",
            true,
            "McKesson",
            0.0316m,
            null));
        _db.UpsertPricingJob(spec, PricingJobStatus.Completed, 1, 1, 0);
        var signer = new RecordingPostSigner();
        var uploader = CreateUploader(signer, _db);

        var receipt = await uploader.UploadAsync(
            spec,
            new PricingJobExecutionResult(
                new PricingJobProgress(spec.JobId, 1, 1, 0, PricingJobStatus.Completed),
                "sql",
                true,
                null),
            commandId,
            CancellationToken.None);

        Assert.True(receipt.Accepted);
        Assert.Equal(1, receipt.Recorded);
        Assert.Equal($"/api/agent/pricing-jobs/{spec.JobId}/results", signer.Path);
        var json = JsonSerializer.Serialize(signer.Payload);
        Assert.Contains("55111064501", json);
        Assert.Contains("McKesson", json);
        Assert.DoesNotContain("Top500.xlsx", json);
        Assert.DoesNotContain("Desktop", json);
        Assert.DoesNotContain("ExcelPath", json);
        var durable = Assert.IsType<AgentStateDb.PricingResultOutboxEntry>(
            _db.GetPricingResultOutbox(spec.JobId));
        Assert.Equal("accepted", durable.State);
        Assert.NotNull(durable.AcceptedReceiptJson);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(durable.AcceptedReceiptJson!))).ToLowerInvariant(),
            durable.AcceptedReceiptSha256);
    }

    [Fact]
    public async Task UploadAsync_NeverSerializesPricingWarnings()
    {
        var spec = AuthorizePricingJob(_db, new PricingJobSpec(
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            @"C:\Users\queen\Desktop\Top500.xlsx",
            "NDC",
            "Supplier",
            "Cost (per unit)"));
        const string commandId = "22222222-2222-4222-8222-222222222222";
        PreparePricingCommandDelivery(_db, commandId, spec);
        _db.SavePricingResult(new SupplierPriceResult(
            spec.JobId,
            7,
            "55111064501",
            false,
            null,
            null,
            @"Patient Jane Doe phone 555-123-4567 failed from C:\Users\queen\Desktop\Top500.xlsx"));
        _db.UpsertPricingJob(spec, PricingJobStatus.Completed, 1, 0, 1);
        var signer = new RecordingPostSigner();
        var uploader = CreateUploader(signer, _db);

        await uploader.UploadAsync(
            spec,
            new PricingJobExecutionResult(
                new PricingJobProgress(spec.JobId, 1, 0, 1, PricingJobStatus.Completed),
                "sql",
                true,
                null),
            commandId,
            CancellationToken.None);

        var json = JsonSerializer.Serialize(signer.Payload);
        Assert.DoesNotContain("warning", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pricing_lookup_failed", json);
        Assert.DoesNotContain("Jane Doe", json);
        Assert.DoesNotContain("555-123-4567", json);
        Assert.DoesNotContain("Top500.xlsx", json);
        Assert.DoesNotContain("Desktop", json);
    }

    [Fact]
    public async Task UploadAsync_NormalizesPricingSourceForCloudContract()
    {
        var spec = AuthorizePricingJob(_db, new PricingJobSpec(
            "cccccccccccccccccccccccccccccccc",
            @"C:\Users\queen\Desktop\Top500.xlsx",
            "NDC",
            "Supplier",
            "Cost (per unit)"));
        const string commandId = "33333333-3333-4333-8333-333333333333";
        PreparePricingCommandDelivery(_db, commandId, spec);
        _db.SavePricingResult(new SupplierPriceResult(
            spec.JobId,
            3,
            "55111064501",
            true,
            "Cardinal",
            0.042m,
            null));
        _db.UpsertPricingJob(spec, PricingJobStatus.Completed, 1, 1, 0);
        var signer = new RecordingPostSigner();
        var uploader = CreateUploader(signer, _db);

        await uploader.UploadAsync(
            spec,
            new PricingJobExecutionResult(
                new PricingJobProgress(spec.JobId, 1, 1, 0, PricingJobStatus.Completed),
                "SqlFirst",
                true,
                null),
            commandId,
            CancellationToken.None);

        var json = JsonSerializer.Serialize(signer.Payload);
        Assert.Contains("\"mode\":\"sql\"", json);
        Assert.Contains("\"source\":\"sql\"", json);
        Assert.DoesNotContain("SqlFirst", json);
    }

    [Theory]
    [InlineData("SQL")]
    [InlineData(" sql")]
    [InlineData("sql ")]
    [InlineData("")]
    [InlineData("unknown")]
    public void PayloadBuilder_RejectsNonCanonicalSources(string source)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            PricingJobCloudUploader.BuildPersistedPayloadEnvelope(
                "exact-source", null, PricingJobStatus.Completed, source,
                0, 0, 0, []));

        Assert.Equal("pricing_result_source_invalid", error.Message);
    }

    [Fact]
    public void AuthorizedDelivery_RejectsManualOrMismatchedObservationModality()
    {
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        var contract = PricingTestAuthority.Contract(modality: "vision");
        var authority = PricingTestAuthority.InstallAuthority(
            _db,
            contract,
            now);
        var spec = new PricingJobSpec(
            "modality-binding",
            @"C:\Pricing.xlsx",
            "NDC",
            "Supplier",
            "Cost",
            authority.ApprovalId,
            authority.ApprovalDigest);
        const string commandId = "44444444-4444-4444-8444-444444444444";
        RegisterPricingCommandBinding(_db, commandId, spec);

        var manual = Assert.Throws<InvalidOperationException>(() =>
            _db.PreparePricingResultDelivery(
                spec,
                commandId,
                null,
                "manual"));
        Assert.Equal("Pricing delivery source is invalid.", manual.Message);

        _db.PreparePricingResultDelivery(spec, commandId, null, "uia");
        Assert.True(_db.TryBindPricingInputIdentity(
            spec.JobId,
            new string('a', 64),
            new string('b', 64),
            contract,
            authority,
            now,
            out var bindCode), bindCode);
        _db.SavePricingResult(new SupplierPriceResult(
            spec.JobId,
            2,
            "55111064501",
            true,
            "McKesson",
            1.25m,
            null));

        var mismatch = Assert.Throws<InvalidOperationException>(() =>
            _db.UpsertPricingJob(
                spec,
                PricingJobStatus.Completed,
                1,
                1,
                0));
        Assert.Equal("pricing_job_authority_binding_invalid", mismatch.Message);
        Assert.Null(_db.GetPricingResultOutbox(spec.JobId));
    }

    [Fact]
    public void PrepareDelivery_AdmitsVisionAsExactPersistedSource()
    {
        const string jobId = "unsupported-executor";
        var uploader = CreateUploader(new RecordingPostSigner(), _db);
        var spec = AuthorizePricingJob(_db, new PricingJobSpec(
            jobId, @"C:\Pricing.xlsx", "NDC", "Supplier", "Cost"),
            modality: "vision");
        const string commandId = "55555555-5555-4555-8555-555555555555";

        RegisterPricingCommandBinding(_db, commandId, spec);
        uploader.PrepareDelivery(
            spec,
            commandId,
            null,
            PricingExecutorMode.VisionFirst);

        Assert.Equal(1, CountRows("pricing_jobs", jobId));
        Assert.Equal(1, CountRows("pricing_result_delivery_intents", jobId));

        _db.SavePricingResult(new SupplierPriceResult(
            jobId, 2, "55111064501", true, "McKesson", 0.0099m, null));
        _db.UpsertPricingJob(
            spec, PricingJobStatus.Completed, 1, 1, 0);
        var persisted = Assert.IsType<AgentStateDb.PricingResultOutboxEntry>(
            _db.GetPricingResultOutbox(jobId));
        var payload = JsonSerializer.Deserialize<JsonElement>(persisted.PayloadJson);
        Assert.Equal("vision", payload.GetProperty("mode").GetString());
        Assert.Equal(
            "vision",
            payload.GetProperty("items")[0].GetProperty("source").GetString());
    }

    [Theory]
    [InlineData("../job")]
    [InlineData("job/path")]
    [InlineData("job%2Fpath")]
    [InlineData("job?path")]
    public void PrepareDelivery_RejectsUnsafeJobIdentityWithoutPersistence(string jobId)
    {
        var uploader = CreateUploader(new RecordingPostSigner(), _db);
        var spec = new PricingJobSpec(
            jobId, @"C:\Pricing.xlsx", "NDC", "Supplier", "Cost");

        Assert.Throws<InvalidOperationException>(() =>
            uploader.PrepareDelivery(
                spec,
                "66666666-6666-4666-8666-666666666666",
                null,
                PricingExecutorMode.SqlFirst));
        Assert.Equal(0, CountRows("pricing_jobs", jobId));
        Assert.Equal(0, CountRows("pricing_result_delivery_intents", jobId));
    }

    [Fact]
    public void PrepareDelivery_RejectsSameJobWithDifferentWorkbookContract()
    {
        var uploader = CreateUploader(new RecordingPostSigner(), _db);
        var original = AuthorizePricingJob(_db, new PricingJobSpec(
            "spec-identity", @"C:\One.xlsx", "NDC", "Supplier", "Cost"));
        var changed = original with { ExcelPath = @"C:\Two.xlsx" };
        var commandId = Guid.NewGuid().ToString("D");
        RegisterPricingCommandBinding(_db, commandId, original);
        uploader.PrepareDelivery(
            original, commandId, null, PricingExecutorMode.SqlFirst);

        var error = Assert.Throws<InvalidOperationException>(() =>
            uploader.PrepareDelivery(
                changed, commandId, null, PricingExecutorMode.SqlFirst));

        Assert.Equal("pricing_job_spec_identity_conflict", error.Message);
    }
}
