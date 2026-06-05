using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.ActionGrammarV1;
using SuavoAgent.Core.Agentic.Adapters;
using SuavoAgent.Core.Audit;
using SuavoAgent.Core.Autonomy;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.Mission;
using SuavoAgent.Core.Reasoning;

namespace SuavoAgent.Core.Agentic;

/// <summary>
/// Composition root for a navigate_app run: wires the pure <see cref="AgenticLoopRunner"/> to its
/// four production adapters, resolving their dependencies from the app container. Built per-run
/// because the actuation envelope (charter, audit chain, deadline) is run-scoped.
///
/// v1 gate-state note: Core has no IPC to read the live Helper actuation gate, so Preflight uses a
/// permissive synthetic state — it still enforces never-blind-on-live-PMS (config Core already has)
/// and the dry-run default, while the AUTHORITATIVE kill-switch / pause enforcement remains Helper-side
/// ActuationGate.CheckOrReject at act time (it rejects mid-run, so the loop sees Rejected and escalates).
/// A dedicated get_actuation_gate_state IPC for richer Core-side fail-fast preflight is a follow-up.
/// </summary>
public static class NavigateLoopFactory
{
    public static IAgenticLoopRunner Create(
        IServiceProvider services,
        NavigateSafetyOptions safetyOptions,
        MissionCharter charter,
        AuditChain audit,
        DateTimeOffset deadlineUtc)
    {
        var perceiver = new HelperPerceiver(services.GetRequiredService<IIpcCommandClient>());

        var reasoner = new TieredBrainReasoner(
            services.GetRequiredService<RuleEngine>(),
            services.GetRequiredService<ILocalInference>(),
            services.GetRequiredService<ActionVerifier>(),
            services.GetRequiredService<ICloudReasoning>(),
            services.GetRequiredService<ILogger<TieredBrain>>());

        var safety = new CompositeSafetyGate(
            gateState: () => new ActuationGateState(
                Enabled: true, DryRun: false, PausedUntilUtc: null, PauseReason: null, KillSwitchTrippedUtc: null),
            ledger: services.GetRequiredService<TaskAutonomyLedger>(),
            options: safetyOptions);

        var actuator = new VerbActuator(
            services.GetRequiredService<VerbRegistry>(),
            services.GetRequiredService<VerbDispatcher>(),
            services,
            envelope: _ => new ActuationEnvelope(charter, audit, Guid.NewGuid().ToString("n"), deadlineUtc));

        return new AgenticLoopRunner(perceiver, reasoner, actuator, safety);
    }
}
