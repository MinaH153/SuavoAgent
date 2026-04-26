namespace SuavoAgent.Helper.Behavioral;

/// <summary>
/// Token-bucket rate limiter used to throttle UIA event emission. Trip A
/// 2026-04-25 hard-reset prevention — UiaInteractionObserver subscribes to
/// TreeScope.Subtree events on PioneerRx; under UIA2 marshalling that can
/// fire dozens-per-sec on busy pharmacist clicks and saturate CPU. This
/// limiter caps sustained throughput while still allowing brief bursts.
///
/// Thread-safe via a single lock on the bucket gate. Allocation-free in the
/// hot path (no LINQ, no boxing). Returns true and decrements when a token
/// is available; returns false otherwise. Refill happens lazily on each
/// acquire attempt so there's no background timer to clean up.
/// </summary>
public sealed class TokenBucketRateLimiter
{
    private readonly int _maxBurst;
    private readonly TimeSpan _refillInterval;
    private readonly Func<long> _ticksProvider;
    private readonly object _gate = new();

    private int _tokens;
    private long _lastRefillTicks;

    public TokenBucketRateLimiter(int maxBurst, TimeSpan refillInterval)
        : this(maxBurst, refillInterval, () => Environment.TickCount64) { }

    /// <summary>
    /// Test-friendly constructor — pass a deterministic ticks provider so
    /// time-dependent behaviour (refill cadence) is unit-testable without
    /// real wall clock.
    /// </summary>
    public TokenBucketRateLimiter(int maxBurst, TimeSpan refillInterval, Func<long> ticksProvider)
    {
        if (maxBurst < 1) throw new ArgumentOutOfRangeException(nameof(maxBurst), "maxBurst must be ≥ 1");
        if (refillInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(refillInterval));
        _maxBurst = maxBurst;
        _refillInterval = refillInterval;
        _ticksProvider = ticksProvider;
        _tokens = maxBurst;
        _lastRefillTicks = ticksProvider();
    }

    public int CurrentTokens
    {
        get { lock (_gate) return _tokens; }
    }

    public bool TryAcquire()
    {
        lock (_gate)
        {
            var now = _ticksProvider();
            var sinceRefill = now - _lastRefillTicks;
            if (sinceRefill >= _refillInterval.TotalMilliseconds)
            {
                var addTokens = (int)(sinceRefill / _refillInterval.TotalMilliseconds);
                _tokens = Math.Min(_maxBurst, _tokens + addTokens);
                _lastRefillTicks = now;
            }

            if (_tokens > 0)
            {
                _tokens--;
                return true;
            }
            return false;
        }
    }
}
