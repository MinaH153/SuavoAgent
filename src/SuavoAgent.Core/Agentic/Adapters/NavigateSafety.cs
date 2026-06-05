using System;
using System.Collections.Generic;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.Config;

namespace SuavoAgent.Core.Agentic.Adapters;

/// <summary>
/// Pure precedence-1 safety decisions for the navigate loop, extracted from the adapter so the
/// gating logic is unit-testable without live IPC / the autonomy DB. Every default fails closed:
/// when safety state can't be confirmed, refuse.
/// </summary>
public static class NavigateSafety
{
    /// <summary>Actuating verbs that change desktop state — these require the autonomy gate.</summary>
    private static readonly HashSet<string> DestructiveVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        // NB: click_by_signature (template replay) MUST be here — omitting it lets replay clicks
        // skip the M3 autonomy gate (Codex 2026-06-04 P1). Keep this in lock-step with every
        // actuating verb the compiler/reasoner can emit.
        "click_by_label", "click_by_signature", "type_into_field", "press_keys", "launch_sandbox_app",
    };

    public static bool IsDestructive(NextAction action)
        => action.Kind == NextActionKind.Act && action.Verb is { } v && DestructiveVerbs.Contains(v);

    /// <summary>
    /// Runs before perceive. Refuses the whole run when the kill-switch is tripped, the gate is
    /// disabled, the operator is active (pause window), the gate state can't be read (fail-closed),
    /// or a live PMS would be driven blind (never-blind-on-live-PMS) without an explicit enable.
    /// </summary>
    public static SafetyVerdict EvaluatePreflight(
        ActuationGateState? gateState,
        DateTimeOffset now,
        bool livePms,
        PricingExecutorMode executorMode,
        bool allowLiveActuation)
    {
        if (gateState is null)
            return SafetyVerdict.Denied("gate_state_unavailable");

        if (!gateState.Enabled || gateState.KillSwitchTrippedUtc is not null)
            return SafetyVerdict.Denied("kill_switch");

        if (gateState.PausedUntilUtc is { } until && until > now)
            return SafetyVerdict.Denied("paused_user_active");

        if (livePms && executorMode == PricingExecutorMode.UiaFirst && !allowLiveActuation)
            return SafetyVerdict.Denied("never_blind_live_pms");

        return SafetyVerdict.Allow;
    }

    /// <summary>
    /// Runs before every act. A non-destructive action passes. A destructive action runs for real
    /// only when autonomy is earned AND enabled for this (task, pharmacy); otherwise it is dispatched
    /// dry-run (audited, not executed) and the run escalates for operator approval. If the gate forces
    /// dry-run, even an authorized action runs dry-run.
    /// </summary>
    public static SafetyVerdict EvaluateGateAction(NextAction action, bool mayRunUnsupervised, bool gateForcesDryRun)
    {
        if (!IsDestructive(action))
            return SafetyVerdict.Allow;

        if (mayRunUnsupervised)
            return gateForcesDryRun ? SafetyVerdict.Dry("gate_dry_run") : SafetyVerdict.Allow;

        return SafetyVerdict.Dry("supervised_autonomy_not_earned");
    }
}
