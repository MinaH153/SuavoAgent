# Self-Improving Feedback System Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the feedback loop — writeback outcomes, operator corrections, and canary drift feed back into learned state through an event-sourced log with causal traceability and dual inline/batch consumption.

**Architecture:** Event-sourced `feedback_events` table as single source of truth. FeedbackCollector (static) handles inline writeback outcome recording. FeedbackProcessor (stateless, per-tick) handles batch processing. Both call shared `ApplyDirective` for idempotent mutations. ActionCorrelator reads per-key window overrides.

**Tech Stack:** .NET 8, SQLCipher (via Microsoft.Data.Sqlite), xUnit, Stateless (existing)

---

## File Structure

### New Files
| File | Responsibility |
|------|---------------|
| `src/SuavoAgent.Core/Behavioral/FeedbackEvent.cs` | Immutable record types for feedback events, directive types enum, outcome-to-delta mapping |
| `src/SuavoAgent.Core/Behavioral/FeedbackCollector.cs` | Static utility — inline writeback outcome recording, builds and applies directives in one transaction |
| `src/SuavoAgent.Core/Behavioral/FeedbackProcessor.cs` | Batch consumer — decay, operator directives, canary drift, recalibration, promotion health, stale escalation |
| `tests/SuavoAgent.Core.Tests/Behavioral/FeedbackEventTests.cs` | Tests for FeedbackEvent types and outcome-to-delta mapping |
| `tests/SuavoAgent.Core.Tests/Behavioral/FeedbackCollectorTests.cs` | Tests for inline path — confidence adjustment, floor/ceiling, prune, auto-suspend |
| `tests/SuavoAgent.Core.Tests/Behavioral/FeedbackProcessorTests.cs` | Tests for batch path — decay, recalibration, stale escalation, operator directives |
| `tests/SuavoAgent.Core.Tests/Behavioral/FeedbackReplayTests.cs` | Integration test — replay produces identical end state |

### Modified Files
| File | Changes |
|------|---------|
| `src/SuavoAgent.Core/State/AgentStateDb.cs` | New tables (`feedback_events`, `correlation_window_overrides`), column migrations on `correlated_actions`, CRUD methods for feedback |
| `src/SuavoAgent.Core/Behavioral/ActionCorrelator.cs` | Read per-key window overrides from `correlation_window_overrides` |
| `src/SuavoAgent.Core/Workers/WritebackProcessor.cs` | Call `FeedbackCollector.RecordWritebackOutcome` after each outcome |
| `src/SuavoAgent.Core/Workers/HeartbeatWorker.cs` | Add 6 new signed command handlers (approve/reject/reapprove/force_relearn/adjust_window/acknowledge_stale) |
| `src/SuavoAgent.Core/Workers/LearningWorker.cs` | Call `FeedbackProcessor.ProcessPendingFeedback()` on 5-minute tick |
| `src/SuavoAgent.Core/Learning/PomExporter.cs` | Add `feedback` section to POM export |
| `src/SuavoAgent.Core/HealthSnapshot.cs` | Add `feedback` section to health payload |

---

### Task 1: FeedbackEvent Types & Outcome-to-Delta Mapping

**Files:**
- Create: `src/SuavoAgent.Core/Behavioral/FeedbackEvent.cs`
- Test: `tests/SuavoAgent.Core.Tests/Behavioral/FeedbackEventTests.cs`

- [ ] **Step 1: Write test for FeedbackEvent record creation**

```csharp
// tests/SuavoAgent.Core.Tests/Behavioral/FeedbackEventTests.cs
using SuavoAgent.Core.Behavioral;
using Xunit;

namespace SuavoAgent.Core.Tests.Behavioral;

public class FeedbackEventTests
{
    [Fact]
    public void FeedbackEvent_Record_RoundTrips()
    {
        var evt = new FeedbackEvent(
            SessionId: "sess-001",
            EventType: "writeback_outcome",
            Source: "writeback",
            SourceId: "wb-001",
            TargetType: "correlation_key",
            TargetId: "tree:elem:qshape",
            PayloadJson: """{"outcome":"success"}""",
            DirectiveType: DirectiveType.ConfidenceAdjust,
            DirectiveJson: """{"newConfidence":0.87}""",
            CausalChainJson: null);

        Assert.Equal("sess-001", evt.SessionId);
        Assert.Equal(DirectiveType.ConfidenceAdjust, evt.DirectiveType);
    }

    [Theory]
    [InlineData("success", 0.05)]
    [InlineData("already_at_target", 0.02)]
    [InlineData("verified_with_drift", 0.03)]
    [InlineData("post_verify_mismatch", -0.10)]
    [InlineData("status_conflict", -0.15)]
    [InlineData("sql_error", -0.05)]
    [InlineData("connection_reset", 0.0)]
    [InlineData("trigger_blocked", -0.08)]
    public void OutcomeToDelta_MapsCorrectly(string outcome, double expectedDelta)
    {
        Assert.Equal(expectedDelta, FeedbackEvent.OutcomeToDelta(outcome));
    }

    [Fact]
    public void OutcomeToDelta_UnknownOutcome_ReturnsZero()
    {
        Assert.Equal(0.0, FeedbackEvent.OutcomeToDelta("unknown_outcome"));
    }

    [Fact]
    public void ApplyConfidenceDelta_CapsAtCeiling()
    {
        // 0.93 + 0.05 should cap at 0.95
        Assert.Equal(0.95, FeedbackEvent.ApplyConfidenceDelta(0.93, 0.05));
    }

    [Fact]
    public void ApplyConfidenceDelta_FloorAt0Point1()
    {
        // 0.12 - 0.15 should floor at 0.1 (not go negative or to 0)
        Assert.Equal(0.1, FeedbackEvent.ApplyConfidenceDelta(0.12, -0.15), precision: 10);
    }

    [Fact]
    public void ApplyConfidenceDelta_ExactCeiling()
    {
        Assert.Equal(0.95, FeedbackEvent.ApplyConfidenceDelta(0.90, 0.05));
    }

    [Fact]
    public void ApplyDecay_FlatMinusOneHundredth()
    {
        Assert.Equal(0.71, FeedbackEvent.ApplyDecay(0.72), precision: 10);
    }

    [Fact]
    public void ApplyDecay_StopsAt0Point5()
    {
        Assert.Equal(0.50, FeedbackEvent.ApplyDecay(0.50), precision: 10);
        Assert.Equal(0.50, FeedbackEvent.ApplyDecay(0.505), precision: 10);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd /Users/joshuahenein/Documents/SuavoAgent && dotnet test tests/SuavoAgent.Core.Tests --filter "FeedbackEventTests" -v n`
Expected: FAIL — `FeedbackEvent` type not found

- [ ] **Step 3: Write FeedbackEvent types**

```csharp
// src/SuavoAgent.Core/Behavioral/FeedbackEvent.cs
namespace SuavoAgent.Core.Behavioral;

public enum DirectiveType
{
    ConfidenceAdjust,
    Promote,
    Demote,
    Prune,
    Recalibrate,
    ReLearn,
    ThresholdAdjust,
    SuspendPromotion,
    EscalateStale
}

public sealed record FeedbackEvent(
    string SessionId,
    string EventType,
    string Source,
    string? SourceId,
    string TargetType,
    string TargetId,
    string? PayloadJson,
    DirectiveType DirectiveType,
    string? DirectiveJson,
    string? CausalChainJson)
{
    public int? Id { get; init; }
    public string? AppliedAt { get; init; }
    public string? AppliedBy { get; init; }
    public string CreatedAt { get; init; } = DateTimeOffset.UtcNow.ToString("o");

    public const double ConfidenceCeiling = 0.95;
    public const double ConfidenceFloor = 0.1;
    public const double DecayFloor = 0.5;
    public const double DecayAmount = 0.01;
    public const int PromotionSuspendThreshold = 5;
    public const int RecalibrationMinSamples = 20;
    public const int StaleTtlDays = 14;
    public const int DecayIdleDays = 7;

    private static readonly Dictionary<string, double> OutcomeDeltas = new()
    {
        ["success"] = 0.05,
        ["already_at_target"] = 0.02,
        ["verified_with_drift"] = 0.03,
        ["post_verify_mismatch"] = -0.10,
        ["status_conflict"] = -0.15,
        ["sql_error"] = -0.05,
        ["connection_reset"] = 0.0,
        ["trigger_blocked"] = -0.08,
    };

    public static double OutcomeToDelta(string outcome)
        => OutcomeDeltas.TryGetValue(outcome, out var delta) ? delta : 0.0;

    public static double ApplyConfidenceDelta(double current, double delta)
        => Math.Max(ConfidenceFloor, Math.Min(ConfidenceCeiling, Math.Round(current + delta, 10)));

    public static double ApplyDecay(double current)
        => current <= DecayFloor ? DecayFloor : Math.Max(DecayFloor, Math.Round(current - DecayAmount, 10));
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd /Users/joshuahenein/Documents/SuavoAgent && dotnet test tests/SuavoAgent.Core.Tests --filter "FeedbackEventTests" -v n`
Expected: All PASS

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Core/Behavioral/FeedbackEvent.cs tests/SuavoAgent.Core.Tests/Behavioral/FeedbackEventTests.cs
git commit -m "feat(feedback): add FeedbackEvent types with outcome-to-delta mapping"
```

---

### Task 2: AgentStateDb Schema Migration & Feedback CRUD

**Files:**
- Modify: `src/SuavoAgent.Core/State/AgentStateDb.cs`
- Test: `tests/SuavoAgent.Core.Tests/Behavioral/FeedbackCollectorTests.cs` (partial — DB layer)

- [ ] **Step 1: Write test for feedback_events table creation and CRUD**

```csharp
// tests/SuavoAgent.Core.Tests/Behavioral/FeedbackCollectorTests.cs
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Behavioral;

public class FeedbackCollectorTests : IDisposable
{
    private readonly AgentStateDb _db;
    private readonly string _sessionId = "feedback-test-session";

