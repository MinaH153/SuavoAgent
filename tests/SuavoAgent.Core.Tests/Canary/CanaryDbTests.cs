using SuavoAgent.Contracts.Canary;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Canary;

public class CanaryDbTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;

    public CanaryDbTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"canary_test_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
    }

    [Fact]
    public void UpsertAndRetrieveBaseline()
    {
        var baseline = new ContractBaseline("pioneerrx", "obj1", "stat1", "qry1", "shape1",
            "composite1", "{}", 1);
        _db.UpsertCanaryBaseline("pharm-1", baseline);
        var loaded = _db.GetCanaryBaseline("pharm-1", "pioneerrx");
        Assert.NotNull(loaded);
        Assert.Equal("obj1", loaded!.ObjectFingerprint);
        Assert.Equal(1, loaded.SchemaEpoch);
    }

    [Fact]
    public void Upsert_UpdatesExistingBaseline()
    {
        var b1 = new ContractBaseline("pioneerrx", "obj1", "stat1", "qry1", "shape1",
            "composite1", "{}", 1);
        _db.UpsertCanaryBaseline("pharm-1", b1);
        var b2 = new ContractBaseline("pioneerrx", "obj2", "stat2", "qry2", "shape2",
            "composite2", "{}", 2);
        _db.UpsertCanaryBaseline("pharm-1", b2);
        var loaded = _db.GetCanaryBaseline("pharm-1", "pioneerrx");
        Assert.Equal("obj2", loaded!.ObjectFingerprint);
        Assert.Equal(2, loaded.SchemaEpoch);
    }

    [Fact]
    public void InsertAndRetrieveIncident()
    {
        _db.InsertCanaryIncident("pharm-1", "pioneerrx", "critical",
            "[\"status_map\"]", "base1", "obs1", "Status changed", 5);
        var incidents = _db.GetOpenCanaryIncidents("pharm-1");
        Assert.Single(incidents);
        Assert.Equal("critical", incidents[0].Severity);
        Assert.Equal(5, incidents[0].DroppedBatchRowCount);
    }

    [Fact]
    public void HoldState_PersistsAndSurvivesReopen()
    {
        _db.UpsertCanaryHold("pharm-1", "pioneerrx", "critical", "base1");
        _db.IncrementCanaryHoldCycles("pharm-1", "pioneerrx");
        _db.IncrementCanaryHoldCycles("pharm-1", "pioneerrx");
        _db.Dispose();
        using var db2 = new AgentStateDb(_dbPath);
        var hold = db2.GetCanaryHold("pharm-1", "pioneerrx");
        Assert.NotNull(hold);
        Assert.Equal(2, hold.Value.BlockedCycles);
    }

    [Fact]
    public void ClearHold()
    {
        _db.UpsertCanaryHold("pharm-1", "pioneerrx", "critical", "base1");
        _db.ClearCanaryHold("pharm-1", "pioneerrx");
        var hold = _db.GetCanaryHold("pharm-1", "pioneerrx");
        Assert.Null(hold);
    }

    [Fact]
    public void NoBaseline_ReturnsNull()
    {
        var loaded = _db.GetCanaryBaseline("nonexistent", "pioneerrx");
        Assert.Null(loaded);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
