using System.Text.Json;
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Behavioral;

public class FeedbackCollectorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;
    private const string SessionId = "feedback-test-session";

    public FeedbackCollectorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"feedback_test_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
        _db.CreateLearningSession(SessionId, "pharm-feedback");
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    // ── InsertFeedbackEvent roundtrip ──

    [Fact]
    public void InsertFeedbackEvent_Roundtrips()
    {
        var evt = new FeedbackEvent(
            SessionId: SessionId,
            EventType: "writeback_outcome",
            Source: "WritebackEngine",
            SourceId: "wb-42",
            TargetType: "correlation",
            TargetId: "key-001",
            PayloadJson: "{\"rx\":123}",
            DirectiveType: DirectiveType.ConfidenceAdjust,
            DirectiveJson: "{\"delta\":0.05}",
            CausalChainJson: "[\"a\",\"b\"]")
        {
            CreatedAt = "2026-04-13T10:00:00Z"
        };

        int id = _db.InsertFeedbackEvent(evt);
        Assert.True(id > 0);

        var loaded = _db.GetFeedbackEvent(id);
        Assert.NotNull(loaded);
        Assert.Equal(id, loaded!.Id);
        Assert.Equal(SessionId, loaded.SessionId);
        Assert.Equal("writeback_outcome", loaded.EventType);
        Assert.Equal("WritebackEngine", loaded.Source);
        Assert.Equal("wb-42", loaded.SourceId);
        Assert.Equal("correlation", loaded.TargetType);
        Assert.Equal("key-001", loaded.TargetId);
        Assert.Equal("{\"rx\":123}", loaded.PayloadJson);
        Assert.Equal(DirectiveType.ConfidenceAdjust, loaded.DirectiveType);
        Assert.Equal("{\"delta\":0.05}", loaded.DirectiveJson);
        Assert.Equal("[\"a\",\"b\"]", loaded.CausalChainJson);
        Assert.Null(loaded.AppliedAt);
        Assert.Null(loaded.AppliedBy);
        Assert.Equal("2026-04-13T10:00:00Z", loaded.CreatedAt);
    }

    // ── MarkFeedbackEventApplied ──

    [Fact]
    public void MarkFeedbackEventApplied_SetsTimestampAndApplier()
    {
        var evt = MakeEvent(DirectiveType.Promote);
        int id = _db.InsertFeedbackEvent(evt);

        _db.MarkFeedbackEventApplied(id, "FeedbackLoop");

        var loaded = _db.GetFeedbackEvent(id);
        Assert.NotNull(loaded);
        Assert.NotNull(loaded!.AppliedAt);
        Assert.Equal("FeedbackLoop", loaded.AppliedBy);
    }

    // ── GetPendingFeedbackEvents ──

    [Fact]
    public void GetPendingFeedbackEvents_ReturnsOnlyUnapplied()
    {
        int id1 = _db.InsertFeedbackEvent(MakeEvent(DirectiveType.ConfidenceAdjust));
        int id2 = _db.InsertFeedbackEvent(MakeEvent(DirectiveType.Promote));
        int id3 = _db.InsertFeedbackEvent(MakeEvent(DirectiveType.Demote));

        // Apply id2
        _db.MarkFeedbackEventApplied(id2, "Loop");

        var pending = _db.GetPendingFeedbackEvents(SessionId);
        Assert.Equal(2, pending.Count);
        Assert.All(pending, e => Assert.Null(e.AppliedAt));
    }

    // ── HasDecayEventToday ──

    [Fact]
    public void HasDecayEventToday_DetectsDuplicates()
    {
        // No decay yet
        Assert.False(_db.HasDecayEventToday(SessionId, "target-001"));

        // Insert a decay event with today's timestamp
        var decayEvt = new FeedbackEvent(
            SessionId: SessionId,
            EventType: "idle_decay",
            Source: "decay",
            SourceId: null,
            TargetType: "correlation",
            TargetId: "target-001",
            PayloadJson: null,
            DirectiveType: DirectiveType.ConfidenceAdjust,
            DirectiveJson: null,
            CausalChainJson: null)
        {
            CreatedAt = DateTimeOffset.UtcNow.ToString("o")
        };
        _db.InsertFeedbackEvent(decayEvt);

        Assert.True(_db.HasDecayEventToday(SessionId, "target-001"));
        // Different target should not match
        Assert.False(_db.HasDecayEventToday(SessionId, "target-999"));
    }

    // ── UpdateCorrelationConfidence ──

    [Fact]
    public void UpdateCorrelationConfidence_MutatesValue()
    {
        _db.UpsertCorrelatedAction(SessionId, "key-conf", "th1", "e1", "Button", "qh1", false, "Rx");

        var before = _db.GetCorrelatedActions(SessionId);
        Assert.Equal(0.3, before[0].Confidence, precision: 1);

        _db.UpdateCorrelationConfidence(SessionId, "key-conf", 0.85);

        var after = _db.GetCorrelatedActions(SessionId);
        Assert.Equal(0.85, after[0].Confidence, precision: 2);
    }

    // ── GetCorrelatedActionExtended ──

    [Fact]
    public void GetCorrelatedActionExtended_ReturnsNewColumnDefaults()
    {
        _db.UpsertCorrelatedAction(SessionId, "key-ext", "th1", "e1", "Button", "qh1", false, "Rx");

        var ext = _db.GetCorrelatedActionExtended(SessionId, "key-ext");
        Assert.NotNull(ext);
        Assert.False(ext!.Value.OperatorApproved);
        Assert.False(ext.Value.OperatorRejected);
        Assert.False(ext.Value.PromotionSuspended);
        Assert.Equal(0, ext.Value.ConsecutiveFailures);
        Assert.False(ext.Value.Stale);
        Assert.Null(ext.Value.StaleSince);
    }

    [Fact]
    public void GetCorrelatedActionExtended_ReturnsNullForMissing()
    {
        var ext = _db.GetCorrelatedActionExtended(SessionId, "nonexistent-key");
        Assert.Null(ext);
    }

    // ── UpdateCorrelationFlags ──

    [Fact]
    public void UpdateCorrelationFlags_SetsOnlyProvidedFields()
    {
        _db.UpsertCorrelatedAction(SessionId, "key-flag", "th1", "e1", "Button", "qh1", false, "Rx");

        _db.UpdateCorrelationFlags(SessionId, "key-flag",
            operatorApproved: true, consecutiveFailures: 3);

        var ext = _db.GetCorrelatedActionExtended(SessionId, "key-flag");
        Assert.NotNull(ext);
        Assert.True(ext!.Value.OperatorApproved);
        Assert.False(ext.Value.OperatorRejected);
        Assert.Equal(3, ext.Value.ConsecutiveFailures);
    }

    // ── FeedbackEventCount ──

    [Fact]
    public void GetFeedbackEventCount_ReturnsCorrectCount()
    {
        Assert.Equal(0, _db.GetFeedbackEventCount(SessionId));

        _db.InsertFeedbackEvent(MakeEvent(DirectiveType.ConfidenceAdjust));
        _db.InsertFeedbackEvent(MakeEvent(DirectiveType.Promote));

        Assert.Equal(2, _db.GetFeedbackEventCount(SessionId));
    }

    // ── FeedbackEventCountByApplier ──

    [Fact]
    public void GetFeedbackEventCountByApplier_FiltersCorrectly()
    {
        int id1 = _db.InsertFeedbackEvent(MakeEvent(DirectiveType.ConfidenceAdjust));
        int id2 = _db.InsertFeedbackEvent(MakeEvent(DirectiveType.Promote));
        _db.MarkFeedbackEventApplied(id1, "FeedbackLoop");
        _db.MarkFeedbackEventApplied(id2, "DecayLoop");

        Assert.Equal(1, _db.GetFeedbackEventCountByApplier(SessionId, "FeedbackLoop"));
        Assert.Equal(1, _db.GetFeedbackEventCountByApplier(SessionId, "DecayLoop"));
        Assert.Equal(0, _db.GetFeedbackEventCountByApplier(SessionId, "Unknown"));
    }

    // ── WindowOverride CRUD ──

    [Fact]
    public void UpsertWindowOverride_Roundtrips()
    {
        _db.UpsertWindowOverride(SessionId, "tree-001", "elem-001", 15.5, 25);

        var window = _db.GetWindowOverride(SessionId, "tree-001", "elem-001");
        Assert.NotNull(window);
        Assert.Equal(15.5, window!.Value, precision: 1);
        Assert.Equal(1, _db.GetWindowOverrideCount(SessionId));
    }

    [Fact]
    public void UpsertWindowOverride_UpdatesOnConflict()
    {
        _db.UpsertWindowOverride(SessionId, "tree-001", "elem-001", 10.0, 20);
        _db.UpsertWindowOverride(SessionId, "tree-001", "elem-001", 25.0, 50);

        var window = _db.GetWindowOverride(SessionId, "tree-001", "elem-001");
        Assert.Equal(25.0, window!.Value, precision: 1);
        Assert.Equal(1, _db.GetWindowOverrideCount(SessionId));
    }

    [Fact]
    public void GetWindowOverride_ReturnsNullForMissing()
    {
        Assert.Null(_db.GetWindowOverride(SessionId, "no-tree", "no-elem"));
    }

    // ── GetFeedbackEventsForTarget ──

    [Fact]
    public void GetFeedbackEventsForTarget_FiltersCorrectly()
    {
        var evt1 = new FeedbackEvent(SessionId, "outcome", "WritebackEngine", null,
            "correlation", "target-A", null, DirectiveType.ConfidenceAdjust, null, null);
        var evt2 = new FeedbackEvent(SessionId, "decay", "decay", null,
            "correlation", "target-A", null, DirectiveType.ConfidenceAdjust, null, null);
        var evt3 = new FeedbackEvent(SessionId, "outcome", "WritebackEngine", null,
            "correlation", "target-B", null, DirectiveType.Promote, null, null);
        _db.InsertFeedbackEvent(evt1);
        _db.InsertFeedbackEvent(evt2);
        _db.InsertFeedbackEvent(evt3);

        // All events for target-A
        var allA = _db.GetFeedbackEventsForTarget(SessionId, "target-A");
        Assert.Equal(2, allA.Count);

        // Only decay events for target-A
        var decayA = _db.GetFeedbackEventsForTarget(SessionId, "target-A", source: "decay");
        Assert.Single(decayA);
        Assert.Equal("decay", decayA[0].Source);
    }

    // ── SuspendedPromotions ──

    [Fact]
    public void GetSuspendedPromotions_ReturnsCorrectKeys()
    {
        _db.UpsertCorrelatedAction(SessionId, "key-suspended", "th1", "e1", "Button", "qh1", false, "Rx");
        _db.UpsertCorrelatedAction(SessionId, "key-active", "th2", "e2", "Button", "qh2", false, "Rx");

        _db.UpdateCorrelationFlags(SessionId, "key-suspended", promotionSuspended: true);

        var suspended = _db.GetSuspendedPromotions(SessionId);
        Assert.Single(suspended);
        Assert.Equal("key-suspended", suspended[0]);
    }

    // ── DeleteCorrelation ──

    [Fact]
    public void DeleteCorrelation_RemovesRow()
    {
        _db.UpsertCorrelatedAction(SessionId, "key-del", "th1", "e1", "Button", "qh1", false, "Rx");
        Assert.Equal(1, _db.GetCorrelatedActionCount(SessionId));

        _db.DeleteCorrelation(SessionId, "key-del");
        Assert.Equal(0, _db.GetCorrelatedActionCount(SessionId));
    }

    // ── FeedbackCollector.RecordWritebackOutcome ──

    [Fact]
    public void RecordWritebackOutcome_Success_IncreasesConfidence()
    {
        SeedCorrelation("rwo-success", 0.60);
        var result = FeedbackCollector.RecordWritebackOutcome(
            _db, SessionId, "task-1", "rwo-success", "success",
            "2026-04-13T10:00:00Z", "2026-04-13T10:00:01Z");
        Assert.Equal(0.65, result, precision: 2);
    }

    [Fact]
    public void RecordWritebackOutcome_Failure_DecreasesConfidence()
    {
        SeedCorrelation("rwo-fail", 0.60);
        var result = FeedbackCollector.RecordWritebackOutcome(
            _db, SessionId, "task-2", "rwo-fail", "status_conflict",
            "2026-04-13T10:00:00Z", "2026-04-13T10:00:01Z");
        Assert.Equal(0.45, result, precision: 2);
    }

    [Fact]
    public void RecordWritebackOutcome_CeilingCapsAt0Point95()
    {
        SeedCorrelation("rwo-ceiling", 0.93);
        var result = FeedbackCollector.RecordWritebackOutcome(
            _db, SessionId, "task-3", "rwo-ceiling", "success",
            "2026-04-13T10:00:00Z", "2026-04-13T10:00:01Z");
        Assert.Equal(0.95, result, precision: 2);
    }

    [Fact]
    public void RecordWritebackOutcome_BelowFloor_EmitsPrune()
    {
        SeedCorrelation("rwo-prune", 0.11);
        FeedbackCollector.RecordWritebackOutcome(
            _db, SessionId, "task-4", "rwo-prune", "status_conflict",
            "2026-04-13T10:00:00Z", "2026-04-13T10:00:01Z");

        var events = _db.GetFeedbackEventsForTarget(SessionId, "rwo-prune");
        Assert.True(events.Count >= 2, $"Expected >= 2 events, got {events.Count}");
        Assert.Contains(events, e => e.DirectiveType == DirectiveType.ConfidenceAdjust);
        Assert.Contains(events, e => e.DirectiveType == DirectiveType.Prune);
    }

    [Fact]
    public void RecordWritebackOutcome_PromotedWith5Failures_SuspendsPromotion()
    {
        SeedCorrelation("rwo-suspend", 0.30);
        _db.UpdateCorrelationFlags(SessionId, "rwo-suspend", operatorApproved: true);

        for (int i = 0; i < 5; i++)
        {
            FeedbackCollector.RecordWritebackOutcome(
                _db, SessionId, $"task-s{i}", "rwo-suspend", "sql_error",
                "2026-04-13T10:00:00Z", "2026-04-13T10:00:01Z");
        }

        var ext = _db.GetCorrelatedActionExtended(SessionId, "rwo-suspend");
        Assert.NotNull(ext);
        Assert.True(ext!.Value.PromotionSuspended);
        var events = _db.GetFeedbackEventsForTarget(SessionId, "rwo-suspend");
        Assert.Contains(events, e => e.DirectiveType == DirectiveType.SuspendPromotion);
    }

    [Fact]
    public void RecordWritebackOutcome_Success_ResetsConsecutiveFailures()
    {
        SeedCorrelation("rwo-reset", 0.60);

        // Two failures
        FeedbackCollector.RecordWritebackOutcome(
            _db, SessionId, "t1", "rwo-reset", "sql_error",
            "2026-04-13T10:00:00Z", "2026-04-13T10:00:01Z");
        FeedbackCollector.RecordWritebackOutcome(
            _db, SessionId, "t2", "rwo-reset", "sql_error",
            "2026-04-13T10:00:02Z", "2026-04-13T10:00:03Z");

        var mid = _db.GetCorrelatedActionExtended(SessionId, "rwo-reset");
        Assert.Equal(2, mid!.Value.ConsecutiveFailures);

        // One success resets
        FeedbackCollector.RecordWritebackOutcome(
            _db, SessionId, "t3", "rwo-reset", "success",
            "2026-04-13T10:00:04Z", "2026-04-13T10:00:05Z");

        var after = _db.GetCorrelatedActionExtended(SessionId, "rwo-reset");
        Assert.Equal(0, after!.Value.ConsecutiveFailures);
    }

    [Fact]
    public void RecordWritebackOutcome_ConnectionReset_ZeroDelta()
    {
        SeedCorrelation("rwo-connreset", 0.60);
        var result = FeedbackCollector.RecordWritebackOutcome(
            _db, SessionId, "task-cr", "rwo-connreset", "connection_reset",
            "2026-04-13T10:00:00Z", "2026-04-13T10:00:01Z");
        Assert.Equal(0.60, result, precision: 2);
    }

    // ── FeedbackCollector.ApplyDirective ──

    [Fact]
    public void ApplyDirective_IsIdempotent()
    {
        SeedCorrelation("rwo-idempotent", 0.50);

        var evt = new FeedbackEvent(
            SessionId: SessionId,
            EventType: "writeback_outcome",
            Source: "inline",
            SourceId: null,
            TargetType: "correlation",
            TargetId: "rwo-idempotent",
            PayloadJson: null,
            DirectiveType: DirectiveType.ConfidenceAdjust,
            DirectiveJson: JsonSerializer.Serialize(new { newConfidence = 0.99 }),
            CausalChainJson: null)
        {
            AppliedAt = "2026-04-13T10:00:00Z",
            AppliedBy = "inline"
        };
        int id = _db.InsertFeedbackEvent(evt);

        var loaded = _db.GetFeedbackEvent(id)!;
        FeedbackCollector.ApplyDirective(_db, SessionId, loaded);
        FeedbackCollector.ApplyDirective(_db, SessionId, loaded);

        // Confidence should NOT have changed because event was already applied
        var actions = _db.GetCorrelatedActions(SessionId);
        var match = actions.First(a => a.CorrelationKey == "rwo-idempotent");
        Assert.Equal(0.50, match.Confidence, precision: 2);
    }

    [Fact]
    public void CausalChain_ForwardOnly_OldEventsUnmodified()
    {
        SeedCorrelation("rwo-causal", 0.60);

        // Event A
        FeedbackCollector.RecordWritebackOutcome(
            _db, SessionId, "t-a", "rwo-causal", "sql_error",
            "2026-04-13T10:00:00Z", "2026-04-13T10:00:01Z");

        var eventsAfterA = _db.GetFeedbackEventsForTarget(SessionId, "rwo-causal");
        var eventA = eventsAfterA.First(e => e.DirectiveType == DirectiveType.ConfidenceAdjust);
        var eventAPayload = eventA.PayloadJson;
        var eventACausal = eventA.CausalChainJson;

        // Event B
        FeedbackCollector.RecordWritebackOutcome(
            _db, SessionId, "t-b", "rwo-causal", "sql_error",
            "2026-04-13T10:00:02Z", "2026-04-13T10:00:03Z");

        // Re-read A — should be unchanged
        var reloadedA = _db.GetFeedbackEvent(eventA.Id!.Value)!;
        Assert.Equal(eventAPayload, reloadedA.PayloadJson);
        Assert.Equal(eventACausal, reloadedA.CausalChainJson);
    }

    // ── Helpers ──

    private void SeedCorrelation(string key, double initialConfidence)
    {
        _db.UpsertCorrelatedAction(SessionId, key, "th1", "e1", "Button", "qh1", false, "Rx");
        _db.UpdateCorrelationConfidence(SessionId, key, initialConfidence);
    }

    private FeedbackEvent MakeEvent(DirectiveType directive, string targetId = "target-001")
        => new(
            SessionId: SessionId,
            EventType: "writeback_outcome",
            Source: "WritebackEngine",
            SourceId: null,
            TargetType: "correlation",
            TargetId: targetId,
            PayloadJson: null,
            DirectiveType: directive,
            DirectiveJson: null,
            CausalChainJson: null);
}
