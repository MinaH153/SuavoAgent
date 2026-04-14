using System.Text.Json;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Behavioral;

/// <summary>
/// Static utility for inline writeback outcome recording and directive application.
/// Called by WritebackProcessor after each writeback, and by the batch FeedbackLoop.
/// </summary>
public static class FeedbackCollector
{
    /// <summary>
    /// Records a writeback outcome inline, adjusting confidence, tracking failures,
    /// emitting prune/suspend events as needed. Returns the new confidence value.
    /// </summary>
    public static double RecordWritebackOutcome(
        AgentStateDb db,
        string sessionId,
        string taskId,
        string correlationKey,
        string outcome,
        string uiEventTimestamp,
        string sqlExecutionTimestamp)
    {
        // (a) Current confidence
        var actions = db.GetCorrelatedActions(sessionId);
        var match = actions.FirstOrDefault(a => a.CorrelationKey == correlationKey);
        double currentConfidence = match.CorrelationKey is not null ? match.Confidence : 0.3;

        // (b) Extended flags
        var ext = db.GetCorrelatedActionExtended(sessionId, correlationKey);
        bool operatorApproved = ext?.OperatorApproved ?? false;
        bool promotionSuspended = ext?.PromotionSuspended ?? false;
        int consecutiveFailures = ext?.ConsecutiveFailures ?? 0;

        // (c) Delta
        double delta = FeedbackEvent.OutcomeToDelta(outcome);

        // (d) New confidence
        double newConfidence = FeedbackEvent.ApplyConfidenceDelta(currentConfidence, delta);

        // (e) Is this a failure?
        bool isFailure = delta < 0;

        // (f) Consecutive failures
        if (isFailure)
            consecutiveFailures++;
        else if (delta > 0)
            consecutiveFailures = 0;
        // delta == 0 leaves consecutiveFailures unchanged

        // (g) Latency
        double latencyMs = 0;
        if (DateTimeOffset.TryParse(uiEventTimestamp, out var uiTs)
            && DateTimeOffset.TryParse(sqlExecutionTimestamp, out var sqlTs))
        {
            latencyMs = Math.Max(0, (sqlTs - uiTs).TotalMilliseconds);
        }

        // (h) Causal chain
        string? causalChainJson = null;
        if (consecutiveFailures > 1)
        {
            var recent = db.GetFeedbackEventsForTarget(sessionId, correlationKey, source: "inline");
            // Take the last (consecutiveFailures - 1) event IDs as causal chain
            var chainIds = recent
                .Where(e => e.EventType == "writeback_outcome" && e.Id.HasValue)
                .Select(e => e.Id!.Value)
                .TakeLast(consecutiveFailures - 1)
                .ToList();
            if (chainIds.Count > 0)
                causalChainJson = JsonSerializer.Serialize(chainIds);
        }

        // (i) Payload
        var payload = new
        {
            taskId,
            outcome,
            correlationKey,
            uiEventTimestamp,
            sqlExecutionTimestamp,
            latencyMs,
            previousConfidence = currentConfidence,
            newConfidence,
            consecutiveFailures
        };
        string payloadJson = JsonSerializer.Serialize(payload);

        // (j) Directive
        string directiveJson = JsonSerializer.Serialize(new { newConfidence });

        // (k) Insert FeedbackEvent -- applied inline on insert
        var now = DateTimeOffset.UtcNow.ToString("o");
        var evt = new FeedbackEvent(
            SessionId: sessionId,
            EventType: "writeback_outcome",
            Source: "inline",
            SourceId: taskId,
            TargetType: "correlation",
            TargetId: correlationKey,
            PayloadJson: payloadJson,
            DirectiveType: DirectiveType.ConfidenceAdjust,
            DirectiveJson: directiveJson,
            CausalChainJson: causalChainJson)
        {
            AppliedAt = now,
            AppliedBy = "inline",
            CreatedAt = now
        };
        db.InsertFeedbackEvent(evt);

        // (l) Update correlation
        db.UpdateCorrelationConfidence(sessionId, correlationKey, newConfidence);
        db.UpdateCorrelationFlags(sessionId, correlationKey,
            consecutiveFailures: consecutiveFailures);

        // (m) Prune trigger
        if (newConfidence <= FeedbackEvent.ConfidenceFloor && isFailure)
        {
            var pruneEvt = new FeedbackEvent(
                SessionId: sessionId,
                EventType: "writeback_outcome",
                Source: "inline",
                SourceId: taskId,
                TargetType: "correlation",
                TargetId: correlationKey,
                PayloadJson: payloadJson,
                DirectiveType: DirectiveType.Prune,
                DirectiveJson: null,
                CausalChainJson: causalChainJson)
            {
                AppliedAt = now,
                AppliedBy = "inline",
                CreatedAt = now
            };
            db.InsertFeedbackEvent(pruneEvt);
            db.RemoveWritebackFlagForCorrelation(sessionId, correlationKey);
        }

        // (n) Auto-suspend trigger
        if (operatorApproved && !promotionSuspended
            && consecutiveFailures >= FeedbackEvent.PromotionSuspendThreshold)
        {
            var suspendEvt = new FeedbackEvent(
                SessionId: sessionId,
                EventType: "writeback_outcome",
                Source: "inline",
                SourceId: taskId,
                TargetType: "correlation",
                TargetId: correlationKey,
                PayloadJson: payloadJson,
                DirectiveType: DirectiveType.SuspendPromotion,
                DirectiveJson: null,
                CausalChainJson: null)
            {
                AppliedAt = now,
                AppliedBy = "inline",
                CreatedAt = now
            };
            db.InsertFeedbackEvent(suspendEvt);
            db.UpdateCorrelationFlags(sessionId, correlationKey, promotionSuspended: true);
        }

        // (o) Return
        return newConfidence;
    }

