using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Tests.Pricing;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public sealed class PricingJobLegacyOutboxQuarantineTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), $"suavo_pricing_quarantine_{Guid.NewGuid():N}");

    public PricingJobLegacyOutboxQuarantineTests() => Directory.CreateDirectory(_tempDir);

    [Fact]
    public async Task LegacyRawSupplier_IsDurablyQuarantinedWithoutEvidenceMutationOrRetry()
    {
        var path = Path.Combine(_tempDir, "raw-supplier.db");
        const string jobId = "legacy-raw-supplier";
        var payload = JsonSerializer.Serialize(new
        {
            commandId = "33333333-3333-4333-8333-333333333333",
            status = PricingJobStatus.Completed,
            mode = "sql",
            totalItems = 1,
            completedItems = 1,
            failedItems = 0,
            items = new[]
            {
                new
                {
                    rowIndex = 2,
                    ndc = "55111064501",
                    supplierName = "Nadim K Dib",
                    found = true,
                },
            },
        });
        using (var initialized = new AgentStateDb(path)) { }
        RemoveMigration19(path);
        var legacyDigest = InsertLegacyOutbox(
            path,
            jobId,
            payload,
            itemCount: 1,
            commandId: "33333333-3333-4333-8333-333333333333");

        var signer = new RecordingSigner();
        using (var db = new AgentStateDb(path))
        {
            var original = Assert.IsType<AgentStateDb.PricingResultOutboxEntry>(
                db.GetPricingResultOutbox(jobId));
            Assert.Equal(legacyDigest, original.PayloadSha256);
            var uploader = new PricingJobCloudUploader(
                signer, db, NullLogger<PricingJobCloudUploader>.Instance);

            await uploader.FlushPendingAsync(
                CancellationToken.None, includeDeferred: true);

            Assert.Equal(0, signer.CallCount);
            var quarantine = Assert.IsType<AgentStateDb.PricingResultOutboxQuarantineEntry>(
                db.GetPricingResultOutboxQuarantine(jobId));
            Assert.Equal(original.PayloadSha256, quarantine.PayloadSha256);
            Assert.Equal("pricing_result_outbox_content_blocked", quarantine.ReasonCode);
            Assert.Empty(db.GetAllPendingPricingResultPayloads(20));
            var retained = Assert.IsType<AgentStateDb.PricingResultOutboxEntry>(
                db.GetPricingResultOutbox(jobId));
            Assert.Equal(original.PayloadJson, retained.PayloadJson);
            Assert.Equal(original.PayloadSha256, retained.PayloadSha256);
            Assert.Equal(0, retained.AttemptCount);
            Assert.Equal("pending", retained.State);
        }

        using (var recovered = new AgentStateDb(path))
        {
            Assert.NotNull(recovered.GetPricingResultOutboxQuarantine(jobId));
            Assert.Empty(recovered.GetPendingPricingResultPayloads(20));
            Assert.Empty(recovered.GetAllPendingPricingResultPayloads(20));
            var uploader = new PricingJobCloudUploader(
                signer, recovered, NullLogger<PricingJobCloudUploader>.Instance);
            await uploader.FlushPendingAsync(
                CancellationToken.None, includeDeferred: true);
            Assert.Equal(0, signer.CallCount);
        }

        AssertImmutable(path, "pricing_result_outbox", jobId);
        AssertImmutable(path, "pricing_result_outbox_quarantine", jobId);
    }

    [Fact]
    public async Task UpgradeRowAboveTwoMiBUtf8_IsQuarantinedBeforeJsonParseOrTransport()
    {
        var path = Path.Combine(_tempDir, "oversized-legacy.db");
        const string jobId = "legacy-oversized";
        using (var initialized = new AgentStateDb(path)) { }

        var prefix = "{\"items\":[],\"padding\":\"";
        const string suffix = "\"}";
        var padding = new string(
            'x',
            PricingResultPayloadBudget.MaximumSerializedBytes -
            Encoding.UTF8.GetByteCount(prefix + suffix) + 1);
        var payload = prefix + padding + suffix;
        Assert.Equal(
            PricingResultPayloadBudget.MaximumSerializedBytes + 1,
            Encoding.UTF8.GetByteCount(payload));
        InsertLegacyOutbox(path, jobId, payload, itemCount: 0);

        var signer = new RecordingSigner();
        using var db = new AgentStateDb(path);
        var uploader = new PricingJobCloudUploader(
            signer, db, NullLogger<PricingJobCloudUploader>.Instance);

        await uploader.FlushPendingAsync(
            CancellationToken.None, includeDeferred: true);

        Assert.Equal(0, signer.CallCount);
        Assert.NotNull(db.GetPricingResultOutboxQuarantine(jobId));
        Assert.Empty(db.GetAllPendingPricingResultPayloads(20));
        Assert.Equal(0, db.GetPricingResultOutbox(jobId)!.AttemptCount);
    }

    [Fact]
    public async Task LegacyOutOfRangeMetric_IsTerminallyQuarantinedWithoutRetry()
    {
        var path = Path.Combine(_tempDir, "legacy-metric.db");
        const string jobId = "legacy-out-of-range-metric";
        var payload = JsonSerializer.Serialize(new
        {
            totalItems = PricingResultPayloadBudget.MaximumSerializedMetric + 1,
            completedItems = 0,
            failedItems = 0,
            items = Array.Empty<object>(),
        });
        var signer = new RecordingSigner();
        using (var initialized = new AgentStateDb(path)) { }
        InsertLegacyOutbox(path, jobId, payload, itemCount: 0);
        using var db = new AgentStateDb(path);
        var uploader = new PricingJobCloudUploader(
            signer, db, NullLogger<PricingJobCloudUploader>.Instance);

        await uploader.FlushPendingAsync(
            CancellationToken.None, includeDeferred: true);

        Assert.Equal(0, signer.CallCount);
        Assert.NotNull(db.GetPricingResultOutboxQuarantine(jobId));
        Assert.Empty(db.GetAllPendingPricingResultPayloads(20));
        Assert.Equal(0, db.GetPricingResultOutbox(jobId)!.AttemptCount);
    }

    [Theory]
    [InlineData("extra_top_level")]
    [InlineData("item_warning")]
    [InlineData("missing_row_index")]
    public async Task LegacyNonExactPayload_IsQuarantinedBeforeTransport(
        string mutation)
    {
        var path = Path.Combine(_tempDir, $"legacy-exact-{mutation}.db");
        var jobId = $"legacy-exact-{mutation}";
        var valid = PricingJobCloudUploader.BuildPersistedPayloadEnvelope(
            jobId, null, PricingJobStatus.Completed, "sql",
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
        using (var initialized = new AgentStateDb(path)) { }
        InsertLegacyOutbox(path, jobId, root.ToJsonString(), itemCount: 1);
        var signer = new RecordingSigner();
        using var db = new AgentStateDb(path);
        var uploader = new PricingJobCloudUploader(
            signer, db, NullLogger<PricingJobCloudUploader>.Instance);

        await uploader.FlushPendingAsync(
            CancellationToken.None, includeDeferred: true);

        Assert.Equal(0, signer.CallCount);
        Assert.NotNull(db.GetPricingResultOutboxQuarantine(jobId));
        Assert.Empty(db.GetAllPendingPricingResultPayloads(20));
    }

    [Fact]
    public void TransportCeiling_CountsUtf8BytesAndAllowsTheExactBoundary()
    {
        var exact = new string(
            '\u00e9', PricingResultPayloadBudget.MaximumSerializedBytes / 2);

        Assert.Equal(
            PricingResultPayloadBudget.MaximumSerializedBytes,
            Encoding.UTF8.GetByteCount(exact));
        Assert.True(PricingJobCloudUploader.IsPersistedPayloadWithinCloudCeiling(exact));
        Assert.False(PricingJobCloudUploader.IsPersistedPayloadWithinCloudCeiling(exact + "x"));
    }

    [Fact]
    public void LegacyPartialOutbox_IsAppendOnlySupersededByLaterCompletedGeneration()
    {
        var path = Path.Combine(_tempDir, "legacy-partial-resume.db");
        const string jobId = "legacy-partial-resume";
        const string commandId = "44444444-4444-4444-8444-444444444444";
        var legacyPayload = JsonSerializer.Serialize(new
        {
            commandId,
            status = PricingJobStatus.Halted,
            mode = "sql",
            totalItems = 2,
            completedItems = 1,
            failedItems = 0,
            items = new[]
            {
                new { rowIndex = 2, ndc = "55111064501", found = true },
            },
        });
        using (var initialized = new AgentStateDb(path)) { }
        var legacyDigest = InsertLegacyOutbox(
            path, jobId, legacyPayload, itemCount: 1);

        AgentStateDb.PricingResultOutboxEntry successor;
        using (var db = new AgentStateDb(path))
        {
            var now = DateTimeOffset.UtcNow;
            var contract = PricingTestAuthority.Contract();
            var authority = PricingTestAuthority.InstallAuthority(
                db,
                contract,
                now);
            var uploader = new PricingJobCloudUploader(
                new RecordingSigner(), db,
                NullLogger<PricingJobCloudUploader>.Instance,
                PricingTestAuthority.TrustedPublicKeys);
            var spec = new PricingJobSpec(
                jobId,
                @"C:\Pricing.xlsx",
                "NDC",
                "Supplier",
                "Cost",
                authority.ApprovalId,
                authority.ApprovalDigest);
            db.UpsertPricingJob(
                spec,
                PricingJobStatus.Pending,
                0,
                0,
                0);
            Assert.True(db.TryRecordNonceAndRegisterPricingIntent(
                Guid.NewGuid().ToString("N"),
                commandId,
                "run_pricing_job",
                Guid.NewGuid().ToString("N"),
                verifiedCommand: null,
                authority.ApprovalId,
                authority.ApprovalDigest));
            uploader.PrepareDelivery(
                spec, commandId, null, PricingExecutorMode.SqlFirst);
            Assert.True(db.TryBindPricingInputIdentity(
                spec.JobId,
                new string('a', 64),
                new string('b', 64),
                contract,
                authority,
                now,
                out var bindCode), bindCode);
            db.SavePricingResult(new SupplierPriceResult(
                jobId, 2, "55111064501", true, "McKesson", 1.25m, null));
            db.SavePricingResult(new SupplierPriceResult(
                jobId, 3, "00093015001", false, null, null, "not_found"));

            db.UpsertPricingJob(spec, PricingJobStatus.Completed, 2, 1, 1);

            successor = Assert.IsType<AgentStateDb.PricingResultOutboxEntry>(
                db.GetPricingResultOutbox(jobId));
            Assert.False(successor.Legacy);
            Assert.Equal(1, successor.Generation);
            Assert.NotEqual(legacyDigest, successor.PayloadSha256);
            Assert.Single(db.GetAllPendingPricingResultPayloads(20));
        }

        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using (var retained = connection.CreateCommand())
        {
            retained.CommandText = """
                SELECT payload_json, payload_sha256, state, attempt_count
                  FROM pricing_result_outbox
                 WHERE job_id = @job
                """;
            retained.Parameters.AddWithValue("@job", jobId);
            using var reader = retained.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(legacyPayload, reader.GetString(0));
            Assert.Equal(legacyDigest, reader.GetString(1));
            Assert.Equal("pending", reader.GetString(2));
            Assert.Equal(0, reader.GetInt32(3));
        }
        using (var supersession = connection.CreateCommand())
        {
            supersession.CommandText = """
                SELECT successor_payload_sha256, reason_code
                  FROM pricing_result_outbox_supersessions
                 WHERE job_id = @job AND superseded_payload_sha256 = @legacy
                """;
            supersession.Parameters.AddWithValue("@job", jobId);
            supersession.Parameters.AddWithValue("@legacy", legacyDigest);
            using var reader = supersession.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(successor.PayloadSha256, reader.GetString(0));
            Assert.Equal(
                "legacy_partial_replaced_by_completed", reader.GetString(1));
        }
    }

    [Theory]
    [InlineData(5001, 0, 0)]
    [InlineData(5000, 5001, 0)]
    [InlineData(5000, 0, 5001)]
    [InlineData(5000, 2501, 2500)]
    [InlineData(-1, 0, 0)]
    public void PayloadBuilder_RejectsEveryOutOfContractSerializedMetric(
        int total,
        int completed,
        int failed)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            PricingJobCloudUploader.BuildPersistedPayloadEnvelope(
                "metric-contract", null, PricingJobStatus.Completed, "sql",
                total, completed, failed, []));

        Assert.Equal("pricing_result_metrics_out_of_range", error.Message);
    }

    [Fact]
    public void PayloadBuilder_RejectsMissingRowsAtServerMaximum()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            PricingJobCloudUploader.BuildPersistedPayloadEnvelope(
                "metric-maximum", null, PricingJobStatus.Completed, "sql",
                PricingResultPayloadBudget.MaximumSerializedMetric,
                0,
                PricingResultPayloadBudget.MaximumSerializedMetric,
                []));

        Assert.Equal("pricing_result_metrics_out_of_range", error.Message);
    }

    private static string InsertLegacyOutbox(
        string path,
        string jobId,
        string payload,
        int itemCount,
        string? commandId = null)
    {
        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO pricing_result_outbox (
                job_id, command_id, payload_json, payload_sha256, item_count, execution_ok,
                state, attempt_count, next_attempt_at, created_at
            ) VALUES (
                @job, @command, @payload, @digest, @count, 0,
                'pending', 0, @now, @now
            )
            """;
        command.Parameters.AddWithValue("@job", jobId);
        command.Parameters.AddWithValue("@command", (object?)commandId ?? DBNull.Value);
        command.Parameters.AddWithValue("@payload", payload);
        command.Parameters.AddWithValue("@digest", digest);
        command.Parameters.AddWithValue("@count", itemCount);
        command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        Assert.Equal(1, command.ExecuteNonQuery());
        return digest;
    }

    private static void RemoveMigration19(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TRIGGER IF EXISTS pricing_result_outbox_quarantine_immutable;
            DROP TRIGGER IF EXISTS pricing_result_outbox_quarantine_no_delete;
            DROP TABLE IF EXISTS pricing_result_outbox_quarantine;
            DROP INDEX IF EXISTS idx_pricing_result_outbox_evidence_identity;
            DELETE FROM schema_migrations WHERE version = @version;
            """;
        command.Parameters.AddWithValue("@version", 19);
        command.ExecuteNonQuery();
    }

    private static void AssertImmutable(string path, string table, string jobId)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = table switch
        {
            "pricing_result_outbox" =>
                "DELETE FROM pricing_result_outbox WHERE job_id = @job",
            "pricing_result_outbox_quarantine" =>
                "DELETE FROM pricing_result_outbox_quarantine WHERE job_id = @job",
            _ => throw new ArgumentOutOfRangeException(nameof(table)),
        };
        command.Parameters.AddWithValue("@job", jobId);
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private sealed class RecordingSigner : IPostSigner
    {
        internal int CallCount { get; private set; }

        public Task<JsonElement?> PostSignedAsync(
            string path,
            object payload,
            CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult<JsonElement?>(null);
        }

        public Task<JsonElement?> PostSignedVerifiedAsync(
            string path,
            object payload,
            string publicKeyDer,
            CancellationToken ct) => PostSignedAsync(path, payload, ct);

        public Task<VerifiedCloudPostResponse?> PostSignedResponseVerifiedAsync(
            string path,
            object payload,
            CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult<VerifiedCloudPostResponse?>(null);
        }
    }
}
