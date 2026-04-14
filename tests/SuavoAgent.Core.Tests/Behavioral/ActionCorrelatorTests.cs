using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Behavioral;

public class ActionCorrelatorTests : IDisposable
{
    private readonly AgentStateDb _db;
    private readonly string _sessionId = "corr-test-session";

    public ActionCorrelatorTests()
    {
        _db = new AgentStateDb(":memory:");
        _db.CreateLearningSession(_sessionId, "pharm-test");
    }

    public void Dispose() => _db.Dispose();

    private ActionCorrelator MakeCorrelator(double windowSeconds = 2.0, bool calibrated = true)
        => new ActionCorrelator(_db, _sessionId, windowSeconds, calibrated);

    [Fact]
    public void UiEventWithinWindow_MatchesSqlEvent_CreatesCorrelation()
    {
        var correlator = MakeCorrelator();
        var uiTime = DateTimeOffset.UtcNow;
        correlator.RecordUiEvent("tree-abc", "elem-001", "Button", uiTime);

        // SQL fires 1s after UI — within 2s window
        var sqlTime = uiTime.AddSeconds(1);
        correlator.TryCorrelateWithSql("qshape-1", sqlTime.ToString("o"), isWrite: true, tablesReferenced: "Prescription");

        var actions = _db.GetCorrelatedActions(_sessionId);
        Assert.Single(actions);
        Assert.Equal("tree-abc:elem-001:qshape-1", actions[0].CorrelationKey);
        Assert.Equal("elem-001", actions[0].ElementId);
        Assert.Equal("tree-abc", actions[0].TreeHash);
        Assert.True(actions[0].IsWrite);
        Assert.Equal("Prescription", actions[0].TablesReferenced);
        Assert.Equal(1, actions[0].OccurrenceCount);
        Assert.Equal(0.3, actions[0].Confidence);
    }

    [Fact]
    public void UiEventOutsideWindow_NoCorrelationCreated()
    {
        var correlator = MakeCorrelator(windowSeconds: 2.0);
        var uiTime = DateTimeOffset.UtcNow;
        correlator.RecordUiEvent("tree-abc", "elem-001", "Button", uiTime);

        // SQL fires 10s after UI — outside 2s window
        var sqlTime = uiTime.AddSeconds(10);
        correlator.TryCorrelateWithSql("qshape-1", sqlTime.ToString("o"), isWrite: false, tablesReferenced: null);

        var actions = _db.GetCorrelatedActions(_sessionId);
        Assert.Empty(actions);
    }

    [Fact]
    public void RepeatedCorrelation_SameKey_IncrementsCountAndRaisesConfidence()
    {
        var correlator = MakeCorrelator();
        var baseTime = DateTimeOffset.UtcNow;

        // Simulate 10 occurrences of the same UI→SQL pair
        for (int i = 0; i < 10; i++)
        {
            var uiTime = baseTime.AddMinutes(i);
            // Re-create correlator each iteration to avoid sliding window expiry issues
            var c = MakeCorrelator();
            c.RecordUiEvent("tree-abc", "elem-001", "Button", uiTime);
            c.TryCorrelateWithSql("qshape-1", uiTime.AddSeconds(0.5).ToString("o"), isWrite: true, tablesReferenced: "Prescription");
        }

        var actions = _db.GetCorrelatedActions(_sessionId);
        Assert.Single(actions);
        Assert.Equal(10, actions[0].OccurrenceCount);
        Assert.Equal(0.9, actions[0].Confidence);
    }

    [Fact]
    public void MediumOccurrenceCount_ConfidenceAt0Point6()
    {
        var baseTime = DateTimeOffset.UtcNow;

        for (int i = 0; i < 5; i++)
        {
            var c = MakeCorrelator();
            var uiTime = baseTime.AddMinutes(i);
            c.RecordUiEvent("tree-xyz", "elem-002", "MenuItem", uiTime);
            c.TryCorrelateWithSql("qshape-2", uiTime.AddSeconds(0.5).ToString("o"), isWrite: false, tablesReferenced: "Rx");
        }

        var actions = _db.GetCorrelatedActions(_sessionId);
        Assert.Single(actions);
        Assert.Equal(5, actions[0].OccurrenceCount);
        Assert.Equal(0.6, actions[0].Confidence);
    }

