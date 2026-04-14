using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Behavioral;

/// <summary>
/// Processes batches of BehavioralEvents received over IPC.
/// Validates, deduplicates tree snapshots, and persists to the state DB.
/// </summary>
public sealed class BehavioralEventReceiver
{
    private static readonly TimeSpan DedupWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DedupPruneWindow = TimeSpan.FromSeconds(120);

    private readonly AgentStateDb _db;
    private readonly string _sessionId;
    private readonly Action<string, string, string?, DateTimeOffset>? _onInteraction;
    private readonly Dictionary<string, DateTimeOffset> _recentTreeHashes = new();
    private long _nextSeq = 1;

    public record BatchResult(bool Accepted, int EventsStored, int EventsRejected);

    /// <param name="db">State database.</param>
    /// <param name="sessionId">Active learning session ID.</param>
    /// <param name="onInteraction">
    /// Optional callback invoked after each Interaction event is persisted.
    /// Args: (treeHash, elementId, controlType, timestamp).
    /// Used to feed ActionCorrelator without creating a direct dependency.
    /// </param>
    public BehavioralEventReceiver(AgentStateDb db, string sessionId,
        Action<string, string, string?, DateTimeOffset>? onInteraction = null)
    {
        _db = db;
        _sessionId = sessionId;
        _onInteraction = onInteraction;
    }

    /// <summary>
    /// Processes a batch of events from the Helper.
    /// </summary>
    /// <param name="events">Events to process.</param>
    /// <param name="droppedSinceLast">Number of events dropped by the Helper since last batch.</param>
    public BatchResult ProcessBatch(IReadOnlyList<BehavioralEvent> events, long droppedSinceLast)
    {
        int stored = 0;
        int rejected = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var evt in events)
        {
            if (!IsValid(evt))
            {
                rejected++;
                continue;
            }

            if (evt.Type == BehavioralEventType.TreeSnapshot)
            {
                var hash = evt.TreeHash!;
                if (_recentTreeHashes.TryGetValue(hash, out var lastSeen) &&
                    (now - lastSeen) < DedupWindow)
                {
                    // duplicate within window — skip
                    continue;
                }
                _recentTreeHashes[hash] = now;
            }

            var seq = _nextSeq++;
            _db.InsertBehavioralEvent(
                sessionId: _sessionId,
                sequenceNum: (int)seq,
                eventType: evt.Type.ToString().ToLowerInvariant(),
                eventSubtype: evt.Subtype,
                treeHash: evt.TreeHash,
                elementId: evt.ElementId,
                controlType: evt.ControlType,
                className: evt.ClassName,
                nameHash: evt.NameHash,
                boundingRect: evt.BoundingRect,
                keystrokeCategory: evt.KeystrokeCat?.ToString().ToLowerInvariant(),
                timingBucket: evt.Timing?.ToString().ToLowerInvariant(),
                keystrokeCount: evt.KeystrokeCount,
                occurrenceCount: evt.OccurrenceCount,
                helperTimestamp: evt.Timestamp.ToString("o"));

            // Notify ActionCorrelator after persisting Interaction events
            if (evt.Type == BehavioralEventType.Interaction
                && _onInteraction is not null
                && evt.ElementId is not null)
            {
                _onInteraction(evt.TreeHash ?? "", evt.ElementId, evt.ControlType, evt.Timestamp);
            }

            stored++;
        }

        PruneStaleDedup(now);

        return new BatchResult(Accepted: true, EventsStored: stored, EventsRejected: rejected);
    }

    private static bool IsValid(BehavioralEvent evt)
    {
        if (!Enum.IsDefined(evt.Type))
            return false;

        return evt.Type switch
        {
            BehavioralEventType.TreeSnapshot => !string.IsNullOrEmpty(evt.TreeHash),
            BehavioralEventType.Interaction => !string.IsNullOrEmpty(evt.ElementId),
            _ => true
        };
    }

    private void PruneStaleDedup(DateTimeOffset now)
    {
        var staleKeys = _recentTreeHashes
            .Where(kvp => (now - kvp.Value) >= DedupPruneWindow)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in staleKeys)
            _recentTreeHashes.Remove(key);
    }
}
