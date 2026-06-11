using System.Text.Json;

namespace SuavoAgent.Contracts.Ipc;

/// <summary>
/// Core→Broker "relaunch the Helper" sentinel contract (mirrors the proven
/// <c>uninstall.request</c> pattern in <c>SuavoAgent.Broker.SelfUninstall</c>).
///
/// Why a sentinel file: Core runs as LocalService and cannot kill/relaunch a process in the
/// interactive user session; the Broker (LocalSystem) is the privileged component whose
/// 5-second watch loop already owns the Helper lifecycle. Core (it has Modify on
/// %ProgramData%\SuavoAgent) drops this file; the Broker consumes it exactly once, kills every
/// SuavoAgent.Helper process (freeing the single-instance command pipe a stranded/wedged
/// Helper holds), and its next watch tick relaunches a fresh Helper into the active console
/// session via the existing integrity-verified launch path.
///
/// Safety properties:
///   - Freshness-bounded: requests older than <see cref="MaxAge"/> are stale — the Broker
///     deletes them WITHOUT acting (a request written while the Broker was down must not fire
///     minutes later under a live pricing run it can no longer see).
///   - Consume-once: the Broker deletes the file BEFORE killing, so a crash mid-restart can
///     never replay the kill in a loop.
///   - Fail-closed: malformed/unparseable sentinel ⇒ delete + ignore, never act.
///   - No PHI: the payload is a reason code, a UTC timestamp, and an opaque command id.
/// </summary>
public static class HelperRestartRequest
{
    /// <summary>Requests older than this are stale and must be deleted unhonored. Also the
    /// window during which Core refuses to START a new pricing job (a restart is imminent).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);

    public const string FileName = "helper-restart.request";
    public const string ReceiptFileName = "helper-restart.receipt";

    /// <summary>Payload of one restart request. <paramref name="RequestedBy"/> is
    /// "operator" (signed restart_helper command) or "self_heal" (Core's strand detector).</summary>
    public sealed record Payload(DateTimeOffset RequestedAtUtc, string Reason, string RequestedBy, string? CommandId);

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SuavoAgent", FileName);

    public static string DefaultReceiptPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SuavoAgent", ReceiptFileName);

    /// <summary>
    /// Atomically writes the sentinel (temp + replace so the Broker can never read a torn
    /// file). Overwrites any pending request — two pending restarts collapse into one, which
    /// is the intended idempotency. Throws on I/O failure; callers surface it as a refused command.
    /// </summary>
    public static void Write(string path, Payload payload)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(payload));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Reads and validates a sentinel. Returns null when the file is absent, malformed, from
    /// the future (&gt; 1 min clock skew), or older than <see cref="MaxAge"/> — every failure
    /// mode maps to "do not act" (fail-closed). Does NOT delete; the consumer decides.
    /// </summary>
    public static Payload? TryRead(string path, DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var payload = JsonSerializer.Deserialize<Payload>(File.ReadAllText(path));
            return payload is not null && IsFresh(payload, now) ? payload : null;
        }
        catch
        {
            return null; // unreadable/torn/malformed ⇒ not a valid request
        }
    }

    /// <summary>Pure freshness rule, unit-testable: within MaxAge, allowing 1 min of forward clock skew.</summary>
    public static bool IsFresh(Payload payload, DateTimeOffset now) =>
        payload.RequestedAtUtc <= now + TimeSpan.FromMinutes(1) &&
        now - payload.RequestedAtUtc <= MaxAge;

    /// <summary>True when a FRESH request is pending — Core's pricing path uses this to refuse
    /// starting a job that a scheduled Helper kill would yank out from under.</summary>
    public static bool IsPending(string path, DateTimeOffset now) => TryRead(path, now) is not null;

    /// <summary>Best-effort delete (consume / discard-stale). Never throws.</summary>
    public static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best-effort */ }
    }
}
