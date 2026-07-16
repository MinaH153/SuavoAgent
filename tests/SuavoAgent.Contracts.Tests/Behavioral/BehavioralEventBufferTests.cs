using SuavoAgent.Contracts.Behavioral;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Behavioral;

public class BehavioralEventBufferTests
{
    private static BehavioralEvent MakeEvent() =>
        BehavioralEvent.TreeSnapshot("hash-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public async Task BelowBatchSize_DoesNotFlush()
    {
        var flushed = new List<IReadOnlyList<BehavioralEvent>>();
        var buf = new BehavioralEventBuffer(
            capacity: 100,
            batchSize: 5,
            flushAction: batch => { flushed.Add(batch); return Task.CompletedTask; });

        buf.Enqueue(MakeEvent());
        buf.Enqueue(MakeEvent());
        buf.Enqueue(MakeEvent());

        await Task.Delay(50); // give fire-and-forget a moment
        Assert.Empty(flushed);
    }

    [Fact]
    public async Task AtBatchSize_FlushesAutomatically()
    {
        var flushed = new List<IReadOnlyList<BehavioralEvent>>();
        var tcs = new TaskCompletionSource();

        var buf = new BehavioralEventBuffer(
            capacity: 100,
            batchSize: 3,
            flushAction: batch =>
            {
                flushed.Add(batch);
                tcs.TrySetResult();
                return Task.CompletedTask;
            });

        buf.Enqueue(MakeEvent());
        buf.Enqueue(MakeEvent());
        buf.Enqueue(MakeEvent()); // triggers flush

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Single(flushed);
        Assert.Equal(3, flushed[0].Count);
    }

    [Fact]
    public async Task OverCapacity_DropsOldest()
    {
        var received = new List<BehavioralEvent>();
        var buf = new BehavioralEventBuffer(
            capacity: 3,
            batchSize: 10, // won't auto-flush
            flushAction: batch => { received.AddRange(batch); return Task.CompletedTask; });

        var e1 = MakeEvent();
        var e2 = MakeEvent();
        var e3 = MakeEvent();
        var e4 = MakeEvent(); // should evict e1

        buf.Enqueue(e1);
        buf.Enqueue(e2);
        buf.Enqueue(e3);
        buf.Enqueue(e4); // evicts e1

        Assert.Equal(1, buf.DroppedEventCount);

        await buf.FlushAsync();
        // buffer should contain e2, e3, e4
        Assert.Equal(3, received.Count);
        Assert.DoesNotContain(received, e => e.TreeHash == e1.TreeHash);
    }

    [Fact]
    public void DroppedEventCount_IncrementsOnEviction()
    {
        var buf = new BehavioralEventBuffer(
            capacity: 2,
            batchSize: 100,
            flushAction: _ => Task.CompletedTask);

        buf.Enqueue(MakeEvent());
        buf.Enqueue(MakeEvent());
        buf.Enqueue(MakeEvent()); // +1 drop
        buf.Enqueue(MakeEvent()); // +1 drop

        Assert.Equal(2, buf.DroppedEventCount);
    }

    [Fact]
    public void DroppedSinceLastFlush_ResetsAfterReset()
    {
        var buf = new BehavioralEventBuffer(
            capacity: 1,
            batchSize: 100,
            flushAction: _ => Task.CompletedTask);

        buf.Enqueue(MakeEvent());
        buf.Enqueue(MakeEvent()); // drops 1
        buf.Enqueue(MakeEvent()); // drops 1

        Assert.Equal(2, buf.DroppedSinceLastFlush);

        buf.ResetDroppedSinceLastFlush();

        Assert.Equal(0, buf.DroppedSinceLastFlush);
        Assert.Equal(2, buf.DroppedEventCount); // total unchanged
    }

    [Fact]
    public async Task AssignsMonotonicSequenceNumbers()
    {
        var received = new List<BehavioralEvent>();
        var buf = new BehavioralEventBuffer(
            capacity: 100,
            batchSize: 3,
            flushAction: batch => { received.AddRange(batch); return Task.CompletedTask; });

        var tcs = new TaskCompletionSource();
        var bufWithSignal = new BehavioralEventBuffer(
            capacity: 100,
            batchSize: 3,
            flushAction: batch =>
            {
                received.AddRange(batch);
                tcs.TrySetResult();
                return Task.CompletedTask;
            });

        bufWithSignal.Enqueue(MakeEvent());
        bufWithSignal.Enqueue(MakeEvent());
        bufWithSignal.Enqueue(MakeEvent());

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var seqs = received.Select(e => e.Seq).ToList();
        Assert.Equal(new long[] { 1, 2, 3 }, seqs);
    }

    [Fact]
    public async Task FlushAsync_ForceFlushesCurrentContents()
    {
        var received = new List<BehavioralEvent>();
        var buf = new BehavioralEventBuffer(
            capacity: 100,
            batchSize: 50, // won't auto-flush
            flushAction: batch => { received.AddRange(batch); return Task.CompletedTask; });

        buf.Enqueue(MakeEvent());
        buf.Enqueue(MakeEvent());

        await buf.FlushAsync();

        Assert.Equal(2, received.Count);
    }

    [Fact]
    public async Task FlushAction_ExceptionSwallowed()
    {
        var buf = new BehavioralEventBuffer(
            capacity: 100,
            batchSize: 2,
            flushAction: _ => throw new InvalidOperationException("boom"));

        buf.Enqueue(MakeEvent());
        buf.Enqueue(MakeEvent()); // triggers flush that throws

        await Task.Delay(100); // give fire-and-forget time to blow up
        // Should not propagate — buffer still usable
        buf.Enqueue(MakeEvent()); // no exception
    }

    [Fact]
    public async Task FailedDelivery_RetriesSameEnvelope_UntilExplicitSuccess()
    {
        var attempts = new List<BehavioralEventBatch>();
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var buffer = new BehavioralEventBuffer(
            capacity: 20,
            batchSize: 2,
            channel: BehavioralEventChannels.Pms,
            flushAction: (batch, _) =>
            {
                lock (attempts) attempts.Add(batch);
                var success = attempts.Count >= 3;
                if (success) delivered.TrySetResult();
                return Task.FromResult(success);
            });

        buffer.Enqueue(MakeEvent());
        buffer.Enqueue(MakeEvent());

        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await buffer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(3, attempts.Count);
        Assert.Single(attempts.Select(batch => batch.BatchId).Distinct());
        Assert.Single(attempts.Select(batch => batch.StreamId).Distinct());
        Assert.All(attempts, batch => Assert.Equal(new long[] { 1, 2 }, batch.Events.Select(e => e.Seq)));
        var telemetry = buffer.SnapshotTelemetry();
        Assert.Equal(2, telemetry.DeliveryFailures);
        Assert.Equal(1, telemetry.DeliveredBatches);
        Assert.Equal(2, telemetry.LastDeliveredSequence);
    }

    [Fact]
    public async Task CapacityEviction_IsDeclaredInEnvelopeAndSequenceGap()
    {
        BehavioralEventBatch? delivered = null;
        await using var buffer = new BehavioralEventBuffer(
            capacity: 2,
            batchSize: 10,
            channel: BehavioralEventChannels.System,
            flushAction: (batch, _) =>
            {
                delivered = batch;
                return Task.FromResult(true);
            });

        buffer.Enqueue(MakeEvent());
        buffer.Enqueue(MakeEvent());
        buffer.Enqueue(MakeEvent());
        await buffer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(delivered);
        Assert.Equal(1, delivered!.DroppedTotal);
        Assert.Equal(2, delivered.FirstSequence);
        Assert.Equal(3, delivered.LastSequence);
        Assert.Equal(new long[] { 2, 3 }, delivered.Events.Select(e => e.Seq));
    }

    [Fact]
    public async Task ForceFlush_DrainsMultipleBatchesInOrder()
    {
        var deliveredSequences = new List<long>();
        await using var buffer = new BehavioralEventBuffer(
            capacity: 20,
            batchSize: 2,
            channel: BehavioralEventChannels.Pms,
            flushAction: (batch, _) =>
            {
                deliveredSequences.AddRange(batch.Events.Select(e => e.Seq));
                return Task.FromResult(true);
            });

        for (var index = 0; index < 5; index++) buffer.Enqueue(MakeEvent());
        await buffer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, deliveredSequences);
        Assert.Equal(3, buffer.SnapshotTelemetry().DeliveredBatches);
    }

