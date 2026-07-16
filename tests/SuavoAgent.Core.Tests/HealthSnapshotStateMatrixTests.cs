using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests;

public sealed class HealthSnapshotStateMatrixTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        "suavo-health-state-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly AgentStateDb _db;

    public HealthSnapshotStateMatrixTests()
    {
        _db = new AgentStateDb(_path);
    }

    [Fact]
    public void ActiveLearningCanaryAndVisionTelemetry_AreProjectedWithoutRawObservationValues()
    {
        const string pharmacy = "pharmacy-health-active";
        const string session = "session-health-active";
        _db.CreateLearningSession(session, pharmacy);
        _db.InsertBehavioralEvent(
            session, 1, "treesnapshot", null, new string('a', 64), null,
            null, null, null, null, null, null, null, 1,
            "2026-07-12T12:00:00Z");
        _db.UpsertCanaryHold(pharmacy, "pioneerrx", "warning", "baseline-health");
        _db.IncrementCanaryHoldCycles(pharmacy, "pioneerrx");
        var vision = new VisionCaptureTelemetry();
        vision.RecordCaptured("storage-structural", "command-structural");
        var workers = new WorkerHealthRegistry();
        workers.RecordFault("learning", 1, false, DateTimeOffset.UtcNow);
        using var services = new ServiceCollection()
            .AddSingleton(vision)
            .AddSingleton(workers)
            .BuildServiceProvider();

        var snapshot = new HealthSnapshot(
            new AgentOptions
            {
                AgentId = "agent-health-active",
                PharmacyId = pharmacy,
                MachineFingerprint = "machine-health-active",
                Vision = new VisionOptions
                {
                    Enabled = true,
                    PeriodicCapture = new VisionPeriodicCaptureOptions { Enabled = true },
                },
            },
            _db,
            services,
            DateTimeOffset.UtcNow.AddMinutes(-1)).Take();

        Assert.Equal("drift_hold", snapshot.GetProperty("canary").GetProperty("status").GetString());
        Assert.Equal(1, snapshot.GetProperty("canary").GetProperty("blockedCycles").GetInt32());
        var behavioral = snapshot.GetProperty("behavioral");
        Assert.Equal(session, behavioral.GetProperty("sessionId").GetString());
        Assert.Equal(1, behavioral.GetProperty("totalEvents").GetInt64());
        Assert.Equal(1, behavioral.GetProperty("treeSnapshotCount").GetInt64());
        var capture = snapshot.GetProperty("vision").GetProperty("capture");
        Assert.Equal(1, capture.GetProperty("attemptCount").GetInt64());
        Assert.Equal("captured", capture.GetProperty("lastOutcome").GetString());
        Assert.Single(snapshot.GetProperty("workers").EnumerateArray());
    }

    [Fact]
    public void MissingIdentityAndServices_UseExplicitEmptyStateInsteadOfInventedHealth()
    {
        using var services = new ServiceCollection().BuildServiceProvider();

        var snapshot = new HealthSnapshot(
            new AgentOptions
            {
                AgentId = null,
                PharmacyId = null,
                MachineFingerprint = null,
            },
            _db,
            services,
            DateTimeOffset.UtcNow).Take();

        Assert.Equal(System.Text.Json.JsonValueKind.Null, snapshot.GetProperty("agentId").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, snapshot.GetProperty("pharmacyId").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, snapshot.GetProperty("machineFingerprint").ValueKind);
        Assert.Equal("clean", snapshot.GetProperty("canary").GetProperty("status").GetString());
        Assert.False(snapshot.GetProperty("writebackEngine").GetProperty("enabled").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null,
            snapshot.GetProperty("vision").GetProperty("capture").ValueKind);
    }

    [Theory]
    [InlineData(0L, 0L, 0.0)]
    [InlineData(5L, 5L, 50.0)]
    [InlineData(3L, 1L, 25.0)]
    public void DropRate_IsBoundedByStoredPlusDroppedEvents(
        long stored,
        long dropped,
        double expected)
    {
        var method = typeof(HealthSnapshot).GetMethod(
            "CalculateDropRate",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.Equal(expected, Assert.IsType<double>(method!.Invoke(null, [stored, dropped])));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_path); } catch { }
    }
}
