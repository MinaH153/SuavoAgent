using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SuavoAgent.Core.Agentic;

/// <summary>
/// On-demand scrubbed perception. Production impl wraps the Core→Helper
/// capture_screen IPC → ScreenCaptureController.CaptureAndExtractAsync, which is
/// already PHI-scrubbed at the factory. Returns null on capture failure (fail-closed);
/// the loop treats null as a no-perception strike, never as "nothing on screen".
/// </summary>
public interface IPerceiver
{
    Task<PerceivedScreen?> PerceiveAsync(CancellationToken ct);
}

/// <summary>What the reasoner produced for one reason step, plus cloud-budget accounting.</summary>
/// <param name="Action">The single grounded action (or Done/Escalate).</param>
/// <param name="UsedCloud">True if this call consumed a cloud reasoning round-trip.</param>
/// <param name="CloudDenied">
/// True if the reasoner needed cloud but was told allowCloud=false (sub-budget spent)
/// and could not decide locally. The loop terminates BudgetExhausted on this.
/// </param>
public sealed record ReasonResult(NextAction Action, bool UsedCloud, bool CloudDenied = false);

/// <summary>Verdict of a postcondition check, plus cloud-budget accounting.</summary>
public sealed record VerifyResult(PostconditionVerdict Verdict, bool UsedCloud, bool CloudDenied = false);

/// <summary>
/// Grounded single-action chooser + postcondition checker. Production impl wraps
/// VisionContextEnricher.Enrich + TieredBrain.DecideAsync (with the new userObjective)
/// and maps a single-actuating-action BrainDecision into a NextAction. Never throws —
/// a Tier-miss / undecidable maps to NextAction.Escalate.
/// </summary>
public interface IReasoner
{
    Task<ReasonResult> ReasonNextAsync(
        AgentObjective objective,
        WorkingMemory memory,
        IReadOnlySet<string> allowedActions,
        bool allowCloud,
        CancellationToken ct);

    Task<VerifyResult> VerifyPostconditionAsync(
        AgentObjective objective,
        NextAction lastAction,
        PerceivedScreen before,
        PerceivedScreen after,
        bool allowCloud,
        CancellationToken ct);
}

/// <summary>
/// Executes exactly ONE action. Production impl resolves the verb via VerbRegistry
/// and calls VerbDispatcher.DispatchAsync (the 7-step fail-closed flow → IActuationGateway
/// → Helper under ActuationGate.CheckOrReject — the authoritative kill/pause/disabled gate).
/// </summary>
public interface IActuator
{
    Task<ActOutcome> ActAsync(NextAction action, ActuationContext ctx, CancellationToken ct);
}

/// <summary>
/// Precedence-1 gate. Preflight runs before perceive (kill-switch / pause-window /
/// never-blind-on-live-PMS). GateAction runs before EVERY act (action-class whitelist +
/// TaskAutonomyLedger.MayRunUnsupervised). AssertScrubbed is a structural egress invariant.
/// The Core-side reads are fail-fast only; the authoritative enforcement is Helper-side
/// ActuationGate.CheckOrReject at act time.
/// </summary>
public interface ISafetyGate
{
    SafetyVerdict Preflight(AgentObjective objective);
    SafetyVerdict GateAction(NextAction action, AgentObjective objective);
    bool AssertScrubbed(PerceivedScreen screen);
}

/// <summary>The pure orchestrator entry point. Owns the loop, budget, accumulation, and termination.</summary>
public interface IAgenticLoopRunner
{
    Task<AgenticLoopResult> RunAsync(AgentObjective objective, AgenticLoopOptions options, CancellationToken ct);
}
