using System;
using System.Collections.Generic;
using System.Linq;
using SuavoAgent.Contracts.Ipc;

namespace SuavoAgent.Core.Agentic.Adapters;

/// <summary>
/// Precedence-1 safety gate used ONLY by the explore_sandbox command — the carve-out that lets the
/// <see cref="PhysarumActionPolicy"/> take REAL multi-step clicks in a $0-blast-radius sandbox app without
/// the autonomy-graduation gate (<see cref="CompositeSafetyGate"/>) dry-run-and-terminating the run on the
/// first click. Codex-reviewed 2026-06-08 (gpt-5.5, xhigh); the three findings are encoded here:
///
/// <list type="bullet">
/// <item><b>Q1 (verb restriction):</b> v1 Allows ONLY <c>click_by_label</c> / <c>click_by_signature</c> /
/// <c>launch_sandbox_app</c>. <c>type_into_field</c> / <c>press_keys</c> are DENIED — with a null Helper
/// <c>_activeTarget</c> they fall through to the current foreground window (keystroke-leak risk). Clicks
/// resolve in-process and don't share that path. Typing exploration unlocks once the Helper binds
/// type/press to the resolved target HWND and fails closed on a null target.</item>
/// <item><b>Q2 (residual, pre-existing):</b> a click resolves coordinates in the allowlisted process then
/// clicks raw screen coordinates with no HWND re-assertion. Mitigated operationally: explore_sandbox runs
/// only on operator-authorized non-PHI / canary boxes, and Preflight hard-denies a live-PMS posture.</item>
/// <item><b>Q3 (DI invariant):</b> this gate is NEVER registered as the shared <c>ISafetyGate</c>; it is
/// constructed only inside the explore_sandbox handler and passed as an explicit safety override. The
/// authoritative backstop remains the Helper-side allowlist + per-verb precondition.</item>
/// </list>
/// </summary>
public sealed class SandboxExploreSafetyGate : ISafetyGate
{
    // v1 explore actuation surface. Kept deliberately tiny + click-only (see Q1 above).
    private static readonly HashSet<string> AllowedExploreVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "click_by_label", "click_by_signature", "launch_sandbox_app",
    };

    private readonly Func<ActuationGateState?> _gateState;
    private readonly Func<DateTimeOffset> _now;

    public SandboxExploreSafetyGate(Func<ActuationGateState?> gateState, Func<DateTimeOffset>? now = null)
    {
        _gateState = gateState ?? throw new ArgumentNullException(nameof(gateState));
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Fail-closed on kill-switch / pause / unreadable gate state. Explore is sandbox-only by
    /// construction: every action is bound to the allowlist below, which admits no PMS process.</summary>
    public SafetyVerdict Preflight(AgentObjective objective)
    {
        var gs = _gateState();
        if (gs is null)
            return SafetyVerdict.Denied("gate_state_unavailable");
        if (!gs.Enabled || gs.KillSwitchTrippedUtc is not null)
            return SafetyVerdict.Denied("kill_switch");
        if (gs.CompromiseDetected)
            return SafetyVerdict.Denied("compromise_detected");
        if (gs.PausedUntilUtc is { } until && until > _now())
            return SafetyVerdict.Denied("paused_user_active");
        return SafetyVerdict.Allow;
    }

    public SafetyVerdict GateAction(NextAction action, AgentObjective objective)
    {
        // Terminal / non-actuating decisions are handled by the loop before acting; let them pass.
        if (action.Kind != NextActionKind.Act || action.Verb is not { } verb)
            return SafetyVerdict.Allow;

        if (!AllowedExploreVerbs.Contains(verb))
            return SafetyVerdict.Denied($"verb_not_allowed_in_explore:{verb}");

        if (TryTarget(action, out var target) &&
            ProtectedDesktopProcessClassifier.IsProtectedIdentity(target))
            return SafetyVerdict.Denied("protected_process_not_sandbox");

        if (!TargetIsAllowlisted(action))
            return SafetyVerdict.Denied("process_not_allowlisted");

        // Honor a Helper-forced dry-run even for an allowed action (matches CompositeSafetyGate semantics).
        return _gateState()?.DryRun == true ? SafetyVerdict.Dry("gate_dry_run") : SafetyVerdict.Allow;
    }

    public bool AssertScrubbed(PerceivedScreen screen) => screen.Scrubbed;

    /// <summary>The action's target process/app key must be in the actuation allowlist — the same set the
    /// Helper independently re-checks. launch uses <c>app_key</c> (e.g. "calculator"); clicks use
    /// <c>process_name</c> (e.g. "notepad" / "notepad.exe"). Matches either keys or values, case-insensitive.</summary>
    private static bool TargetIsAllowlisted(NextAction action)
    {
        if (!TryTarget(action, out var target)) return false;

        var allow = ActuationAllowlistedSandboxApps.ProcessNames;
        return allow.Keys.Concat(allow.Values).Any(a => string.Equals(a, target, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryTarget(NextAction action, out string target)
    {
        target = string.Empty;
        var paramKey = string.Equals(action.Verb, "launch_sandbox_app", StringComparison.OrdinalIgnoreCase)
            ? "app_key"
            : "process_name";
        if (action.Parameters is null ||
            !action.Parameters.TryGetValue(paramKey, out var raw) ||
            raw is not string value ||
            string.IsNullOrWhiteSpace(value))
            return false;
        target = value;
        return true;
    }
}
