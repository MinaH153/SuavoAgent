namespace SuavoAgent.Core.State;

/// <summary>
/// Live "what step am I on right now" for the currently-running task. Executors
/// (e.g. <see cref="SuavoAgent.Core.Pricing.SqlPricingJobRunner"/>) write the
/// current step; <c>HeartbeatWorker</c> reads it and ships it as the heartbeat's
/// <c>currentStep</c> so the cockpit can show a live sub-step under the working run.
///
/// PHI-safe by construction: the label is an OPERATIONAL phrase only (the cloud
/// also strips digits + non-safe chars defensively); progress rides the counts.
/// Thread-safe via a single volatile reference swap — the read path needs no lock.
/// </summary>
public sealed class AgentActivity
{
    /// <summary>A snapshot of the current step. <paramref name="Total"/> 0 = indeterminate.</summary>
    public sealed record StepSnapshot(string Label, int Done, int Total);

    private volatile StepSnapshot? _current;

    /// <summary>Set the current step. <paramref name="label"/> must be operational text (no PHI).</summary>
    public void Set(string label, int done = 0, int total = 0)
    {
        _current = new StepSnapshot(
            label ?? string.Empty,
            done < 0 ? 0 : done,
            total < 0 ? 0 : total);
    }

    /// <summary>Clear when the task finishes or the agent goes idle.</summary>
    public void Clear() => _current = null;

    /// <summary>The current step, or null when idle.</summary>
    public StepSnapshot? Current => _current;
}
