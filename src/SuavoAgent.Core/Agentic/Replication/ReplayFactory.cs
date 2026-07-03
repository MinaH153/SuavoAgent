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

    /// <summary>
    /// Composition root for the operator-approved FSD-engage path (<c>run_learned_template</c>): wires
    /// <see cref="GatedTemplateExecutor"/> to the SAME production perceiver / actuator / safety adapters as
    /// <see cref="Create"/> — same gate policy, no bypass, no parallel gate. The only difference from
    /// <see cref="Create"/> is the executor on top (click-family only, preflight + entry/drift/ExpectedAfter
    /// verify, dry-run-is-a-STOP). Same v1 synthetic-gate-state caveat (Helper CheckOrReject authoritative).
    /// </summary>
    public static GatedTemplateExecutor CreateExecutor(
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

        return new GatedTemplateExecutor(perceiver, actuator, safety);
    }
}
