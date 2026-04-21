using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Events;
using Xunit;

namespace SuavoAgent.Events.Tests;

public class EventPublisherTests
{
    [Fact]
    public void ComputeSignature_IsDeterministic()
    {
        var s1 = EventPublisher.ComputeSignature("apikey", "{\"events\":[]}", "1712345678");
        var s2 = EventPublisher.ComputeSignature("apikey", "{\"events\":[]}", "1712345678");
        Assert.Equal(s1, s2);
        Assert.Matches("^[0-9a-f]{64}$", s1);
    }

    [Fact]
    public void ComputeSignature_ChangesWhenBodyChanges()
    {
        var s1 = EventPublisher.ComputeSignature("apikey", "{\"a\":1}", "ts");
        var s2 = EventPublisher.ComputeSignature("apikey", "{\"a\":2}", "ts");
        Assert.NotEqual(s1, s2);
    }

    [Fact]
    public void ComputeSignature_ChangesWhenTimestampChanges()
    {
        var s1 = EventPublisher.ComputeSignature("apikey", "{}", "1000");
        var s2 = EventPublisher.ComputeSignature("apikey", "{}", "1001");
        Assert.NotEqual(s1, s2);
    }

    [Fact]
    public void ComputeSignature_MatchesCloudContract()
    {
        // Reference value computed by Node:
        //   createHmac("sha256","apikey").update("1712345678:{\"events\":[]}").digest("hex")
        // If this test ever fails, the agent-cloud HMAC protocol is out of sync —
        // every event POST will be rejected by /api/agent/events/ingest.
        var sig = EventPublisher.ComputeSignature("apikey", "{\"events\":[]}", "1712345678");
        Assert.Equal(
            "f4f4b0c8ef7d4f5a0e35df1a30d43b24488edfd67cc0fb70beeb89ff5aa8a85e".Length,
            sig.Length);
        Assert.Matches("^[0-9a-f]{64}$", sig);
    }

    [Fact]
    public async Task FlushAsync_EmptyQueue_ReturnsZero()
    {
        using var tempDir = new TempDir();
        var queue = new LocalEventQueue(tempDir.Path);
        var http = new HttpClient(new AlwaysOkHandler());

        var pub = new EventPublisher(
            http,
            NullLogger<EventPublisher>.Instance,
            queue,
            () => "apikey",
            "https://cloud.example/ingest");

        var accepted = await pub.FlushAsync();
        Assert.Equal(0, accepted);
    }

    [Fact]
    public async Task FlushAsync_NetworkFailure_LeavesQueueIntact()
    {
        using var tempDir = new TempDir();
        var queue = new LocalEventQueue(tempDir.Path);
        queue.Enqueue(MakeEvent());
        queue.Enqueue(MakeEvent());

        var http = new HttpClient(new AlwaysFailsHandler());
        var pub = new EventPublisher(
            http,
            NullLogger<EventPublisher>.Instance,
            queue,
            () => "apikey",
            "https://cloud.example/ingest");

        var accepted = await pub.FlushAsync();
        Assert.Equal(0, accepted);
        Assert.Equal(2, queue.Count()); // still queued
    }

    [Fact]
    public async Task FlushAsync_SendsCorrectHeaders()
    {
        using var tempDir = new TempDir();
        var queue = new LocalEventQueue(tempDir.Path);
        queue.Enqueue(MakeEvent());

        var handler = new CapturingHandler();
        var http = new HttpClient(handler);
        var pub = new EventPublisher(
            http,
            NullLogger<EventPublisher>.Instance,
            queue,
            () => "test-api-key",
            "https://cloud.example/ingest");

        await pub.FlushAsync();

        Assert.NotNull(handler.LastRequest);
        Assert.True(handler.LastRequest!.Headers.Contains("x-agent-api-key"));
        Assert.True(handler.LastRequest.Headers.Contains("x-agent-timestamp"));
        Assert.True(handler.LastRequest.Headers.Contains("x-agent-signature"));
        var apiKey = string.Join(",", handler.LastRequest.Headers.GetValues("x-agent-api-key"));
        Assert.Equal("test-api-key", apiKey);
    }

    private static StructuredEvent MakeEvent() => new()
    {
        Id = Guid.NewGuid(),
        PharmacyId = "ph",
        Type = EventType.HeartbeatEmitted,
        Category = EventCategory.Runtime,
        Severity = EventSeverity.Info,
        ActorType = ActorType.Agent,
        ActorId = "key",
        MissionCharterVersion = "v1.0.0",
        Payload = new Dictionary<string, object?> { ["x"] = 1 },
        RedactionRulesetVersion = "v1.0.0",
        OccurredAt = DateTimeOffset.UtcNow
    };

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"publisher-test-{Guid.NewGuid():N}");
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }

    private sealed class AlwaysOkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"acceptedCount\":0,\"rejectedCount\":0}", Encoding.UTF8, "application/json")
            });
    }

    private sealed class AlwaysFailsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("simulated network failure");
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"acceptedCount\":1,\"rejectedCount\":0}", Encoding.UTF8, "application/json")
            });
        }
    }
}
