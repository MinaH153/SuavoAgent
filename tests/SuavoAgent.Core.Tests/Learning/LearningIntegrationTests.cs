using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

public class LearningIntegrationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;

    public LearningIntegrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_learnint_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
    }

    [Fact]
    public void FullLearningFlow_CreateSession_ObserveProcess_DiscoverSchema()
    {
        // Create session
        _db.CreateLearningSession("sess-1", "pharm-1");

        // Observe processes
        _db.UpsertObservedProcess("sess-1", "PioneerPharmacy.exe",
            @"C:\PioneerRx\PioneerPharmacy.exe",
            windowTitleScrubbed: "Point of Sale", isPmsCandidate: true);
        _db.UpsertObservedProcess("sess-1", "chrome.exe",
            @"C:\Chrome\chrome.exe");

        // Discover schema
        _db.InsertDiscoveredSchema("sess-1", "svr-hash", "PioneerPharmacySystem",
            "Prescription", "RxTransaction", "RxTransactionID", "uniqueidentifier",
            16, false, true, false, null, null, "identifier");
        _db.InsertDiscoveredSchema("sess-1", "svr-hash", "PioneerPharmacySystem",
            "Prescription", "RxTransaction", "RxTransactionStatusTypeID", "uniqueidentifier",
            16, false, false, true, "Prescription.RxTransactionStatusType", "ID", "identifier");

        // Audit trail
        _db.AppendLearningAudit("sess-1", "process", "scan", "PioneerPharmacy.exe", false);
        _db.AppendLearningAudit("sess-1", "sql", "discover", "Prescription.RxTransaction", false);

        // Verify
        var session = _db.GetLearningSession("sess-1");
        Assert.NotNull(session);
        Assert.Equal("discovery", session.Value.Phase);
        Assert.Equal("observer", session.Value.Mode);

        var processes = _db.GetObservedProcesses("sess-1");
        Assert.Equal(2, processes.Count);
        Assert.True(processes[0].IsPmsCandidate); // PioneerPharmacy first (higher count)

        var schemas = _db.GetDiscoveredSchemas("sess-1");
        Assert.Equal(2, schemas.Count);

        Assert.Equal(2, _db.GetLearningAuditCount("sess-1"));

        // Phase transition
        Assert.True(LearningSession.IsValidPhaseTransition("discovery", "pattern"));
        Assert.False(LearningSession.IsValidPhaseTransition("discovery", "model"));

        _db.UpdateLearningPhase("sess-1", "pattern");
        session = _db.GetLearningSession("sess-1");
        Assert.Equal("pattern", session.Value.Phase);
    }

    [Fact]
    public void ModeTransitions_FollowTeslaFsdModel()
    {
        // Forward progression
        Assert.True(LearningSession.IsValidModeTransition("observer", "supervised"));
        Assert.True(LearningSession.IsValidModeTransition("supervised", "autonomous"));

        // Skip not allowed
        Assert.False(LearningSession.IsValidModeTransition("observer", "autonomous"));

        // Downgrade always allowed
        Assert.True(LearningSession.IsValidModeTransition("autonomous", "supervised"));
        Assert.True(LearningSession.IsValidModeTransition("autonomous", "observer"));
        Assert.True(LearningSession.IsValidModeTransition("supervised", "observer"));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
