using System;
using Microsoft.Extensions.DependencyInjection;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.ActionGrammarV1;
using SuavoAgent.Core.Agentic.Adapters;
using SuavoAgent.Core.Audit;
using SuavoAgent.Core.Autonomy;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.Mission;

namespace SuavoAgent.Core.Agentic.Replication;

/// <summary>
/// Composition root for a learned-template replay: wires <see cref="TemplateReplayer"/> to the same
/// production perceiver / actuator / safety adapters the NL loop uses (no reasoner — the template IS
/// the plan). Built per-run because the actuation envelope is run-scoped. Mirrors
/// <see cref="NavigateLoopFactory"/>; same v1 synthetic-gate-state caveat (Helper CheckOrReject is
/// authoritative at act time).
/// </summary>
public static class ReplayFactory
{
    public static TemplateReplayer Create(
        IServiceProvider services,
        NavigateSafetyOptions safetyOptions,
        MissionCharter charter,
        AuditChain audit,
        DateTimeOffset deadlineUtc)
    {
        var perceiver = new HelperPerceiver(services.GetRequiredService<IIpcCommandClient>());

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

        return new TemplateReplayer(perceiver, actuator, safety);
    }
}
