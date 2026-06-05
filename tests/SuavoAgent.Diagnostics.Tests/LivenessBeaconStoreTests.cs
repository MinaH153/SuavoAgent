using System;
using System.IO;
using SuavoAgent.Diagnostics;
using Xunit;

namespace SuavoAgent.Diagnostics.Tests;

/// <summary>Cross-process liveness beacon I/O — the read/write the hang detector depends on.</summary>
public sealed class LivenessBeaconStoreTests : IDisposable
{
    private readonly string _dir;

    public LivenessBeaconStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "liveness-test-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Write_ThenRead_RoundTripsTimestamp_ToMs()
    {
        var store = new LivenessBeaconStore(_dir);
        var t = new DateTimeOffset(2026, 6, 4, 12, 0, 0, 500, TimeSpan.Zero);

        store.Write("SuavoAgent.Core", t);

        Assert.Equal(t.ToUnixTimeMilliseconds(), store.Read("SuavoAgent.Core")!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void Read_NoBeacon_ReturnsNull()
    {
        Assert.Null(new LivenessBeaconStore(_dir).Read("SuavoAgent.Broker"));
    }

    [Fact]
    public void Read_CorruptBeacon_ReturnsNull()
    {
        File.WriteAllText(Path.Combine(_dir, "SuavoAgent.Core.beacon"), "not-a-number");
        Assert.Null(new LivenessBeaconStore(_dir).Read("SuavoAgent.Core"));
    }

    [Fact]
    public void Write_Overwrites_PreviousBeacon()
    {
        var store = new LivenessBeaconStore(_dir);
        var t1 = new DateTimeOffset(2026, 6, 4, 12, 0, 0, TimeSpan.Zero);
        var t2 = t1.AddSeconds(30);

        store.Write("SuavoAgent.Core", t1);
        store.Write("SuavoAgent.Core", t2);

        Assert.Equal(t2.ToUnixTimeMilliseconds(), store.Read("SuavoAgent.Core")!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void Components_AreIsolated()
    {
        var store = new LivenessBeaconStore(_dir);
        store.Write("SuavoAgent.Core", DateTimeOffset.UnixEpoch);

        Assert.NotNull(store.Read("SuavoAgent.Core"));
        Assert.Null(store.Read("SuavoAgent.Broker"));
    }
}
