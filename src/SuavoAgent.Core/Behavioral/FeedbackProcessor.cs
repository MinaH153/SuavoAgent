using System.Text.Json;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Behavioral;

/// <summary>
/// Batch consumer for the feedback pipeline. Stateless — constructed fresh each tick.
/// Processes pending directives, idle decay, stale escalation, and recalibration.
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
    /// Main entry — runs directives, decay, and stale escalation in order.
    /// Recalibration is NOT called here (invoked by LearningWorker during active phase).
    /// </summary>
    public void ProcessPendingFeedback()
    {
        ProcessPendingDirectives();
        ProcessDecay();
        ProcessStaleEscalation();
    }

    /// <summary>
    /// Applies all pending (unapplied) feedback events via FeedbackCollector.ApplyDirective.
    /// </summary>
    public void ProcessPendingDirectives()
    {
        var pending = _db.GetPendingFeedbackEvents(_sessionId);
        foreach (var evt in pending)
            FeedbackCollector.ApplyDirective(_db, _sessionId, evt);
    }

    /// <summary>
    /// For each idle correlation (no activity in DecayIdleDays), subtract DecayAmount
    /// from confidence. Max one decay event per key per day. Stops at DecayFloor.
    /// </summary>
    public void ProcessDecay()
    {
        var idle = _db.GetIdleCorrelations(_sessionId, FeedbackEvent.DecayIdleDays);
        var now = DateTimeOffset.UtcNow.ToString("o");

        foreach (var (key, confidence, _) in idle)
        {
            if (_db.HasDecayEventToday(_sessionId, key))
                continue;

            double newConfidence = FeedbackEvent.ApplyDecay(confidence);
            if (newConfidence == confidence)
                continue; // already at floor

            var payload = JsonSerializer.Serialize(new
            {
                previousConfidence = confidence,
                newConfidence,
                decayApplied = FeedbackEvent.DecayAmount
            });

            var evt = new FeedbackEvent(
                SessionId: _sessionId,
                EventType: "idle_decay",
                Source: "decay",
                SourceId: null,
                TargetType: "correlation",
                TargetId: key,
                PayloadJson: payload,
                DirectiveType: DirectiveType.ConfidenceAdjust,
                DirectiveJson: null,
                CausalChainJson: null)
            {
                AppliedAt = now,
                AppliedBy = "batch",
                CreatedAt = now
            };
            _db.InsertFeedbackEvent(evt);
            _db.UpdateCorrelationConfidence(_sessionId, key, newConfidence);
        }
    }

    /// <summary>
    /// For each stale correlation past StaleTtlDays: if a non-stale replacement exists
    /// for the same tree_hash+element_id, delete the stale one (superseded). Otherwise,
    /// emit an EscalateStale event for operator attention.
    /// </summary>
    public void ProcessStaleEscalation()
    {
        var expired = _db.GetExpiredStaleCorrelations(_sessionId, FeedbackEvent.StaleTtlDays);
        var now = DateTimeOffset.UtcNow.ToString("o");

        foreach (var (key, staleSince) in expired)
        {
            // Parse tree_hash and element_id from correlation key
            // Format: prefix:tree_hash:element_id:query_shape_hash (or tree_hash:element_id:query_shape_hash)
            var parts = key.Split(':');
            if (parts.Length < 3) continue;

            // The key format can vary; use second-to-last and third-to-last segments
            // Convention: tree_hash:element_id:query_shape_hash or prefix:tree_hash:element_id:query_shape_hash
            string treeHash, elementId;
            if (parts.Length >= 4)
            {
                treeHash = parts[parts.Length - 3];
                elementId = parts[parts.Length - 2];
            }
            else
            {
                treeHash = parts[0];
                elementId = parts[1];
            }

            bool hasReplacement = _db.HasReplacementCorrelation(_sessionId, treeHash, elementId, key);

            if (hasReplacement)
            {
                _db.DeleteCorrelation(_sessionId, key);
            }
            else
            {
                var payload = JsonSerializer.Serialize(new
                {
                    staleSince,
                    ttlDays = FeedbackEvent.StaleTtlDays
                });

                var evt = new FeedbackEvent(
                    SessionId: _sessionId,
                    EventType: "stale_escalation",
                    Source: "batch",
                    SourceId: null,
                    TargetType: "correlation",
                    TargetId: key,
                    PayloadJson: payload,
                    DirectiveType: DirectiveType.EscalateStale,
                    DirectiveJson: null,
                    CausalChainJson: null)
                {
                    AppliedAt = now,
                    AppliedBy = "batch",
                    CreatedAt = now
                };
                _db.InsertFeedbackEvent(evt);
            }
        }
    }

    /// <summary>
    /// Recalibrates timing windows based on recent writeback latency data.
    /// NOT called from ProcessPendingFeedback — invoked by LearningWorker during active phase.
    /// Requires >= RecalibrationMinSamples per target to act.
    /// </summary>
    public void ProcessRecalibration()
    {
        var targets = _db.GetRecentWritebackTargets(_sessionId, withinDays: 7);

        foreach (var targetKey in targets)
        {
            var events = _db.GetFeedbackEventsForTarget(_sessionId, targetKey, "writeback");
            if (events.Count < FeedbackEvent.RecalibrationMinSamples)
                continue;

            // Parse latencyMs from each event's payload
            var latencies = new List<double>();
            foreach (var evt in events)
            {
                if (evt.PayloadJson is null) continue;
                using var doc = JsonDocument.Parse(evt.PayloadJson);
                if (doc.RootElement.TryGetProperty("latencyMs", out var lat))
                    latencies.Add(lat.GetDouble());
            }

            if (latencies.Count < FeedbackEvent.RecalibrationMinSamples)
                continue;

            latencies.Sort();

            double p50 = latencies[(int)(latencies.Count * 0.50)];
            double p95 = latencies[(int)(latencies.Count * 0.95)];

            // Parse tree_hash and element_id from correlation key
            var parts = targetKey.Split(':');
            string treeHash, elementId;
            if (parts.Length >= 4)
            {
                treeHash = parts[parts.Length - 3];
                elementId = parts[parts.Length - 2];
            }
            else if (parts.Length >= 3)
            {
                treeHash = parts[0];
                elementId = parts[1];
            }
            else
            {
                continue;
            }

            // Get current window (in seconds)
            double currentWindow = _db.GetWindowOverride(_sessionId, treeHash, elementId) ?? 10.0;
            double currentWindowMs = currentWindow * 1000.0;

            double? newWindowMs = null;

            if (p95 > currentWindowMs)
            {
                newWindowMs = p95 * 1.2;
            }
            else if (p50 < currentWindowMs * 0.5)
            {
                newWindowMs = p50 * 2.0;
            }

            if (newWindowMs is null)
                continue;

            double newWindowSeconds = newWindowMs.Value / 1000.0;
            var now = DateTimeOffset.UtcNow.ToString("o");

            var directiveJson = JsonSerializer.Serialize(new
            {
                treeHash,
                elementId,
                windowSeconds = newWindowSeconds,
                sampleCount = latencies.Count
            });

            var payload = JsonSerializer.Serialize(new
            {
                p50,
                p95,
                previousWindowMs = currentWindowMs,
                newWindowMs = newWindowMs.Value,
                sampleCount = latencies.Count
            });

            var recalEvt = new FeedbackEvent(
                SessionId: _sessionId,
                EventType: "recalibration",
                Source: "batch",
                SourceId: null,
                TargetType: "correlation",
                TargetId: targetKey,
                PayloadJson: payload,
                DirectiveType: DirectiveType.Recalibrate,
                DirectiveJson: directiveJson,
                CausalChainJson: null)
            {
                CreatedAt = now
            };
            int id = _db.InsertFeedbackEvent(recalEvt);

            // Apply immediately
            var inserted = _db.GetFeedbackEvent(id);
            if (inserted is not null)
                FeedbackCollector.ApplyDirective(_db, _sessionId, inserted);
        }
    }
}
