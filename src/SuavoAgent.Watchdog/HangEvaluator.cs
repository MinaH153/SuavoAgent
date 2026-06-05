namespace SuavoAgent.Watchdog;

/// <summary>
/// A supervised process's liveness, as the Watchdog sees it. The SCM only reports
/// RUNNING / STOPPED — it cannot see a process that is alive-but-hung (deadlocked on an
/// IPC waiter or DB lock while the service host still reports RUNNING). To close that
/// silent-failure class, each long-running process emits a liveness beacon (a timestamp it
/// refreshes only while its main loop is actually servicing); the Watchdog reads the beacon
/// alongside the SCM state.
/// </summary>
/// <param name="ScmState">Raw service state from sc.exe queryex.</param>
/// <param name="LastBeaconAt">Most recent liveness beacon timestamp; null = none seen yet.</param>
/// <param name="Now">Current time.</param>
/// <param name="StaleThreshold">A RUNNING process whose beacon is older than this is hung.</param>
/// <param name="BeaconTrackingSince">When the Watchdog began expecting beacons for this process (startup grace).</param>
public readonly record struct LivenessSnapshot(
    ServiceState ScmState,
    DateTimeOffset? LastBeaconAt,
    DateTimeOffset Now,
    TimeSpan StaleThreshold,
    DateTimeOffset? BeaconTrackingSince);

/// <summary>Liveness verdict for a process the SCM reports as present.</summary>
public enum LivenessVerdict
{
    /// <summary>RUNNING and beacon fresh — genuinely serving.</summary>
    Live,
    /// <summary>RUNNING per SCM but the beacon is stale (or never arrived past grace) — deadlocked/hung.</summary>
    Hung,
    /// <summary>Not RUNNING — SCM-level supervision (WatchdogDecisionEngine) owns this case.</summary>
    NotRunning,
    /// <summary>RUNNING and within startup grace with no beacon yet — give it time, don't restart.</summary>
    BeaconPending,
}

/// <summary>
/// Pure detector for the alive-but-hung failure class. Only meaningful for a RUNNING process:
/// a stale (or never-arrived-past-grace) liveness beacon means the process is up to the SCM but
/// not actually doing work, so it must be force-cycled (stop+start), which a plain sc.exe start
/// against a RUNNING service would NOT do. Fail-safe: a missing beacon inside the startup-grace
/// window is BeaconPending (never a spurious restart of a slow starter).
/// </summary>
public static class HangEvaluator
{
    public static readonly TimeSpan DefaultStaleThreshold = TimeSpan.FromSeconds(60);

    public static LivenessVerdict Evaluate(LivenessSnapshot s)
    {
        if (s.ScmState != ServiceState.Running)
            return LivenessVerdict.NotRunning;

        if (s.LastBeaconAt is null)
        {
            // No beacon yet: tolerate it only within the startup-grace window.
            var trackingFor = s.BeaconTrackingSince is { } since ? s.Now - since : TimeSpan.Zero;
            return trackingFor <= s.StaleThreshold
                ? LivenessVerdict.BeaconPending
                : LivenessVerdict.Hung;
        }

        return s.Now - s.LastBeaconAt.Value > s.StaleThreshold
            ? LivenessVerdict.Hung
            : LivenessVerdict.Live;
    }
}
