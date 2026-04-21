using SuavoAgent.Events;
using Xunit;

namespace SuavoAgent.Events.Tests;

public class LocalEventQueueTests
{
    private static StructuredEvent MakeEvent(DateTimeOffset? at = null, string type = EventType.HeartbeatEmitted) =>
        new()
        {
            Id = Guid.NewGuid(),
            PharmacyId = "ph",
            Type = type,
            Category = EventCategory.Runtime,
            Severity = EventSeverity.Info,
            ActorType = ActorType.Agent,
            ActorId = "key-1",
            MissionCharterVersion = "v1.0.0",
            Payload = new Dictionary<string, object?> { ["x"] = 1 },
            RedactionRulesetVersion = "v1.0.0",
            OccurredAt = at ?? DateTimeOffset.UtcNow
        };

    [Fact]
    public void Enqueue_Then_Peek_ReturnsEvent()
    {
        using var tempDir = new TempDir();
        var q = new LocalEventQueue(tempDir.Path);
        var evt = MakeEvent();
        q.Enqueue(evt);
        var batch = q.PeekBatch();
        Assert.Single(batch);
        Assert.Equal(evt.Id, batch[0].Id);
    }

    [Fact]
    public void PeekBatch_Returns_OldestFirst()
    {
        using var tempDir = new TempDir();
        var q = new LocalEventQueue(tempDir.Path);
        var now = DateTimeOffset.UtcNow;
        var older = MakeEvent(now.AddMinutes(-5));
        var newer = MakeEvent(now);
        q.Enqueue(newer);
        q.Enqueue(older);
        var batch = q.PeekBatch();
        Assert.Equal(2, batch.Count);
        Assert.Equal(older.Id, batch[0].Id);
        Assert.Equal(newer.Id, batch[1].Id);
    }

    [Fact]
    public void Ack_Removes_AckedEvents()
    {
        using var tempDir = new TempDir();
        var q = new LocalEventQueue(tempDir.Path);
        var a = MakeEvent();
        var b = MakeEvent();
        q.Enqueue(a);
        q.Enqueue(b);
        Assert.Equal(2, q.Count());
        q.Ack(new[] { a.Id });
        Assert.Equal(1, q.Count());
        var remaining = q.PeekBatch();
        Assert.Single(remaining);
        Assert.Equal(b.Id, remaining[0].Id);
    }

    [Fact]
    public void SizeCap_DropsOldestWhenExceeded()
    {
        using var tempDir = new TempDir();
        // Cap of ~1 KB so a handful of events trigger drops.
        var q = new LocalEventQueue(tempDir.Path, maxBytes: 1024);
        for (var i = 0; i < 20; i++)
        {
            q.Enqueue(MakeEvent(DateTimeOffset.UtcNow.AddSeconds(i)));
        }
        var count = q.Count();
        // Cannot assert an exact count (depends on JSON serialization size),
        // but we should have fewer than 20 (at least one drop occurred).
        Assert.InRange(count, 1, 19);
    }

    [Fact]
    public void PeekBatch_SkipsCorruptFiles()
    {
        using var tempDir = new TempDir();
        var q = new LocalEventQueue(tempDir.Path);
        q.Enqueue(MakeEvent());

        // Write a corrupt JSON file alongside the valid ones.
        File.WriteAllText(Path.Combine(tempDir.Path, "00000000000000000000-deadbeefdeadbeefdeadbeefdeadbeef.json"), "{not valid json}");

        var batch = q.PeekBatch();
        Assert.Single(batch); // corrupt file skipped
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"events-test-{Guid.NewGuid():N}");
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
