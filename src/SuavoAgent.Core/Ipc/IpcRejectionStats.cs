namespace SuavoAgent.Core.Ipc;

/// <summary>
/// Thread-safe in-memory counter for IPC peer-validation rejections.
///
/// Trip A 2026-04-25 surfaced the gap this fixes: the IPC peer-validation
/// bug rejected every Helper connection silently — Core kept heartbeating
/// "online" while every Helper observation was dropped, and capture
/// counters stayed at 0. The cloud had no signal that anything was wrong.
///
/// HeartbeatWorker reads <see cref="Count"/>, <see cref="LastReason"/>,
/// and <see cref="LastAt"/> on every heartbeat and ships them in the
/// payload's <c>helper</c> object so the dashboard can flag silent
/// IPC failure remotely.
///
/// Counter resets on Core restart — a steadily-growing count between
/// restarts is the signal of interest, not the absolute number.
/// </summary>
public static class IpcRejectionStats
{
    // All three fields live under the same lock so the heartbeat snapshot is
    // atomic. Codex flagged the prior split (count via Interlocked, reason
    // via lock) as a race: a heartbeat could read N=12 from one Record() and
    // reason="image_path_unreadable" from a different one. The new pattern
    // takes the lock for both Record() and Snapshot(), so the three values
    // ship together every time. Public Count/LastReason/LastAt accessors
    // remain for backward compat and read through the lock.
    private static long _count;
    private static string? _lastReason;
    private static DateTimeOffset? _lastAt;
    private static readonly object _gate = new();

    public static long Count
    {
        get { lock (_gate) return _count; }
    }

    public static string? LastReason
    {
        get { lock (_gate) return _lastReason; }
    }

    public static DateTimeOffset? LastAt
    {
        get { lock (_gate) return _lastAt; }
    }

    /// <summary>
    /// Atomic snapshot of all three telemetry fields. HeartbeatWorker calls
    /// this once per heartbeat assembly so count + reason + timestamp are
    /// consistent with each other. Tuple result is value-typed so callers
    /// can pattern-destructure without extra allocation.
    /// </summary>
    public static (long Count, string? LastReason, DateTimeOffset? LastAt) Snapshot()
    {
        lock (_gate)
        {
            return (_count, _lastReason, _lastAt);
        }
    }

    /// <summary>
    /// Increment the counter and capture the most recent rejection reason.
    /// Called from each fail-closed branch in <see cref="IpcPipeServer"/>.
    /// Reasons should be short, stable, low-cardinality strings — they end
    /// up in heartbeat payloads + dashboard surfaces. Callers must
    /// pre-sanitize unbounded inputs (e.g. process names) so the cardinality
    /// stays low.
    /// </summary>
    public static void Record(string reason)
    {
        lock (_gate)
        {
            _count++;
            _lastReason = reason;
            _lastAt = DateTimeOffset.UtcNow;
        }
    }
}
