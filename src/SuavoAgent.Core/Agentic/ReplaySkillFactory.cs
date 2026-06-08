using System;
using Microsoft.Extensions.DependencyInjection;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.ActionGrammarV1;
using SuavoAgent.Core.Agentic.Adapters;
using SuavoAgent.Core.Audit;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.Mission;

namespace SuavoAgent.Core.Agentic;

/// <summary>
/// Composition root for a deterministic <see cref="VerifiedSkillReplayer"/> run: wires the same production
/// perceiver + actuator the agentic loop uses, plus the <see cref="SandboxExploreSafetyGate"/> — so a
/// replayed skill is confined exactly like the run that learned it (allowlisted sandbox process, live-PMS
/// hard-denied, kill/pause honored Helper-side). No reasoner: replay is deterministic by construction.
/// </summary>
public static class ReplaySkillFactory
{
    public static VerifiedSkillReplayer Create(
        IServiceProvider services,
        MissionCharter charter,
        AuditChain audit,
        DateTimeOffset deadlineUtc)
    {
        var perceiver = new HelperPerceiver(services.GetRequiredService<IIpcCommandClient>());

        // Core has no live-gate IPC, so a synthetic clean state (matches NavigateLoopFactory); the
        // authoritative kill/pause enforcement is Helper-side at act time, and the per-action allowlist
        // check is the structural confinement.
        var safety = new SandboxExploreSafetyGate(
            gateState: () => new ActuationGateState(
                Enabled: true, DryRun: false, PausedUntilUtc: null, PauseReason: null, KillSwitchTrippedUtc: null));

        var actuator = new VerbActuator(
            services.GetRequiredService<VerbRegistry>(),
            services.GetRequiredService<VerbDispatcher>(),
            services,
            envelope: _ => new ActuationEnvelope(charter, audit, Guid.NewGuid().ToString("n"), deadlineUtc));

        return new VerifiedSkillReplayer(perceiver, actuator, safety);
    }
}
