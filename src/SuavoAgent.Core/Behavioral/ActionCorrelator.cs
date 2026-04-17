using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Behavioral;

/// <summary>
/// Links UI events to SQL events by timestamp proximity.
/// The writeback discovery engine — correlates what the user clicked
/// with what SQL query followed, building a map of UI→SQL causation.
/// </summary>
public sealed class ActionCorrelator
{
    private record UiEvent(string TreeHash, string ElementId, string? ControlType, DateTimeOffset Timestamp);

    private static readonly TimeSpan SlidingWindowExpiry = TimeSpan.FromSeconds(30);

    private readonly AgentStateDb _db;
    private readonly string _sessionId;
    private double _correlationWindowSeconds;
    private readonly List<UiEvent> _window = new();
    private readonly HashSet<string> _seededShapes = new();
    private string? _activeSeedDigest;
    private readonly object _lock = new();

    public ActionCorrelator(AgentStateDb db, string sessionId, double correlationWindowSeconds = 2.0, bool clockCalibrated = true)
    {
        _db = db;
        _sessionId = sessionId;
        _correlationWindowSeconds = clockCalibrated ? correlationWindowSeconds : 5.0;
    }

    /// <summary>
    /// Sets the active seed digest for confirming seed items on independent observation.
    /// </summary>
    public void SetActiveSeedDigest(string? digest) { lock (_lock) _activeSeedDigest = digest; }

    /// <summary>
    /// Registers query shape hashes that came from collective intelligence seeds.
    /// Seeded shapes reach 0.6 confidence at 2 co-occurrences instead of 3.
    /// </summary>
    public void RegisterSeededShapes(IEnumerable<string> shapeHashes)
    {
        lock (_lock)
        {
            foreach (var h in shapeHashes) _seededShapes.Add(h);
        }
    }

    /// <summary>
    /// Updates the correlation window: 2s when calibrated, 5s when not.
    /// </summary>
    public void SetClockCalibrated(bool calibrated)
    {
        lock (_lock) _correlationWindowSeconds = calibrated ? 2.0 : 5.0;
    }

    /// <summary>
    /// Records a UI event into the sliding window. Events older than 30s are pruned.
    /// </summary>
    public void RecordUiEvent(string treeHash, string elementId, string? controlType, DateTimeOffset timestamp)
    {
        lock (_lock)
        {
            PruneExpired(timestamp);
            _window.Add(new UiEvent(treeHash, elementId, controlType, timestamp));
        }
    }

    /// <summary>
    /// Attempts to correlate a SQL event with a recent UI event.
    /// If a UI event is found within ±window seconds of the SQL execution time,
    /// upserts a correlated action record.
    /// </summary>
    public void TryCorrelateWithSql(string queryShapeHash, string lastExecutionTimeIso, bool isWrite, string? tablesReferenced)
    {
        if (!DateTimeOffset.TryParse(lastExecutionTimeIso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var sqlTime))
            return;

        UiEvent? closest;
        bool isSeeded;
        string? activeSeed;

        lock (_lock)
        {
            PruneExpired(sqlTime);
            if (_window.Count == 0) return;

            double effectiveWindowSeconds = _correlationWindowSeconds;
            var closestUi = _window[^1];
            var overrideWindow = _db.GetWindowOverride(_sessionId, closestUi.TreeHash, closestUi.ElementId);
            if (overrideWindow.HasValue)
                effectiveWindowSeconds = overrideWindow.Value;

            var window = TimeSpan.FromSeconds(effectiveWindowSeconds);
            closest = null;
            var closestDelta = TimeSpan.MaxValue;
            foreach (var evt in _window)
            {
                var delta = (sqlTime - evt.Timestamp).Duration();
                if (delta <= window && delta < closestDelta)
                {
                    closest = evt;
                    closestDelta = delta;
                }
            }

            if (closest is null) return;
            isSeeded = _seededShapes.Contains(queryShapeHash);
            activeSeed = _activeSeedDigest;
        }

        var correlationKey = $"{closest.TreeHash}:{closest.ElementId}:{queryShapeHash}";
        _db.UpsertCorrelatedAction(
            sessionId: _sessionId,
            correlationKey: correlationKey,
            treeHash: closest.TreeHash,
            elementId: closest.ElementId,
            controlType: closest.ControlType,
            queryShapeHash: queryShapeHash,
            isWrite: isWrite,
            tablesReferenced: tablesReferenced,
            seededShape: isSeeded);

        if (activeSeed is not null && isSeeded)
        {
            var now = DateTimeOffset.UtcNow.ToString("o");
            _db.ConfirmSeedItem(activeSeed, "query_shape", queryShapeHash, now);
            _db.ConfirmSeedItem(activeSeed, "correlation", correlationKey, now);
        }
    }

    private void PruneExpired(DateTimeOffset referenceTime)
    {
        _window.RemoveAll(e => (referenceTime - e.Timestamp) > SlidingWindowExpiry);
    }
}
