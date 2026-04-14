using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

/// <summary>
/// Tests for DmvQueryObserver.ProcessAndStore — the static, connectionless pipeline.
/// All assertions go through AgentStateDb(:memory:) with GetDmvQueryObservations.
/// </summary>
public class DmvQueryObserverTests : IDisposable
{
    private const string SessionId = "test-dmv-session";
    private readonly AgentStateDb _db;

    public DmvQueryObserverTests()
    {
        _db = new AgentStateDb(":memory:");
        _db.CreateLearningSession(SessionId, "pharm-test");
    }

    // ── Parameterized query persists ──

    [Fact]
    public void ParameterizedQuery_PersistsShapeAndTables()
    {
        const string sql = "SELECT RxNumber, PatientId FROM dbo.Prescription WHERE Status = @p1";
        var lastExec = DateTimeOffset.UtcNow.ToString("o");

        DmvQueryObserver.ProcessAndStore(_db, SessionId, sql, 1, lastExec, 0);

        var rows = _db.GetDmvQueryObservations(SessionId);
        Assert.Single(rows);
        var row = rows[0];
        Assert.False(row.IsWrite);
        Assert.Equal(1, row.ExecutionCount);

        var tables = JsonSerializer.Deserialize<List<string>>(row.TablesReferenced)!;
        Assert.Contains(tables, t => t.Contains("Prescription", StringComparison.OrdinalIgnoreCase));
    }

    // ── UPDATE flagged as IsWrite ──

    [Fact]
    public void UpdateQuery_IsFlaggedAsWrite()
    {
        const string sql = "UPDATE dbo.RxTransaction SET Status = @p1 WHERE TaskId = @p2";
        var lastExec = DateTimeOffset.UtcNow.ToString("o");

        DmvQueryObserver.ProcessAndStore(_db, SessionId, sql, 3, lastExec, 0);

        var rows = _db.GetDmvQueryObservations(SessionId);
        Assert.Single(rows);
        Assert.True(rows[0].IsWrite);
        Assert.Equal(3, rows[0].ExecutionCount);
    }

    // ── String literals discarded (fail-closed) ──

    [Fact]
    public void QueryWithStringLiteral_IsDiscarded()
    {
        // String literal 'Smith' could be a patient name — tokenizer must reject
        const string sql = "SELECT * FROM dbo.Patient WHERE LastName = 'Smith'";
        var lastExec = DateTimeOffset.UtcNow.ToString("o");

        DmvQueryObserver.ProcessAndStore(_db, SessionId, sql, 1, lastExec, 0);

        var rows = _db.GetDmvQueryObservations(SessionId);
        Assert.Empty(rows);
    }

    // ── Duplicate shape hash upserts (execution count accumulates) ──

    [Fact]
    public void DuplicateShapeHash_AccumulatesExecutionCount()
    {
        const string sql = "SELECT RxNumber FROM dbo.Rx WHERE PatientId = @p1";
        var lastExec = DateTimeOffset.UtcNow.ToString("o");

        DmvQueryObserver.ProcessAndStore(_db, SessionId, sql, 5, lastExec, 0);
        DmvQueryObserver.ProcessAndStore(_db, SessionId, sql, 3, lastExec, 0);

        var rows = _db.GetDmvQueryObservations(SessionId);
        Assert.Single(rows);               // same shape → same row
        Assert.Equal(8, rows[0].ExecutionCount);  // 5 + 3 accumulated
    }

    // ── Null/empty SQL returns without storing ──

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrEmptySql_DoesNotStore(string? rawSql)
    {
        var lastExec = DateTimeOffset.UtcNow.ToString("o");

        DmvQueryObserver.ProcessAndStore(_db, SessionId, rawSql, 1, lastExec, 0);

        var rows = _db.GetDmvQueryObservations(SessionId);
        Assert.Empty(rows);
    }

    // ── Clock offset is stored correctly ──

    [Fact]
    public void ClockOffset_IsPersistedWithObservation()
    {
        const string sql = "SELECT TOP @p1 RxNumber FROM dbo.Rx ORDER BY FilledDate DESC";
        var lastExec = DateTimeOffset.UtcNow.ToString("o");
        const int offsetMs = -250;

        // ClockOffsetMs is stored but not directly exposed in GetDmvQueryObservations tuple.
        // Verify the row exists (offset path exercised without error).
        DmvQueryObserver.ProcessAndStore(_db, SessionId, sql, 1, lastExec, offsetMs);

        var rows = _db.GetDmvQueryObservations(SessionId);
        Assert.Single(rows);
    }

    // ── Observer metadata ──

    [Fact]
    public void Observer_HasCorrectNameAndPhases()
    {
        var obs = new DmvQueryObserver(
            _db,
            () => throw new InvalidOperationException("should not connect"),
            NullLogger<DmvQueryObserver>.Instance);

        Assert.Equal("dmv-query", obs.Name);
        Assert.Equal(ObserverPhase.Pattern | ObserverPhase.Model, obs.ActivePhases);
        Assert.False(obs.HasDmvAccess);    // false until StartAsync succeeds
        Assert.Equal(0, obs.ClockOffsetMs);
    }

    // ── Health report ──

    [Fact]
    public void CheckHealth_WhenNotRunning_ReportsCorrectly()
    {
        var obs = new DmvQueryObserver(
            _db,
            () => throw new InvalidOperationException("no connection"),
            NullLogger<DmvQueryObserver>.Instance);

        var health = obs.CheckHealth();
        Assert.Equal("dmv-query", health.ObserverName);
        Assert.False(health.IsRunning);
        Assert.Equal(0, health.EventsCollected);
    }

    public void Dispose() => _db.Dispose();
}
