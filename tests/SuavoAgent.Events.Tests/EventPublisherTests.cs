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
        using var tempDir = new TempDir();
        var queue = new LocalEventQueue(tempDir.Path);
        var http = new HttpClient();
        byte[] key = Encoding.UTF8.GetBytes("test-key-32-bytes-long-padding!!");

        var pub = new EventPublisher(
            http,
            NullLogger<EventPublisher>.Instance,
            queue,
            () => key,
            "https://cloud.example/ingest",
            "ph");

        var body = Encoding.UTF8.GetBytes("{\"events\":[]}");
        var sig1 = pub.ComputeSignature(body, "1712345678");
        var sig2 = pub.ComputeSignature(body, "1712345678");
        Assert.Equal(sig1, sig2);
        Assert.Matches("^[0-9a-f]{64}$", sig1);
    }

    [Fact]
    public void ComputeSignature_ChangesWhenBodyChanges()
    {
        using var tempDir = new TempDir();
        var queue = new LocalEventQueue(tempDir.Path);
        var http = new HttpClient();
        byte[] key = Encoding.UTF8.GetBytes("test-key-32-bytes-long-padding!!");

        var pub = new EventPublisher(
            http,
            NullLogger<EventPublisher>.Instance,
            queue,
            () => key,
            "https://cloud.example/ingest",
            "ph");

        var s1 = pub.ComputeSignature(Encoding.UTF8.GetBytes("{\"a\":1}"), "ts");
        var s2 = pub.ComputeSignature(Encoding.UTF8.GetBytes("{\"a\":2}"), "ts");
        Assert.NotEqual(s1, s2);
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
            () => new byte[32],
            "https://cloud.example/ingest",
            "ph");

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
            () => new byte[32],
            "https://cloud.example/ingest",
            "ph");

        var accepted = await pub.FlushAsync();
        Assert.Equal(0, accepted);
        Assert.Equal(2, queue.Count()); // still queued
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
}
