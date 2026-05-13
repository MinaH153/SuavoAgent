namespace SuavoAgent.Diagnostics;

/// Configuration for <see cref="Wire"/>. Owned by each entry point; passed
/// into <c>AttachUnhandledHooks</c> as the literal first statement of
/// Program.cs per spec §7 PR 4.
public sealed class WireOptions
{
    /// <summary>
    /// Existing %ProgramData%\SuavoAgent\logs\startup-crash.log path. Wire
    /// preserves this file fallback as defense-in-depth alongside the
    /// structured events.jsonl journal.
    /// </summary>
    public string LocalCrashLogPath { get; init; } = string.Empty;

    /// <summary>
    /// Structured local journal (events.jsonl) path. Default rotation:
    /// daily, 30-day retention. PHI-scrubbed before write.
    /// </summary>
    public string LocalJournalPath { get; init; } = string.Empty;

    /// <summary>
    /// Sentry DSN. Read from $env:SUAVO_SENTRY_DSN or appsettings
    /// "Diagnostics:Dsn". Empty → local-journal-only mode (mesh still
    /// computes fingerprints, just never POSTs).
    /// </summary>
    public string? Dsn { get; init; }

    /// <summary>
    /// Master toggle. False → Wire still scrubs + journals locally, but no
    /// Sentry transport. Default <c>true</c>; preflight (PR 1) warns if DSN
    /// missing but does NOT fail.
    /// </summary>
    public bool EnableSentry { get; init; } = true;

    /// <summary>
    /// Per-component emit budget in events/sec. Spec contract: 10/sec.
    /// Excess events still local-journal; Sentry POST is dropped and
    /// <c>mesh.rate_limited_total</c> counter increments.
    /// </summary>
    public int EmitBudgetPerSecond { get; init; } = 10;

    /// <summary>
    /// MeshHeartbeatWorker tick interval. Spec contract: 5 minutes through
    /// full Wire path. Phase A A1 alarm fires at &gt;30min absence.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Sentry HTTP client timeout (Codex-corrected: use ConfigureClient,
    /// not non-existent SentryOptions.SendTimeout). Default 2s.
    /// </summary>
    public TimeSpan SentryHttpTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// SocketsHttpHandler connect-timeout for Sentry transport. Spec
    /// chaos-test contract: 250ms so DNS/TCP failures never block the
    /// 50ms p99 local-first crash handler. Used in CreateHttpMessageHandler.
    /// </summary>
    public TimeSpan SentryConnectTimeout { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// PhiScrubber.Sanitize hard timeout. Spec contract: 10ms; on overrun,
    /// fail-closed (drop extras, keep fingerprint-only signal).
    /// </summary>
    public TimeSpan ScrubberTimeout { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// FingerprintComputer hard timeout. Spec contract: 10ms; on overrun,
    /// emit fp-fallback synthetic fingerprint with component+signal_kind.
    /// </summary>
    public TimeSpan FingerprintTimeout { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// LocalJournal.Append best-effort timeout. Spec contract: 20ms.
    /// </summary>
    public TimeSpan LocalJournalTimeout { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Synthetic environment tag — release / dev / staging. Sent to Sentry
    /// in the event payload.
    /// </summary>
    public string Environment { get; init; } = "release";
}
