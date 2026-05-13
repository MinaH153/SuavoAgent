using System.Collections.Concurrent;

namespace SuavoAgent.Diagnostics;

/// Per-component token-bucket rate limiter. Spec §4 contract: 10 events/sec
/// per component. Excess events still local-journal (audit trail intact)
/// but the Sentry POST is dropped; <c>mesh.rate_limited_total</c>
/// counter increments and is surfaced via the next heartbeat.
public sealed class EmitBudget
{
    private readonly int _capacity;
    private readonly TimeSpan _refillInterval;
    private readonly ConcurrentDictionary<WireComponent, Bucket> _buckets = new();
    private long _rateLimitedTotal;

    public EmitBudget(int eventsPerSecond, TimeSpan? refillInterval = null)
    {
        _capacity = Math.Max(1, eventsPerSecond);
        _refillInterval = refillInterval ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>
    /// Returns true if a token was consumed (the Sentry POST may proceed).
    /// False = budget exhausted, drop the Sentry POST. Counter increments
    /// either way for visibility.
    /// </summary>
    public bool TryConsume(WireComponent component)
    {
        var bucket = _buckets.GetOrAdd(component, _ => new Bucket(_capacity, _refillInterval));
        var allowed = bucket.TryConsume();
        if (!allowed)
        {
            Interlocked.Increment(ref _rateLimitedTotal);
        }
        return allowed;
    }

    public long RateLimitedTotal => Interlocked.Read(ref _rateLimitedTotal);

    private sealed class Bucket
    {
        private readonly int _capacity;
        private readonly TimeSpan _refillInterval;
        private int _tokens;
        private DateTimeOffset _lastRefill;
        private readonly object _lock = new();

        public Bucket(int capacity, TimeSpan refillInterval)
        {
            _capacity = capacity;
            _refillInterval = refillInterval;
            _tokens = capacity;
            _lastRefill = DateTimeOffset.UtcNow;
        }

        public bool TryConsume()
        {
            lock (_lock)
            {
                Refill();
                if (_tokens <= 0) return false;
                _tokens--;
                return true;
            }
        }

        private void Refill()
        {
            var now = DateTimeOffset.UtcNow;
            var elapsed = now - _lastRefill;
            if (elapsed < _refillInterval) return;

            // How many full intervals have passed? Refill that many caps.
            var intervals = (int)(elapsed.Ticks / _refillInterval.Ticks);
            if (intervals <= 0) return;

            _tokens = Math.Min(_capacity, _tokens + (_capacity * intervals));
            _lastRefill = _lastRefill + TimeSpan.FromTicks(_refillInterval.Ticks * intervals);
        }
    }
}