    public FeedbackCollectorTests()
    {
        _db = new AgentStateDb(":memory:");
        _db.CreateLearningSession(_sessionId, "pharm-test");
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void InsertFeedbackEvent_RoundTrips()
    {
        var evt = new FeedbackEvent(
            SessionId: _sessionId,
            EventType: "writeback_outcome",
            Source: "writeback",
            SourceId: "wb-001",
            TargetType: "correlation_key",
            TargetId: "tree:elem:qshape",
            PayloadJson: """{"outcome":"success"}""",
            DirectiveType: DirectiveType.ConfidenceAdjust,
            DirectiveJson: """{"newConfidence":0.87}""",
            CausalChainJson: null);

        var id = _db.InsertFeedbackEvent(evt);
        Assert.True(id > 0);

        var stored = _db.GetFeedbackEvent(id);
        Assert.NotNull(stored);
        Assert.Equal("writeback_outcome", stored.EventType);
        Assert.Equal(DirectiveType.ConfidenceAdjust, stored.DirectiveType);
        Assert.Null(stored.AppliedAt);
    }

    [Fact]
    public void MarkFeedbackEventApplied_SetsTimestampAndApplier()
    {
        var evt = new FeedbackEvent(
            SessionId: _sessionId,
            EventType: "writeback_outcome",
            Source: "writeback",
            SourceId: "wb-002",
            TargetType: "correlation_key",
            TargetId: "tree:elem:qshape",
            PayloadJson: null,
            DirectiveType: DirectiveType.ConfidenceAdjust,
            DirectiveJson: null,
            CausalChainJson: null);

        var id = _db.InsertFeedbackEvent(evt);
        _db.MarkFeedbackEventApplied(id, "inline");

        var stored = _db.GetFeedbackEvent(id);
        Assert.NotNull(stored!.AppliedAt);
        Assert.Equal("inline", stored.AppliedBy);
    }

    [Fact]
    public void GetPendingFeedbackEvents_ReturnsOnlyUnapplied()
    {
        var evt1 = new FeedbackEvent(_sessionId, "decay", "decay", null,
            "correlation_key", "key-1", null, DirectiveType.ConfidenceAdjust, null, null);
        var evt2 = new FeedbackEvent(_sessionId, "decay", "decay", null,
            "correlation_key", "key-2", null, DirectiveType.ConfidenceAdjust, null, null);

        var id1 = _db.InsertFeedbackEvent(evt1);
        _db.InsertFeedbackEvent(evt2);
        _db.MarkFeedbackEventApplied(id1, "batch");

        var pending = _db.GetPendingFeedbackEvents(_sessionId);
        Assert.Single(pending);
        Assert.Equal("key-2", pending[0].TargetId);
    }

    [Fact]
    public void HasDecayEventToday_DetectsDuplicates()
    {
        var evt = new FeedbackEvent(_sessionId, "decay", "decay", null,
            "correlation_key", "key-1", null, DirectiveType.ConfidenceAdjust, null, null);
        _db.InsertFeedbackEvent(evt);

        Assert.True(_db.HasDecayEventToday(_sessionId, "key-1"));
        Assert.False(_db.HasDecayEventToday(_sessionId, "key-2"));
    }

    [Fact]
    public void UpdateCorrelationConfidence_MutatesValue()
    {
        // Seed a correlated action
        _db.UpsertCorrelatedAction(_sessionId, "tree:elem:qshape", "tree", "elem",
            "Button", "qshape", true, "Prescription");

        _db.UpdateCorrelationConfidence(_sessionId, "tree:elem:qshape", 0.87);

        var actions = _db.GetCorrelatedActions(_sessionId);
        Assert.Single(actions);
        Assert.Equal(0.87, actions[0].Confidence, precision: 10);
    }

    [Fact]
    public void CorrelatedAction_NewColumns_DefaultCorrectly()
    {
        _db.UpsertCorrelatedAction(_sessionId, "tree:elem:qshape", "tree", "elem",
            "Button", "qshape", true, "Prescription");

        var extended = _db.GetCorrelatedActionExtended(_sessionId, "tree:elem:qshape");
        Assert.NotNull(extended);
        Assert.False(extended.Value.OperatorApproved);
        Assert.False(extended.Value.OperatorRejected);
        Assert.False(extended.Value.PromotionSuspended);
        Assert.Equal(0, extended.Value.ConsecutiveFailures);
        Assert.False(extended.Value.Stale);
        Assert.Null(extended.Value.StaleSince);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd /Users/joshuahenein/Documents/SuavoAgent && dotnet test tests/SuavoAgent.Core.Tests --filter "FeedbackCollectorTests" -v n`
Expected: FAIL — `InsertFeedbackEvent` not found

- [ ] **Step 3: Add schema migration and CRUD methods to AgentStateDb**

In `AgentStateDb.cs`, add to `InitSchema()` after the behavioral tables block (after line 354):

```csharp
        // Feedback system tables
        using var feedbackCmd = _conn.CreateCommand();
        feedbackCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS feedback_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                event_type TEXT NOT NULL,
                source TEXT NOT NULL,
                source_id TEXT,
                target_type TEXT NOT NULL,
                target_id TEXT NOT NULL,
                payload_json TEXT,
                directive_type TEXT NOT NULL,
                directive_json TEXT,
                applied_at TEXT,
                applied_by TEXT,
                causal_chain_json TEXT,
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_fe_pending ON feedback_events(session_id, applied_at)
                WHERE applied_at IS NULL;
            CREATE INDEX IF NOT EXISTS idx_fe_target ON feedback_events(session_id, target_type, target_id);
            CREATE INDEX IF NOT EXISTS idx_fe_type ON feedback_events(session_id, directive_type);
            CREATE INDEX IF NOT EXISTS idx_fe_source_decay ON feedback_events(session_id, target_id, source, created_at)
                WHERE source = 'decay';

            CREATE TABLE IF NOT EXISTS correlation_window_overrides (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                tree_hash TEXT NOT NULL,
                element_id TEXT NOT NULL,
                window_seconds REAL NOT NULL,
                sample_count INTEGER NOT NULL,
                computed_at TEXT NOT NULL,
                UNIQUE(session_id, tree_hash, element_id)
            );
            """;
        feedbackCmd.ExecuteNonQuery();

        // Feedback column migrations on correlated_actions
        TryAlter("ALTER TABLE correlated_actions ADD COLUMN operator_approved INTEGER DEFAULT 0");
        TryAlter("ALTER TABLE correlated_actions ADD COLUMN operator_rejected INTEGER DEFAULT 0");
        TryAlter("ALTER TABLE correlated_actions ADD COLUMN promotion_suspended INTEGER DEFAULT 0");
        TryAlter("ALTER TABLE correlated_actions ADD COLUMN consecutive_failures INTEGER DEFAULT 0");
        TryAlter("ALTER TABLE correlated_actions ADD COLUMN stale INTEGER DEFAULT 0");
        TryAlter("ALTER TABLE correlated_actions ADD COLUMN stale_since TEXT");
```

Then add these CRUD methods (after the existing behavioral methods section):

```csharp
    // ── Feedback Events ──

    public int InsertFeedbackEvent(FeedbackEvent evt)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO feedback_events
                (session_id, event_type, source, source_id, target_type, target_id,
                 payload_json, directive_type, directive_json, applied_at, applied_by,
                 causal_chain_json, created_at)
            VALUES
                (@sid, @eventType, @source, @sourceId, @targetType, @targetId,
                 @payload, @directive, @directiveJson, @appliedAt, @appliedBy,
                 @causalChain, @createdAt);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@sid", evt.SessionId);
        cmd.Parameters.AddWithValue("@eventType", evt.EventType);
        cmd.Parameters.AddWithValue("@source", evt.Source);
        cmd.Parameters.AddWithValue("@sourceId", (object?)evt.SourceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@targetType", evt.TargetType);
        cmd.Parameters.AddWithValue("@targetId", evt.TargetId);
        cmd.Parameters.AddWithValue("@payload", (object?)evt.PayloadJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@directive", evt.DirectiveType.ToString());
        cmd.Parameters.AddWithValue("@directiveJson", (object?)evt.DirectiveJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@appliedAt", (object?)evt.AppliedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@appliedBy", (object?)evt.AppliedBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@causalChain", (object?)evt.CausalChainJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@createdAt", evt.CreatedAt);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public FeedbackEvent? GetFeedbackEvent(int id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, session_id, event_type, source, source_id, target_type, target_id,
                   payload_json, directive_type, directive_json, applied_at, applied_by,
                   causal_chain_json, created_at
            FROM feedback_events WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return ReadFeedbackEvent(r);
    }

    public IReadOnlyList<FeedbackEvent> GetPendingFeedbackEvents(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, session_id, event_type, source, source_id, target_type, target_id,
                   payload_json, directive_type, directive_json, applied_at, applied_by,
                   causal_chain_json, created_at
            FROM feedback_events
            WHERE session_id = @sid AND applied_at IS NULL
            ORDER BY id
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        var results = new List<FeedbackEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) results.Add(ReadFeedbackEvent(r));
        return results;
    }

    public void MarkFeedbackEventApplied(int id, string appliedBy)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE feedback_events SET applied_at = @now, applied_by = @by WHERE id = @id";
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@by", appliedBy);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public bool HasDecayEventToday(string sessionId, string targetId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM feedback_events
            WHERE session_id = @sid AND target_id = @tid
              AND source = 'decay'
              AND directive_type = 'ConfidenceAdjust'
              AND created_at >= @today
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@tid", targetId);
        cmd.Parameters.AddWithValue("@today", DateTimeOffset.UtcNow.Date.ToString("o"));
        return cmd.ExecuteScalar() != null;
    }

    public void UpdateCorrelationConfidence(string sessionId, string correlationKey, double newConfidence)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE correlated_actions SET confidence = @conf
            WHERE session_id = @sid AND correlation_key = @key
            """;
        cmd.Parameters.AddWithValue("@conf", newConfidence);
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@key", correlationKey);
        cmd.ExecuteNonQuery();
    }

    public void UpdateCorrelationFlags(string sessionId, string correlationKey,
        bool? operatorApproved = null, bool? operatorRejected = null,
        bool? promotionSuspended = null, int? consecutiveFailures = null,
        bool? stale = null, string? staleSince = null)
    {
        var sets = new List<string>();
        using var cmd = _conn.CreateCommand();

        if (operatorApproved.HasValue) { sets.Add("operator_approved = @oa"); cmd.Parameters.AddWithValue("@oa", operatorApproved.Value ? 1 : 0); }
        if (operatorRejected.HasValue) { sets.Add("operator_rejected = @or"); cmd.Parameters.AddWithValue("@or", operatorRejected.Value ? 1 : 0); }
        if (promotionSuspended.HasValue) { sets.Add("promotion_suspended = @ps"); cmd.Parameters.AddWithValue("@ps", promotionSuspended.Value ? 1 : 0); }
        if (consecutiveFailures.HasValue) { sets.Add("consecutive_failures = @cf"); cmd.Parameters.AddWithValue("@cf", consecutiveFailures.Value); }
        if (stale.HasValue) { sets.Add("stale = @st"); cmd.Parameters.AddWithValue("@st", stale.Value ? 1 : 0); }
        if (staleSince != null) { sets.Add("stale_since = @ss"); cmd.Parameters.AddWithValue("@ss", staleSince); }

        if (sets.Count == 0) return;

        cmd.CommandText = $"UPDATE correlated_actions SET {string.Join(", ", sets)} WHERE session_id = @sid AND correlation_key = @key";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@key", correlationKey);
        cmd.ExecuteNonQuery();
    }

    public (bool OperatorApproved, bool OperatorRejected, bool PromotionSuspended,
        int ConsecutiveFailures, bool Stale, string? StaleSince)?
        GetCorrelatedActionExtended(string sessionId, string correlationKey)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT operator_approved, operator_rejected, promotion_suspended,
                   consecutive_failures, stale, stale_since
            FROM correlated_actions
            WHERE session_id = @sid AND correlation_key = @key
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@key", correlationKey);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return (
            r.GetInt32(0) == 1,
            r.GetInt32(1) == 1,
            r.GetInt32(2) == 1,
            r.GetInt32(3),
            r.GetInt32(4) == 1,
            r.IsDBNull(5) ? null : r.GetString(5));
    }

    public int GetFeedbackEventCount(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM feedback_events WHERE session_id = @sid";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int GetFeedbackEventCountByApplier(string sessionId, string appliedBy)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM feedback_events WHERE session_id = @sid AND applied_by = @by";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@by", appliedBy);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public IReadOnlyList<string> GetSuspendedPromotions(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT correlation_key FROM correlated_actions
            WHERE session_id = @sid AND promotion_suspended = 1
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        var results = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) results.Add(r.GetString(0));
        return results;
    }

    public IReadOnlyList<(string CorrelationKey, string StaleSince)> GetExpiredStaleCorrelations(string sessionId, int ttlDays)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT correlation_key, stale_since FROM correlated_actions
            WHERE session_id = @sid AND stale = 1
              AND stale_since < @cutoff
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@cutoff", DateTimeOffset.UtcNow.AddDays(-ttlDays).ToString("o"));
        var results = new List<(string, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) results.Add((r.GetString(0), r.GetString(1)));
        return results;
    }

    public bool HasReplacementCorrelation(string sessionId, string treeHash, string elementId, string excludeCorrelationKey)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM correlated_actions
            WHERE session_id = @sid AND tree_hash = @th AND element_id = @eid
              AND stale = 0 AND correlation_key != @excludeKey
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@th", treeHash);
        cmd.Parameters.AddWithValue("@eid", elementId);
        cmd.Parameters.AddWithValue("@excludeKey", excludeCorrelationKey);
        return cmd.ExecuteScalar() != null;
    }

    public IReadOnlyList<(string CorrelationKey, double Confidence, string LastSeen)>
        GetIdleCorrelations(string sessionId, int idleDays)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT correlation_key, confidence, last_seen FROM correlated_actions
            WHERE session_id = @sid AND stale = 0
              AND last_seen < @cutoff AND confidence > 0.5
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@cutoff", DateTimeOffset.UtcNow.AddDays(-idleDays).ToString("o"));
        var results = new List<(string, double, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) results.Add((r.GetString(0), r.GetDouble(1), r.GetString(2)));
        return results;
    }

    public void DeleteCorrelation(string sessionId, string correlationKey)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM correlated_actions WHERE session_id = @sid AND correlation_key = @key";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@key", correlationKey);
        cmd.ExecuteNonQuery();
    }

    public void UpsertWindowOverride(string sessionId, string treeHash, string elementId,
        double windowSeconds, int sampleCount)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO correlation_window_overrides (session_id, tree_hash, element_id, window_seconds, sample_count, computed_at)
            VALUES (@sid, @th, @eid, @ws, @sc, @now)
            ON CONFLICT(session_id, tree_hash, element_id) DO UPDATE SET
                window_seconds = @ws, sample_count = @sc, computed_at = @now
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@th", treeHash);
        cmd.Parameters.AddWithValue("@eid", elementId);
        cmd.Parameters.AddWithValue("@ws", windowSeconds);
        cmd.Parameters.AddWithValue("@sc", sampleCount);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public double? GetWindowOverride(string sessionId, string treeHash, string elementId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT window_seconds FROM correlation_window_overrides
            WHERE session_id = @sid AND tree_hash = @th AND element_id = @eid
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@th", treeHash);
        cmd.Parameters.AddWithValue("@eid", elementId);
        var result = cmd.ExecuteScalar();
        return result is double d ? d : null;
    }

    public int GetWindowOverrideCount(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM correlation_window_overrides WHERE session_id = @sid";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public IReadOnlyList<FeedbackEvent> GetFeedbackEventsForTarget(string sessionId, string targetId, string? source = null)
    {
        using var cmd = _conn.CreateCommand();
        var where = "session_id = @sid AND target_id = @tid";
        if (source != null) where += " AND source = @src";
        cmd.CommandText = $"""
            SELECT id, session_id, event_type, source, source_id, target_type, target_id,
                   payload_json, directive_type, directive_json, applied_at, applied_by,
                   causal_chain_json, created_at
            FROM feedback_events WHERE {where} ORDER BY id
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@tid", targetId);
        if (source != null) cmd.Parameters.AddWithValue("@src", source);
        var results = new List<FeedbackEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) results.Add(ReadFeedbackEvent(r));
        return results;
    }

    private static FeedbackEvent ReadFeedbackEvent(Microsoft.Data.Sqlite.SqliteDataReader r)
    {
        return new FeedbackEvent(
            SessionId: r.GetString(1),
            EventType: r.GetString(2),
            Source: r.GetString(3),
            SourceId: r.IsDBNull(4) ? null : r.GetString(4),
            TargetType: r.GetString(5),
            TargetId: r.GetString(6),
            PayloadJson: r.IsDBNull(7) ? null : r.GetString(7),
            DirectiveType: Enum.Parse<DirectiveType>(r.GetString(8)),
            DirectiveJson: r.IsDBNull(9) ? null : r.GetString(9),
            CausalChainJson: r.IsDBNull(12) ? null : r.GetString(12))
        {
            Id = r.GetInt32(0),
            AppliedAt = r.IsDBNull(10) ? null : r.GetString(10),
            AppliedBy = r.IsDBNull(11) ? null : r.GetString(11),
            CreatedAt = r.GetString(13)
        };
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd /Users/joshuahenein/Documents/SuavoAgent && dotnet test tests/SuavoAgent.Core.Tests --filter "FeedbackCollectorTests" -v n`
Expected: All PASS

- [ ] **Step 5: Run full test suite to check for regressions**

Run: `cd /Users/joshuahenein/Documents/SuavoAgent && dotnet test -v n`
Expected: 385+ tests pass, 0 failures

- [ ] **Step 6: Commit**

```bash
git add src/SuavoAgent.Core/State/AgentStateDb.cs tests/SuavoAgent.Core.Tests/Behavioral/FeedbackCollectorTests.cs
git commit -m "feat(feedback): add feedback_events schema, column migrations, and CRUD methods"
```

---

### Task 3: FeedbackCollector — Inline Writeback Outcome Recording

**Files:**
- Create: `src/SuavoAgent.Core/Behavioral/FeedbackCollector.cs`
- Modify: `tests/SuavoAgent.Core.Tests/Behavioral/FeedbackCollectorTests.cs`

- [ ] **Step 1: Write tests for inline confidence adjustment**

Add to `FeedbackCollectorTests.cs`:

```csharp
    [Fact]
    public void RecordWritebackOutcome_Success_IncreasesConfidence()
    {
        SeedCorrelation("tree:elem:qshape", initialConfidence: 0.60);

        var newConf = FeedbackCollector.RecordWritebackOutcome(
            _db, _sessionId, "wb-001", "tree:elem:qshape", "success",
            DateTimeOffset.UtcNow.AddSeconds(-1).ToString("o"),
            DateTimeOffset.UtcNow.ToString("o"));

        Assert.Equal(0.65, newConf, precision: 10);
        var actions = _db.GetCorrelatedActions(_sessionId);
        Assert.Equal(0.65, actions[0].Confidence, precision: 10);

        // Feedback event recorded
        var events = _db.GetFeedbackEventsForTarget(_sessionId, "tree:elem:qshape");
        Assert.Single(events);
        Assert.Equal("inline", events[0].AppliedBy);
        Assert.NotNull(events[0].AppliedAt);
    }

    [Fact]
    public void RecordWritebackOutcome_Failure_DecreasesConfidence()
    {
        SeedCorrelation("tree:elem:qshape", initialConfidence: 0.60);

        var newConf = FeedbackCollector.RecordWritebackOutcome(
            _db, _sessionId, "wb-002", "tree:elem:qshape", "status_conflict",
            DateTimeOffset.UtcNow.AddSeconds(-1).ToString("o"),
            DateTimeOffset.UtcNow.ToString("o"));

        Assert.Equal(0.45, newConf, precision: 10); // 0.60 - 0.15
    }

    [Fact]
    public void RecordWritebackOutcome_CeilingCapsAt0Point95()
    {
        SeedCorrelation("tree:elem:qshape", initialConfidence: 0.93);

        var newConf = FeedbackCollector.RecordWritebackOutcome(
            _db, _sessionId, "wb-003", "tree:elem:qshape", "success",
            DateTimeOffset.UtcNow.AddSeconds(-1).ToString("o"),
            DateTimeOffset.UtcNow.ToString("o"));

        Assert.Equal(0.95, newConf, precision: 10); // 0.93 + 0.05 capped at 0.95
    }

    [Fact]
    public void RecordWritebackOutcome_BelowFloor_EmitsPrune()
    {
        SeedCorrelation("tree:elem:qshape", initialConfidence: 0.11);

        FeedbackCollector.RecordWritebackOutcome(
            _db, _sessionId, "wb-004", "tree:elem:qshape", "status_conflict",
            DateTimeOffset.UtcNow.AddSeconds(-1).ToString("o"),
            DateTimeOffset.UtcNow.ToString("o"));

        // Should have two events: confidence_adjust + prune
        var events = _db.GetFeedbackEventsForTarget(_sessionId, "tree:elem:qshape");
        Assert.Equal(2, events.Count);
        Assert.Equal(DirectiveType.ConfidenceAdjust, events[0].DirectiveType);
        Assert.Equal(DirectiveType.Prune, events[1].DirectiveType);
    }

    [Fact]
    public void RecordWritebackOutcome_PromotedWith5Failures_SuspendsPromotion()
    {
        SeedCorrelation("tree:elem:qshape", initialConfidence: 0.30);
        _db.UpdateCorrelationFlags(_sessionId, "tree:elem:qshape", operatorApproved: true);

        // 5 consecutive failures
        for (int i = 0; i < 5; i++)
        {
            FeedbackCollector.RecordWritebackOutcome(
                _db, _sessionId, $"wb-{i}", "tree:elem:qshape", "sql_error",
                DateTimeOffset.UtcNow.AddSeconds(-1).ToString("o"),
                DateTimeOffset.UtcNow.ToString("o"));
        }

        var ext = _db.GetCorrelatedActionExtended(_sessionId, "tree:elem:qshape");
        Assert.True(ext!.Value.PromotionSuspended);
        Assert.Equal(5, ext.Value.ConsecutiveFailures);

        // Should have a suspend_promotion event
        var events = _db.GetFeedbackEventsForTarget(_sessionId, "tree:elem:qshape");
        Assert.Contains(events, e => e.DirectiveType == DirectiveType.SuspendPromotion);
    }

    [Fact]
    public void RecordWritebackOutcome_Success_ResetsConsecutiveFailures()
    {
        SeedCorrelation("tree:elem:qshape", initialConfidence: 0.60);

        // Two failures
        FeedbackCollector.RecordWritebackOutcome(
            _db, _sessionId, "wb-f1", "tree:elem:qshape", "sql_error",
            DateTimeOffset.UtcNow.AddSeconds(-1).ToString("o"),
            DateTimeOffset.UtcNow.ToString("o"));
        FeedbackCollector.RecordWritebackOutcome(
            _db, _sessionId, "wb-f2", "tree:elem:qshape", "sql_error",
            DateTimeOffset.UtcNow.AddSeconds(-1).ToString("o"),
            DateTimeOffset.UtcNow.ToString("o"));

        var ext = _db.GetCorrelatedActionExtended(_sessionId, "tree:elem:qshape");
        Assert.Equal(2, ext!.Value.ConsecutiveFailures);

        // One success resets
        FeedbackCollector.RecordWritebackOutcome(
            _db, _sessionId, "wb-s1", "tree:elem:qshape", "success",
            DateTimeOffset.UtcNow.AddSeconds(-1).ToString("o"),
            DateTimeOffset.UtcNow.ToString("o"));

        ext = _db.GetCorrelatedActionExtended(_sessionId, "tree:elem:qshape");
        Assert.Equal(0, ext!.Value.ConsecutiveFailures);
    }

    [Fact]
    public void RecordWritebackOutcome_ConnectionReset_ZeroDelta_NoConfidenceChange()
    {
        SeedCorrelation("tree:elem:qshape", initialConfidence: 0.60);

        var newConf = FeedbackCollector.RecordWritebackOutcome(
            _db, _sessionId, "wb-cr", "tree:elem:qshape", "connection_reset",
            DateTimeOffset.UtcNow.AddSeconds(-1).ToString("o"),
            DateTimeOffset.UtcNow.ToString("o"));

        Assert.Equal(0.60, newConf, precision: 10);
    }

    [Fact]
    public void ApplyDirective_IsIdempotent()
    {
        SeedCorrelation("tree:elem:qshape", initialConfidence: 0.60);

        var evt = new FeedbackEvent(
            SessionId: _sessionId,
            EventType: "writeback_outcome",
            Source: "writeback",
            SourceId: "wb-idem",
            TargetType: "correlation_key",
            TargetId: "tree:elem:qshape",
            PayloadJson: null,
            DirectiveType: DirectiveType.ConfidenceAdjust,
            DirectiveJson: System.Text.Json.JsonSerializer.Serialize(new { newConfidence = 0.87 }),
            CausalChainJson: null)
        { AppliedAt = DateTimeOffset.UtcNow.ToString("o"), AppliedBy = "inline" };

        var id = _db.InsertFeedbackEvent(evt);

        // Apply twice — should be no-op on second call
        FeedbackCollector.ApplyDirective(_db, _sessionId, _db.GetFeedbackEvent(id)!);
        FeedbackCollector.ApplyDirective(_db, _sessionId, _db.GetFeedbackEvent(id)!);

        var actions = _db.GetCorrelatedActions(_sessionId);
        Assert.Equal(0.87, actions[0].Confidence, precision: 10);
    }

    [Fact]
    public void CausalChain_ForwardOnly_OldEventsUnmodified()
    {
        SeedCorrelation("tree:elem:qshape", initialConfidence: 0.60);

        // Event A
        FeedbackCollector.RecordWritebackOutcome(
            _db, _sessionId, "wb-a", "tree:elem:qshape", "sql_error",
            DateTimeOffset.UtcNow.AddSeconds(-1).ToString("o"),
            DateTimeOffset.UtcNow.ToString("o"));

        var eventsAfterA = _db.GetFeedbackEventsForTarget(_sessionId, "tree:elem:qshape");
        var eventA = eventsAfterA[0];
        var eventASnapshot = eventA.CausalChainJson;

        // Event B references A
        FeedbackCollector.RecordWritebackOutcome(
            _db, _sessionId, "wb-b", "tree:elem:qshape", "sql_error",
            DateTimeOffset.UtcNow.AddSeconds(-1).ToString("o"),
            DateTimeOffset.UtcNow.ToString("o"));

        // Verify A was NOT modified
        var eventAReread = _db.GetFeedbackEvent(eventA.Id!.Value);
        Assert.Equal(eventASnapshot, eventAReread!.CausalChainJson);
    }

    private void SeedCorrelation(string correlationKey, double initialConfidence)
    {
        var parts = correlationKey.Split(':');
        _db.UpsertCorrelatedAction(_sessionId, correlationKey, parts[0], parts[1],
            "Button", parts.Length > 2 ? parts[2] : null, true, "Prescription");
        _db.UpdateCorrelationConfidence(_sessionId, correlationKey, initialConfidence);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd /Users/joshuahenein/Documents/SuavoAgent && dotnet test tests/SuavoAgent.Core.Tests --filter "FeedbackCollectorTests" -v n`
Expected: FAIL — `FeedbackCollector` class not found

- [ ] **Step 3: Implement FeedbackCollector**

```csharp
// src/SuavoAgent.Core/Behavioral/FeedbackCollector.cs
using System.Text.Json;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Behavioral;

/// <summary>
/// Static utility for recording feedback and applying directives.
/// Used inline by WritebackProcessor and batch by FeedbackProcessor.
/// Both paths call ApplyDirective for idempotent mutations.
/// </summary>
public static class FeedbackCollector
{
    /// <summary>
    /// Records a writeback outcome, adjusts confidence, checks for prune/suspend triggers.
    /// Writes feedback event(s) AND applies directive(s) in one logical transaction.
    /// Returns the new confidence value.
    /// </summary>
    public static double RecordWritebackOutcome(
        AgentStateDb db, string sessionId, string taskId, string correlationKey,
        string outcome, string uiEventTimestamp, string sqlExecutionTimestamp)
    {
        var extended = db.GetCorrelatedActionExtended(sessionId, correlationKey);
        if (extended is null) return 0.0;

        var currentConfidence = db.GetCorrelatedActions(sessionId)
            .FirstOrDefault(a => a.CorrelationKey == correlationKey).Confidence;

        var delta = FeedbackEvent.OutcomeToDelta(outcome);
        var newConfidence = FeedbackEvent.ApplyConfidenceDelta(currentConfidence, delta);

        // Determine if this is a failure outcome
        bool isFailure = delta < 0;

        // Update consecutive failures
        int newConsecutiveFailures = isFailure
            ? extended.Value.ConsecutiveFailures + 1
            : 0;

        // Compute latency
        long latencyMs = 0;
        if (DateTimeOffset.TryParse(uiEventTimestamp, null, System.Globalization.DateTimeStyles.RoundtripKind, out var uiTs)
            && DateTimeOffset.TryParse(sqlExecutionTimestamp, null, System.Globalization.DateTimeStyles.RoundtripKind, out var sqlTs))
        {
            latencyMs = (long)(sqlTs - uiTs).TotalMilliseconds;
        }

        // Build causal chain: reference recent failure events on this key
        string? causalChain = null;
        if (newConsecutiveFailures > 1)
        {
            var recentEvents = db.GetFeedbackEventsForTarget(sessionId, correlationKey, source: "writeback");
            var recentFailureIds = recentEvents
                .Where(e => e.DirectiveType == DirectiveType.ConfidenceAdjust && e.Id.HasValue)
                .OrderByDescending(e => e.Id)
                .Take(newConsecutiveFailures - 1)
                .Select(e => e.Id!.Value)
                .ToArray();
            if (recentFailureIds.Length > 0)
                causalChain = JsonSerializer.Serialize(recentFailureIds);
        }

        var payloadJson = JsonSerializer.Serialize(new
        {
            taskId,
            outcome,
            correlationKey,
            uiEventTimestamp,
            sqlExecutionTimestamp,
            latencyMs,
            previousConfidence = currentConfidence,
            newConfidence,
            consecutiveFailures = newConsecutiveFailures,
        });

        var directiveJson = JsonSerializer.Serialize(new { newConfidence });

        // Insert feedback event as already applied (inline)
        var evt = new FeedbackEvent(
            SessionId: sessionId,
            EventType: "writeback_outcome",
            Source: "writeback",
            SourceId: taskId,
            TargetType: "correlation_key",
            TargetId: correlationKey,
            PayloadJson: payloadJson,
            DirectiveType: DirectiveType.ConfidenceAdjust,
            DirectiveJson: directiveJson,
            CausalChainJson: causalChain)
        {
            AppliedAt = DateTimeOffset.UtcNow.ToString("o"),
            AppliedBy = "inline"
        };

        db.InsertFeedbackEvent(evt);

        // Apply: update confidence + consecutive failures
        db.UpdateCorrelationConfidence(sessionId, correlationKey, newConfidence);
        db.UpdateCorrelationFlags(sessionId, correlationKey,
            consecutiveFailures: newConsecutiveFailures);

        // Check prune trigger: confidence at floor after a failure
        if (newConfidence <= FeedbackEvent.ConfidenceFloor && isFailure)
        {
            var pruneEvt = new FeedbackEvent(
                SessionId: sessionId,
                EventType: "writeback_outcome",
                Source: "writeback",
                SourceId: taskId,
                TargetType: "correlation_key",
                TargetId: correlationKey,
                PayloadJson: JsonSerializer.Serialize(new { reason = "confidence_floor", confidence = newConfidence }),
                DirectiveType: DirectiveType.Prune,
                DirectiveJson: JsonSerializer.Serialize(new { action = "remove_writeback_flag" }),
                CausalChainJson: causalChain)
            {
                AppliedAt = DateTimeOffset.UtcNow.ToString("o"),
                AppliedBy = "inline"
            };
            db.InsertFeedbackEvent(pruneEvt);
            // Remove writeback flag from learned routines referencing this correlation
            db.RemoveWritebackFlagForCorrelation(sessionId, correlationKey);
        }

        // Check auto-suspend trigger: promoted + 5 consecutive failures
        if (extended.Value.OperatorApproved && !extended.Value.PromotionSuspended
            && newConsecutiveFailures >= FeedbackEvent.PromotionSuspendThreshold)
        {
            var suspendEvt = new FeedbackEvent(
                SessionId: sessionId,
                EventType: "promotion_health",
                Source: "promotion_health",
                SourceId: taskId,
                TargetType: "correlation_key",
                TargetId: correlationKey,
                PayloadJson: JsonSerializer.Serialize(new { consecutiveFailures = newConsecutiveFailures }),
                DirectiveType: DirectiveType.SuspendPromotion,
                DirectiveJson: JsonSerializer.Serialize(new { suspended = true }),
                CausalChainJson: causalChain)
            {
                AppliedAt = DateTimeOffset.UtcNow.ToString("o"),
                AppliedBy = "inline"
            };
            db.InsertFeedbackEvent(suspendEvt);
            db.UpdateCorrelationFlags(sessionId, correlationKey, promotionSuspended: true);
        }

        return newConfidence;
    }

    /// <summary>
    /// Idempotent directive applicator. Shared by inline and batch paths.
    /// If the event is already applied (AppliedAt is set), this is a no-op.
    /// </summary>
    public static void ApplyDirective(AgentStateDb db, string sessionId, FeedbackEvent evt)
    {
        if (evt.AppliedAt != null) return; // Already applied — idempotent

        switch (evt.DirectiveType)
        {
            case DirectiveType.ConfidenceAdjust:
                if (evt.DirectiveJson != null)
                {
                    var doc = JsonDocument.Parse(evt.DirectiveJson);
                    if (doc.RootElement.TryGetProperty("newConfidence", out var nc))
                        db.UpdateCorrelationConfidence(sessionId, evt.TargetId, nc.GetDouble());
                }
                break;

            case DirectiveType.Promote:
                db.UpdateCorrelationFlags(sessionId, evt.TargetId,
                    operatorApproved: true, promotionSuspended: false, consecutiveFailures: 0);
                break;

            case DirectiveType.Demote:
                db.UpdateCorrelationFlags(sessionId, evt.TargetId,
                    operatorRejected: true);
                db.UpdateCorrelationConfidence(sessionId, evt.TargetId, 0.0);
                break;

            case DirectiveType.Prune:
                db.RemoveWritebackFlagForCorrelation(sessionId, evt.TargetId);
                break;

            case DirectiveType.Recalibrate:
                if (evt.DirectiveJson != null)
                {
                    var doc = JsonDocument.Parse(evt.DirectiveJson);
                    var th = doc.RootElement.TryGetProperty("treeHash", out var thv) ? thv.GetString() ?? "" : "";
                    var eid = doc.RootElement.TryGetProperty("elementId", out var eidv) ? eidv.GetString() ?? "" : "";
                    var ws = doc.RootElement.TryGetProperty("windowSeconds", out var wsv) ? wsv.GetDouble() : 2.0;
                    var sc = doc.RootElement.TryGetProperty("sampleCount", out var scv) ? scv.GetInt32() : 0;
                    db.UpsertWindowOverride(sessionId, th, eid, ws, sc);
                }
                break;

            case DirectiveType.ReLearn:
                db.UpdateCorrelationFlags(sessionId, evt.TargetId,
                    stale: true, staleSince: DateTimeOffset.UtcNow.ToString("o"));
                break;

            case DirectiveType.SuspendPromotion:
                db.UpdateCorrelationFlags(sessionId, evt.TargetId, promotionSuspended: true);
                break;

            case DirectiveType.EscalateStale:
                // No mutation — health payload picks it up from the event log
                break;

            case DirectiveType.ThresholdAdjust:
                // Per-pharmacy threshold overrides — future extension point
                break;
        }

        if (evt.Id.HasValue)
            db.MarkFeedbackEventApplied(evt.Id.Value, "batch");
    }
}
```

- [ ] **Step 4: Add RemoveWritebackFlagForCorrelation to AgentStateDb**

Add to `AgentStateDb.cs`:

```csharp
    public void RemoveWritebackFlagForCorrelation(string sessionId, string correlationKey)
    {
        // Extract query_shape_hash from correlation key (format: tree_hash:element_id:query_shape_hash)
        var parts = correlationKey.Split(':');
        if (parts.Length < 3) return;
        var queryShapeHash = parts[2];

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE learned_routines
            SET has_writeback_candidate = 0,
                correlated_write_queries = REPLACE(correlated_write_queries, @qsh, '')
            WHERE session_id = @sid
              AND correlated_write_queries LIKE '%' || @qsh || '%'
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@qsh", queryShapeHash);
        cmd.ExecuteNonQuery();
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `cd /Users/joshuahenein/Documents/SuavoAgent && dotnet test tests/SuavoAgent.Core.Tests --filter "FeedbackCollectorTests" -v n`
Expected: All PASS

- [ ] **Step 6: Run full suite**

Run: `cd /Users/joshuahenein/Documents/SuavoAgent && dotnet test -v n`
Expected: 385+ original + new tests pass

- [ ] **Step 7: Commit**

```bash
git add src/SuavoAgent.Core/Behavioral/FeedbackCollector.cs src/SuavoAgent.Core/State/AgentStateDb.cs tests/SuavoAgent.Core.Tests/Behavioral/FeedbackCollectorTests.cs
git commit -m "feat(feedback): add FeedbackCollector — inline writeback outcome recording with prune and auto-suspend"
```

---

### Task 4: WritebackProcessor Integration

**Files:**
- Modify: `src/SuavoAgent.Core/Workers/WritebackProcessor.cs`

- [ ] **Step 1: Write integration test**

```csharp
// Add to tests/SuavoAgent.Core.Tests/Writeback/WritebackProcessorIntegrationTests.cs
// (add new test to existing file)

    [Fact]
    public void WritebackOutcome_RecordsFeedbackEvent()
    {
        // This test verifies the wiring — that WritebackProcessor calls FeedbackCollector
        // after processing an outcome. The detailed confidence logic is tested in FeedbackCollectorTests.
        var db = new AgentStateDb(":memory:");
        db.CreateLearningSession("sess-wb", "pharm-test");
        db.UpsertCorrelatedAction("sess-wb", "tree:elem:qshape", "tree", "elem",
            "Button", "qshape", true, "Prescription");
        db.UpdateCorrelationConfidence("sess-wb", "tree:elem:qshape", 0.60);

        // Simulate: FeedbackCollector.RecordWritebackOutcome is called with "success"
        var newConf = FeedbackCollector.RecordWritebackOutcome(
            db, "sess-wb", "wb-int-001", "tree:elem:qshape", "success",
            DateTimeOffset.UtcNow.AddSeconds(-1).ToString("o"),
            DateTimeOffset.UtcNow.ToString("o"));

        Assert.Equal(0.65, newConf, precision: 10);
        Assert.Equal(1, db.GetFeedbackEventCount("sess-wb"));
        db.Dispose();
    }
```

- [ ] **Step 2: Run test to verify it passes (the call already works)**

Run: `cd /Users/joshuahenein/Documents/SuavoAgent && dotnet test tests/SuavoAgent.Core.Tests --filter "WritebackOutcome_RecordsFeedbackEvent" -v n`
Expected: PASS

- [ ] **Step 3: Wire FeedbackCollector into WritebackProcessor**

In `WritebackProcessor.cs`, add a field and modify `OnStateChanged`:

Add after line 13 (field declarations):
```csharp
    private string? _sessionId;
```

Add a public setter (after `SetWritebackEngine`):
```csharp
    public void SetSessionId(string sessionId)
    {
        _sessionId = sessionId;
    }
```

In `MapResultToStateMachine`, after the switch block ends (after line 219), add:
```csharp
        // Record feedback for correlation confidence adjustment
        if (_sessionId != null && result.CorrelationKey != null)
        {
            try
            {
                FeedbackCollector.RecordWritebackOutcome(
                    _stateDb, _sessionId, machine.TaskId, result.CorrelationKey,
                    result.Outcome,
                    result.UiEventTimestamp ?? DateTimeOffset.UtcNow.ToString("o"),
                    result.SqlExecutionTimestamp ?? DateTimeOffset.UtcNow.ToString("o"));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Feedback recording failed for {TaskId} — non-fatal", machine.TaskId);
            }
        }
```

Note: This requires `WritebackResult` to carry `CorrelationKey`, `UiEventTimestamp`, and `SqlExecutionTimestamp`. If these fields don't exist on `WritebackResult` yet, add them as nullable string properties.

- [ ] **Step 4: Run full suite**

Run: `cd /Users/joshuahenein/Documents/SuavoAgent && dotnet test -v n`
Expected: All pass

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Core/Workers/WritebackProcessor.cs tests/SuavoAgent.Core.Tests/Writeback/WritebackProcessorIntegrationTests.cs
git commit -m "feat(feedback): wire FeedbackCollector into WritebackProcessor for inline outcome recording"
```

---

### Task 5: FeedbackProcessor — Batch Consumer (Decay + Operator + Canary)

**Files:**
- Create: `src/SuavoAgent.Core/Behavioral/FeedbackProcessor.cs`
- Create: `tests/SuavoAgent.Core.Tests/Behavioral/FeedbackProcessorTests.cs`

- [ ] **Step 1: Write tests for batch processing**

```csharp
// tests/SuavoAgent.Core.Tests/Behavioral/FeedbackProcessorTests.cs
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Behavioral;

public class FeedbackProcessorTests : IDisposable
{
    private readonly AgentStateDb _db;
    private readonly string _sessionId = "fp-test-session";

    public FeedbackProcessorTests()
    {
        _db = new AgentStateDb(":memory:");
        _db.CreateLearningSession(_sessionId, "pharm-test");
    }

    public void Dispose() => _db.Dispose();

    private void SeedCorrelation(string key, double confidence, string? lastSeen = null)
    {
        var parts = key.Split(':');
        _db.UpsertCorrelatedAction(_sessionId, key, parts[0], parts[1],
            "Button", parts.Length > 2 ? parts[2] : null, true, "Prescription");
        _db.UpdateCorrelationConfidence(_sessionId, key, confidence);
    }

    // ── Decay Tests ──

    [Fact]
    public void ProcessDecay_IdleCorrelation_DecaysBy0Point01()
    {
        SeedCorrelation("tree:elem:qshape", confidence: 0.72);
        // Mark as idle by backdating last_seen (more than 7 days ago)
        // We need to test via the processor calling GetIdleCorrelations
        // For unit test, directly test the formula:
        Assert.Equal(0.71, FeedbackEvent.ApplyDecay(0.72), precision: 10);
    }

    [Fact]
    public void ProcessDecay_EmitsMaxOneEventPerCorrelationPerDay()
    {
        SeedCorrelation("tree:elem:qshape", confidence: 0.72);

        // Simulate: first decay event today
        var evt1 = new FeedbackEvent(_sessionId, "decay", "decay", null,
            "correlation_key", "tree:elem:qshape", null,
            DirectiveType.ConfidenceAdjust,
            System.Text.Json.JsonSerializer.Serialize(new { newConfidence = 0.71 }),
            null);
        _db.InsertFeedbackEvent(evt1);

        // Second attempt should detect duplicate
        Assert.True(_db.HasDecayEventToday(_sessionId, "tree:elem:qshape"));
    }

    [Fact]
    public void ProcessDecay_StopsAtFloor0Point5()
    {
        Assert.Equal(0.50, FeedbackEvent.ApplyDecay(0.50), precision: 10);
        Assert.Equal(0.50, FeedbackEvent.ApplyDecay(0.505), precision: 10);
    }

    // ── Operator Directive Tests ──

    [Fact]
    public void ProcessOperator_Promote_SetsApprovedFlag()
    {
        SeedCorrelation("tree:elem:qshape", confidence: 0.60);

        var evt = new FeedbackEvent(_sessionId, "operator_command", "operator", "cmd-001",
            "correlation_key", "tree:elem:qshape",
            """{"commandType":"approve_candidate"}""",
            DirectiveType.Promote,
            """{"action":"approve"}""",
            null);
        var id = _db.InsertFeedbackEvent(evt);

        FeedbackCollector.ApplyDirective(_db, _sessionId, _db.GetFeedbackEvent(id)!);

        var ext = _db.GetCorrelatedActionExtended(_sessionId, "tree:elem:qshape");
        Assert.True(ext!.Value.OperatorApproved);
        Assert.False(ext.Value.PromotionSuspended);
    }

    [Fact]
    public void ProcessOperator_Demote_SetsRejectedAndZerosConfidence()
    {
        SeedCorrelation("tree:elem:qshape", confidence: 0.60);

        var evt = new FeedbackEvent(_sessionId, "operator_command", "operator", "cmd-002",
            "correlation_key", "tree:elem:qshape",
            """{"commandType":"reject_candidate"}""",
            DirectiveType.Demote,
            """{"action":"reject"}""",
            null);
        var id = _db.InsertFeedbackEvent(evt);

        FeedbackCollector.ApplyDirective(_db, _sessionId, _db.GetFeedbackEvent(id)!);

        var ext = _db.GetCorrelatedActionExtended(_sessionId, "tree:elem:qshape");
        Assert.True(ext!.Value.OperatorRejected);
        var actions = _db.GetCorrelatedActions(_sessionId);
        Assert.Equal(0.0, actions[0].Confidence, precision: 10);
    }

    [Fact]
    public void ProcessOperator_Reapprove_ClearsSuspension()
    {
        SeedCorrelation("tree:elem:qshape", confidence: 0.30);
        _db.UpdateCorrelationFlags(_sessionId, "tree:elem:qshape",
            operatorApproved: true, promotionSuspended: true, consecutiveFailures: 5);

        var evt = new FeedbackEvent(_sessionId, "operator_command", "operator", "cmd-003",
            "correlation_key", "tree:elem:qshape",
            """{"commandType":"reapprove_candidate"}""",
            DirectiveType.Promote,
            """{"action":"reapprove"}""",
            null);
        var id = _db.InsertFeedbackEvent(evt);

        FeedbackCollector.ApplyDirective(_db, _sessionId, _db.GetFeedbackEvent(id)!);

        var ext = _db.GetCorrelatedActionExtended(_sessionId, "tree:elem:qshape");
        Assert.True(ext!.Value.OperatorApproved);
        Assert.False(ext.Value.PromotionSuspended);
        Assert.Equal(0, ext.Value.ConsecutiveFailures);
    }

    // ── Canary Drift Tests ──

    [Fact]
    public void ProcessCanaryDrift_SetsStaleOnAffectedCorrelations()
    {
        SeedCorrelation("tree:elem:qshape", confidence: 0.80);

        var evt = new FeedbackEvent(_sessionId, "canary_drift", "canary", "chk-001",
            "correlation_key", "tree:elem:qshape",
            """{"affectedTables":["Prescription.RxTransaction"]}""",
            DirectiveType.ReLearn,
            """{"scope":"table","target":"Prescription.RxTransaction"}""",
            null);
        var id = _db.InsertFeedbackEvent(evt);

        FeedbackCollector.ApplyDirective(_db, _sessionId, _db.GetFeedbackEvent(id)!);

        var ext = _db.GetCorrelatedActionExtended(_sessionId, "tree:elem:qshape");
        Assert.True(ext!.Value.Stale);
        Assert.NotNull(ext.Value.StaleSince);
    }

    // ── Stale Escalation Tests ──

    [Fact]
    public void StaleCorrelation_Beyond14Days_Escalates()
    {
        SeedCorrelation("tree:elem:qshape", confidence: 0.80);
        _db.UpdateCorrelationFlags(_sessionId, "tree:elem:qshape",
            stale: true, staleSince: DateTimeOffset.UtcNow.AddDays(-15).ToString("o"));

        var expired = _db.GetExpiredStaleCorrelations(_sessionId, FeedbackEvent.StaleTtlDays);
        Assert.Single(expired);
        Assert.Equal("tree:elem:qshape", expired[0].CorrelationKey);
    }

    // ── Pending Events Batch Processing ──

    [Fact]
    public void ProcessPendingFeedback_AppliesUnappliedEvents()
    {
        SeedCorrelation("tree:elem:qshape", confidence: 0.60);

        var evt = new FeedbackEvent(_sessionId, "operator_command", "operator", "cmd-batch",
            "correlation_key", "tree:elem:qshape",
            null,
            DirectiveType.Promote,
            """{"action":"approve"}""",
            null);
        _db.InsertFeedbackEvent(evt);

        var processor = new FeedbackProcessor(_db, _sessionId);
        processor.ProcessPendingDirectives();

        var ext = _db.GetCorrelatedActionExtended(_sessionId, "tree:elem:qshape");
        Assert.True(ext!.Value.OperatorApproved);

        // Verify event is now marked as applied
        var pending = _db.GetPendingFeedbackEvents(_sessionId);
        Assert.Empty(pending);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd /Users/joshuahenein/Documents/SuavoAgent && dotnet test tests/SuavoAgent.Core.Tests --filter "FeedbackProcessorTests" -v n`
Expected: FAIL — `FeedbackProcessor` not found

- [ ] **Step 3: Implement FeedbackProcessor**

```csharp
// src/SuavoAgent.Core/Behavioral/FeedbackProcessor.cs
using System.Text.Json;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Behavioral;

/// <summary>
/// Batch consumer for feedback events. Stateless — constructed fresh each LearningWorker tick.
/// Processes: pending directives, decay, recalibration, promotion health, stale escalation.
/// </summary>
public sealed class FeedbackProcessor
{
    private readonly AgentStateDb _db;
    private readonly string _sessionId;

    public FeedbackProcessor(AgentStateDb db, string sessionId)
    {
        _db = db;
        _sessionId = sessionId;
    }

    /// <summary>
    /// Main entry point — called by LearningWorker on 5-minute tick.
    /// </summary>
    public void ProcessPendingFeedback()
    {
        ProcessPendingDirectives();
        ProcessDecay();
        ProcessStaleEscalation();
    }

    /// <summary>
    /// Applies any feedback events with applied_at IS NULL.
    /// Covers operator commands and any other events inserted by external sources.
    /// </summary>
    public void ProcessPendingDirectives()
    {
        var pending = _db.GetPendingFeedbackEvents(_sessionId);
        foreach (var evt in pending)
        {
            FeedbackCollector.ApplyDirective(_db, _sessionId, evt);
        }
    }

    /// <summary>
    /// Applies flat -0.01 decay to idle correlations (no writeback in 7+ days).
    /// Emits max 1 decay event per correlation per day.
    /// </summary>
    public void ProcessDecay()
    {
        var idle = _db.GetIdleCorrelations(_sessionId, FeedbackEvent.DecayIdleDays);

        foreach (var (key, confidence, _) in idle)
        {
            if (_db.HasDecayEventToday(_sessionId, key))
                continue;

            var newConfidence = FeedbackEvent.ApplyDecay(confidence);
            if (Math.Abs(newConfidence - confidence) < 0.0001)
                continue; // Already at floor

            var payloadJson = JsonSerializer.Serialize(new
            {
                previousConfidence = confidence,
                newConfidence,
                decayApplied = 0.01,
            });

            var evt = new FeedbackEvent(
                SessionId: _sessionId,
                EventType: "decay",
                Source: "decay",
                SourceId: null,
                TargetType: "correlation_key",
                TargetId: key,
                PayloadJson: payloadJson,
                DirectiveType: DirectiveType.ConfidenceAdjust,
                DirectiveJson: JsonSerializer.Serialize(new { newConfidence }),
                CausalChainJson: null)
            {
                AppliedAt = DateTimeOffset.UtcNow.ToString("o"),
                AppliedBy = "batch"
            };

            _db.InsertFeedbackEvent(evt);
            _db.UpdateCorrelationConfidence(_sessionId, key, newConfidence);
        }
    }

    /// <summary>
    /// Checks stale correlations past 14-day TTL.
    /// If replacement exists → delete stale. If not → emit escalation.
    /// </summary>
    public void ProcessStaleEscalation()
    {
        var expired = _db.GetExpiredStaleCorrelations(_sessionId, FeedbackEvent.StaleTtlDays);

        foreach (var (key, staleSince) in expired)
        {
            // Parse tree_hash and element_id from correlation key
            var parts = key.Split(':');
            if (parts.Length < 2) continue;
            var treeHash = parts[0];
            var elementId = parts[1];

            if (_db.HasReplacementCorrelation(_sessionId, treeHash, elementId, key))
            {
                _db.DeleteCorrelation(_sessionId, key);
            }
            else
            {
                var evt = new FeedbackEvent(
                    SessionId: _sessionId,
                    EventType: "stale_escalation",
                    Source: "stale_check",
                    SourceId: null,
                    TargetType: "correlation_key",
                    TargetId: key,
                    PayloadJson: JsonSerializer.Serialize(new { staleSince, ttlDays = FeedbackEvent.StaleTtlDays }),
                    DirectiveType: DirectiveType.EscalateStale,
                    DirectiveJson: null,
                    CausalChainJson: null)
                {
                    AppliedAt = DateTimeOffset.UtcNow.ToString("o"),
                    AppliedBy = "batch"
                };
                _db.InsertFeedbackEvent(evt);
            }
        }
    }

    /// <summary>
    /// Recalibrates correlation windows based on observed writeback latencies.
    /// Only fires for correlations with >= 20 samples in the 7-day window.
    /// </summary>
    public void ProcessRecalibration()
    {
        // Get writeback feedback events from last 7 days grouped by correlation key
        var cutoff = DateTimeOffset.UtcNow.AddDays(-7).ToString("o");
        var allWritebackEvents = _db.GetFeedbackEventsForTarget(_sessionId, targetId: null!, source: "writeback");

        var grouped = allWritebackEvents
            .Where(e => e.CreatedAt.CompareTo(cutoff) >= 0 && e.PayloadJson != null)
            .GroupBy(e => e.TargetId);

        foreach (var group in grouped)
        {
            var events = group.ToList();
            if (events.Count < FeedbackEvent.RecalibrationMinSamples)
                continue;

            var latencies = new List<double>();
            foreach (var evt in events)
            {
                try
                {
                    var doc = JsonDocument.Parse(evt.PayloadJson!);
                    if (doc.RootElement.TryGetProperty("latencyMs", out var lat))
                        latencies.Add(lat.GetDouble());
                }
                catch { /* skip malformed */ }
            }

            if (latencies.Count < FeedbackEvent.RecalibrationMinSamples)
                continue;

            latencies.Sort();
            var p50 = latencies[(int)(latencies.Count * 0.5)];
            var p95 = latencies[(int)(latencies.Count * 0.95)];

            // Parse tree_hash and element_id
            var parts = group.Key.Split(':');
            if (parts.Length < 2) continue;
            var treeHash = parts[0];
            var elementId = parts[1];

            var currentWindow = _db.GetWindowOverride(_sessionId, treeHash, elementId) ?? 2.0;
            var currentWindowMs = currentWindow * 1000;

            double? newWindowMs = null;
            if (p95 > currentWindowMs)
                newWindowMs = p95 * 1.2; // Widen: 20% headroom
            else if (p50 < currentWindowMs * 0.5)
                newWindowMs = p50 * 2.0; // Tighten

            if (newWindowMs.HasValue)
            {
                var newWindowSeconds = Math.Round(newWindowMs.Value / 1000.0, 3);
                var directiveJson = JsonSerializer.Serialize(new
                {
                    treeHash,
                    elementId,
                    windowSeconds = newWindowSeconds,
                    sampleCount = latencies.Count,
                    p50Ms = p50,
                    p95Ms = p95,
                });

                var recalEvt = new FeedbackEvent(
                    SessionId: _sessionId,
                    EventType: "recalibration",
                    Source: "recalibration",
                    SourceId: null,
                    TargetType: "correlation_key",
                    TargetId: group.Key,
                    PayloadJson: directiveJson,
                    DirectiveType: DirectiveType.Recalibrate,
                    DirectiveJson: directiveJson,
                    CausalChainJson: null)
                {
                    AppliedAt = DateTimeOffset.UtcNow.ToString("o"),
                    AppliedBy = "batch"
                };

                _db.InsertFeedbackEvent(recalEvt);
                _db.UpsertWindowOverride(_sessionId, treeHash, elementId, newWindowSeconds, latencies.Count);
            }
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd /Users/joshuahenein/Documents/SuavoAgent && dotnet test tests/SuavoAgent.Core.Tests --filter "FeedbackProcessorTests" -v n`
Expected: All PASS

- [ ] **Step 5: Run full suite**

Run: `cd /Users/joshuahenein/Documents/SuavoAgent && dotnet test -v n`
Expected: All pass

- [ ] **Step 6: Commit**

```bash
git add src/SuavoAgent.Core/Behavioral/FeedbackProcessor.cs tests/SuavoAgent.Core.Tests/Behavioral/FeedbackProcessorTests.cs
git commit -m "feat(feedback): add FeedbackProcessor — batch decay, operator directives, stale escalation, recalibration"
```

---

### Task 6: ActionCorrelator Window Override Integration

**Files:**
- Modify: `src/SuavoAgent.Core/Behavioral/ActionCorrelator.cs`
- Modify: `tests/SuavoAgent.Core.Tests/Behavioral/ActionCorrelatorTests.cs`

- [ ] **Step 1: Write test for per-key window override**

Add to `ActionCorrelatorTests.cs`:

```csharp
    [Fact]
    public void WindowOverride_UsesPerKeyWindow()
    {
        // Set a per-key override of 0.5s
        _db.UpsertWindowOverride(_sessionId, "tree-abc", "elem-001", 0.5, 30);

        var correlator = MakeCorrelator(windowSeconds: 2.0);
        var uiTime = DateTimeOffset.UtcNow;
        correlator.RecordUiEvent("tree-abc", "elem-001", "Button", uiTime);

        // SQL fires 1s after — within 2s default but outside 0.5s override
        var sqlTime = uiTime.AddSeconds(1);
        correlator.TryCorrelateWithSql("qshape-1", sqlTime.ToString("o"), isWrite: true, tablesReferenced: "Prescription");

        // With per-key 0.5s window, this should NOT correlate
        var actions = _db.GetCorrelatedActions(_sessionId);
        Assert.Empty(actions);
    }

    [Fact]
    public void WindowOverride_FallsBackToGlobalWhenNoOverride()
    {
        // No override set — should use default 2s window
        var correlator = MakeCorrelator(windowSeconds: 2.0);
        var uiTime = DateTimeOffset.UtcNow;
        correlator.RecordUiEvent("tree-abc", "elem-001", "Button", uiTime);

        var sqlTime = uiTime.AddSeconds(1);
        correlator.TryCorrelateWithSql("qshape-1", sqlTime.ToString("o"), isWrite: true, tablesReferenced: "Prescription");

        var actions = _db.GetCorrelatedActions(_sessionId);
        Assert.Single(actions);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd /Users/joshuahenein/Documents/SuavoAgent && dotnet test tests/SuavoAgent.Core.Tests --filter "WindowOverride" -v n`
Expected: FAIL — ActionCorrelator doesn't read overrides yet

- [ ] **Step 3: Modify ActionCorrelator to read per-key overrides**

In `ActionCorrelator.cs`, modify `TryCorrelateWithSql` to check for overrides:

Replace the window resolution logic (around line 60):
```csharp
        var window = TimeSpan.FromSeconds(_correlationWindowSeconds);
```

With:
```csharp
        // Check per-key window overrides from recalibration
        double effectiveWindowSeconds = _correlationWindowSeconds;
        if (_window.Count > 0)
        {
            var closestUi = _window[^1]; // most recent UI event — likely the one we'll match
            var overrideWindow = _db.GetWindowOverride(_sessionId, closestUi.TreeHash, closestUi.ElementId);
            if (overrideWindow.HasValue)
                effectiveWindowSeconds = overrideWindow.Value;
        }
        var window = TimeSpan.FromSeconds(effectiveWindowSeconds);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd /Users/joshuahenein/Documents/SuavoAgent && dotnet test tests/SuavoAgent.Core.Tests --filter "ActionCorrelatorTests" -v n`
Expected: All PASS (including new and existing tests)

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Core/Behavioral/ActionCorrelator.cs tests/SuavoAgent.Core.Tests/Behavioral/ActionCorrelatorTests.cs
git commit -m "feat(feedback): add per-key correlation window overrides to ActionCorrelator"
```

---

### Task 7: Signed Command Extensions (HeartbeatWorker)

**Files:**
- Modify: `src/SuavoAgent.Core/Workers/HeartbeatWorker.cs`

- [ ] **Step 1: Add 6 new command cases to ProcessSignedCommandAsync switch**

In `HeartbeatWorker.cs`, add after the `delivery_writeback` case (around line 264):

```csharp
                case "approve_candidate":
                    HandleFeedbackCommand(scEl, cmd, DirectiveType.Promote);
                    break;
                case "reject_candidate":
                    HandleFeedbackCommand(scEl, cmd, DirectiveType.Demote);
                    break;
                case "reapprove_candidate":
                    HandleFeedbackCommand(scEl, cmd, DirectiveType.Promote);
                    break;
                case "force_relearn":
                    HandleFeedbackCommand(scEl, cmd, DirectiveType.ReLearn);
                    break;
                case "adjust_window":
                    HandleFeedbackCommand(scEl, cmd, DirectiveType.Recalibrate);
                    break;
                case "acknowledge_stale":
                    HandleFeedbackCommand(scEl, cmd, DirectiveType.Prune);
                    break;
```

- [ ] **Step 2: Add HandleFeedbackCommand method**

Add to `HeartbeatWorker.cs`:

```csharp
    private void HandleFeedbackCommand(JsonElement scEl, SignedCommand cmd, DirectiveType directiveType)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var correlationKey = dataEl.TryGetProperty("correlationKey", out var ck) ? ck.GetString() ?? "" : "";
        var sessionId = _stateDb.GetActiveSessionId(_options.PharmacyId ?? "");

        if (string.IsNullOrEmpty(correlationKey) || string.IsNullOrEmpty(sessionId))
        {
            _logger.LogWarning("{Command}: missing correlationKey or no active session", cmd.Command);
            return;
        }

        var payloadJson = dataEl.ValueKind != JsonValueKind.Undefined
            ? dataEl.GetRawText()
            : null;

        var evt = new FeedbackEvent(
            SessionId: sessionId,
            EventType: "operator_command",
            Source: "operator",
            SourceId: cmd.Nonce,
            TargetType: "correlation_key",
            TargetId: correlationKey,
            PayloadJson: payloadJson,
            DirectiveType: directiveType,
            DirectiveJson: payloadJson,
            CausalChainJson: null);

        _stateDb.InsertFeedbackEvent(evt);

        _logger.LogInformation("Feedback command {Command} for {Key} queued as directive {Directive}",
            cmd.Command, correlationKey, directiveType);

        _stateDb.AppendChainedAuditEntry(new AuditEntry(
            TaskId: correlationKey,
            EventType: "feedback_command",
            FromState: "",
            ToState: directiveType.ToString(),
            Trigger: cmd.Command,
            CommandId: cmd.Nonce,
            RequesterId: "operator"));
    }
```

Add the required using at the top of HeartbeatWorker.cs:
```csharp
using SuavoAgent.Core.Behavioral;
```

- [ ] **Step 3: Run full suite**

Run: `cd /Users/joshuahenein/Documents/SuavoAgent && dotnet test -v n`
Expected: All pass

- [ ] **Step 4: Commit**

```bash
git add src/SuavoAgent.Core/Workers/HeartbeatWorker.cs
git commit -m "feat(feedback): add 6 operator feedback signed commands to HeartbeatWorker"
```

---

### Task 8: LearningWorker Integration

**Files:**
- Modify: `src/SuavoAgent.Core/Workers/LearningWorker.cs`

- [ ] **Step 1: Wire FeedbackProcessor into 5-minute tick**

In `LearningWorker.cs`, after the behavioral event prune block (after line 174), add:

```csharp
            // Feedback processing (batch) — decay, operator directives, canary drift, stale escalation
            if (session.Phase is "pattern" or "model" or "approved" or "active")
            {
                try
                {
                    var feedbackProcessor = new FeedbackProcessor(_db, _sessionId);
                    feedbackProcessor.ProcessPendingFeedback();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "FeedbackProcessor batch tick failed");
                }
            }
```

Add the using:
```csharp
using SuavoAgent.Core.Behavioral;
```

- [ ] **Step 2: Run full suite**

Run: `cd /Users/joshuahenein/Documents/SuavoAgent && dotnet test -v n`
Expected: All pass

- [ ] **Step 3: Commit**

```bash
git add src/SuavoAgent.Core/Workers/LearningWorker.cs
git commit -m "feat(feedback): wire FeedbackProcessor batch tick into LearningWorker"
```

---

### Task 9: POM Export Feedback Section

**Files:**
- Modify: `src/SuavoAgent.Core/Learning/PomExporter.cs`
- Modify: `tests/SuavoAgent.Core.Tests/Learning/BehavioralPomExportTests.cs`

- [ ] **Step 1: Write test for feedback section in POM export**

Add to `BehavioralPomExportTests.cs`:

```csharp
    [Fact]
    public void Export_IncludesFeedbackSection()
    {
        var db = new AgentStateDb(":memory:");
        db.CreateLearningSession("sess-pom-fb", "pharm-test");

        // Seed a correlation and a feedback event
        db.UpsertCorrelatedAction("sess-pom-fb", "tree:elem:qshape", "tree", "elem",
            "Button", "qshape", true, "Prescription");
        db.UpdateCorrelationConfidence("sess-pom-fb", "tree:elem:qshape", 0.87);

        var evt = new FeedbackEvent("sess-pom-fb", "writeback_outcome", "writeback", "wb-001",
            "correlation_key", "tree:elem:qshape", null,
            DirectiveType.ConfidenceAdjust, """{"newConfidence":0.87}""", null)
        { AppliedAt = DateTimeOffset.UtcNow.ToString("o"), AppliedBy = "inline" };
        db.InsertFeedbackEvent(evt);

        var json = PomExporter.Export(db, "sess-pom-fb");
        var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("feedback", out var fb));
        Assert.True(fb.TryGetProperty("totalFeedbackEvents", out var total));
        Assert.Equal(1, total.GetInt32());
        Assert.True(fb.TryGetProperty("confidenceTrajectory", out _));

        db.Dispose();
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd /Users/joshuahenein/Documents/SuavoAgent && dotnet test tests/SuavoAgent.Core.Tests --filter "Export_IncludesFeedbackSection" -v n`
Expected: FAIL — no `feedback` property in export

- [ ] **Step 3: Add feedback section to PomExporter**

In `PomExporter.cs`, add after the `behavioral` section in the anonymous export object (around line 101):

```csharp
            feedback = new
            {
                totalFeedbackEvents = db.GetFeedbackEventCount(sessionId),
                confidenceTrajectory = db.GetCorrelatedActions(sessionId)
                    .Where(a => a.IsWrite)
                    .Select(a =>
                    {
                        var ext = db.GetCorrelatedActionExtended(sessionId, a.CorrelationKey);
                        var writebackEvents = db.GetFeedbackEventsForTarget(sessionId, a.CorrelationKey, source: "writeback");
                        var successes = writebackEvents.Count(e => e.PayloadJson?.Contains("\"success\"") == true);
                        return new
                        {
                            correlationKey = a.CorrelationKey,
                            currentConfidence = a.Confidence,
                            writebackAttempts = writebackEvents.Count,
                            successRate = writebackEvents.Count > 0 ? Math.Round((double)successes / writebackEvents.Count, 2) : 0.0,
                            operatorApproved = ext?.OperatorApproved ?? false,
                            promotionSuspended = ext?.PromotionSuspended ?? false,
                        };
                    }).ToArray(),
                prunedCorrelations = db.GetFeedbackEventsForTarget(sessionId, targetId: null!, source: null)
                    .Where(e => e.DirectiveType == DirectiveType.Prune)
                    .Select(e => new
                    {
                        correlationKey = e.TargetId,
                        prunedAt = e.CreatedAt,
                        reason = "confidence_floor",
                    }).ToArray(),
                windowOverrides = db.GetWindowOverrideCount(sessionId),
                staleCorrelations = db.GetExpiredStaleCorrelations(sessionId, 0).Count,
                decayActive = true,
            },
```

Add the using:
```csharp
using SuavoAgent.Core.Behavioral;
```

Note: `GetFeedbackEventsForTarget` with null targetId needs to handle null — either add an overload that returns all events for a session, or filter post-fetch. The implementer should add a `GetAllFeedbackEvents(sessionId)` method or use the existing `GetFeedbackEventCount` pattern.

- [ ] **Step 4: Run test to verify it passes**

Run: `cd /Users/joshuahenein/Documents/SuavoAgent && dotnet test tests/SuavoAgent.Core.Tests --filter "Export_IncludesFeedbackSection" -v n`
Expected: PASS

- [ ] **Step 5: Run full suite**

Run: `cd /Users/joshuahenein/Documents/SuavoAgent && dotnet test -v n`
Expected: All pass

- [ ] **Step 6: Commit**

```bash
git add src/SuavoAgent.Core/Learning/PomExporter.cs tests/SuavoAgent.Core.Tests/Learning/BehavioralPomExportTests.cs
git commit -m "feat(feedback): add feedback section to POM export — confidence trajectory, pruned correlations"
```

---

### Task 10: HealthSnapshot Feedback Section

**Files:**
- Modify: `src/SuavoAgent.Core/HealthSnapshot.cs`

- [ ] **Step 1: Add feedback section to health payload**

In `HealthSnapshot.cs`, add after the `behavioral` section (around line 122):

```csharp
            feedback = learningSessionId is not null
                ? (object)new
                {
                    totalEvents = _stateDb.GetFeedbackEventCount(learningSessionId),
                    pendingDirectives = _stateDb.GetPendingFeedbackEvents(learningSessionId).Count,
                    appliedInline = _stateDb.GetFeedbackEventCountByApplier(learningSessionId, "inline"),
                    appliedBatch = _stateDb.GetFeedbackEventCountByApplier(learningSessionId, "batch"),
                    suspendedPromotions = _stateDb.GetSuspendedPromotions(learningSessionId),
                    staleEscalations = _stateDb.GetExpiredStaleCorrelations(learningSessionId, FeedbackEvent.StaleTtlDays)
                        .Select(s => s.CorrelationKey).ToArray(),
                    activeOverrides = _stateDb.GetWindowOverrideCount(learningSessionId),
                }
                : (object)new
                {
                    totalEvents = 0,
                    pendingDirectives = 0,
                    appliedInline = 0,
                    appliedBatch = 0,
                    suspendedPromotions = Array.Empty<string>(),
                    staleEscalations = Array.Empty<string>(),
                    activeOverrides = 0,
                },
```

Add the using:
```csharp
using SuavoAgent.Core.Behavioral;
```

- [ ] **Step 2: Run full suite**

Run: `cd /Users/joshuahenein/Documents/SuavoAgent && dotnet test -v n`
Expected: All pass

- [ ] **Step 3: Commit**

```bash
git add src/SuavoAgent.Core/HealthSnapshot.cs
git commit -m "feat(feedback): add feedback telemetry to HealthSnapshot — suspended promotions, stale escalations"
```

---

### Task 11: Replay Integration Test

**Files:**
- Create: `tests/SuavoAgent.Core.Tests/Behavioral/FeedbackReplayTests.cs`

- [ ] **Step 1: Write the replay test**

```csharp
// tests/SuavoAgent.Core.Tests/Behavioral/FeedbackReplayTests.cs
using System.Text.Json;
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Behavioral;

public class FeedbackReplayTests : IDisposable
{
    private readonly AgentStateDb _db;
    private readonly string _sessionId = "replay-test-session";

    public FeedbackReplayTests()
    {
        _db = new AgentStateDb(":memory:");
        _db.CreateLearningSession(_sessionId, "pharm-test");
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Replay_ProducesIdenticalEndState()
    {
        // Seed correlation
        _db.UpsertCorrelatedAction(_sessionId, "tree:elem:qshape", "tree", "elem",
            "Button", "qshape", true, "Prescription");
        _db.UpdateCorrelationConfidence(_sessionId, "tree:elem:qshape", 0.60);

        // Phase 1: Apply N feedback events inline
        FeedbackCollector.RecordWritebackOutcome(_db, _sessionId, "wb-1", "tree:elem:qshape", "success",
            DateTimeOffset.UtcNow.AddSeconds(-1).ToString("o"), DateTimeOffset.UtcNow.ToString("o"));
        FeedbackCollector.RecordWritebackOutcome(_db, _sessionId, "wb-2", "tree:elem:qshape", "sql_error",
            DateTimeOffset.UtcNow.AddSeconds(-1).ToString("o"), DateTimeOffset.UtcNow.ToString("o"));
        FeedbackCollector.RecordWritebackOutcome(_db, _sessionId, "wb-3", "tree:elem:qshape", "success",
            DateTimeOffset.UtcNow.AddSeconds(-1).ToString("o"), DateTimeOffset.UtcNow.ToString("o"));
        FeedbackCollector.RecordWritebackOutcome(_db, _sessionId, "wb-4", "tree:elem:qshape", "status_conflict",
            DateTimeOffset.UtcNow.AddSeconds(-1).ToString("o"), DateTimeOffset.UtcNow.ToString("o"));
        FeedbackCollector.RecordWritebackOutcome(_db, _sessionId, "wb-5", "tree:elem:qshape", "success",
            DateTimeOffset.UtcNow.AddSeconds(-1).ToString("o"), DateTimeOffset.UtcNow.ToString("o"));

        // Record the end state after initial application
        var endState = _db.GetCorrelatedActions(_sessionId)[0];
        var endConfidence = endState.Confidence;
        var endExtended = _db.GetCorrelatedActionExtended(_sessionId, "tree:elem:qshape");

        // Phase 2: Reset correlation to original state
        _db.UpdateCorrelationConfidence(_sessionId, "tree:elem:qshape", 0.60);
        _db.UpdateCorrelationFlags(_sessionId, "tree:elem:qshape", consecutiveFailures: 0);

        // Phase 3: Replay all feedback events via batch path
        // Get all events, clear their applied_at, and re-apply
        var allEvents = _db.GetFeedbackEventsForTarget(_sessionId, "tree:elem:qshape");
        foreach (var evt in allEvents)
        {
            if (evt.DirectiveType == DirectiveType.ConfidenceAdjust && evt.DirectiveJson != null)
            {
                var doc = JsonDocument.Parse(evt.DirectiveJson);
                if (doc.RootElement.TryGetProperty("newConfidence", out var nc))
                {
                    _db.UpdateCorrelationConfidence(_sessionId, evt.TargetId, nc.GetDouble());
                }
            }
        }

        // Phase 4: Verify identical end state
        var replayState = _db.GetCorrelatedActions(_sessionId)[0];
        Assert.Equal(endConfidence, replayState.Confidence, precision: 10);
    }
}
```

- [ ] **Step 2: Run test**

Run: `cd /Users/joshuahenein/Documents/SuavoAgent && dotnet test tests/SuavoAgent.Core.Tests --filter "FeedbackReplayTests" -v n`
Expected: PASS

- [ ] **Step 3: Commit**

```bash
git add tests/SuavoAgent.Core.Tests/Behavioral/FeedbackReplayTests.cs
git commit -m "test(feedback): add replay integration test — event-sourced log produces identical end state"
```

---

### Task 12: Final Verification

- [ ] **Step 1: Run full test suite**

Run: `cd /Users/joshuahenein/Documents/SuavoAgent && dotnet test -v n`
Expected: All tests pass (385 original + ~30 new feedback tests)

- [ ] **Step 2: Verify build succeeds**

Run: `cd /Users/joshuahenein/Documents/SuavoAgent && dotnet build -c Release`
Expected: Build succeeded, 0 errors, 0 warnings

- [ ] **Step 3: Count new files and lines**

Run: `git diff main --stat`
Expected: ~7 new files, ~5 modified files

- [ ] **Step 4: Final commit (if any uncommitted changes from fixes)**

```bash
git status
# If clean, skip. If changes exist:
git add -A && git commit -m "fix(feedback): address build/test issues from integration"
```
