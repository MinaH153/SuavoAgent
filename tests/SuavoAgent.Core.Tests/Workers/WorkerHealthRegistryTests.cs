using System;
using System.Linq;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

/// <summary>
/// Process-wide supervised-worker health. Each ResilientHostedService records a fault here so the
/// heartbeat (HealthSnapshot) can surface a restart-looping / escalated worker to the cloud —
/// closed-loop remediation rather than only detecting a fully-silent agent.
/// </summary>
public class WorkerHealthRegistryTests
{
    [Fact]
    public void Snapshot_EmptyByDefault()
    {
        var r = new WorkerHealthRegistry();
        Assert.Empty(r.Snapshot());
    }

    [Fact]
    public void RecordFault_SurfacesWorker()
    {
        var r = new WorkerHealthRegistry();
        var at = DateTimeOffset.UtcNow;

        r.RecordFault("rx-detection", restartCount: 1, escalated: false, faultedAtUtc: at);

        var w = Assert.Single(r.Snapshot());
        Assert.Equal("rx-detection", w.Name);
        Assert.Equal(1, w.RestartCount);
        Assert.False(w.Escalated);
        Assert.Equal(at, w.LastFaultUtc);
    }

    [Fact]
    public void RecordFault_LatestPerWorker_Overwrites()
    {
        var r = new WorkerHealthRegistry();
        r.RecordFault("rx-detection", 1, false, DateTimeOffset.UtcNow);
        r.RecordFault("rx-detection", 6, true, DateTimeOffset.UtcNow);

        var w = Assert.Single(r.Snapshot());
        Assert.Equal(6, w.RestartCount);
        Assert.True(w.Escalated);
    }

    [Fact]
    public void RecordFault_TracksMultipleWorkers_Independently()
    {
        var r = new WorkerHealthRegistry();
        r.RecordFault("rx-detection", 2, false, DateTimeOffset.UtcNow);
        r.RecordFault("heartbeat", 1, false, DateTimeOffset.UtcNow);

        var snap = r.Snapshot().OrderBy(w => w.Name).ToArray();
        Assert.Equal(2, snap.Length);
        Assert.Equal("heartbeat", snap[0].Name);
        Assert.Equal("rx-detection", snap[1].Name);
    }
}
