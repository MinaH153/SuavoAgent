namespace SuavoAgent.Contracts.Behavioral;

/// <summary>
/// Bounded, single-writer delivery buffer for behavioral events.
/// Failed deliveries remain in-flight and are retried in-order. A batch is
/// removed only after the receiver explicitly acknowledges it.
/// </summary>
public sealed class BehavioralEventBuffer : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan TimerFlushInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(5);

    private readonly int _capacity;
    private readonly int _batchSize;
    private readonly Func<BehavioralEventBatch, CancellationToken, Task<BehavioralBatchDeliveryResult>> _flushAction;
    private readonly string _channel;
    private string _streamId;
    private ObservationKeyLease? _activeLease;
    private readonly IBehavioralEventSpool? _spool;
    private readonly Action<string>? _onPersistenceFault;
    private readonly Action<string>? _onQuarantine;
    private readonly Queue<BehavioralEvent> _queue = new();
    private readonly List<QuarantinedBehavioralBatch> _quarantined = [];
    private readonly object _lock = new();
    private readonly SemaphoreSlim _flushSignal = new(0, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _deliveryPump;
    private BehavioralEventBatch? _inFlight;
    private TaskCompletionSource? _drainedWaiter;
    private bool _forceFlush;
    private long _seq;
    private long _droppedTotal;
    private long _droppedSinceFlush;
    private long _deliveredBatches;
    private long _deliveryFailures;
    private long _lastDeliveredSequence;
    private DateTimeOffset? _lastDeliveryUtc;
    private DateTimeOffset? _lastFailureUtc;
    private string? _persistenceFaultCode;
    private bool _disposed;

    public BehavioralEventBuffer(
        int capacity,
        int batchSize,
        Func<IReadOnlyList<BehavioralEvent>, Task> flushAction)
        : this(
            capacity,
            batchSize,
            channel: "legacy",
            flushAction: async (batch, _) =>
            {
                await flushAction(batch.Events).ConfigureAwait(false);
                return BehavioralBatchDeliveryResult.Acknowledged;
            },
            activeLease: null,
            spool: null,
            onPersistenceFault: null,
            onQuarantine: null,
            initialize: true)
    {
    }

    public BehavioralEventBuffer(
        int capacity,
        int batchSize,
        string channel,
        Func<BehavioralEventBatch, CancellationToken, Task<bool>> flushAction)
        : this(
            capacity,
            batchSize,
            channel,
            async (batch, cancellationToken) =>
                await flushAction(batch, cancellationToken).ConfigureAwait(false)
                    ? BehavioralBatchDeliveryResult.Acknowledged
                    : BehavioralBatchDeliveryResult.Retry,
            activeLease: null,
            spool: null,
            onPersistenceFault: null,
            onQuarantine: null,
            initialize: true)
    {
    }

    public BehavioralEventBuffer(
        int capacity,
        int batchSize,
        string channel,
        Func<BehavioralEventBatch, CancellationToken, Task<BehavioralBatchDeliveryResult>> flushAction,
        ObservationKeyLease activeLease,
        IBehavioralEventSpool spool,
        Action<string>? onPersistenceFault = null,
        Action<string>? onQuarantine = null)
        : this(
            capacity,
            batchSize,
            channel,
            flushAction,
            activeLease,
            spool,
            onPersistenceFault,
            onQuarantine,
            initialize: true)
    {
    }

    private BehavioralEventBuffer(
        int capacity,
        int batchSize,
        string channel,
        Func<BehavioralEventBatch, CancellationToken, Task<BehavioralBatchDeliveryResult>> flushAction,
        ObservationKeyLease? activeLease,
        IBehavioralEventSpool? spool,
        Action<string>? onPersistenceFault,
        Action<string>? onQuarantine,
        bool initialize)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize));
        if (string.IsNullOrWhiteSpace(channel))
            throw new ArgumentException("A delivery channel is required.", nameof(channel));

        _capacity = capacity;
        _batchSize = batchSize;
        _channel = channel;
        _streamId = Guid.NewGuid().ToString("N");
        _flushAction = flushAction ?? throw new ArgumentNullException(nameof(flushAction));
        _activeLease = activeLease;
        _spool = spool;
        _onPersistenceFault = onPersistenceFault;
        _onQuarantine = onQuarantine;

        if (_spool is not null)
        {
            if (_activeLease is null)
                throw new ArgumentNullException(nameof(activeLease));
            try
            {
                RestoreOrInitializeDurableState(_spool.Load());
                _spool.Save(CreateStateLocked());
            }
            catch (Exception ex)
            {
                _spool.Dispose();
                if (ex is BehavioralEventPersistenceException) throw;
                throw new BehavioralEventPersistenceException("observation_spool_initialization_failed", ex);
            }
        }
        _deliveryPump = Task.Run(DeliveryPumpAsync);
    }

    public void Dispose()
    {
        DisposeAsyncCore(waitForDrain: TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore(waitForDrain: TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }


    /// <summary>Total events dropped since creation (evicted due to capacity).</summary>
    public long DroppedEventCount
    {
        get { lock (_lock) return _droppedTotal; }
    }

    public string StreamId => _streamId;

    public ObservationKeyLease? ActiveLease
    {
        get { lock (_lock) return _activeLease; }
    }

    public bool HasPendingActiveDelivery
    {
        get { lock (_lock) return _queue.Count > 0 || _inFlight is not null; }
    }

    public BehavioralBufferTelemetry SnapshotTelemetry()
    {
        lock (_lock)
        {
            return new BehavioralBufferTelemetry(
                _streamId,
                _channel,
                _queue.Count,
                _inFlight?.Events.Count ?? 0,
                _droppedTotal,
                _deliveredBatches,
                _deliveryFailures,
                _lastDeliveredSequence,
                _lastDeliveryUtc,
                _lastFailureUtc,
                _quarantined.Count,
                _persistenceFaultCode is null,
                _persistenceFaultCode,
                _activeLease?.Epoch,
                _activeLease?.ExpiresAtUtc);
        }
    }

    /// <summary>Events dropped since last call to ResetDroppedSinceLastFlush.</summary>
    public long DroppedSinceLastFlush
    {
        get { lock (_lock) return _droppedSinceFlush; }
    }

    /// <summary>Resets the per-flush drop counter (call after reading in heartbeat).</summary>
    public void ResetDroppedSinceLastFlush()
    {
        string? fault = null;
        lock (_lock)
        {
            _droppedSinceFlush = 0;
            if (!TryPersistLocked()) fault = _persistenceFaultCode;
        }
        ReportPersistenceFault(fault);
    }

    /// <summary>
    /// Enqueues an event with a monotonic sequence number.
    /// Evicts oldest if at capacity. Triggers flush when batch size reached.
    /// </summary>
    public void Enqueue(BehavioralEvent ev)
    {
        bool shouldSignal;
        string? fault = null;

        lock (_lock)
        {
            if (_disposed || _persistenceFaultCode is not null) return;

            var sequenced = BoundForTransport(ev).WithSeq(++_seq);

            // Evict oldest if full
            if (_queue.Count >= _capacity)
            {
                _queue.Dequeue();
                _droppedTotal++;
                _droppedSinceFlush++;
            }

            _queue.Enqueue(sequenced);
            shouldSignal = _queue.Count >= _batchSize;
            if (!TryPersistLocked())
            {
                shouldSignal = false;
                fault = _persistenceFaultCode;
            }
        }

        ReportPersistenceFault(fault);
        if (shouldSignal)
            SignalPump();
    }

    /// <summary>
    /// Records events intentionally discarded before enqueue (for example a
    /// UIA rate limiter). Sequence numbers are advanced so Core can observe
    /// the exact gap instead of mistaking throttling for inactivity.
    /// </summary>
    public void RecordDropped(int eventCount = 1)
    {
        if (eventCount <= 0) throw new ArgumentOutOfRangeException(nameof(eventCount));
        string? fault = null;
        lock (_lock)
        {
            if (_disposed || _persistenceFaultCode is not null) return;
            _seq += eventCount;
            _droppedTotal += eventCount;
            _droppedSinceFlush += eventCount;
            if (!TryPersistLocked()) fault = _persistenceFaultCode;
        }
        ReportPersistenceFault(fault);
    }

    /// <summary>
    /// Starts a new stream/epoch only after every active batch from the prior
    /// lease has either received an exact ACK or been retained in quarantine.
    /// Quarantined evidence is never deleted by rotation.
    /// </summary>
    public void RotateLease(ObservationKeyLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        string? fault = null;
        lock (_lock)
        {
            if (_queue.Count > 0 || _inFlight is not null)
                throw new InvalidOperationException("observation_lease_rotation_requires_drain");
            if (_persistenceFaultCode is not null)
                throw new BehavioralEventPersistenceException(_persistenceFaultCode);

            _activeLease = lease;
            _streamId = Guid.NewGuid().ToString("N");
            _seq = 0;
            _droppedTotal = 0;
            _droppedSinceFlush = 0;
            _lastDeliveredSequence = 0;
            if (!TryPersistLocked()) fault = _persistenceFaultCode;
        }
        ReportPersistenceFault(fault);
        if (fault is not null)
            throw new BehavioralEventPersistenceException(fault);
    }

    /// <summary>Force-flushes all current buffer contents.</summary>
    public Task FlushAsync(CancellationToken ct = default)
    {
        Task waitTask;
        lock (_lock)
        {
            if (_persistenceFaultCode is not null)
                return Task.FromException(new BehavioralEventPersistenceException(_persistenceFaultCode));
            if (_queue.Count == 0 && _inFlight is null)
                return Task.CompletedTask;

            _forceFlush = true;
            _drainedWaiter ??= new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            waitTask = _drainedWaiter.Task;
        }

        SignalPump();
        return ct.CanBeCanceled ? waitTask.WaitAsync(ct) : waitTask;
    }

    // ── Private ──────────────────────────────────────────────────────────────

    private async Task DeliveryPumpAsync()
    {
        var retryDelay = InitialRetryDelay;

        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await _flushSignal.WaitAsync(TimerFlushInterval, _shutdown.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            while (!_shutdown.IsCancellationRequested)
            {
                BehavioralEventBatch? batch;
                lock (_lock)
                {
                    batch = _inFlight ?? CreateBatchLocked();
                    if (batch is null)
                    {
                        CompleteDrainLocked();
                        break;
                    }
                    _inFlight = batch;
                    if (!TryPersistLocked())
                    {
                        CompleteDrainLocked(new BehavioralEventPersistenceException(
                            _persistenceFaultCode ?? "observation_spool_write_failed"));
                        batch = null;
                    }
                }

                if (batch is null)
                {
                    ReportPersistenceFault(_persistenceFaultCode);
                    break;
                }

                BehavioralBatchDeliveryResult deliveryResult;
                try
                {
                    deliveryResult = await _flushAction(batch, _shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    deliveryResult = BehavioralBatchDeliveryResult.Retry;
                }

                if (deliveryResult.Disposition == BehavioralBatchDeliveryDisposition.Retry)
                {
                    string? fault = null;
                    lock (_lock)
                    {
                        _deliveryFailures++;
                        _lastFailureUtc = DateTimeOffset.UtcNow;
                        if (!TryPersistLocked()) fault = _persistenceFaultCode;
                    }

                    if (fault is not null)
                    {
                        ReportPersistenceFault(fault);
                        break;
                    }

                    try
                    {
                        await Task.Delay(retryDelay, _shutdown.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    retryDelay = TimeSpan.FromMilliseconds(Math.Min(
                        retryDelay.TotalMilliseconds * 2,
                        MaximumRetryDelay.TotalMilliseconds));
                    continue;
                }

                retryDelay = InitialRetryDelay;
                bool continueImmediately;
                string? acknowledgementFault = null;
                string? quarantineCode = null;
                lock (_lock)
                {
                    if (deliveryResult.Disposition == BehavioralBatchDeliveryDisposition.Acknowledged)
                    {
                        _deliveredBatches++;
                        _lastDeliveredSequence = batch.LastSequence;
                        _lastDeliveryUtc = DateTimeOffset.UtcNow;
                    }
                    else
                    {
                        quarantineCode = deliveryResult.ReasonCode
                            ?? "observation_batch_quarantined";
                        _quarantined.Add(new QuarantinedBehavioralBatch(
                            batch,
                            quarantineCode,
                            DateTimeOffset.UtcNow));
                    }
                    _inFlight = null;

                    if (!TryPersistLocked()) acknowledgementFault = _persistenceFaultCode;

                    continueImmediately = _forceFlush || _queue.Count >= _batchSize;
                    if (_queue.Count == 0)
                        CompleteDrainLocked();
                }

                if (acknowledgementFault is not null)
                {
                    ReportPersistenceFault(acknowledgementFault);
                    break;
                }
                if (quarantineCode is not null)
                    ReportQuarantine(quarantineCode);

                if (!continueImmediately)
                    break;
            }
        }
    }

    private BehavioralEventBatch? CreateBatchLocked()
    {
        if (_queue.Count == 0) return null;

        var count = Math.Min(_batchSize, _queue.Count);
        var events = new List<BehavioralEvent>(count);
        for (var i = 0; i < count; i++)
            events.Add(_queue.Dequeue());

        var batch = new BehavioralEventBatch
        {
            ContractVersion = _activeLease is null
                ? BehavioralEventBatch.LegacyContractVersion
                : BehavioralEventBatch.CurrentContractVersion,
            BatchId = Guid.NewGuid().ToString("N"),
            StreamId = _streamId,
            Channel = _channel,
            FirstSequence = events[0].Seq,
            LastSequence = events[^1].Seq,
            DroppedTotal = _droppedTotal,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Events = events,
        };
        return _activeLease is null
            ? batch
            : ObservationBatchAuthentication.Seal(batch, _activeLease);
    }

    private void CompleteDrainLocked(Exception? exception = null)
    {
        _forceFlush = false;
        if (exception is null)
            _drainedWaiter?.TrySetResult();
        else
            _drainedWaiter?.TrySetException(exception);
        _drainedWaiter = null;
    }

    private void SignalPump()
    {
        try { _flushSignal.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task DisposeAsyncCore(TimeSpan waitForDrain)
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        using var timeout = new CancellationTokenSource(waitForDrain);
        try { await FlushAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (BehavioralEventPersistenceException) { }

        _shutdown.Cancel();
        SignalPump();
        try { await _deliveryPump.ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        _spool?.Dispose();
        _shutdown.Dispose();
        _flushSignal.Dispose();
    }

    private void RestoreOrInitializeDurableState(BehavioralEventBufferState? state)
    {
        if (state is null) return;
        if (state.ContractVersion != BehavioralEventBufferState.CurrentContractVersion)
            throw new BehavioralEventPersistenceException("observation_spool_version_unsupported");
        if (!string.Equals(state.Channel, _channel, StringComparison.Ordinal))
            throw new BehavioralEventPersistenceException("observation_spool_channel_mismatch");
        if (!Guid.TryParseExact(state.StreamId, "N", out _))
            throw new BehavioralEventPersistenceException("observation_spool_stream_invalid");
        if (state.LastAssignedSequence < 0 || state.DroppedTotal < 0 || state.DroppedSinceFlush < 0)
            throw new BehavioralEventPersistenceException("observation_spool_counter_invalid");
        if (state.QueuedEvents is null || state.QuarantinedBatches is null)
            throw new BehavioralEventPersistenceException("observation_spool_corrupt");
        if (state.QueuedEvents.Count > _capacity)
            throw new BehavioralEventPersistenceException("observation_spool_capacity_exceeded");
        if ((state.QueuedEvents.Count > 0 || state.InFlight is not null) && state.ActiveLease is null)
            throw new BehavioralEventPersistenceException("observation_spool_lease_missing");

        // Empty prior streams do not retain their lease across a Helper
        // process boundary. The freshly issued lease passed to the constructor
        // gets a new stream id below; quarantine evidence remains protected.
        if (state.QueuedEvents.Count == 0 && state.InFlight is null)
        {
            _quarantined.AddRange(state.QuarantinedBatches);
            return;
        }

        _streamId = state.StreamId;
        _activeLease = state.ActiveLease;
        _seq = state.LastAssignedSequence;
        _droppedTotal = state.DroppedTotal;
        _droppedSinceFlush = state.DroppedSinceFlush;
        _deliveredBatches = state.DeliveredBatches;
        _deliveryFailures = state.DeliveryFailures;
        _lastDeliveredSequence = state.LastDeliveredSequence;
        _lastDeliveryUtc = state.LastDeliveryUtc;
        _lastFailureUtc = state.LastFailureUtc;
        _inFlight = state.InFlight;
        foreach (var behavioralEvent in state.QueuedEvents)
            _queue.Enqueue(behavioralEvent);
        _quarantined.AddRange(state.QuarantinedBatches);

        var maximumPersistedSequence = Math.Max(
            _inFlight?.LastSequence ?? 0,
            _queue.Count == 0 ? 0 : _queue.Max(behavioralEvent => behavioralEvent.Seq));
        if (maximumPersistedSequence > _seq)
            throw new BehavioralEventPersistenceException("observation_spool_sequence_invalid");
    }

    private BehavioralEventBufferState CreateStateLocked() => new()
    {
        StreamId = _streamId,
        Channel = _channel,
        LastAssignedSequence = _seq,
        DroppedTotal = _droppedTotal,
        DroppedSinceFlush = _droppedSinceFlush,
        DeliveredBatches = _deliveredBatches,
        DeliveryFailures = _deliveryFailures,
        LastDeliveredSequence = _lastDeliveredSequence,
        LastDeliveryUtc = _lastDeliveryUtc,
        LastFailureUtc = _lastFailureUtc,
        ActiveLease = _activeLease,
        QueuedEvents = _queue.ToArray(),
        InFlight = _inFlight,
        QuarantinedBatches = _quarantined.ToArray(),
    };

    private bool TryPersistLocked()
    {
        if (_spool is null || _persistenceFaultCode is not null) return _persistenceFaultCode is null;
        try
        {
            _spool.Save(CreateStateLocked());
            return true;
        }
        catch (BehavioralEventPersistenceException ex)
        {
            _persistenceFaultCode = ex.Code;
            return false;
        }
        catch
        {
            _persistenceFaultCode = "observation_spool_write_failed";
            return false;
        }
    }

    private void ReportPersistenceFault(string? code)
    {
        if (code is null) return;
        try { _onPersistenceFault?.Invoke(code); }
        catch { }
    }

    private void ReportQuarantine(string code)
    {
        try { _onQuarantine?.Invoke(code); }
        catch { }
    }

    private static BehavioralEvent BoundForTransport(BehavioralEvent behavioralEvent) =>
        behavioralEvent with
        {
            Subtype = Truncate(behavioralEvent.Subtype, 128),
            TreeHash = Truncate(behavioralEvent.TreeHash, 1024),
            ElementId = Truncate(behavioralEvent.ElementId, 512),
            ControlType = Truncate(behavioralEvent.ControlType, 128),
            ClassName = Truncate(behavioralEvent.ClassName, 256),
            NameHash = Truncate(behavioralEvent.NameHash, 128),
            BoundingRect = Truncate(behavioralEvent.BoundingRect, 128),
            OccurrenceCount = Math.Clamp(behavioralEvent.OccurrenceCount, 0, 1_000_000),
        };

    private static string? Truncate(string? value, int maximumLength) =>
        value is null || value.Length <= maximumLength
            ? value
            : value[..maximumLength];
}
