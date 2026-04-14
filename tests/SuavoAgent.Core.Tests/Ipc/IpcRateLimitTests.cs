using SuavoAgent.Core.Ipc;
using Xunit;

namespace SuavoAgent.Core.Tests.Ipc;

public class IpcRateLimitTests
{
    [Fact]
    public void TryAcquire_AllowsEventsUnderLimit()
    {
        var limiter = new EventRateLimiter(maxEventsPerSecond: 10);

        for (int i = 0; i < 10; i++)
            Assert.True(limiter.TryAcquire());

        Assert.Equal(0, limiter.DroppedTotal);
    }

    [Fact]
    public void TryAcquire_RejectsEventsOverLimit()
    {
        var limiter = new EventRateLimiter(maxEventsPerSecond: 5);

        for (int i = 0; i < 5; i++)
            Assert.True(limiter.TryAcquire());

        Assert.False(limiter.TryAcquire());
        Assert.False(limiter.TryAcquire());
    }

    [Fact]
    public void DroppedTotal_IncrementsOnRejection()
    {
        var limiter = new EventRateLimiter(maxEventsPerSecond: 3);

        for (int i = 0; i < 3; i++)
            limiter.TryAcquire();

        limiter.TryAcquire(); // rejected 1
        limiter.TryAcquire(); // rejected 2
        limiter.TryAcquire(); // rejected 3

        Assert.Equal(3, limiter.DroppedTotal);
    }

    [Fact]
    public void ResetWindow_AllowsNewEvents()
    {
        var limiter = new EventRateLimiter(maxEventsPerSecond: 2);

        Assert.True(limiter.TryAcquire());
        Assert.True(limiter.TryAcquire());
        Assert.False(limiter.TryAcquire());

        limiter.ResetWindow();

        Assert.True(limiter.TryAcquire());
        Assert.True(limiter.TryAcquire());
        Assert.False(limiter.TryAcquire());
    }

    [Fact]
    public void CapBatchSize_ReturnsOriginalWhenUnderMax()
    {
        var items = new List<int> { 1, 2, 3 };
        var result = EventRateLimiter.CapBatchSize(items, 10);

        Assert.Same(items, result); // same reference, no copy
    }

    [Fact]
    public void CapBatchSize_CapsAtSpecifiedMax()
    {
        var items = Enumerable.Range(1, 500).ToList();
        var result = EventRateLimiter.CapBatchSize(items, 200);

        Assert.Equal(200, result.Count);
        Assert.Equal(1, result[0]);
        Assert.Equal(200, result[199]);
    }

    [Fact]
    public void CapBatchSize_ExactBoundary_ReturnsOriginal()
    {
        var items = Enumerable.Range(1, 200).ToList();
        var result = EventRateLimiter.CapBatchSize(items, 200);

        Assert.Same(items, result);
    }
}
