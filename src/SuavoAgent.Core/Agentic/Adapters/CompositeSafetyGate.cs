using System;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.Autonomy;
using SuavoAgent.Core.Config;

namespace SuavoAgent.Core.Agentic.Adapters;

/// <summary>Deployment safety posture for a navigate run.</summary>
/// <param name="EnableTaskAutonomy">Master switch — false ⇒ every destructive action stays supervised.</param>
/// <param name="LivePms">True when the foreground target is a live PMS (never-blind hazard applies).</param>
/// <param name="ExecutorMode">SqlFirst (read-only, safe) vs UiaFirst (drives the live desktop).</param>
/// <param name="AllowLiveActuation">Explicit opt-in to drive a live PMS via UiaFirst.</param>
public sealed record NavigateSafetyOptions(
    bool EnableTaskAutonomy,
    bool LivePms,
    PricingExecutorMode ExecutorMode,
    bool AllowLiveActuation);

/// <summary>
/// Production <see cref="ISafetyGate"/>. Composes the Helper-reported actuation gate state
/// (kill-switch / pause / dry-run, read over IPC), the M3 <see cref="TaskAutonomyLedger"/>
/// (per-task earned autonomy), and the never-blind-on-live-PMS posture. The Core-side reads are
/// fail-fast; the authoritative enforcement remains Helper-side ActuationGate.CheckOrReject at act
/// time. All decision logic lives in pure <see cref="NavigateSafety"/>.
/// </summary>
public sealed class CompositeSafetyGate : ISafetyGate
{
    private readonly Func<ActuationGateState?> _gateState;
    private readonly TaskAutonomyLedger _ledger;
    private readonly NavigateSafetyOptions _options;
    private readonly Func<DateTimeOffset> _now;

    public CompositeSafetyGate(
        Func<ActuationGateState?> gateState,
        TaskAutonomyLedger ledger,
        NavigateSafetyOptions options,
        Func<DateTimeOffset>? now = null)
    {
        _gateState = gateState ?? throw new ArgumentNullException(nameof(gateState));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public SafetyVerdict Preflight(AgentObjective objective)
        => NavigateSafety.EvaluatePreflight(
            _gateState(), _now(), _options.LivePms, _options.ExecutorMode, _options.AllowLiveActuation);

    public SafetyVerdict GateAction(NextAction action, AgentObjective objective)
    {
        // Only consult the autonomy ledger for destructive actions (avoid a DB read otherwise).
        var mayRunUnsupervised = NavigateSafety.IsDestructive(action)
            && _ledger.MayRunUnsupervised(objective.TaskKey, objective.PharmacyId, _options.EnableTaskAutonomy);

        return NavigateSafety.EvaluateGateAction(action, mayRunUnsupervised, _gateState()?.DryRun ?? false);
    }

    public bool AssertScrubbed(PerceivedScreen screen) => screen.Scrubbed;
}
