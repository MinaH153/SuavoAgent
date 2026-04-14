using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Behavioral;

public class RoutineDetectorTests : IDisposable
{
    private readonly AgentStateDb _db;
    private readonly string _sessionId = "routine-test-session";

    public RoutineDetectorTests()
    {
        _db = new AgentStateDb(":memory:");
        _db.CreateLearningSession(_sessionId, "pharm-test");
    }

    public void Dispose() => _db.Dispose();

    private void InsertInteraction(int seq, string treeHash, string elementId,
        string controlType, DateTimeOffset timestamp)
    {
        _db.InsertBehavioralEvent(_sessionId, seq, "interaction", "invoked",
            treeHash, elementId, controlType, null, null, null,
            null, null, null, 1, timestamp.ToString("o"));
    }

    [Fact]
    public void SixRepetitionsOf3StepSequence_RoutineDiscovered()
    {
        // 6 repetitions of A → B → C, each within 30s
        var baseTime = DateTimeOffset.UtcNow;
        int seq = 1;

        for (int rep = 0; rep < 6; rep++)
        {
            var t0 = baseTime.AddMinutes(rep * 2);
            InsertInteraction(seq++, "treeA", "elemA", "Button", t0);
            InsertInteraction(seq++, "treeB", "elemB", "Edit", t0.AddSeconds(5));
            InsertInteraction(seq++, "treeC", "elemC", "Button", t0.AddSeconds(10));
        }

        var detector = new RoutineDetector(_db, _sessionId);
        detector.DetectAndPersist();

        var routines = _db.GetLearnedRoutines(_sessionId);
        Assert.NotEmpty(routines);

        var routine = routines[0];
        Assert.True(routine.Frequency >= 5);
        Assert.True(routine.PathLength >= 3);
    }

    [Fact]
    public void OnlyTwoRepetitions_NoRoutineDiscovered()
    {
        // Only 2 repetitions — below MinFrequency of 5
        var baseTime = DateTimeOffset.UtcNow;
        int seq = 1;

        for (int rep = 0; rep < 2; rep++)
        {
            var t0 = baseTime.AddMinutes(rep * 2);
            InsertInteraction(seq++, "treeA", "elemA", "Button", t0);
            InsertInteraction(seq++, "treeB", "elemB", "Edit", t0.AddSeconds(5));
            InsertInteraction(seq++, "treeC", "elemC", "Button", t0.AddSeconds(10));
        }

        var detector = new RoutineDetector(_db, _sessionId);
        detector.DetectAndPersist();

        var routines = _db.GetLearnedRoutines(_sessionId);
        Assert.Empty(routines);
    }

    [Fact]
    public void RoutineWithWritebackCandidate_FlaggedCorrectly()
    {
        // Insert 6 repetitions of A → B → C where B has a correlated write action
        var baseTime = DateTimeOffset.UtcNow;
        int seq = 1;

        for (int rep = 0; rep < 6; rep++)
        {
            var t0 = baseTime.AddMinutes(rep * 2);
            InsertInteraction(seq++, "treeA", "elemA", "Button", t0);
            InsertInteraction(seq++, "treeB", "elemB", "Edit", t0.AddSeconds(5));
            InsertInteraction(seq++, "treeC", "elemC", "Button", t0.AddSeconds(10));
        }

        // Mark elemB as a correlated write action
        _db.UpsertCorrelatedAction(_sessionId, "treeB:elemB:qshape-write",
            "treeB", "elemB", "Edit", "qshape-write", true, "Prescription");

        var detector = new RoutineDetector(_db, _sessionId);
        detector.DetectAndPersist();

        var routines = _db.GetLearnedRoutines(_sessionId);
        Assert.NotEmpty(routines);
        Assert.True(routines[0].HasWritebackCandidate);
    }

    [Fact]
    public void LongGapBetweenActions_NoRoutineFormedAcrossGap()
    {
        // Steps A→B are fine (5s apart), but B→C has a 60s gap — breaks the sequence
        var baseTime = DateTimeOffset.UtcNow;
        int seq = 1;

        for (int rep = 0; rep < 6; rep++)
        {
            var t0 = baseTime.AddMinutes(rep * 5);
            InsertInteraction(seq++, "treeA", "elemA", "Button", t0);
            InsertInteraction(seq++, "treeB", "elemB", "Edit", t0.AddSeconds(5));
            // 60-second gap — exceeds the 30s edge creation limit
            InsertInteraction(seq++, "treeC", "elemC", "Button", t0.AddSeconds(65));
        }

        var detector = new RoutineDetector(_db, _sessionId);
        detector.DetectAndPersist();

        // A→B edges should form (freq=6), but B→C edges should not (gap > 30s)
        // So no 3-step path can be built from A→B alone
        var routines = _db.GetLearnedRoutines(_sessionId);

        // Any discovered routine must NOT span the B→C gap
        foreach (var r in routines)
        {
            Assert.DoesNotContain("elemC", r.PathJson);
        }
    }
}