    [Fact]
    public void UiEventWithoutSql_NoCorrelationCreated()
    {
        var correlator = MakeCorrelator();
        correlator.RecordUiEvent("tree-abc", "elem-001", "Button", DateTimeOffset.UtcNow);

        // Never call TryCorrelateWithSql
        var actions = _db.GetCorrelatedActions(_sessionId);
        Assert.Empty(actions);
    }

    [Fact]
    public void OldUiEvents_ExpireFromSlidingWindow()
    {
        var correlator = MakeCorrelator();
        var oldTime = DateTimeOffset.UtcNow.AddSeconds(-35); // 35s ago — beyond 30s expiry
        correlator.RecordUiEvent("tree-old", "elem-old", "Button", oldTime);

        // SQL fires now — but the UI event is >30s old and should be pruned
        var sqlNow = DateTimeOffset.UtcNow;
        correlator.TryCorrelateWithSql("qshape-1", sqlNow.ToString("o"), isWrite: false, tablesReferenced: null);

        var actions = _db.GetCorrelatedActions(_sessionId);
        Assert.Empty(actions);
    }

    [Fact]
    public void UncalibratedClock_UsesWider5sWindow()
    {
        // calibrated=false → 5s window
        var correlator = MakeCorrelator(calibrated: false);
        var uiTime = DateTimeOffset.UtcNow;
        correlator.RecordUiEvent("tree-abc", "elem-001", "Button", uiTime);

        // SQL fires 4s after — outside calibrated 2s window but inside uncalibrated 5s window
        var sqlTime = uiTime.AddSeconds(4);
        correlator.TryCorrelateWithSql("qshape-1", sqlTime.ToString("o"), isWrite: true, tablesReferenced: "Prescription");

        var actions = _db.GetCorrelatedActions(_sessionId);
        Assert.Single(actions);
    }

    [Fact]
    public void UncalibratedClock_SetClockCalibrated_NarrowsWindow()
    {
        var correlator = new ActionCorrelator(_db, _sessionId, clockCalibrated: false);
        correlator.SetClockCalibrated(true);

        var uiTime = DateTimeOffset.UtcNow;
        correlator.RecordUiEvent("tree-abc", "elem-001", "Button", uiTime);

        // SQL fires 4s after — outside calibrated 2s window (which is now active)
        var sqlTime = uiTime.AddSeconds(4);
        correlator.TryCorrelateWithSql("qshape-1", sqlTime.ToString("o"), isWrite: false, tablesReferenced: null);

        var actions = _db.GetCorrelatedActions(_sessionId);
        Assert.Empty(actions);
    }

    [Fact]
    public void SqlBeforeUiEvent_WithinWindow_StillMatches()
    {
        var correlator = MakeCorrelator();
        var uiTime = DateTimeOffset.UtcNow;
        correlator.RecordUiEvent("tree-abc", "elem-001", "Button", uiTime);

        // SQL fires 1s BEFORE UI — still within ±2s
        var sqlTime = uiTime.AddSeconds(-1);
        correlator.TryCorrelateWithSql("qshape-1", sqlTime.ToString("o"), isWrite: false, tablesReferenced: null);

        var actions = _db.GetCorrelatedActions(_sessionId);
        Assert.Single(actions);
    }

    [Fact]
    public void MultipleUiEvents_ClosestMatchSelected()
    {
        var correlator = MakeCorrelator();
        var baseTime = DateTimeOffset.UtcNow;

        // Two UI events: one 1.8s before SQL, one 0.3s before SQL
        correlator.RecordUiEvent("tree-far", "elem-far", "Button", baseTime);
        correlator.RecordUiEvent("tree-close", "elem-close", "Button", baseTime.AddSeconds(1.5));

        var sqlTime = baseTime.AddSeconds(1.8);
        correlator.TryCorrelateWithSql("qshape-1", sqlTime.ToString("o"), isWrite: true, tablesReferenced: "Rx");

        var actions = _db.GetCorrelatedActions(_sessionId);
        Assert.Single(actions);
        // elem-close is 0.3s from SQL; elem-far is 1.8s — closest wins
        Assert.Equal("tree-close:elem-close:qshape-1", actions[0].CorrelationKey);
    }
}
