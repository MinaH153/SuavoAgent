using System.Diagnostics;
using Serilog;
using SuavoAgent.Helper.Behavioral;
using Xunit;

namespace SuavoAgent.Helper.Tests.Behavioral;

public sealed class CoalescingResnapshotSchedulerTests
{
    [Fact]
    public async Task BurstSignals_CoalesceIntoOneResnapshot()
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        var captures = 0;
        using var scheduler = new CoalescingResnapshotScheduler(
            () => Interlocked.Increment(ref captures),
            logger,
            debounce: TimeSpan.FromMilliseconds(30),
            minimumInterval: TimeSpan.FromMilliseconds(30));

        for (var index = 0; index < 100; index++)
            scheduler.Request();

        await WaitUntilAsync(() => Volatile.Read(ref captures) == 1);
        await Task.Delay(100);

        Assert.Equal(1, Volatile.Read(ref captures));
        Assert.Equal(100, scheduler.RequestedCount);
        Assert.Equal(100, scheduler.CompletedThrough);
    }

    [Fact]
    public async Task StructureCallbackSignal_ReturnsImmediately_AndNeverOverlapsSlowCapture()
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        using var firstCaptureStarted = new ManualResetEventSlim();
        using var releaseFirstCapture = new ManualResetEventSlim();
        var captures = 0;
        var active = 0;
        var maximumActive = 0;
        using var scheduler = new CoalescingResnapshotScheduler(
            () =>
            {
                var nowActive = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximumActive, nowActive);
                var captureNumber = Interlocked.Increment(ref captures);
                if (captureNumber == 1)
                {
                    firstCaptureStarted.Set();
                    releaseFirstCapture.Wait(TimeSpan.FromSeconds(2));
                }
                Interlocked.Decrement(ref active);
            },
            logger,
            debounce: TimeSpan.FromMilliseconds(10),
            minimumInterval: TimeSpan.FromMilliseconds(10));

        var callbackStopwatch = Stopwatch.StartNew();
        scheduler.Request();
        callbackStopwatch.Stop();
        Assert.True(
            callbackStopwatch.Elapsed < TimeSpan.FromMilliseconds(50),
            $"Signal path blocked for {callbackStopwatch.Elapsed.TotalMilliseconds:F1}ms");

        Assert.True(firstCaptureStarted.Wait(TimeSpan.FromSeconds(1)));
        for (var index = 0; index < 50; index++)
            scheduler.Request();
        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref captures));

        releaseFirstCapture.Set();
        await WaitUntilAsync(() => Volatile.Read(ref captures) == 2);
        Assert.Equal(1, Volatile.Read(ref maximumActive));
    }

    [Fact]
    public async Task SignalsDuringRateLimitWait_CoalesceIntoOneLaterCapture()
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        var captures = 0;
        using var scheduler = new CoalescingResnapshotScheduler(
            () => Interlocked.Increment(ref captures),
            logger,
            debounce: TimeSpan.FromMilliseconds(10),
            minimumInterval: TimeSpan.FromMilliseconds(150));

        scheduler.Request();
        await WaitUntilAsync(() => Volatile.Read(ref captures) == 1);

        scheduler.Request();
        await Task.Delay(30);
        for (var index = 0; index < 100; index++)
            scheduler.Request();

        await WaitUntilAsync(() => Volatile.Read(ref captures) == 2);
        await Task.Delay(100);
        Assert.Equal(2, Volatile.Read(ref captures));
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref maximum);
            if (candidate <= current) return;
            if (Interlocked.CompareExchange(ref maximum, candidate, current) == current) return;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!predicate() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(predicate());
    }
}
