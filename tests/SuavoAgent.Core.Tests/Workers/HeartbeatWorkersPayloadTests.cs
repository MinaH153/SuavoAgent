using System;
using System.Linq;
using System.Text.Json;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

/// <summary>
/// The heartbeat wire's supervised-worker liveness block (self-heal Chunk 3b). The cloud's
/// agent-health-watch reads stats.workers[] to remediate a restart-looping/escalated worker at
/// worker granularity, vs. only detecting a fully-silent agent. No PHI — static worker names only.
/// </summary>
public class HeartbeatWorkersPayloadTests
{
    private static JsonElement Serialize(object[] payload)
        => JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(payload));

    [Fact]
    public void BuildWorkersPayload_EmptyWhenNoRegistry()
    {
        Assert.Empty(HeartbeatWorker.BuildWorkersPayload(null));
    }

    [Fact]
    public void BuildWorkersPayload_EmptyWhenNoFaults()
    {
        Assert.Empty(HeartbeatWorker.BuildWorkersPayload(new WorkerHealthRegistry()));
    }

    [Fact]
    public void BuildWorkersPayload_SurfacesFault_AsWireShape()
    {
        var registry = new WorkerHealthRegistry();
        registry.RecordFault("rx-detection", restartCount: 6, escalated: true, faultedAtUtc: DateTimeOffset.UtcNow);

        var json = Serialize(HeartbeatWorker.BuildWorkersPayload(registry));

        var w = json.EnumerateArray().Single();
        Assert.Equal("rx-detection", w.GetProperty("name").GetString());
        Assert.Equal(6, w.GetProperty("restartCount").GetInt32());
        Assert.True(w.GetProperty("escalated").GetBoolean());
        Assert.True(DateTimeOffset.TryParse(w.GetProperty("lastFaultUtc").GetString(), out _));
    }
}
