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
}
