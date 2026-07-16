using System;
using Microsoft.Extensions.DependencyInjection;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.ActionGrammarV1;
using SuavoAgent.Core.Agentic.Adapters;
using SuavoAgent.Core.Audit;
using SuavoAgent.Core.Autonomy;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.Mission;

namespace SuavoAgent.Core.Agentic;

/// <summary>
/// Composition root for a REPLAY-FIRST attempt (MOAT Increment 2): wires <see cref="ReplayFirstRunner"/>
/// to the SAME production ports <see cref="NavigateLoopFactory"/> gives the agentic loop for the same
/// command — perceiver (PMS cadence path, or the window-scoped sandbox path for explore), the
/// autonomy-graduated <see cref="CompositeSafetyGate"/> (or the handler's explicit explore-gate
/// override), and the <see cref="VerbActuator"/> under the run-scoped actuation envelope.
///
/// <para>This deliberately does NOT reuse <see cref="ReplaySkillFactory"/>: that one hardwires the
/// <see cref="SandboxExploreSafetyGate"/> (sandbox-allowlist-only), which is correct for the explicit
/// operator <c>replay_skill</c> command but would let a NAVIGATE replay bypass the M3 autonomy gate.
/// Replay is still actuation — preflight (kill-switch / pause / never-blind-on-live-PMS) AND earned
/// autonomy apply to replayed actions IDENTICALLY to reasoned ones (design risk #10: gate parity).</para>
///
/// <para>The caller supplies the Helper-reported actuation-gate snapshot read over authenticated IPC.
/// An unavailable snapshot fails preflight closed; Helper remains authoritative at act time.</para>
/// </summary>
public static class NavigateReplayFactory
{
    /// <param name="targetProcess">MUST mirror the loop the replay stands in for: <c>"sandbox"</c> for
    /// explore_sandbox (window-scoped capture of the launch-established window), null for navigate_app
    /// (PMS cadence path) — same rule as <see cref="NavigateLoopFactory"/>'s policyOptions switch.</param>
    /// <param name="safetyOverride">The handler's explore gate when replacing an explore_sandbox loop
    /// (same Codex Q3 invariant: constructed in the handler, never DI-registered). Null for navigate ⇒
    /// the autonomy-graduated CompositeSafetyGate, exactly like the loop.</param>
    public static ReplayFirstRunner Create(
        IServiceProvider services,
        NavigateSafetyOptions safetyOptions,
        MissionCharter charter,
        AuditChain audit,
        DateTimeOffset deadlineUtc,
        ActuationGateState? helperGateState,
        string? targetProcess = null,
        ISafetyGate? safetyOverride = null)
    {
        var perceiver = new HelperPerceiver(
            services.GetRequiredService<IIpcCommandClient>(),
            targetProcess: targetProcess);

        var safety = safetyOverride ?? new CompositeSafetyGate(
            gateState: () => helperGateState,
            ledger: services.GetRequiredService<TaskAutonomyLedger>(),
            options: safetyOptions);

        var actuator = new VerbActuator(
            services.GetRequiredService<VerbRegistry>(),
            services.GetRequiredService<VerbDispatcher>(),
            services,
            envelope: _ => new ActuationEnvelope(charter, audit, Guid.NewGuid().ToString("n"), deadlineUtc));

        return new ReplayFirstRunner(perceiver, actuator, safety);
    }
}
