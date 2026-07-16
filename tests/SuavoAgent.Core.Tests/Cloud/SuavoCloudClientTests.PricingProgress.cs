using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public sealed partial class SuavoCloudClientTests
{
    [Fact]
    public async Task PricingProgress_PostsOnlyFixedPhiFreeFieldsWithHmac()
    {
        var commandId = Guid.NewGuid().ToString("D");
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    success = true,
                    data = new
                    {
                        commandId,
                        sequence = 4,
                        stage = "pricing_items",
                        idempotent = false,
                    },
                }),
                Encoding.UTF8,
                "application/json"),
        });
        using var client = new SuavoCloudClient(
            new AgentOptions
            {
                ApiKey = "agent-secret",
                CloudUrl = "https://suavollc.com",
            },
            handler);
        var progress = new PioneerRxTop500ExportProgress(
            commandId,
            4,
            "pricing_items",
            17,
            500,
            2,
            new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));

        Assert.True(await client.TryPostPricingProgressAsync(
            progress,
            CancellationToken.None));
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal(
            $"/api/agent/commands/{commandId}/pricing-progress",
            handler.LastPath);
        Assert.NotNull(handler.LastSignature);
        Assert.NotNull(handler.LastNonce);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(handler.LastBody!))).ToLowerInvariant(),
            handler.LastContentSha256);

        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(
            new[]
            {
                "needsReview", "occurredAt", "processed",
                "sequence", "stage", "total",
            },
            body.RootElement.EnumerateObject()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(4, body.RootElement.GetProperty("sequence").GetInt32());
        Assert.Equal("pricing_items", body.RootElement.GetProperty("stage").GetString());
        Assert.Equal(17, body.RootElement.GetProperty("processed").GetInt32());
        Assert.Equal(500, body.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(2, body.RootElement.GetProperty("needsReview").GetInt32());
    }

    [Fact]
    public async Task PricingProgress_RejectsFreeTextOrNonMonotonicCountsBeforeNetwork()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"success\":true}"),
        });
        using var client = new SuavoCloudClient(
            new AgentOptions
            {
                ApiKey = "agent-secret",
                CloudUrl = "https://suavollc.com",
            },
            handler);
        var invalid = new PioneerRxTop500ExportProgress(
            Guid.NewGuid().ToString("D"),
            4,
            "pricing 00093505698",
            2,
            1,
            0,
            DateTimeOffset.UtcNow);

        Assert.False(await client.TryPostPricingProgressAsync(
            invalid,
            CancellationToken.None));
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task PricingProgressPublisher_StopsAfterFirstGapOrTransportFailure()
    {
        var commandId = Guid.NewGuid().ToString("D");
        var transport = new ProgressTransport(true, false, true);
        var publisher = new PricingCommandProgressPublisher(
            transport,
            commandId,
            new FixedProgressTimeProvider());

        Assert.True(await publisher.PublishWaitingToStartAsync(
            CancellationToken.None));
        Assert.False(await publisher.PublishFixedAsync(
            new PioneerRxTop500ExportProgress(
                commandId,
                2,
                PioneerRxTop500ExportStages.GeneratingReport,
                0,
                0,
                0,
                DateTimeOffset.UtcNow),
            CancellationToken.None));
        Assert.False(await publisher.PublishFixedAsync(
            new PioneerRxTop500ExportProgress(
                commandId,
                3,
                PioneerRxTop500ExportStages.ExportingReport,
                0,
                0,
                0,
                DateTimeOffset.UtcNow),
            CancellationToken.None));

        Assert.Equal(2, transport.Events.Count);
        Assert.Equal(new[] { 1, 2 }, transport.Events.Select(item => item.Sequence));
    }

    [Fact]
    public async Task InitialProgressTransportFailure_DoesNotAbortWorklistContinuation()
    {
        var commandId = Guid.NewGuid().ToString("D");
        var transport = new ProgressTransport(false);
        var publisher = new PricingCommandProgressPublisher(
            transport,
            commandId,
            new FixedProgressTimeProvider());
        var continuationCalls = 0;

        var result = await HeartbeatWorker.ContinueAfterBestEffortInitialProgressAsync(
            publisher,
            _ =>
            {
                continuationCalls++;
                return Task.FromResult("worklist_built");
            },
            CancellationToken.None);

        Assert.Equal("worklist_built", result);
        Assert.Equal(1, continuationCalls);
        Assert.Single(transport.Events);
        Assert.Equal("waiting_to_start", transport.Events[0].Stage);
    }

    private sealed class ProgressTransport(params bool[] outcomes) :
        IPricingProgressTransport
    {
        private readonly Queue<bool> _outcomes = new(outcomes);
        internal List<PioneerRxTop500ExportProgress> Events { get; } = [];

        public Task<bool> TryPostPricingProgressAsync(
            PioneerRxTop500ExportProgress progress,
            CancellationToken ct)
        {
            Events.Add(progress);
            return Task.FromResult(_outcomes.Dequeue());
        }
    }

    private sealed class FixedProgressTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(
            2026,
            7,
            15,
            12,
            0,
            0,
            TimeSpan.Zero);
    }
}
