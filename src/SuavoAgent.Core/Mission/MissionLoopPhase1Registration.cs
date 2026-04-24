using Microsoft.Extensions.DependencyInjection;
using SuavoAgent.Core.ActionGrammarV1;
using SuavoAgent.Core.ActionGrammarV1.Policy;
using SuavoAgent.Core.ActionGrammarV1.Verbs.LookupPatient;
using SuavoAgent.Core.ActionGrammarV1.Verbs.QueryTopNdcs;
using SuavoAgent.Core.Audit;

namespace SuavoAgent.Core.Mission;

/// <summary>
/// DI registration helper for the Mission Loop Phase 1 runtime.
///
/// Intentionally NOT called from <c>Program.cs</c>. Call sites land
/// post-Nadim pilot once Phase A items A1/A2 close and the
/// <c>mission-loop.phase1.enabled</c> config gate flips.
///
/// Callers MUST also register an <see cref="Adapters.IPharmacyReadAdapter"/>
/// appropriate for the target environment (production Pioneer-Rx /
/// Computer-Rx adapter, or a test fixture).
/// </summary>
public static class MissionLoopPhase1Registration
{
    /// <summary>
    /// Register the full Mission Loop Phase 1 pipeline: charter loader,
    /// audit chain, authz policy, verb dispatcher, verb catalogue, planner,
    /// executor, evaluator. Idempotent at the service-registration level —
    /// callers may invoke multiple times without side-effect duplication.
    /// </summary>
    public static IServiceCollection AddMissionLoopPhase1(this IServiceCollection services)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        services.AddSingleton<MissionCharterLoader>();
        services.AddSingleton<AuditChain>();

        services.AddSingleton<IAuthzPolicy, CharterDrivenAuthzPolicy>();
        services.AddSingleton<VerbDispatcher>();

        services.AddSingleton<IVerb, LookupPatientVerb>();
        services.AddSingleton<IVerb, QueryTopNdcsForPatientVerb>();

        services.AddSingleton<IMissionPlanner, RuleBasedMissionPlanner>();
        services.AddSingleton<MissionExecutor>();
        services.AddSingleton<MissionEvaluator>();

        return services;
    }

    /// <summary>
    /// Back-compat shim for callers built against the earlier scaffold. New
    /// code should call <see cref="AddMissionLoopPhase1"/>.
    /// </summary>
    public static IServiceCollection AddMissionLoopPhase1Stubs(this IServiceCollection services) =>
        AddMissionLoopPhase1(services);
}
