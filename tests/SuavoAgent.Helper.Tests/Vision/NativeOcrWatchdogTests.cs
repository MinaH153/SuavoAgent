using SuavoAgent.Helper.Vision;
using Xunit;

namespace SuavoAgent.Helper.Tests.Vision;

public sealed class NativeOcrWatchdogTests
{
    [Fact]
    public async Task Completed_native_operation_returns_without_fail_stop()
    {
        var stopped = false;

        var result = await NativeOcrWatchdog.RunAsync(
            () => 42,
            TimeSpan.FromSeconds(1),
            CancellationToken.None,
            () => stopped = true);

        Assert.Equal(42, result);
        Assert.False(stopped);
    }

    [Fact]
    public async Task Timeout_invokes_fail_stop_before_any_result_can_return()
    {
        var stopped = 0;
        var operationCompleted = 0;
        var failStopObservedLiveOperation = 0;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var watchdog = NativeOcrWatchdog.RunAsync(
            () =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
                Interlocked.Exchange(ref operationCompleted, 1);
                return 42;
            },
            TimeSpan.FromMilliseconds(250),
            CancellationToken.None,
            () =>
            {
                if (Volatile.Read(ref operationCompleted) == 0)
                    Interlocked.Exchange(ref failStopObservedLiveOperation, 1);
                Interlocked.Increment(ref stopped);
            });

        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        try
        {
            await Assert.ThrowsAsync<NativeOcrTimeoutException>(() => watchdog);
        }
        finally
        {
            release.Set();
        }

        Assert.Equal(1, stopped);
        Assert.Equal(1, failStopObservedLiveOperation);
    }

    [Fact]
    public async Task Timeout_watchdog_is_not_starved_by_synchronous_native_work()
    {
        var stopped = 0;
        using var release = new ManualResetEventSlim();

        await Assert.ThrowsAsync<NativeOcrTimeoutException>(() =>
            NativeOcrWatchdog.RunAsync(
                () =>
                {
                    release.Wait(TimeSpan.FromSeconds(5));
                    return 42;
                },
                TimeSpan.FromMilliseconds(100),
                CancellationToken.None,
                () =>
                {
                    Interlocked.Increment(ref stopped);
                    release.Set();
                }));

        Assert.Equal(1, stopped);
    }

    [Fact]
    public async Task Cancellation_after_native_entry_also_requires_fail_stop()
    {
        var stopped = 0;
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(15));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NativeOcrWatchdog.RunAsync(
                () =>
                {
                    Thread.Sleep(150);
                    return 42;
                },
                TimeSpan.FromSeconds(1),
                cancellation.Token,
                () => Interlocked.Increment(ref stopped)));

        Assert.Equal(1, stopped);
    }

    [Fact]
    public async Task Already_cancelled_request_never_enters_native_or_fail_stop()
    {
        var entered = false;
        var stopped = false;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NativeOcrWatchdog.RunAsync(
                () =>
                {
                    entered = true;
                    return 42;
                },
                TimeSpan.FromSeconds(1),
                cancellation.Token,
                () => stopped = true));

        Assert.False(entered);
        Assert.False(stopped);
    }

    [Fact]
    public async Task Native_exception_propagates_without_misclassifying_as_timeout()
    {
        var stopped = false;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NativeOcrWatchdog.RunAsync<int>(
                () => throw new InvalidDataException("native failure"),
                TimeSpan.FromSeconds(1),
                CancellationToken.None,
                () => stopped = true));

        Assert.False(stopped);
    }
}