    [Fact]
    public async Task PreEnqueueDrop_AdvancesSourceSequenceAndDropTruth()
    {
        BehavioralEventBatch? delivered = null;
        await using var buffer = new BehavioralEventBuffer(
            10,
            10,
            BehavioralEventChannels.Pms,
            (batch, _) =>
            {
                delivered = batch;
                return Task.FromResult(true);
            });

        buffer.RecordDropped(3);
        buffer.Enqueue(MakeEvent());
        await buffer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(delivered);
        Assert.Equal(3, delivered!.DroppedTotal);
        Assert.Equal(4, delivered.FirstSequence);
        Assert.Equal(4, delivered.LastSequence);
    }

    [Fact]
    public async Task ProductionSizedBatch_RemainsInsideIpcFrameLimit()
    {
        BehavioralEventBatch? delivered = null;
        await using var buffer = new BehavioralEventBuffer(
            500,
            20,
            BehavioralEventChannels.Pms,
            (batch, _) =>
            {
                delivered = batch;
                return Task.FromResult(true);
            });
        var oversized = new BehavioralEvent
        {
            Type = BehavioralEventType.Interaction,
            Subtype = new string('s', 2000),
            TreeHash = new string('t', 8000),
            ElementId = new string('e', 4000),
            ControlType = new string('c', 2000),
            ClassName = new string('k', 4000),
            NameHash = new string('n', 2000),
            BoundingRect = new string('b', 2000),
            OccurrenceCount = 1,
            Timestamp = DateTimeOffset.UtcNow,
        };

        for (var index = 0; index < 20; index++) buffer.Enqueue(oversized);
        await buffer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2));

        var json = System.Text.Json.JsonSerializer.Serialize(delivered);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(json) <= SuavoAgent.Contracts.Ipc.IpcFraming.MaxPayloadSize);
    }

    [Fact]
    public async Task CrashReconstruction_ReplaysExactPersistedInflightEnvelopeThenRotates()
    {
        var oldLease = Lease(epoch: 4);
        var freshLease = Lease(epoch: 5);
        var originalBatch = SealedBatch(oldLease, streamId: Guid.NewGuid().ToString("N"));
        var spool = new MemorySpool(new BehavioralEventBufferState
        {
            StreamId = originalBatch.StreamId,
            Channel = BehavioralEventChannels.Pms,
            LastAssignedSequence = originalBatch.LastSequence,
            DroppedTotal = originalBatch.DroppedTotal,
            DroppedSinceFlush = 0,
            ActiveLease = oldLease,
            InFlight = originalBatch,
        });
        BehavioralEventBatch? replayed = null;

        await using var reconstructed = new BehavioralEventBuffer(
            20,
            10,
            BehavioralEventChannels.Pms,
            (batch, _) =>
            {
                replayed = batch;
                return Task.FromResult(BehavioralBatchDeliveryResult.Acknowledged);
            },
            freshLease,
            spool);

        await reconstructed.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(originalBatch, replayed);
        Assert.Null(spool.State!.InFlight);
        var oldStream = reconstructed.StreamId;

        reconstructed.RotateLease(freshLease);

        Assert.NotEqual(oldStream, reconstructed.StreamId);
        Assert.Equal(freshLease.LeaseId, reconstructed.ActiveLease!.LeaseId);
        Assert.Equal(0, spool.State.LastAssignedSequence);
    }

    [Fact]
    public async Task LostAckAfterReconstruction_RetriesSamePersistedEnvelopeUntilExactAck()
    {
        var lease = Lease(7);
        var batch = SealedBatch(lease, Guid.NewGuid().ToString("N"));
        var spool = new MemorySpool(new BehavioralEventBufferState
        {
            StreamId = batch.StreamId,
            Channel = BehavioralEventChannels.System,
            LastAssignedSequence = batch.LastSequence,
            ActiveLease = lease,
            InFlight = batch,
        });
        var attempts = new List<BehavioralEventBatch>();

        await using var reconstructed = new BehavioralEventBuffer(
            20,
            10,
            BehavioralEventChannels.System,
            (candidate, _) =>
            {
                attempts.Add(candidate);
                return Task.FromResult(attempts.Count == 1
                    ? BehavioralBatchDeliveryResult.Retry
                    : BehavioralBatchDeliveryResult.Acknowledged);
            },
            Lease(8),
            spool);

        await reconstructed.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, attempts.Count);
        Assert.All(attempts, candidate => Assert.Equal(batch.BatchId, candidate.BatchId));
        Assert.Null(spool.State!.InFlight);
        Assert.Equal(1, reconstructed.SnapshotTelemetry().DeliveryFailures);
    }

    [Fact]
    public async Task NonRetryableLeaseFailure_RetainsEncryptedSpoolEvidenceInQuarantine()
    {
        var lease = Lease(9);
        var spool = new MemorySpool();
        var reported = new List<string>();
        await using var buffer = new BehavioralEventBuffer(
            20,
            1,
            BehavioralEventChannels.Pms,
            (_, _) => Task.FromResult(
                BehavioralBatchDeliveryResult.Quarantine("observation_lease_expired")),
            lease,
            spool,
            onQuarantine: reported.Add);

        buffer.Enqueue(MakeEvent());
        await buffer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, buffer.SnapshotTelemetry().QuarantinedBatches);
        var quarantined = Assert.Single(spool.State!.QuarantinedBatches);
        Assert.Equal("observation_lease_expired", quarantined.ReasonCode);
        Assert.Equal(["observation_lease_expired"], reported);
        Assert.Null(spool.State.InFlight);
    }

    [Fact]
    public void PersistenceWriteFailure_FailsClosedAndSurfacesStableStatusCode()
    {
        var observedFaults = new List<string>();
        var spool = new FailAfterInitialSaveSpool();
        using var buffer = new BehavioralEventBuffer(
            20,
            10,
            BehavioralEventChannels.Pms,
            (_, _) => Task.FromResult(BehavioralBatchDeliveryResult.Acknowledged),
            Lease(11),
            spool,
            observedFaults.Add);

        buffer.Enqueue(MakeEvent());
        buffer.Enqueue(MakeEvent());

        Assert.Equal(["observation_spool_write_failed"], observedFaults);
        Assert.False(buffer.SnapshotTelemetry().PersistenceHealthy);
        Assert.Equal(0, buffer.SnapshotTelemetry().DeliveredBatches);
    }

    private static ObservationKeyLease Lease(long epoch) => new()
    {
        LeaseId = $"opaque-lease-{epoch}",
        SessionBinding = $"opaque-session-{epoch}",
        Epoch = epoch,
        IssuedAtUtc = DateTimeOffset.UtcNow,
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(15),
        KeyMaterial = Convert.ToBase64String(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
    };

    private static BehavioralEventBatch SealedBatch(ObservationKeyLease lease, string streamId)
    {
        var behavioralEvent = MakeEvent().WithSeq(1);
        return ObservationBatchAuthentication.Seal(new BehavioralEventBatch
        {
            BatchId = Guid.NewGuid().ToString("N"),
            StreamId = streamId,
            Channel = BehavioralEventChannels.Pms,
            FirstSequence = 1,
            LastSequence = 1,
            DroppedTotal = 0,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Events = [behavioralEvent],
        }, lease);
    }

    private sealed class MemorySpool(BehavioralEventBufferState? state = null)
        : IBehavioralEventSpool
    {
        public BehavioralEventBufferState? State { get; private set; } = state;
        public BehavioralEventBufferState? Load() => State;
        public void Save(BehavioralEventBufferState next) => State = next;
        public void Dispose() { }
    }

    private sealed class FailAfterInitialSaveSpool : IBehavioralEventSpool
    {
        private int _saveCount;
        public BehavioralEventBufferState? Load() => null;
        public void Save(BehavioralEventBufferState state)
        {
            if (Interlocked.Increment(ref _saveCount) > 1)
                throw new BehavioralEventPersistenceException("observation_spool_write_failed");
        }
        public void Dispose() { }
    }
}
