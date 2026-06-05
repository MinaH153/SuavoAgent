using System;

namespace SuavoAgent.Core.Agentic;

/// <summary>
/// Pure step-count + wall-clock + per-run cloud-call budget. No I/O — the clock is
/// injected so deadline behavior is deterministically testable. Three independent
/// limits, all fail-closed: the loop stops on whichever trips first.
/// </summary>
public sealed class LoopBudget
{
    private readonly int _maxSteps;
    private readonly int _maxCloud;
    private readonly TimeSpan? _deadline;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly DateTimeOffset _start;
    private int _steps;
    private int _cloud;

    public LoopBudget(int maxSteps, int maxCloud, TimeSpan? deadline, Func<DateTimeOffset> utcNow)
    {
        _maxSteps = maxSteps;
        _maxCloud = maxCloud;
        _deadline = deadline;
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _start = _utcNow();
    }

    public int StepsUsed => _steps;

    public int CloudUsed => _cloud;

    /// <summary>True while the per-run cloud sub-budget has room. False ⇒ local-only fallback.</summary>
    public bool CloudRemaining => _cloud < _maxCloud;

    /// <summary>Claim one step slot. Returns false once MaxSteps is reached (caller terminates BudgetExhausted).</summary>
    public bool TryStep()
    {
        if (_steps >= _maxSteps)
            return false;
        _steps++;
        return true;
    }

    /// <summary>Record that a cloud reasoning/verify round-trip was consumed this step.</summary>
    public void ConsumeCloud() => _cloud++;

    /// <summary>True once the wall-clock deadline (if any) has elapsed since construction.</summary>
    public bool DeadlinePassed() => _deadline is { } d && (_utcNow() - _start) >= d;
}
