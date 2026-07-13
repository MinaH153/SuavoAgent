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
    [Fact]
    public async Task FailedOrdinaryUpload_RetriesExactDurablePayloadWithoutRereadingResults()
    {
        var spec = AuthorizePricingJob(_db, new PricingJobSpec(
            "99999999999999999999999999999999",
            @"C:\Pricing.xlsx", "NDC", "Supplier", "Cost"));
        const string commandId = "77777777-7777-4777-8777-777777777777";
        PreparePricingCommandDelivery(_db, commandId, spec);
        _db.SavePricingResult(new SupplierPriceResult(
            spec.JobId, 2, "55111064501", true, "McKesson", 0.0316m, null));
        _db.UpsertPricingJob(spec, PricingJobStatus.Completed, 1, 1, 0);
        var signer = new RejectOncePostSigner();
        var uploader = CreateUploader(signer, _db);
        var execution = new PricingJobExecutionResult(
            new PricingJobProgress(spec.JobId, 1, 1, 0, PricingJobStatus.Completed),
            "sql", true, null);
        var first = await uploader.UploadAsync(
            spec, execution, commandId,
            CancellationToken.None);
        Assert.False(first.Accepted);
        var pending = Assert.IsType<AgentStateDb.PricingResultOutboxEntry>(
            _db.GetPricingResultOutbox(spec.JobId));
        Assert.Equal("pending", pending.State);
        Assert.Equal(pending.PayloadJson, signer.Payloads[0]);

        // Simulate mutable local state after the failed transmission. The
        // retry must use the committed payload, never this replacement row.
        _db.SavePricingResult(new SupplierPriceResult(
            spec.JobId, 2, "00000000000", true, "Changed", 9.99m, null));
        await uploader.FlushPendingAsync(
            CancellationToken.None, includeDeferred: true);

        Assert.Equal(2, signer.Payloads.Count);
        Assert.Equal(signer.Payloads[0], signer.Payloads[1]);
        Assert.Contains("55111064501", signer.Payloads[1]);
        Assert.DoesNotContain("00000000000", signer.Payloads[1]);
        Assert.Equal("accepted", _db.GetPricingResultOutbox(spec.JobId)!.State);
    }

    [Fact]
    public void PendingResultOutbox_SurvivesDatabaseRestartWithExactPayloadAndSourceBinding()
    {
        var path = Path.Combine(_tempDir, "restart-state.db");
        var sourceId = Guid.NewGuid();
        AgentStateDb.PricingResultOutboxEntry staged;
        using (var first = new AgentStateDb(path))
        {
            var payload = PricingJobCloudUploader.BuildPersistedPayloadEnvelope(
                "restart-job",
                "restart-command",
                PricingJobStatus.Completed,
                "sql",
                1,
                1,
                0,
                [new SupplierPriceResult(
                    "restart-job", 2, "55111064501", true,
                    "McKesson", 1.25m, null)],
                "11111111-1111-4111-8111-111111111111",
                new string('a', 64));
            staged = first.StagePricingResultPayload(
                "restart-job", "restart-command", sourceId,
                payload.Json, payload.ItemCount, true);
        }

        using var recovered = new AgentStateDb(path);
        var pending = Assert.IsType<AgentStateDb.PricingResultOutboxEntry>(
            recovered.GetPricingResultOutbox("restart-job"));

        Assert.Equal("pending", pending.State);
        Assert.Equal(staged.PayloadJson, pending.PayloadJson);
        Assert.Equal(staged.PayloadSha256, pending.PayloadSha256);
        Assert.Equal(sourceId, pending.SourceUploadId);
        Assert.Single(recovered.GetAllPendingPricingResultPayloads(20));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalExecutionCommit_AtomicallyStagesPayloadBeforeCallerCrash(
        bool stagedSource)
    {
        var path = Path.Combine(_tempDir, $"terminal-crash-{stagedSource}.db");
        var sourceId = stagedSource ? Guid.NewGuid() : (Guid?)null;
        var spec = new PricingJobSpec(
            stagedSource ? "terminal-staged" : "terminal-ordinary",
            @"C:\Pricing.xlsx", "NDC", "Supplier", "Cost");
        AgentStateDb.PricingResultOutboxEntry committed;
        using (var first = new AgentStateDb(path))
        {
            var uploader = CreateUploader(new RecordingPostSigner(), first);
            spec = AuthorizePricingJob(first, spec);
            RegisterPricingCommandBinding(
                first,
                "88888888-8888-4888-8888-888888888888",
                spec);
            uploader.PrepareDelivery(
                spec,
                "88888888-8888-4888-8888-888888888888",
                sourceId,
                PricingExecutorMode.SqlFirst);
            first.SavePricingResult(new SupplierPriceResult(
                spec.JobId, 2, "55111064501", true, "McKesson", 0.0316m, null));

            // This is the executor's terminal DB boundary. Simulate a process
            // crash immediately after it returns and before UploadAsync runs.
            first.UpsertPricingJob(spec, PricingJobStatus.Completed, 1, 1, 0);
            committed = Assert.IsType<AgentStateDb.PricingResultOutboxEntry>(
                first.GetPricingResultOutbox(spec.JobId));
            Assert.Equal(sourceId, committed.SourceUploadId);
            Assert.Equal("pending", committed.State);
        }

        var signer = new RecordingPostSigner();
        using var recovered = new AgentStateDb(path);
        var recoveredUploader = CreateUploader(signer, recovered);
        await recoveredUploader.FlushPendingAsync(
            CancellationToken.None, includeDeferred: true);

        Assert.Equal(committed.PayloadJson, JsonSerializer.Serialize(signer.Payload));
        Assert.Equal("accepted", recovered.GetPricingResultOutbox(spec.JobId)!.State);
    }

    [Fact]
    public void Startup_ReconstructsTerminalIntentWhenLegacyCrashLeftNoOutbox()
    {
        var path = Path.Combine(_tempDir, "legacy-terminal-gap.db");
        var sourceId = Guid.NewGuid();
        var spec = new PricingJobSpec(
            "legacy-terminal", @"C:\Pricing.xlsx", "NDC", "Supplier", "Cost");
        using (var first = new AgentStateDb(path))
        {
            var uploader = CreateUploader(new RecordingPostSigner(), first);
            spec = AuthorizePricingJob(first, spec);
            RegisterPricingCommandBinding(
                first,
                "77777777-7777-4777-8777-777777777777",
                spec);
            uploader.PrepareDelivery(
                spec,
                "77777777-7777-4777-8777-777777777777",
                sourceId,
                PricingExecutorMode.SqlFirst);
        }

        // Reproduce the old crash shape directly: terminal job/results were
        // durable but the outbox write never happened.
        using (var raw = new SqliteConnection($"Data Source={path}"))
        {
            raw.Open();
            using var transaction = raw.BeginTransaction();
            using (var result = raw.CreateCommand())
            {
                result.Transaction = transaction;
                result.CommandText = """
                    INSERT INTO pricing_results (
                        job_id, row_index, ndc, found, supplier_name, cost_per_unit
                    ) VALUES (@job, 2, @ndc, 1, @supplier, @cost)
                    """;
                result.Parameters.AddWithValue("@job", spec.JobId);
                result.Parameters.AddWithValue("@ndc", "55111064501");
                result.Parameters.AddWithValue("@supplier", "McKesson");
                result.Parameters.AddWithValue("@cost", 0.0316m);
                result.ExecuteNonQuery();
            }
            using (var terminal = raw.CreateCommand())
            {
                terminal.Transaction = transaction;
                terminal.CommandText = """
                    UPDATE pricing_jobs
                       SET status = 'completed', total_items = 1,
                           completed_items = 1, failed_items = 0
                     WHERE job_id = @job
                    """;
                terminal.Parameters.AddWithValue("@job", spec.JobId);
                Assert.Equal(1, terminal.ExecuteNonQuery());
            }
            transaction.Commit();
        }

        using var recovered = new AgentStateDb(path);
        var outbox = Assert.IsType<AgentStateDb.PricingResultOutboxEntry>(
            recovered.GetPricingResultOutbox(spec.JobId));
        Assert.Equal("pending", outbox.State);
        Assert.Equal(sourceId, outbox.SourceUploadId);
        Assert.Contains("55111064501", outbox.PayloadJson);
    }
}
