namespace SuavoAgent.Contracts.Behavioral;

/// <summary>
/// Bounded ring buffer for behavioral events.
/// Thread-safe. Flush is fire-and-forget (exceptions swallowed).
/// Evicts oldest events when at capacity.
/// </summary>
public sealed class BehavioralEventBuffer
{
    private readonly int _capacity;
    private readonly int _batchSize;
    private readonly Func<IReadOnlyList<BehavioralEvent>, Task> _flushAction;
    private readonly Queue<BehavioralEvent> _queue = new();
    private readonly object _lock = new();
    private long _seq;
    private long _droppedTotal;
    private long _droppedSinceFlush;

    public BehavioralEventBuffer(
        int capacity,
        int batchSize,
        Func<IReadOnlyList<BehavioralEvent>, Task> flushAction)
    {
        _capacity = capacity;
        _batchSize = batchSize;
        _flushAction = flushAction;
    }

    /// <summary>Total events dropped since creation (evicted due to capacity).</summary>
    public long DroppedEventCount
    {
        get { lock (_lock) return _droppedTotal; }
    }

    /// <summary>Events dropped since last call to ResetDroppedSinceLastFlush.</summary>
    public long DroppedSinceLastFlush
    {
        get { lock (_lock) return _droppedSinceFlush; }
    }

    /// <summary>Resets the per-flush drop counter (call after reading in heartbeat).</summary>
    public void ResetDroppedSinceLastFlush()
    {
        lock (_lock) _droppedSinceFlush = 0;
    }

    /// <summary>
    /// Enqueues an event with a monotonic sequence number.
    /// Evicts oldest if at capacity. Triggers flush when batch size reached.
    /// </summary>
    public void Enqueue(BehavioralEvent ev)
    {
        bool shouldFlush;
        List<BehavioralEvent>? batch = null;

        lock (_lock)
        {
            // Evict oldest if full
            if (_queue.Count >= _capacity)
            {
                _queue.Dequeue();
                _droppedTotal++;
                _droppedSinceFlush++;
            }

            var sequenced = ev.WithSeq(++_seq);
            _queue.Enqueue(sequenced);

            shouldFlush = _queue.Count >= _batchSize;
            if (shouldFlush)
                batch = DrainLocked();
        }

        if (shouldFlush && batch is not null)
            FireAndForget(batch);
    }

    /// <summary>Force-flushes all current buffer contents.</summary>
    public Task FlushAsync()
    {
        List<BehavioralEvent> batch;
        lock (_lock)
        {
            if (_queue.Count == 0)
                return Task.CompletedTask;
            batch = DrainLocked();
        }
        return InvokeFlushSafe(batch);
    }

    // ── Private ──────────────────────────────────────────────────────────────

    private List<BehavioralEvent> DrainLocked()
    {
        var batch = new List<BehavioralEvent>(_queue.Count);
        while (_queue.Count > 0)
            batch.Add(_queue.Dequeue());
        return batch;
    }

    private void FireAndForget(IReadOnlyList<BehavioralEvent> batch) =>
        Task.Run(() => InvokeFlushSafe(batch));

    private async Task InvokeFlushSafe(IReadOnlyList<BehavioralEvent> batch)
    {
        try
        {
            await _flushAction(batch).ConfigureAwait(false);
        }
        catch
        {
            // Swallow — buffer must not crash the agent process
        }
    }
}
