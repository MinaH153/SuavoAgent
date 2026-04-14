using Microsoft.Data.Sqlite;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.State;

public class SchemaHardeningTests : IDisposable
{
    private readonly AgentStateDb _db;

    public SchemaHardeningTests()
    {
        _db = new AgentStateDb(":memory:");
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void GetActiveSessionId_ReturnsActivePhaseSession()
    {
        _db.CreateLearningSession("sess-active", "pharm-1");
        // Walk through all phases: discovery -> pattern -> model -> approved -> active
        _db.UpdateLearningPhase("sess-active", "pattern");
        _db.UpdateLearningPhase("sess-active", "model");
        _db.UpdateLearningPhase("sess-active", "approved");
        _db.UpdateLearningPhase("sess-active", "active");

        var result = _db.GetActiveSessionId("pharm-1");
        Assert.Equal("sess-active", result);
    }

    [Fact]
    public void GetActiveSessionId_ReturnsDiscoveryPhaseSession()
    {
        _db.CreateLearningSession("sess-disc", "pharm-1");
        // Default phase is discovery

        var result = _db.GetActiveSessionId("pharm-1");
        Assert.Equal("sess-disc", result);
    }

    [Fact]
    public void GetOrCreateHmacSalt_ReturnsSameSaltOnSecondCall()
    {
        _db.CreateLearningSession("sess-salt", "pharm-1");
        var salt1 = _db.GetOrCreateHmacSalt("sess-salt");
        var salt2 = _db.GetOrCreateHmacSalt("sess-salt");
        Assert.Equal(salt1, salt2);
        Assert.NotEmpty(salt1);
    }

    [Fact]
    public void GetOrCreateHmacSalt_Atomic_CoalescePreservesFirstSalt()
    {
        _db.CreateLearningSession("sess-race", "pharm-1");
        var salt1 = _db.GetOrCreateHmacSalt("sess-race");

        // Simulate a "second writer" by calling again — COALESCE preserves first
        var salt2 = _db.GetOrCreateHmacSalt("sess-race");
        Assert.Equal(salt1, salt2);
    }
}
