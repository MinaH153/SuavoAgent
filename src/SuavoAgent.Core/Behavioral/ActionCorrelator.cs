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

    public ActionCorrelator(AgentStateDb db, string sessionId, double correlationWindowSeconds = 2.0, bool clockCalibrated = true)
    {
        _db = db;
        _sessionId = sessionId;
        _correlationWindowSeconds = clockCalibrated ? correlationWindowSeconds : 5.0;
    }

    /// <summary>
    /// Updates the correlation window: 2s when calibrated, 5s when not.
    /// </summary>
    public void SetClockCalibrated(bool calibrated)
    {
        _correlationWindowSeconds = calibrated ? 2.0 : 5.0;
    }

    /// <summary>
    /// Records a UI event into the sliding window. Events older than 30s are pruned.
    /// </summary>
    public void RecordUiEvent(string treeHash, string elementId, string? controlType, DateTimeOffset timestamp)
    {
        PruneExpired(timestamp);
        _window.Add(new UiEvent(treeHash, elementId, controlType, timestamp));
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

        PruneExpired(sqlTime);

        if (_window.Count == 0)
            return;

        var window = TimeSpan.FromSeconds(_correlationWindowSeconds);
        UiEvent? closest = null;
        TimeSpan closestDelta = TimeSpan.MaxValue;

        foreach (var evt in _window)
        {
            var delta = (sqlTime - evt.Timestamp).Duration();
            if (delta <= window && delta < closestDelta)
            {
                closest = evt;
                closestDelta = delta;
            }
        }

        if (closest is null)
            return;

        var correlationKey = $"{closest.TreeHash}:{closest.ElementId}:{queryShapeHash}";
        _db.UpsertCorrelatedAction(
            sessionId: _sessionId,
            correlationKey: correlationKey,
            treeHash: closest.TreeHash,
            elementId: closest.ElementId,
            controlType: closest.ControlType,
            queryShapeHash: queryShapeHash,
            isWrite: isWrite,
            tablesReferenced: tablesReferenced);
    }

    private void PruneExpired(DateTimeOffset referenceTime)
    {
        _window.RemoveAll(e => (referenceTime - e.Timestamp) > SlidingWindowExpiry);
    }
}
