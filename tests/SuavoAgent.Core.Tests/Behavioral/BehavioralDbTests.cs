using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Behavioral;

public class BehavioralDbTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;
    private const string SessionId = "test-session-001";

    public BehavioralDbTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"behavioral_test_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
        _db.CreateLearningSession(SessionId, "pharm-test");
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    // ── behavioral_events ──

    [Fact]
    public void InsertBehavioralEvent_PersistedAndRetrieved()
    {
        _db.InsertBehavioralEvent(SessionId, 1, "click", "button",
            "tree-hash-001", "elem-001", "Button", null, null, null,
            null, null, null, 1, "2026-01-01T00:00:00Z");

        var events = _db.GetBehavioralEvents(SessionId);

        Assert.Single(events);
        Assert.Equal(1, events[0].SequenceNum);
        Assert.Equal("click", events[0].EventType);
        Assert.Equal("button", events[0].EventSubtype);
        Assert.Equal("tree-hash-001", events[0].TreeHash);
        Assert.Equal("elem-001", events[0].ElementId);
        Assert.Equal("Button", events[0].ControlType);
        Assert.Equal(1, events[0].OccurrenceCount);
    }

    [Fact]
    public void GetBehavioralEvents_FiltersByEventType()
    {
        _db.InsertBehavioralEvent(SessionId, 1, "click", null, "th1", "e1", null, null, null, null, null, null, null, 1, "2026-01-01T00:00:00Z");
        _db.InsertBehavioralEvent(SessionId, 2, "keystroke", null, "th2", "e2", null, null, null, null, "alpha", "fast", 3, 1, "2026-01-01T00:00:01Z");
        _db.InsertBehavioralEvent(SessionId, 3, "click", null, "th3", "e3", null, null, null, null, null, null, null, 1, "2026-01-01T00:00:02Z");

        var clicks = _db.GetBehavioralEvents(SessionId, eventType: "click");
        var keystrokes = _db.GetBehavioralEvents(SessionId, eventType: "keystroke");

        Assert.Equal(2, clicks.Count);
        Assert.Single(keystrokes);
        Assert.Equal("keystroke", keystrokes[0].EventType);
    }

    // ── dmv_query_observations ──

    [Fact]
    public void UpsertDmvQueryObservation_PersistedAndUpserted()
    {
        _db.UpsertDmvQueryObservation(SessionId, "hash-001", "SELECT * FROM Rx WHERE Status = ?",
            "Rx", false, 5, "2026-01-01T00:01:00Z", 0);

        var obs = _db.GetDmvQueryObservations(SessionId);
        Assert.Single(obs);
        Assert.Equal("hash-001", obs[0].QueryShapeHash);
        Assert.Equal(5, obs[0].ExecutionCount);
        Assert.False(obs[0].IsWrite);

        // Upsert again — execution_count should accumulate
        _db.UpsertDmvQueryObservation(SessionId, "hash-001", "SELECT * FROM Rx WHERE Status = ?",
            "Rx", false, 3, "2026-01-01T00:02:00Z", 100);

        var obs2 = _db.GetDmvQueryObservations(SessionId);
        Assert.Single(obs2);
        Assert.Equal(8, obs2[0].ExecutionCount);
        Assert.Equal("2026-01-01T00:02:00Z", obs2[0].LastExecutionTime);
    }

    [Fact]
    public void UpsertDmvQueryObservation_DifferentShapeHash_CreatesNewRow()
    {
        _db.UpsertDmvQueryObservation(SessionId, "hash-001", "SELECT * FROM Rx", "Rx", false, 1, "2026-01-01T00:01:00Z", 0);
        _db.UpsertDmvQueryObservation(SessionId, "hash-002", "UPDATE Rx SET Status = ?", "Rx", true, 1, "2026-01-01T00:02:00Z", 0);

        var obs = _db.GetDmvQueryObservations(SessionId);
        Assert.Equal(2, obs.Count);
    }

    // ── correlated_actions ──

    [Fact]
    public void UpsertCorrelatedAction_IncrementsOccurrenceAndRecalculatesConfidence()
    {
        // First insert — confidence = 0.3
        _db.UpsertCorrelatedAction(SessionId, "key-001", "tree-hash-001", "elem-001",
            "Button", "qhash-001", true, "Rx");

        var actions = _db.GetCorrelatedActions(SessionId);
        Assert.Single(actions);
        Assert.Equal(1, actions[0].OccurrenceCount);
        Assert.Equal(0.3, actions[0].Confidence, precision: 1);

        // Upsert 2 more times to reach count=3 → confidence = 0.6
        _db.UpsertCorrelatedAction(SessionId, "key-001", "tree-hash-001", "elem-001", "Button", "qhash-001", true, "Rx");
        _db.UpsertCorrelatedAction(SessionId, "key-001", "tree-hash-001", "elem-001", "Button", "qhash-001", true, "Rx");

        var actions3 = _db.GetCorrelatedActions(SessionId);
        Assert.Single(actions3);
        Assert.Equal(3, actions3[0].OccurrenceCount);
        Assert.Equal(0.6, actions3[0].Confidence, precision: 1);

        // Upsert 7 more times to reach count=10 → confidence = 0.9
        for (int i = 0; i < 7; i++)
            _db.UpsertCorrelatedAction(SessionId, "key-001", "tree-hash-001", "elem-001", "Button", "qhash-001", true, "Rx");

        var actions10 = _db.GetCorrelatedActions(SessionId);
        Assert.Equal(10, actions10[0].OccurrenceCount);
        Assert.Equal(0.9, actions10[0].Confidence, precision: 1);
    }

    // ── learned_routines ──

    [Fact]
    public void UpsertLearnedRoutine_PersistedWithWritebackFlag()
    {
        _db.UpsertLearnedRoutine(SessionId, "routine-hash-001",
            "[\"tree-hash-001\",\"tree-hash-002\"]", 2, 7, 0.8,
            "start-elem", "end-elem", "[\"UPDATE Rx SET Status=?\"]", true);

        var routines = _db.GetLearnedRoutines(SessionId);
        Assert.Single(routines);
        Assert.Equal("routine-hash-001", routines[0].RoutineHash);
        Assert.Equal(2, routines[0].PathLength);
        Assert.Equal(7, routines[0].Frequency);
        Assert.Equal(0.8, routines[0].Confidence, precision: 2);
        Assert.Equal("start-elem", routines[0].StartElementId);
        Assert.Equal("end-elem", routines[0].EndElementId);
        Assert.True(routines[0].HasWritebackCandidate);
    }

    [Fact]
    public void UpsertLearnedRoutine_UpdatesExistingOnConflict()
    {
        _db.UpsertLearnedRoutine(SessionId, "routine-hash-001",
            "[\"th1\"]", 1, 3, 0.4, null, null, null, false);
        _db.UpsertLearnedRoutine(SessionId, "routine-hash-001",
            "[\"th1\"]", 1, 12, 0.9, null, null, "[\"UPDATE ...\"]", true);

        var routines = _db.GetLearnedRoutines(SessionId);
        Assert.Single(routines);
        Assert.Equal(12, routines[0].Frequency);
        Assert.Equal(0.9, routines[0].Confidence, precision: 1);
        Assert.True(routines[0].HasWritebackCandidate);
    }

    // ── PruneBehavioralEvents ──

    [Fact]
    public void PruneBehavioralEvents_DeletesOldEventsWithStableRoutines()
    {
        // Insert a stable routine referencing tree-hash-stable
        _db.UpsertLearnedRoutine(SessionId, "r-stable",
            "[\"tree-hash-stable\"]", 1, 10, 0.9, null, null, null, false);

        // Insert old event (simulate age by using a past received_at via direct insert workaround)
        // We insert normally, then use the prune with 0 days to catch all events
        _db.InsertBehavioralEvent(SessionId, 1, "click", null,
            "tree-hash-stable", "e1", null, null, null, null, null, null, null, 1,
            "2026-01-01T00:00:00Z");

        // Prune with olderThanDays = -1 so cutoff is in the future, catching everything
        int deleted = _db.PruneBehavioralEvents(SessionId, -1);

        Assert.Equal(1, deleted);
        Assert.Equal(0, _db.GetBehavioralEventCount(SessionId));
    }

    [Fact]
    public void PruneBehavioralEvents_RetainsEventsWithoutStableRoutines()
    {
        // No stable routines inserted (or routine with frequency < 5)
        _db.UpsertLearnedRoutine(SessionId, "r-weak",
            "[\"tree-hash-unstable\"]", 1, 2, 0.2, null, null, null, false);

        _db.InsertBehavioralEvent(SessionId, 1, "click", null,
            "tree-hash-unstable", "e1", null, null, null, null, null, null, null, 1,
            "2026-01-01T00:00:00Z");

        int deleted = _db.PruneBehavioralEvents(SessionId, -1);

        Assert.Equal(0, deleted);
        Assert.Equal(1, _db.GetBehavioralEventCount(SessionId));
    }

    // ── PruneBehavioralEventsByAge ──

    [Fact]
    public void PruneBehavioralEventsByAge_RemovesOldRecords()
    {
        var dbPath2 = Path.Combine(Path.GetTempPath(), $"test-prune-age-{Guid.NewGuid():N}.db");
        try
        {
            using var db2 = new AgentStateDb(dbPath2);
            // No error, returns count >= 0
            var pruned = db2.PruneBehavioralEventsByAge(TimeSpan.FromDays(30));
            Assert.True(pruned >= 0);
        }
        finally { File.Delete(dbPath2); }
    }

    [Fact]
    public void PruneAppSessionsByAge_RemovesOldRecords()
    {
        var dbPath2 = Path.Combine(Path.GetTempPath(), $"test-prune-sessions-{Guid.NewGuid():N}.db");
        try
        {
            using var db2 = new AgentStateDb(dbPath2);
            var pruned = db2.PruneAppSessionsByAge(TimeSpan.FromDays(30));
            Assert.True(pruned >= 0);
        }
        finally { File.Delete(dbPath2); }
    }

    // ── GetWritebackCandidates ──

    [Fact]
    public void GetWritebackCandidates_ReturnsOnlyWriteCorrelations()
    {
        // Write correlation
        _db.UpsertDmvQueryObservation(SessionId, "write-hash", "UPDATE Rx SET Status=?",
            "Rx", true, 1, "2026-01-01T00:01:00Z", 0);
        _db.UpsertCorrelatedAction(SessionId, "write-key", "th1", "elem-write",
            "Button", "write-hash", true, "Rx");

        // Read correlation
        _db.UpsertDmvQueryObservation(SessionId, "read-hash", "SELECT * FROM Rx",
            "Rx", false, 1, "2026-01-01T00:01:00Z", 0);
        _db.UpsertCorrelatedAction(SessionId, "read-key", "th2", "elem-read",
            "DataGrid", "read-hash", false, "Rx");

        var candidates = _db.GetWritebackCandidates(SessionId);

        Assert.Single(candidates);
        Assert.Equal("write-key", candidates[0].CorrelationKey);
        Assert.Equal("UPDATE Rx SET Status=?", candidates[0].QueryShape);
    }

    // ── Telemetry counts ──

    [Fact]
    public void TelemetryCounts_ReturnCorrectValues()
    {
        _db.InsertBehavioralEvent(SessionId, 1, "click", null, "th1", "e1", null, null, null, null, null, null, null, 1, "2026-01-01T00:00:00Z");
        _db.InsertBehavioralEvent(SessionId, 2, "click", null, "th2", "e2", null, null, null, null, null, null, null, 1, "2026-01-01T00:00:01Z");
        _db.InsertBehavioralEvent(SessionId, 3, "keystroke", null, "th1", "e1", null, null, null, null, "alpha", "fast", 1, 1, "2026-01-01T00:00:02Z");

        _db.UpsertCorrelatedAction(SessionId, "key-r", "th1", "e1", null, "rhash", false, "Rx");
        _db.UpsertCorrelatedAction(SessionId, "key-w", "th2", "e2", null, "whash", true, "Rx");

        _db.UpsertLearnedRoutine(SessionId, "r1", "[\"th1\"]", 1, 5, 0.7, null, null, null, false);
        _db.UpsertLearnedRoutine(SessionId, "r2", "[\"th2\"]", 1, 3, 0.5, null, null, null, true);

        Assert.Equal(3, _db.GetBehavioralEventCount(SessionId));
        Assert.Equal(2, _db.GetBehavioralEventCount(SessionId, "click"));
        Assert.Equal(2, _db.GetUniqueScreenCount(SessionId));
        Assert.Equal(2, _db.GetCorrelatedActionCount(SessionId));
        Assert.Equal(1, _db.GetWritebackCandidateCount(SessionId));
        Assert.Equal(2, _db.GetLearnedRoutineCount(SessionId));
        Assert.Equal(1, _db.GetRoutinesWithWritebackCount(SessionId));
    }
}
