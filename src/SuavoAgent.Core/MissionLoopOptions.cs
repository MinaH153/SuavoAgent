namespace SuavoAgent.Core;

/// <summary>
/// Bootstrap-only toggle for the Mission Loop pipeline. Kept deliberately
/// flat so the cloud config-push pipeline (ConfigOverrideStore +
/// ConfigSyncWorker) can flip <c>MissionLoop.Phase1.Enabled</c> with a
/// single JSON key replacement without mutating the whole options tree.
///
/// Default is off — the pilot-flip skill is the only approved path to
/// enable Phase 1 against a real pharmacy. See
/// <c>~/.claude/projects/.../memory/pilot-flip-staging-rehearsal-runbook-2026-04-24.md</c>
/// for the 8-gate rehearsal that must precede the flip on production.
/// </summary>
public sealed class MissionLoopOptions
{
    public MissionLoopPhase1Options Phase1 { get; set; } = new();
}

public sealed class MissionLoopPhase1Options
{
    /// <summary>
    /// When true, <c>Program.cs</c> calls
    /// <see cref="SuavoAgent.Core.Mission.MissionLoopPhase1Registration.AddMissionLoopPhase1"/>.
    /// When false, Mission Loop services are never registered — consumers
    /// resolving them fail fast instead of silently hitting a half-wired
    /// pipeline.
    /// </summary>
    public bool Enabled { get; set; }
}
