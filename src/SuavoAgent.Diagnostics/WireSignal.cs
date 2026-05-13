namespace SuavoAgent.Diagnostics;

/// Enum of signal kinds the mesh can fingerprint. Spec §3 canonical shape.
/// signal_kind is field 2 of fp-v1; covers Bug 22 (win32), Bug 23
/// (invariant_violation), Bug 24 (managed_exception) plus future hangs +
/// exit codes + unobserved tasks.
public enum WireSignalKind
{
    ManagedException,
    Win32,
    UnmanagedNative,
    InvariantViolation,
    UnobservedTask,
    ExitCode,
    Hang,
}

/// Component enum — field 1 of fp-v1. Fixed: Core / Broker / Helper /
/// Watchdog / Setup / Publish. Adding new components requires ruleset
/// version bump.
public enum WireComponent
{
    Core,
    Broker,
    Helper,
    Watchdog,
    Setup,
    Publish,
}

/// Representation of a signal as it traverses the Wire pipeline
/// (scrub → fingerprint → journal → Sentry). Public so FingerprintComputer
/// + tests can construct synthetic Bug 22/23/24 calibration signals
/// without going through Wire's full state machine.
public sealed class WireSignal
{
    public required WireComponent Component { get; init; }
    public required WireSignalKind Kind { get; init; }
    public Exception? Exception { get; init; }
    public string? InvariantId { get; init; }
    public int? ExitCode { get; init; }
    public string? Stage { get; init; }
    public Dictionary<string, string>? ExtraTags { get; init; }
    public DateTimeOffset OccurredAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// Internal sentinel exception used by Wire's recursion contract — see
/// §8.1 RESOLVED v1.0. A second-entry handler-self-failure path emits
/// this to mark the local-journal-only degraded marker without recursing.
internal sealed class WireRecursionSentinel : Exception
{
    public WireRecursionSentinel(string message) : base(message) { }
}
