using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

public class LearningSessionTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;

    public LearningSessionTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_learn_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
    }

    [Fact]
    public void CreateSession_Persists()
    {
        _db.CreateLearningSession("sess-1", "pharm-1");
        var session = _db.GetLearningSession("sess-1");
        Assert.NotNull(session);
        Assert.Equal("pharm-1", session.Value.PharmacyId);
        Assert.Equal("discovery", session.Value.Phase);
        Assert.Equal("observer", session.Value.Mode);
    }

    [Fact]
    public void UpdatePhase_Transitions()
    {
        _db.CreateLearningSession("sess-1", "pharm-1");
        _db.UpdateLearningPhase("sess-1", "pattern");
        var session = _db.GetLearningSession("sess-1");
        Assert.Equal("pattern", session.Value.Phase);
    }

    [Fact]
    public void UpdateMode_Transitions()
    {
        _db.CreateLearningSession("sess-1", "pharm-1");
        _db.UpdateLearningMode("sess-1", "supervised");
        var session = _db.GetLearningSession("sess-1");
        Assert.Equal("supervised", session.Value.Mode);
    }

    [Fact]
    public void InsertObservedProcess_Persists()
    {
        _db.CreateLearningSession("sess-1", "pharm-1");
        _db.UpsertObservedProcess("sess-1", "PioneerPharmacy.exe",
            @"C:\Program Files\PioneerRx\PioneerPharmacy.exe",
            windowTitleScrubbed: "Point of Sale", isPmsCandidate: true);

        var processes = _db.GetObservedProcesses("sess-1");
        Assert.Single(processes);
        Assert.Equal("PioneerPharmacy.exe", processes[0].ProcessName);
        Assert.True(processes[0].IsPmsCandidate);
    }

    [Fact]
    public void UpsertObservedProcess_IncrementsCount()
    {
        _db.CreateLearningSession("sess-1", "pharm-1");
        _db.UpsertObservedProcess("sess-1", "PioneerPharmacy.exe",
            @"C:\PioneerRx\PioneerPharmacy.exe", windowTitleScrubbed: "POS", isPmsCandidate: false);
        _db.UpsertObservedProcess("sess-1", "PioneerPharmacy.exe",
            @"C:\PioneerRx\PioneerPharmacy.exe", windowTitleScrubbed: "POS", isPmsCandidate: false);

        var processes = _db.GetObservedProcesses("sess-1");
        Assert.Single(processes);
        Assert.Equal(2, processes[0].OccurrenceCount);
    }

    [Fact]
    public void InsertDiscoveredSchema_Persists()
    {
        _db.CreateLearningSession("sess-1", "pharm-1");
        _db.InsertDiscoveredSchema("sess-1", "svr-hash", "PioneerPharmacySystem",
            "Prescription", "RxTransaction", "RxTransactionID", "uniqueidentifier",
            maxLength: 16, isNullable: false, isPk: true, isFk: false,
            fkTargetTable: null, fkTargetColumn: null, inferredPurpose: "identifier");

        var schemas = _db.GetDiscoveredSchemas("sess-1");
        Assert.Single(schemas);
        Assert.Equal("Prescription", schemas[0].SchemaName);
        Assert.Equal("RxTransaction", schemas[0].TableName);
    }

    [Fact]
    public void InsertLearningAudit_Chains()
    {
        _db.CreateLearningSession("sess-1", "pharm-1");
        _db.AppendLearningAudit("sess-1", "process", "scan", "PioneerPharmacy.exe", phiScrubbed: false);
        _db.AppendLearningAudit("sess-1", "sql", "discover", "Prescription.RxTransaction", phiScrubbed: false);

        var count = _db.GetLearningAuditCount("sess-1");
        Assert.Equal(2, count);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
