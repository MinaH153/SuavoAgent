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
    private static long _count;
    private static string? _lastReason;
    private static DateTimeOffset? _lastAt;
    private static readonly object _gate = new();

    public static long Count => Interlocked.Read(ref _count);

    public static string? LastReason
    {
        get { lock (_gate) return _lastReason; }
    }

    public static DateTimeOffset? LastAt
    {
        get { lock (_gate) return _lastAt; }
    }

    /// <summary>
    /// Increment the counter and capture the most recent rejection reason.
    /// Called from each fail-closed branch in <see cref="IpcPipeServer"/>.
    /// Reasons should be short, stable, low-cardinality strings — they end
    /// up in heartbeat payloads + dashboard surfaces.
    /// </summary>
    public static void Record(string reason)
    {
        Interlocked.Increment(ref _count);
        lock (_gate)
        {
            _lastReason = reason;
            _lastAt = DateTimeOffset.UtcNow;
        }
    }
}
