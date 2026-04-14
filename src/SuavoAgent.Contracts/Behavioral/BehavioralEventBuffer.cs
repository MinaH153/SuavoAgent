namespace SuavoAgent.Contracts.Behavioral;

/// <summary>
/// Bounded ring buffer for behavioral events.
/// Thread-safe. Flush is fire-and-forget (exceptions swallowed).
/// Evicts oldest events when at capacity.
/// Flushes when batch size (50 events) OR 5-second timer fires — whichever first.
/// </summary>
public sealed class BehavioralEventBuffer : IDisposable
{
    private static readonly TimeSpan TimerFlushInterval = TimeSpan.FromSeconds(5);

    private readonly int _capacity;
    private readonly int _batchSize;
    private readonly Func<IReadOnlyList<BehavioralEvent>, Task> _flushAction;
    private readonly Queue<BehavioralEvent> _queue = new();
    private readonly object _lock = new();
    private readonly Timer _flushTimer;
    private long _seq;
    private long _droppedTotal;
    private long _droppedSinceFlush;
    private bool _disposed;

    public BehavioralEventBuffer(
        int capacity,
        int batchSize,
        Func<IReadOnlyList<BehavioralEvent>, Task> flushAction)
    {
        _capacity = capacity;
        _batchSize = batchSize;
        _flushAction = flushAction;

        // Timer-based flush: fires every 5 seconds to drain partial batches
        _flushTimer = new Timer(_ => FireAndForgetFlush(), null,
            TimerFlushInterval, TimerFlushInterval);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _flushTimer.Dispose();
    }

    private void FireAndForgetFlush() =>
        Task.Run(() => FlushAsync());


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
