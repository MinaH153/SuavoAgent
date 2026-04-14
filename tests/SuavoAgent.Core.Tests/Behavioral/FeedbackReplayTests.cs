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
        // Seed correlation at 0.60
        _db.UpsertCorrelatedAction(_sessionId, "tree:elem:qshape", "tree", "elem",
            "Button", "qshape", true, "Prescription");
        _db.UpdateCorrelationConfidence(_sessionId, "tree:elem:qshape", 0.60);

        // Phase 1: Apply N feedback events inline
        var now = DateTimeOffset.UtcNow;
        FeedbackCollector.RecordWritebackOutcome(_db, _sessionId, "wb-1", "tree:elem:qshape", "success",
            now.AddSeconds(-5).ToString("o"), now.AddSeconds(-4).ToString("o"));
        FeedbackCollector.RecordWritebackOutcome(_db, _sessionId, "wb-2", "tree:elem:qshape", "sql_error",
            now.AddSeconds(-3).ToString("o"), now.AddSeconds(-2).ToString("o"));
        FeedbackCollector.RecordWritebackOutcome(_db, _sessionId, "wb-3", "tree:elem:qshape", "success",
            now.AddSeconds(-1).ToString("o"), now.ToString("o"));
        FeedbackCollector.RecordWritebackOutcome(_db, _sessionId, "wb-4", "tree:elem:qshape", "status_conflict",
            now.AddSeconds(1).ToString("o"), now.AddSeconds(2).ToString("o"));
        FeedbackCollector.RecordWritebackOutcome(_db, _sessionId, "wb-5", "tree:elem:qshape", "success",
            now.AddSeconds(3).ToString("o"), now.AddSeconds(4).ToString("o"));

        // Record end state
        var endConfidence = _db.GetCorrelatedActions(_sessionId)
            .First(a => a.CorrelationKey == "tree:elem:qshape").Confidence;

        // Phase 2: Reset to original
        _db.UpdateCorrelationConfidence(_sessionId, "tree:elem:qshape", 0.60);
        _db.UpdateCorrelationFlags(_sessionId, "tree:elem:qshape", consecutiveFailures: 0);

        // Phase 3: Replay all confidence_adjust events via their stored newConfidence
        var allEvents = _db.GetFeedbackEventsForTarget(_sessionId, "tree:elem:qshape", "inline");
        double replayedConfidence = 0.60;
        foreach (var evt in allEvents.Where(e => e.DirectiveType == DirectiveType.ConfidenceAdjust && e.DirectiveJson != null))
        {
            var doc = JsonDocument.Parse(evt.DirectiveJson!);
            if (doc.RootElement.TryGetProperty("newConfidence", out var nc))
                replayedConfidence = nc.GetDouble();
        }

        // Phase 4: Verify identical
        Assert.Equal(endConfidence, replayedConfidence, precision: 10);
    }
}
