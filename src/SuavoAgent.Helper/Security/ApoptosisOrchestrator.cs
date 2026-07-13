using SuavoAgent.Helper.Actuation;
using SuavoAgent.Contracts.Models;

namespace SuavoAgent.Helper.Security;

/// <summary>
/// Applies a corroboration verdict to the local actuation gate — the on-box half of the immune reflex.
///
///   Observe   → audit only; no gate change, no cloud signal (allowlisted/known-safe toucher).
///   Degrade   → REVERSIBLE: dry-run + disable. All actuation blocked, but the agent stays online and keeps
///               reading (pharmacy never goes blind); clears on the next Helper restart — no latch.
///   Apoptosis → FAIL-CLOSED + LATCHED: dry-run is set FIRST (the reversible window) THEN the kill switch
///               latches. Recovery needs a deliberate Helper restart.
///
/// Each rung records the compromise on the gate (<see cref="ActuationGate.RecordHoneytokenCompromise"/>) so
/// Core reads it via the actuation.get_state IPC and emits the PHI-free self-compromise heartbeat. The Tier-2
/// LLM flush is Core-side (the model runs in the Core process, not the Helper) — Core does it when it sees an
/// apoptosis-level compromise in the gate state.
/// </summary>
public sealed class ApoptosisOrchestrator
{
    private readonly ActuationGate _gate;

    public ApoptosisOrchestrator(ActuationGate gate)
        => _gate = gate ?? throw new ArgumentNullException(nameof(gate));

    public void OnCompromise(CorroborationResult result)
    {
        var reasonLabel = HoneytokenReasonLabels.Normalize(result.ReasonLabel);
        switch (result.Level)
        {
            case CorroborationLevel.Observe:
                break;

            case CorroborationLevel.Degrade:
                _gate.SetDryRun(true);
                _gate.SetEnabled(false, $"honeytoken_degrade:{reasonLabel}");
                _gate.RecordHoneytokenCompromise("degrade", reasonLabel);
                break;

            case CorroborationLevel.Apoptosis:
                _gate.SetDryRun(true); // reversible window first
                _gate.TripKillSwitch($"honeytoken_compromise:{reasonLabel}"); // then latch
                _gate.RecordHoneytokenCompromise("apoptosis", reasonLabel);
                break;
        }
    }
}
