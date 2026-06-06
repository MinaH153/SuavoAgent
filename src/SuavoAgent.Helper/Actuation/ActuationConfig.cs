namespace SuavoAgent.Helper.Actuation;

/// <summary>
/// Helper-side actuation configuration. Driven by appsettings.json under the
/// <c>Actuation</c> section. The default is the safest reading: NOT enabled,
/// dry-run on, 5-min pause window. Operators flip explicitly per pharmacy.
///
/// Locked decisions (next-session-2026-05-03-track-5-actuation.md, 2026-05-02):
///   - DryRun on by default for first 2 weeks at every site
///   - Pharmacy-side opt-in is enforced cloud-side (consent column),
///     but the agent ALSO refuses if its own gate is off
///   - 5-min pause window after detected user input
/// </summary>
public sealed record ActuationConfig
{
    public bool Enabled { get; init; }
    public bool DryRun { get; init; } = true;
    public TimeSpan UserInputPauseWindow { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan DefaultUiaTimeout { get; init; } = TimeSpan.FromSeconds(8);
    public int DefaultPerKeyDelayMs { get; init; } = 25;
    public int DefaultInterChordDelayMs { get; init; } = 80;

    /// <summary>
    /// When true (default), failure to register the local Ctrl+Shift+Esc /
    /// Ctrl+Shift+F12 hotkey trips the gate immediately (fail-closed). The
    /// pre-build decision (locked 2026-05-02) requires both local hotkey AND
    /// dashboard ABORT for safety. When the agent runs in Session 0 (Windows
    /// service context — LocalService/NetworkService), RegisterHotKey fails
    /// because Session 0 has no interactive desktop. Sandbox-tier installs
    /// can flip this to false; cloud ABORT remains the kill path. Production
    /// installs should keep this true and fix the spawning flow so Helper
    /// runs in the user's interactive session.
    /// </summary>
    public bool RequireKillSwitchHotkey { get; init; } = true;

    /// <summary>
    /// When false, IpcCommandServer accepts cross-service-context Core
    /// connections even if it cannot read Core's process image path. The PID
    /// + ProcessName check still runs first; only the install-dir prefix
    /// validation is relaxed. Required when Helper runs as NetworkService
    /// (Broker context) trying to query a LocalService Core — OpenProcess
    /// returns null and Process.MainModule throws Access Denied. Default
    /// true for prod where all four services run as the same SID.
    /// </summary>
    public bool RelaxIpcClientPathValidation { get; init; } = false;

    /// <summary>
    /// Arms the honeytoken immune reflex (the decoy-file watcher → corroborate → apoptosis). DEFAULT FALSE
    /// and intentionally so: v1 ships with <c>NullFileAccessAttributor</c> (Windows FileSystemWatcher can't
    /// name the accessing process), so EVERY touch resolves to "unknown" → reversible Degrade, and a second
    /// distinct touch → Apoptosis. Without process attribution the system allowlist (SearchIndexer / Defender
    /// / Backup / OneDrive → Observe) can't fire, so a routine AV scan or backup WOULD trip a live pharmacy.
    /// The full ladder is built + unit-tested; keep this OFF until real PID resolution (ETW kernel-file-IO /
    /// handle enumeration) lands AND is validated on a box. Flipping this true before then risks a
    /// false-positive degrade/quarantine — precedence-1 never-brick.
    /// </summary>
    public bool HoneytokenReflexEnabled { get; init; }

    public static ActuationConfig SafeDefault() => new()
    {
        Enabled = false,
        DryRun = true,
        UserInputPauseWindow = TimeSpan.FromMinutes(5),
        DefaultUiaTimeout = TimeSpan.FromSeconds(8),
        DefaultPerKeyDelayMs = 25,
        DefaultInterChordDelayMs = 80,
        RequireKillSwitchHotkey = true,
        RelaxIpcClientPathValidation = false,
    };
}