    /// <summary>
    /// Shared idempotent applicator. Used by both inline and batch paths.
    /// If the event is already applied, this is a no-op.
    /// </summary>
    public static void ApplyDirective(AgentStateDb db, string sessionId, FeedbackEvent evt)
    {
        // (a) Idempotent guard
        if (evt.AppliedAt is not null) return;

        // (b) Switch on directive type
        switch (evt.DirectiveType)
        {
            case DirectiveType.ConfidenceAdjust:
            {
                if (evt.DirectiveJson is not null)
                {
                    using var doc = JsonDocument.Parse(evt.DirectiveJson);
                    if (doc.RootElement.TryGetProperty("newConfidence", out var nc))
                        db.UpdateCorrelationConfidence(sessionId, evt.TargetId, nc.GetDouble());
                }
                break;
            }

            case DirectiveType.Promote:
                db.UpdateCorrelationFlags(sessionId, evt.TargetId,
                    operatorApproved: true, promotionSuspended: false, consecutiveFailures: 0);
                break;

            case DirectiveType.Demote:
                db.UpdateCorrelationFlags(sessionId, evt.TargetId, operatorRejected: true);
                db.UpdateCorrelationConfidence(sessionId, evt.TargetId, 0.0);
                break;

            case DirectiveType.Prune:
                db.RemoveWritebackFlagForCorrelation(sessionId, evt.TargetId);
                break;

            case DirectiveType.Recalibrate:
            {
                if (evt.DirectiveJson is not null)
                {
                    using var doc = JsonDocument.Parse(evt.DirectiveJson);
                    var root = doc.RootElement;
                    string treeHash = root.GetProperty("treeHash").GetString()!;
                    string elementId = root.GetProperty("elementId").GetString()!;
                    double windowSeconds = root.GetProperty("windowSeconds").GetDouble();
                    int sampleCount = root.GetProperty("sampleCount").GetInt32();
                    db.UpsertWindowOverride(sessionId, treeHash, elementId, windowSeconds, sampleCount);
                }
                break;
            }

            case DirectiveType.ReLearn:
                db.UpdateCorrelationFlags(sessionId, evt.TargetId,
                    stale: true, staleSince: DateTimeOffset.UtcNow.ToString("o"));
                break;

            case DirectiveType.SuspendPromotion:
                db.UpdateCorrelationFlags(sessionId, evt.TargetId, promotionSuspended: true);
                break;

            case DirectiveType.EscalateStale:
                // No mutation -- health payload picks it up
                break;

            case DirectiveType.ThresholdAdjust:
                // No-op -- future extension
                break;
        }

        // (c) Mark applied
        if (evt.Id.HasValue)
            db.MarkFeedbackEventApplied(evt.Id.Value, "batch");
    }
}
