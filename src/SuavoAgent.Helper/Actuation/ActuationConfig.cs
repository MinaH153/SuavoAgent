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

    public static ActuationConfig SafeDefault() => new()
    {
        Enabled = false,
        DryRun = true,
        UserInputPauseWindow = TimeSpan.FromMinutes(5),
        DefaultUiaTimeout = TimeSpan.FromSeconds(8),
        DefaultPerKeyDelayMs = 25,
        DefaultInterChordDelayMs = 80,
        RequireKillSwitchHotkey = true,
    };
}
