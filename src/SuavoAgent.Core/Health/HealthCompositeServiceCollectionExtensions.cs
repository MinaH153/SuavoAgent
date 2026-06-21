using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;

namespace SuavoAgent.Core.Health;

/// <summary>
/// Registers the <c>agent.health_composite</c> emission stack. These classes
/// were built + unit-tested (Wave 1B Track 1.4) but never registered, so in
/// production <see cref="SuavoAgent.Core.Workers.HeartbeatWorker"/> resolved
/// <see cref="IHealthSignals"/> / <see cref="HealthCompositeCalculator"/> as
/// null and emitted ZERO composites — leaving the cloud agent-health-watch
/// alarm blind to a silently-degraded box (e.g. a dead Helper behind a green
/// heartbeat). Call this AFTER <c>IpcPipeServer</c>, <c>RxDetectionWorker</c>,
/// and <c>AgentStateDb</c> are registered.
/// </summary>
public static class HealthCompositeServiceCollectionExtensions
{
    public static IServiceCollection AddHealthComposite(this IServiceCollection services)
    {
        services.AddSingleton<IBusinessHoursProvider, DefaultBusinessHoursProvider>();

        services.AddSingleton(sp =>
            new HealthCompositeCalculator(sp.GetRequiredService<IBusinessHoursProvider>()));

        services.AddSingleton<IHealthSignals>(sp =>
        {
            var db = sp.GetRequiredService<AgentStateDb>();
            var pharmacyId = sp.GetRequiredService<IOptions<AgentOptions>>().Value.PharmacyId ?? "";
            // Schema canary is "green" when there is no active drift-hold for the
            // PioneerRx adapter (GetCanaryHold returns null). The calculator wraps
            // each signal read in try/catch, so a throw here degrades to false
            // (conservative) without breaking the heartbeat critical path.
            return new HealthSignalsProvider(
                sp.GetRequiredService<IpcPipeServer>(),
                sp.GetRequiredService<RxDetectionWorker>(),
                () => db.GetCanaryHold(pharmacyId, "pioneerrx") is null,
                // QA C2: command pipe is "healthy" unless the actuation prober has a CONCLUSIVE strand
                // (pipe connected but ping dead). Null tracker / no probe yet → healthy (don't false-flag
                // startup or a headless box; ConsecutiveStrandFailures already excludes the benign cases).
                () => (sp.GetService<ActuationReadinessTracker>()?.Current?.ConsecutiveStrandFailures ?? 0) == 0);
        });

        return services;
    }
}
