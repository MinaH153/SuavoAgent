using SuavoAgent.Helper.Behavioral;
using Xunit;

namespace SuavoAgent.Helper.Tests;

// Trip A 2026-04-25 hard-reset prevention. UiaInteractionObserver wraps this
// limiter to throttle UIA event emission. Pin the contract so a future
// refactor can't silently weaken it.
public class TokenBucketRateLimiterTests
{
    [Fact]
    public void StartsWithBurstCapacity()
    {
        var clock = new FakeClock();
        var limiter = new TokenBucketRateLimiter(maxBurst: 5, refillInterval: TimeSpan.FromSeconds(1), clock.Now);
        Assert.Equal(5, limiter.CurrentTokens);
    }

    [Fact]
    public void TryAcquire_DrainsBurstThenRejects()
    {
        var clock = new FakeClock();
        var limiter = new TokenBucketRateLimiter(maxBurst: 3, refillInterval: TimeSpan.FromSeconds(1), clock.Now);

        Assert.True(limiter.TryAcquire());
        Assert.True(limiter.TryAcquire());
        Assert.True(limiter.TryAcquire());
        Assert.False(limiter.TryAcquire()); // empty
        Assert.False(limiter.TryAcquire());
        Assert.Equal(0, limiter.CurrentTokens);
    }

    [Fact]
    public void Refill_AddsOneTokenPerInterval()
    {
        var clock = new FakeClock();
        var limiter = new TokenBucketRateLimiter(maxBurst: 5, refillInterval: TimeSpan.FromMilliseconds(200), clock.Now);

        for (var i = 0; i < 5; i++) Assert.True(limiter.TryAcquire());
        Assert.False(limiter.TryAcquire());

        clock.Advance(TimeSpan.FromMilliseconds(200));
        Assert.True(limiter.TryAcquire());
        Assert.False(limiter.TryAcquire());
    }

    [Fact]
    public void Refill_NeverExceedsMaxBurst()
    {
        var clock = new FakeClock();
        var limiter = new TokenBucketRateLimiter(maxBurst: 5, refillInterval: TimeSpan.FromMilliseconds(100), clock.Now);

        // Drain
        for (var i = 0; i < 5; i++) Assert.True(limiter.TryAcquire());

        // Advance 10× the refill interval. Refill is lazy — happens on the
        // next TryAcquire, not when CurrentTokens is read. So drain again
        // immediately after the time-advance and verify we get exactly 5
        // tokens (capped at maxBurst, not 10).
        clock.Advance(TimeSpan.FromMilliseconds(1000));

        var refilledHits = 0;
        for (var i = 0; i < 10; i++)
        {
            if (limiter.TryAcquire()) refilledHits++;
        }
        Assert.Equal(5, refilledHits);
    }

    [Fact]
    public void Refill_PartialIntervalDoesNotAddTokens()
    {
        var clock = new FakeClock();
        var limiter = new TokenBucketRateLimiter(maxBurst: 5, refillInterval: TimeSpan.FromMilliseconds(200), clock.Now);

        for (var i = 0; i < 5; i++) Assert.True(limiter.TryAcquire());

        clock.Advance(TimeSpan.FromMilliseconds(199)); // just under one interval
        Assert.False(limiter.TryAcquire());

        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(limiter.TryAcquire());
    }

    [Fact]
    public void Constructor_RejectsBadInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TokenBucketRateLimiter(maxBurst: 0, refillInterval: TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TokenBucketRateLimiter(maxBurst: -1, refillInterval: TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TokenBucketRateLimiter(maxBurst: 5, refillInterval: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TokenBucketRateLimiter(maxBurst: 5, refillInterval: TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public void Sustained_RealisticUiaScenario_HoldsAt5HzPlusBurst()
    {
        // Default UiaInteractionObserver budget: burst=25, refill=200ms (5/sec).
        // Verify a 1-second sustained 100Hz storm gets capped at burst+5 emits.
        var clock = new FakeClock();
        var limiter = new TokenBucketRateLimiter(maxBurst: 25, refillInterval: TimeSpan.FromMilliseconds(200), clock.Now);

        var emitted = 0;
        for (var i = 0; i < 100; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(10));
            if (limiter.TryAcquire()) emitted++;
        }

        // 100 attempts over 1000ms with 200ms refill = 5 refills = up to
        // 25 burst + 5 = 30 emits maximum. Anything > 30 means refill is
        // double-counting; anything < 25 means burst is being undercredited.
        Assert.InRange(emitted, 25, 30);
    }
}

internal sealed class FakeClock
{
    private long _ticks;
    public long Now() => _ticks;
    public void Advance(TimeSpan delta) => _ticks += (long)delta.TotalMilliseconds;
}
