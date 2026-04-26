using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Behavioral;

/// <summary>
/// Processes batches of BehavioralEvents received over IPC.
/// Validates, deduplicates tree snapshots, and persists to the state DB.
///
/// Codex 2026-04-27 review redesign — instead of a mutable <c>_sessionId</c>
/// field re-bound at runtime via Configure(), the receiver is constructed
/// with a <c>Func&lt;string&gt; sessionResolver</c> that the receiver invokes
/// once per batch. This eliminates three classes of bug from the prior
/// Configure() design:
///
///   • Pre-Configure window — events arriving between agent boot and
///     LearningWorker.Configure() got attributed to a placeholder session
///     forever. Now every batch resolves the active session lazily, so the
///     placeholder window is one batch wide and self-heals.
///   • Configure / ProcessBatch race — two threadpool tasks could see
///     half-updated <c>_sessionId</c> / <c>_onInteraction</c>. The resolver
///     runs inside the per-batch lock so all reads / writes of mutable
///     state are inside the same critical section.
///   • Tree-snapshot dedup bleed — a hash cached under the placeholder
///     session was silently de-duped against the same hash arriving under
///     the live session. Now <c>_recentTreeHashes</c> is cleared whenever
///     the resolver returns a session id different from the previous batch.
///
/// The <c>onInteraction</c> callback (used by ActionCorrelator wiring) is
/// still mutable via SetInteractionCallback so LearningWorker can bind a
/// correlator instance once it's ready. The rebind goes through the same
/// per-batch lock so an in-flight batch sees a coherent view.
/// </summary>
public sealed class BehavioralEventReceiver
{
    private static readonly TimeSpan DedupWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DedupPruneWindow = TimeSpan.FromSeconds(120);
    private const string PreLearningSessionPlaceholder = "pre-learning";

    private readonly AgentStateDb _db;
    private readonly Func<string?> _sessionResolver;
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _recentTreeHashes = new();
    private Action<string, string, string?, DateTimeOffset>? _onInteraction;
    private string _currentSessionId;
    private long _nextSeq = 1;
    private long _totalDroppedEvents;

    /// <summary>
    /// Running total of events dropped by the Helper since the current
    /// session began. Reset whenever <see cref="_sessionResolver"/> returns
    /// a new session id so PomExporter sees per-session drop counts rather
    /// than agent-process-lifetime totals.
    /// </summary>
    public long TotalDroppedEvents => Interlocked.Read(ref _totalDroppedEvents);

    public record BatchResult(bool Accepted, int EventsStored, int EventsRejected);

    /// <param name="db">State database.</param>
    /// <param name="sessionResolver">
    /// Returns the active learning session id, or null if no learning
    /// session is active yet. Invoked once per batch under the receiver's
    /// internal lock — must be cheap and thread-safe (DB reads are fine).
    /// </param>
    /// <param name="onInteraction">
    /// Optional initial callback invoked after each Interaction event is
    /// persisted. Args: (treeHash, elementId, controlType, timestamp).
    /// LearningWorker rebinds this via <see cref="SetInteractionCallback"/>
    /// once ActionCorrelator is constructed.
    /// </param>
    public BehavioralEventReceiver(AgentStateDb db, Func<string?> sessionResolver,
        Action<string, string, string?, DateTimeOffset>? onInteraction = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _sessionResolver = sessionResolver ?? throw new ArgumentNullException(nameof(sessionResolver));
        _onInteraction = onInteraction;
        _currentSessionId = ResolveSessionSafe();
    }

    /// <summary>
    /// Convenience constructor for tests — pins the receiver to a fixed
    /// session id without a resolver. Production code should always use
    /// the resolver overload so session changes are picked up lazily.
    /// </summary>
    public BehavioralEventReceiver(AgentStateDb db, string sessionId,
        Action<string, string, string?, DateTimeOffset>? onInteraction = null)
        : this(db, () => sessionId, onInteraction)
    {
    }

    /// <summary>
    /// Re-binds the post-persist interaction callback. Used by
    /// LearningWorker once ActionCorrelator is constructed for the
    /// active session. Fully thread-safe with in-flight ProcessBatch.
    /// </summary>
    public void SetInteractionCallback(
        Action<string, string, string?, DateTimeOffset>? onInteraction)
    {
        lock (_lock)
        {
            _onInteraction = onInteraction;
        }
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

        // Resolve the active session lazily so events arriving before
        // LearningWorker had a chance to register an interaction callback
        // still land under the correct session id.
        Action<string, string, string?, DateTimeOffset>? onInteractionSnapshot;
        string sessionId;
        lock (_lock)
        {
            sessionId = ResolveSessionSafe();
            if (!string.Equals(sessionId, _currentSessionId, StringComparison.Ordinal))
            {
                // Session boundary — reset all per-session counters / dedup
                // so the new session starts with a clean slate. Without this,
                // a snapshot cached under the placeholder session would be
                // silently dropped if the same hash arrived under the live
                // session within DedupWindow.
                _recentTreeHashes.Clear();
                _nextSeq = 1;
                Interlocked.Exchange(ref _totalDroppedEvents, 0);
                _currentSessionId = sessionId;
            }
            Interlocked.Add(ref _totalDroppedEvents, droppedSinceLast);
            onInteractionSnapshot = _onInteraction;

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
                    sessionId: sessionId,
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

                stored++;
            }

            PruneStaleDedup(now);
        }

        // Run interaction callbacks OUTSIDE the lock — they call into
        // ActionCorrelator which may take its own lock; nesting risks
        // ordered-lock-acquisition deadlocks if a future caller flips the
        // order. Snapshot the callback and the events that need it inside
        // the lock, replay outside.
        //
        // Codex round-3 H-NEW-1 — guard must mirror IsValid()'s
        // !string.IsNullOrEmpty check, not just `is not null`. An
        // Interaction with ElementId="" passes the `is not null` predicate
        // but fails IsValid + skips persistence; pre-PR the callback was
        // inside the persist loop so it never fired for empty-id events.
        // Replicate that semantics outside the lock so ActionCorrelator
        // never sees phantom empty-id interactions.
        if (onInteractionSnapshot is not null)
        {
            foreach (var evt in events)
            {
                if (evt.Type == BehavioralEventType.Interaction
                    && !string.IsNullOrEmpty(evt.ElementId))
                {
                    onInteractionSnapshot(evt.TreeHash ?? "", evt.ElementId, evt.ControlType, evt.Timestamp);
                }
            }
        }

        return new BatchResult(Accepted: true, EventsStored: stored, EventsRejected: rejected);
    }

    private string ResolveSessionSafe()
    {
        try
        {
            return _sessionResolver() ?? PreLearningSessionPlaceholder;
        }
        catch
        {
            return PreLearningSessionPlaceholder;
        }
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
