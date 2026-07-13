using SuavoAgent.Helper.Presence;

namespace SuavoAgent.Helper.Companion;

/// <summary>
/// The six states the workstation companion is allowed to claim. These are
/// deliberately operational, not decorative: every non-idle state must be
/// backed by a live Helper signal.
/// </summary>
public enum CompanionState
{
    Watching,
    Learning,
    Working,
    Paused,
    NeedsAttention,
    Offline,
}

/// <summary>PHI-free inputs used to derive the visible companion state.</summary>
public sealed record CompanionSignals(
    bool CoreConnected,
    bool ObservationActive,
    bool LearningActive,
    PresenceMode PresenceMode,
    bool ActuationEnabled,
    bool DryRun,
    bool ActuationPaused,
    bool KillSwitchTripped,
    bool CompromiseDetected,
    bool ControlSynchronized = true);

/// <summary>
/// Complete, fixed-copy view model for the native overlay. It intentionally
/// contains no workflow labels, window titles, patient data, or error text.
/// </summary>
public sealed record CompanionPresentation(
    CompanionState State,
    string Title,
    string Status,
    bool CanPause,
    bool CanResume,
    bool CanStop);

/// <summary>Pure priority mapping from real Helper signals to visible state.</summary>
public static class CompanionStateLogic
{
    public static CompanionPresentation Evaluate(CompanionSignals signals)
    {
        ArgumentNullException.ThrowIfNull(signals);

        var state = signals switch
        {
            // A locally stopped or compromised actuator must remain prominent,
            // even if its cloud connection is also down.
            { KillSwitchTripped: true } or { CompromiseDetected: true }
                => CompanionState.NeedsAttention,

            // "Offline" means the Helper cannot prove both its Core link and
            // its observation runtime. It never pretends to watch while blind.
            { CoreConnected: false } or { ObservationActive: false }
                => CompanionState.Offline,

            // A local control is not terminal until Core acknowledges the
            // same state. Helper remains fail-closed, but the UI must expose
            // the split state instead of claiming an ordinary pause.
            { ControlSynchronized: false }
                => CompanionState.NeedsAttention,

            // Human input only means Learning while the PHI-minimized
            // behavioral observers are genuinely attached and collecting.
            { LearningActive: true, PresenceMode: PresenceMode.Observing }
                => CompanionState.Learning,

            // A disabled or temporarily paused gate cannot click or type.
            { ActuationEnabled: false } or { ActuationPaused: true }
                => CompanionState.Paused,

            // Actual agent cursor activity is the only Working signal, and a
            // newly closed gate takes precedence over its short activity tail.
            { PresenceMode: PresenceMode.Driving }
                => CompanionState.Working,

            _ => CompanionState.Watching,
        };

        var canResume = signals.ActuationEnabled
            && signals.ActuationPaused
            && !signals.KillSwitchTripped
            && !signals.CompromiseDetected;
        var canPause = signals.ActuationEnabled
            && !signals.ActuationPaused
            && !signals.KillSwitchTripped
            && !signals.CompromiseDetected;

        return new CompanionPresentation(
            state,
            CompanionStatusText.Title(state),
            CompanionStatusText.Status(state, signals.DryRun),
            CanPause: canPause,
            CanResume: canResume,
            CanStop: !signals.KillSwitchTripped);
    }
}
