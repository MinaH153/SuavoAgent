using Microsoft.Extensions.DependencyInjection;
using SuavoAgent.Core.Audit;

namespace SuavoAgent.Core.Mission;

/// <summary>
/// DI registration helper for the Mission Loop Phase 1 scaffolding.
///
/// Intentionally NOT called from <c>Program.cs</c>. Call sites land
/// post-Nadim pilot once Phase A items A1/A2 close and the
/// <c>mission-loop.phase1.enabled</c> config gate flips.
/// </summary>
public static class MissionLoopPhase1Registration
{
    /// <summary>
    /// Register <see cref="MissionCharterLoader"/> and <see cref="AuditChain"/>
    /// as singletons. Idempotent: callers may invoke multiple times without
    /// duplicate registration side effects.
    /// </summary>
    public static IServiceCollection AddMissionLoopPhase1Stubs(this IServiceCollection services)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        services.AddSingleton<MissionCharterLoader>();
        services.AddSingleton<AuditChain>();
        return services;
    }
}
