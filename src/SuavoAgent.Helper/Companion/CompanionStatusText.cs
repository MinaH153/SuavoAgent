namespace SuavoAgent.Helper.Companion;

/// <summary>
/// Closed vocabulary for the companion. No runtime or user-provided value is
/// accepted here, so status copy cannot accidentally display PHI.
/// </summary>
public static class CompanionStatusText
{
    public static string Title(CompanionState state) => state switch
    {
        CompanionState.Watching => "Watching",
        CompanionState.Learning => "Learning",
        CompanionState.Working => "Working",
        CompanionState.Paused => "Paused",
        CompanionState.NeedsAttention => "Needs attention",
        CompanionState.Offline => "Offline",
        _ => "Offline",
    };

    public static string Status(CompanionState state, bool dryRun) => state switch
    {
        CompanionState.Watching
            => "Watching this workstation. Autopilot acts only on an approved task.",
        CompanionState.Learning
            => "Learning workflow structure locally. Autopilot is paused while you work.",
        CompanionState.Working when dryRun
            => "Rehearsing an approved action. No keyboard or mouse input is being sent.",
        CompanionState.Working
            => "Autopilot is working now. Move the mouse or use Stop to interrupt it.",
        CompanionState.Paused
            => "Observation continues. Autopilot will not click or type.",
        CompanionState.NeedsAttention
            => "Autopilot is stopped. Open Suavo to review before restarting.",
        CompanionState.Offline
            => "SuavoAgent is offline. Autopilot cannot act.",
        _ => "SuavoAgent is offline. Autopilot cannot act.",
    };
}
