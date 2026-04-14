using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

public class PhaseGateTests : IDisposable
{
    private readonly AgentStateDb _db;
    private const string SessionId = "sess-1";

    public PhaseGateTests()
    {
        _db = new AgentStateDb(":memory:");
        _db.CreateLearningSession(SessionId, "pharm-1");
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Evaluate_NoSeeds_ReturnsNotReady()
    {
        var gate = new PhaseGate(_db, SessionId, "pattern", seedDigest: null,
            phaseStartedAt: DateTimeOffset.UtcNow.AddHours(-80),
            canaryClean: true, unseededPatternCount: 10);

        var result = gate.Evaluate();
        Assert.False(result.Ready);
        Assert.Contains(result.Gates, g => g.Name == "seeded_confirmation" && !g.Passed);
    }

    [Fact]
    public void Evaluate_CalendarFloorNotMet_ReturnsNotReady()
    {
        var gate = new PhaseGate(_db, SessionId, "pattern", seedDigest: "d-1",
            phaseStartedAt: DateTimeOffset.UtcNow.AddHours(-24),
            canaryClean: true, unseededPatternCount: 10);

        _db.InsertSeedItem("d-1", "query_shape", "qs-1", "2026-04-14T00:00:00Z");
        _db.ConfirmSeedItem("d-1", "query_shape", "qs-1", "2026-04-14T01:00:00Z");

        var result = gate.Evaluate();
        Assert.False(result.Ready);
        Assert.Contains(result.Gates, g => g.Name == "calendar_floor" && !g.Passed);
    }

    [Fact]
    public void Evaluate_AllGatesPass_Pattern()
    {
        var gate = new PhaseGate(_db, SessionId, "pattern", seedDigest: "d-1",
            phaseStartedAt: DateTimeOffset.UtcNow.AddHours(-80),
            canaryClean: true, unseededPatternCount: 6);

        for (int i = 0; i < 5; i++)
            _db.InsertSeedItem("d-1", "query_shape", $"qs-{i}", "2026-04-14T00:00:00Z");

        _db.ConfirmSeedItem("d-1", "query_shape", "qs-0", "2026-04-14T01:00:00Z");
        _db.ConfirmSeedItem("d-1", "query_shape", "qs-1", "2026-04-14T01:00:00Z");
        _db.ConfirmSeedItem("d-1", "query_shape", "qs-2", "2026-04-14T01:00:00Z");
        _db.ConfirmSeedItem("d-1", "query_shape", "qs-3", "2026-04-14T01:00:00Z");
        _db.RejectSeedItem("d-1", "query_shape", "qs-4", "2026-04-14T01:00:00Z");

        var result = gate.Evaluate();
        Assert.True(result.Ready);
        Assert.All(result.Gates, g => Assert.True(g.Passed));
    }

    [Fact]
    public void Evaluate_CanaryWarning_ReturnsNotReady()
    {
        var gate = new PhaseGate(_db, SessionId, "pattern", seedDigest: "d-1",
            phaseStartedAt: DateTimeOffset.UtcNow.AddHours(-80),
            canaryClean: false,
            unseededPatternCount: 10);

        _db.InsertSeedItem("d-1", "query_shape", "qs-0", "2026-04-14T00:00:00Z");
        _db.ConfirmSeedItem("d-1", "query_shape", "qs-0", "2026-04-14T01:00:00Z");

        var result = gate.Evaluate();
        Assert.False(result.Ready);
        Assert.Contains(result.Gates, g => g.Name == "canary_clean" && !g.Passed);
    }

    [Fact]
    public void Evaluate_UnseededBelowMinimum_ReturnsNotReady()
    {
        var gate = new PhaseGate(_db, SessionId, "pattern", seedDigest: "d-1",
            phaseStartedAt: DateTimeOffset.UtcNow.AddHours(-80),
            canaryClean: true, unseededPatternCount: 3);

        _db.InsertSeedItem("d-1", "query_shape", "qs-0", "2026-04-14T00:00:00Z");
        _db.ConfirmSeedItem("d-1", "query_shape", "qs-0", "2026-04-14T01:00:00Z");

        var result = gate.Evaluate();
        Assert.False(result.Ready);
        Assert.Contains(result.Gates, g => g.Name == "unseeded_minimum" && !g.Passed);
    }

    [Fact]
    public void Evaluate_ModelPhase_Uses48hFloor()
    {
        var gate = new PhaseGate(_db, SessionId, "model", seedDigest: "d-1",
            phaseStartedAt: DateTimeOffset.UtcNow.AddHours(-50),
            canaryClean: true, unseededPatternCount: 6);

        _db.InsertSeedItem("d-1", "correlation", "c-1", "2026-04-14T00:00:00Z");
        _db.ConfirmSeedItem("d-1", "correlation", "c-1", "2026-04-14T01:00:00Z");

        var result = gate.Evaluate();
        Assert.True(result.Ready);
        Assert.Contains(result.Gates, g => g.Name == "calendar_floor" && g.Passed);
    }

    [Fact]
    public void Evaluate_ConfirmationBelow50Pct_TriggersAbort()
    {
        var gate = new PhaseGate(_db, SessionId, "pattern", seedDigest: "d-1",
            phaseStartedAt: DateTimeOffset.UtcNow.AddHours(-80),
            canaryClean: true, unseededPatternCount: 10);

        for (int i = 0; i < 4; i++)
            _db.InsertSeedItem("d-1", "query_shape", $"qs-{i}", "2026-04-14T00:00:00Z");
        _db.ConfirmSeedItem("d-1", "query_shape", "qs-0", "2026-04-14T01:00:00Z");

        var result = gate.Evaluate();
        Assert.True(result.AbortAcceleration);
    }
}
