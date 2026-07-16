using Microsoft.Data.Sqlite;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

public sealed class PricingLegacyManualDeliveryRecoveryTests
{
    [Fact]
    public void CompletedLegacyManualIntent_IsQuarantinedWithoutRelabeling_OnEveryRestart()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"suavo-pricing-manual-upgrade-{Guid.NewGuid():N}.db");
        const string jobId = "legacy-manual-delivery";
        try
        {
            using (var initial = new AgentStateDb(path))
            {
                initial.UpsertPricingJob(
                    new PricingJobSpec(
                        jobId,
                        @"C:\Pricing.xlsx",
                        "NDC",
                        "Supplier",
                        "Cost"),
                    PricingJobStatus.Completed,
                    0,
                    0,
                    0);
            }
            using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT INTO pricing_result_delivery_intents (
                        job_id, command_id, source_upload_id, source_mode,
                        approval_id, grant_digest, prepared_at, terminal_at)
                    VALUES (
                        @job, NULL, NULL, 'manual',
                        NULL, NULL, @prepared, NULL)
                    """;
                insert.Parameters.AddWithValue("@job", jobId);
                insert.Parameters.AddWithValue(
                    "@prepared", DateTimeOffset.UtcNow.ToString("O"));
                Assert.Equal(1, insert.ExecuteNonQuery());
            }

            using (var recovered = new AgentStateDb(path))
            {
                var quarantine = Assert.IsType<
                    AgentStateDb.PricingResultDeliveryQuarantine>(
                    recovered.GetPricingResultDeliveryQuarantine(jobId));
                Assert.Null(quarantine.CommandId);
                Assert.Equal("manual", quarantine.SourceMode);
                Assert.Equal(
                    "pricing_result_source_invalid",
                    quarantine.ReasonCode);
                Assert.Null(recovered.GetPricingResultOutbox(jobId));
            }

            using var restarted = new AgentStateDb(path);
            var retained = Assert.IsType<
                AgentStateDb.PricingResultDeliveryQuarantine>(
                restarted.GetPricingResultDeliveryQuarantine(jobId));
            Assert.Equal("manual", retained.SourceMode);
            Assert.Null(restarted.GetPricingResultOutbox(jobId));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
