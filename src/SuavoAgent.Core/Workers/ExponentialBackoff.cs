using System;

namespace SuavoAgent.Core.Workers;

/// <summary>
/// Stateful exponential backoff for reconnect/retry loops. <see cref="NextDelay"/> returns the
/// delay for the current attempt and advances; <see cref="Reset"/> returns to the initial delay
/// (call after a successful connect). Capped to avoid runaway waits. Deterministic (no jitter) so
/// it's unit-testable; replaces fixed retry delays that would otherwise hammer a down dependency.
/// </summary>
public sealed class ExponentialBackoff
{
    private readonly TimeSpan _initial;
    private readonly TimeSpan _max;
    private readonly double _factor;
    private int _attempt;

    public ExponentialBackoff(TimeSpan initial, TimeSpan max, double factor = 2.0)
    {
        if (initial <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(initial));
        if (max < initial) throw new ArgumentOutOfRangeException(nameof(max));
        if (factor < 1.0) throw new ArgumentOutOfRangeException(nameof(factor));
        _initial = initial;
        _max = max;
        _factor = factor;
    }

    public int Attempt => _attempt;

    public TimeSpan NextDelay()
    {
        var seconds = _initial.TotalSeconds * Math.Pow(_factor, _attempt);
        // Stop advancing once we've reached the cap — the delay is clamped anyway, and this
        // avoids an unbounded _attempt (theoretical int overflow on a forever-down dependency).
        if (seconds < _max.TotalSeconds) _attempt++;
        return TimeSpan.FromSeconds(Math.Min(seconds, _max.TotalSeconds));
    }

    public void Reset() => _attempt = 0;
}
