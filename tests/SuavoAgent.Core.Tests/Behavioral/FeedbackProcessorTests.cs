using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Behavioral;

public class FeedbackProcessorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;
    private const string SessionId = "fp-test-session";

    public FeedbackProcessorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"fp_test_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
        _db.CreateLearningSession(SessionId, "pharm-fp-test");
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    // ── ProcessDecay ──

    [Fact]
    public void ProcessDecay_FlatMinusOneHundredth()
    {
        SeedCorrelation("decay:th1:e1:qh1", 0.72);
        BackdateLastSeen("decay:th1:e1:qh1", days: 10);

        var processor = new FeedbackProcessor(_db, SessionId);
        processor.ProcessDecay();

        var actions = _db.GetCorrelatedActions(SessionId);
        var match = actions.First(a => a.CorrelationKey == "decay:th1:e1:qh1");
        Assert.Equal(0.71, match.Confidence, precision: 2);
    }

    [Fact]
    public void ProcessDecay_EmitsMaxOneEventPerDay()
    {
        SeedCorrelation("decay-once:th1:e1:qh1", 0.80);
        BackdateLastSeen("decay-once:th1:e1:qh1", days: 10);

        var p1 = new FeedbackProcessor(_db, SessionId);
        p1.ProcessDecay();

        // First decay: 0.80 → 0.79
        Assert.True(_db.HasDecayEventToday(SessionId, "decay-once:th1:e1:qh1"));

        var p2 = new FeedbackProcessor(_db, SessionId);
        p2.ProcessDecay();

        // Second run should skip — still 0.79
        var actions = _db.GetCorrelatedActions(SessionId);
        var match = actions.First(a => a.CorrelationKey == "decay-once:th1:e1:qh1");
        Assert.Equal(0.79, match.Confidence, precision: 2);
    }

    [Fact]
    public void ProcessDecay_StopsAtFloor()
    {
        SeedCorrelation("decay-floor:th1:e1:qh1", 0.50);
        BackdateLastSeen("decay-floor:th1:e1:qh1", days: 10);

        var processor = new FeedbackProcessor(_db, SessionId);
        processor.ProcessDecay();

        var actions = _db.GetCorrelatedActions(SessionId);
        var match = actions.First(a => a.CorrelationKey == "decay-floor:th1:e1:qh1");
        Assert.Equal(0.50, match.Confidence, precision: 2);

        Assert.False(_db.HasDecayEventToday(SessionId, "decay-floor:th1:e1:qh1"));
    }

    // ── ProcessPendingDirectives (operator directives) ──

    [Fact]
    public void ProcessOperator_Promote_SetsApprovedFlag()
    {
        SeedCorrelation("promote:th1:e1:qh1", 0.60);

        var evt = new FeedbackEvent(
            SessionId: SessionId,
            EventType: "operator_directive",
            Source: "operator",
            SourceId: null,
            TargetType: "correlation",
            TargetId: "promote:th1:e1:qh1",
            PayloadJson: null,
            DirectiveType: DirectiveType.Promote,
            DirectiveJson: null,
            CausalChainJson: null);
        _db.InsertFeedbackEvent(evt);

        var processor = new FeedbackProcessor(_db, SessionId);
        processor.ProcessPendingDirectives();

        var ext = _db.GetCorrelatedActionExtended(SessionId, "promote:th1:e1:qh1");
        Assert.NotNull(ext);
        Assert.True(ext!.Value.OperatorApproved);
        Assert.False(ext.Value.PromotionSuspended);
    }

    [Fact]
    public void ProcessOperator_Demote_SetsRejectedAndZerosConfidence()
    {
        SeedCorrelation("demote:th1:e1:qh1", 0.70);

        var evt = new FeedbackEvent(
            SessionId: SessionId,
            EventType: "operator_directive",
            Source: "operator",
            SourceId: null,
            TargetType: "correlation",
            TargetId: "demote:th1:e1:qh1",
            PayloadJson: null,
            DirectiveType: DirectiveType.Demote,
            DirectiveJson: null,
            CausalChainJson: null);
        _db.InsertFeedbackEvent(evt);

        var processor = new FeedbackProcessor(_db, SessionId);
        processor.ProcessPendingDirectives();

        var ext = _db.GetCorrelatedActionExtended(SessionId, "demote:th1:e1:qh1");
        Assert.NotNull(ext);
        Assert.True(ext!.Value.OperatorRejected);

        var actions = _db.GetCorrelatedActions(SessionId);
        var match = actions.First(a => a.CorrelationKey == "demote:th1:e1:qh1");
        Assert.Equal(0.0, match.Confidence, precision: 2);
    }

    [Fact]
    public void ProcessOperator_Reapprove_ClearsSuspension()
    {
        SeedCorrelation("reapprove:th1:e1:qh1", 0.40);
        _db.UpdateCorrelationFlags(SessionId, "reapprove:th1:e1:qh1",
            operatorApproved: true, promotionSuspended: true, consecutiveFailures: 5);

        var evt = new FeedbackEvent(
            SessionId: SessionId,
            EventType: "operator_directive",
            Source: "operator",
            SourceId: null,
            TargetType: "correlation",
            TargetId: "reapprove:th1:e1:qh1",
            PayloadJson: null,
            DirectiveType: DirectiveType.Promote,
            DirectiveJson: null,
            CausalChainJson: null);
        _db.InsertFeedbackEvent(evt);

        var processor = new FeedbackProcessor(_db, SessionId);
        processor.ProcessPendingDirectives();

        var ext = _db.GetCorrelatedActionExtended(SessionId, "reapprove:th1:e1:qh1");
        Assert.NotNull(ext);
        Assert.True(ext!.Value.OperatorApproved);
        Assert.False(ext.Value.PromotionSuspended);
        Assert.Equal(0, ext.Value.ConsecutiveFailures);
    }

    // ── ProcessPendingDirectives (canary drift / ReLearn) ──

    [Fact]
    public void ProcessCanaryDrift_SetsStale()
    {
        SeedCorrelation("canary:th1:e1:qh1", 0.80);

        var evt = new FeedbackEvent(
            SessionId: SessionId,
            EventType: "schema_drift",
            Source: "canary",
            SourceId: null,
            TargetType: "correlation",
            TargetId: "canary:th1:e1:qh1",
            PayloadJson: null,
            DirectiveType: DirectiveType.ReLearn,
            DirectiveJson: null,
            CausalChainJson: null);
        _db.InsertFeedbackEvent(evt);

        var processor = new FeedbackProcessor(_db, SessionId);
        processor.ProcessPendingDirectives();

        var ext = _db.GetCorrelatedActionExtended(SessionId, "canary:th1:e1:qh1");
        Assert.NotNull(ext);
        Assert.True(ext!.Value.Stale);
        Assert.NotNull(ext.Value.StaleSince);
    }

    // ── ProcessStaleEscalation ──

    [Fact]
    public void StaleCorrelation_Beyond14Days_Escalates()
    {
        SeedCorrelation("stale:th1:e1:qh1", 0.60);
        var fifteenDaysAgo = DateTimeOffset.UtcNow.AddDays(-15).ToString("o");
        _db.UpdateCorrelationFlags(SessionId, "stale:th1:e1:qh1",
            stale: true, staleSince: fifteenDaysAgo);

        var processor = new FeedbackProcessor(_db, SessionId);
        processor.ProcessStaleEscalation();

        var events = _db.GetFeedbackEventsForTarget(SessionId, "stale:th1:e1:qh1");
        Assert.Contains(events, e => e.DirectiveType == DirectiveType.EscalateStale);
    }

    [Fact]
    public void StaleCorrelation_WithReplacement_DeletesSuperseded()
    {
        SeedCorrelation("stale-del:th1:e1:qh1", 0.60);
        var fifteenDaysAgo = DateTimeOffset.UtcNow.AddDays(-15).ToString("o");
        _db.UpdateCorrelationFlags(SessionId, "stale-del:th1:e1:qh1",
            stale: true, staleSince: fifteenDaysAgo);

        // Non-stale replacement with same tree_hash + element_id
        _db.UpsertCorrelatedAction(SessionId, "replacement:th1:e1:qh2", "th1", "e1", "Button", "qh2", false, "Rx");

        var processor = new FeedbackProcessor(_db, SessionId);
        processor.ProcessStaleEscalation();

        var actions = _db.GetCorrelatedActions(SessionId);
        Assert.DoesNotContain(actions, a => a.CorrelationKey == "stale-del:th1:e1:qh1");
        Assert.Contains(actions, a => a.CorrelationKey == "replacement:th1:e1:qh2");
    }

    // ── ProcessPendingFeedback (orchestration) ──

    [Fact]
    public void ProcessPendingFeedback_AppliesUnappliedEvents()
    {
        SeedCorrelation("pending:th1:e1:qh1", 0.50);

        var evt = new FeedbackEvent(
            SessionId: SessionId,
            EventType: "operator_directive",
            Source: "operator",
            SourceId: null,
            TargetType: "correlation",
            TargetId: "pending:th1:e1:qh1",
            PayloadJson: null,
            DirectiveType: DirectiveType.Promote,
            DirectiveJson: null,
            CausalChainJson: null);
        int id = _db.InsertFeedbackEvent(evt);

        var processor = new FeedbackProcessor(_db, SessionId);
        processor.ProcessPendingFeedback();

        var loaded = _db.GetFeedbackEvent(id);
        Assert.NotNull(loaded);
        Assert.NotNull(loaded!.AppliedAt);

        var ext = _db.GetCorrelatedActionExtended(SessionId, "pending:th1:e1:qh1");
        Assert.True(ext!.Value.OperatorApproved);
    }

    // ── ProcessRecalibration ──

    [Fact]
    public void ProcessRecalibration_WidensWindow_WhenP95ExceedsCurrent()
    {
        SeedCorrelation("recal:th1:e1:qh1", 0.80);
        _db.UpsertWindowOverride(SessionId, "th1", "e1", 5.0, 10);

        // Insert 25 writeback events with varied latencies
        for (int i = 0; i < 25; i++)
        {
            double latency = i < 20 ? 3000 + i * 100 : 8000 + i * 100; // p95 will exceed 5000ms
            var payload = JsonSerializer.Serialize(new { latencyMs = latency });
            var evt = new FeedbackEvent(
                SessionId: SessionId,
                EventType: "writeback_outcome",
                Source: "writeback",
                SourceId: $"task-recal-{i}",
                TargetType: "correlation",
                TargetId: "recal:th1:e1:qh1",
                PayloadJson: payload,
                DirectiveType: DirectiveType.ConfidenceAdjust,
                DirectiveJson: null,
                CausalChainJson: null)
            {
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-1).ToString("o"),
                AppliedAt = DateTimeOffset.UtcNow.AddDays(-1).ToString("o"),
                AppliedBy = "inline"
            };
            _db.InsertFeedbackEvent(evt);
        }

        var processor = new FeedbackProcessor(_db, SessionId);
        processor.ProcessRecalibration();

        var events = _db.GetFeedbackEventsForTarget(SessionId, "recal:th1:e1:qh1");
        Assert.Contains(events, e => e.DirectiveType == DirectiveType.Recalibrate);

        var window = _db.GetWindowOverride(SessionId, "th1", "e1");
        Assert.NotNull(window);
        Assert.True(window!.Value > 5.0);
    }

    // ── Helpers ──

    private void SeedCorrelation(string key, double initialConfidence)
    {
        // Parse tree_hash, element_id from key pattern: prefix:tree_hash:element_id:query_shape_hash
        var parts = key.Split(':');
        string treeHash = parts.Length >= 4 ? parts[1] : "th-default";
        string elementId = parts.Length >= 4 ? parts[2] : "el-default";
        string queryShapeHash = parts.Length >= 4 ? parts[3] : "qsh-default";

        _db.UpsertCorrelatedAction(SessionId, key, treeHash, elementId, "Button", queryShapeHash, false, "Rx");
        _db.UpdateCorrelationConfidence(SessionId, key, initialConfidence);
    }

    private void BackdateLastSeen(string correlationKey, int days)
    {
        var backdated = DateTimeOffset.UtcNow.AddDays(-days).ToString("o");
        var connField = typeof(AgentStateDb).GetField("_conn", BindingFlags.NonPublic | BindingFlags.Instance);
        var conn = (SqliteConnection)connField!.GetValue(_db)!;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE correlated_actions SET last_seen = @ls WHERE correlation_key = @key AND session_id = @sid";
        cmd.Parameters.AddWithValue("@ls", backdated);
        cmd.Parameters.AddWithValue("@key", correlationKey);
        cmd.Parameters.AddWithValue("@sid", SessionId);
        cmd.ExecuteNonQuery();
    }
}
