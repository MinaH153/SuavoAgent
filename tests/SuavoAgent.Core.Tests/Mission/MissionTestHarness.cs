using Microsoft.Extensions.DependencyInjection;
using SuavoAgent.Core.ActionGrammarV1;
using SuavoAgent.Core.ActionGrammarV1.Policy;
using SuavoAgent.Core.ActionGrammarV1.Verbs.LookupPatient;
using SuavoAgent.Core.ActionGrammarV1.Verbs.QueryTopNdcs;
using SuavoAgent.Core.Adapters;
using SuavoAgent.Core.Audit;
using SuavoAgent.Core.Mission;

namespace SuavoAgent.Core.Tests.Mission;

/// <summary>
/// Shared wiring for Mission-Loop Phase-1 tests. Owns the DI container so
/// every test gets a fresh service provider — no cross-test contamination.
/// </summary>
internal sealed class MissionTestHarness : IDisposable
{
    public ServiceProvider Provider { get; }
    public MockPharmacyReadAdapter Adapter { get; }
    public AuditChain Audit => Provider.GetRequiredService<AuditChain>();
    public VerbDispatcher Dispatcher => Provider.GetRequiredService<VerbDispatcher>();
    public IMissionPlanner Planner => Provider.GetRequiredService<IMissionPlanner>();
    public MissionExecutor Executor => Provider.GetRequiredService<MissionExecutor>();
    public MissionEvaluator Evaluator => Provider.GetRequiredService<MissionEvaluator>();

    public MissionTestHarness(MockPharmacyReadAdapter? adapter = null)
    {
        var services = new ServiceCollection();
        services.AddMissionLoopPhase1();
        Adapter = adapter ?? new MockPharmacyReadAdapter();
        services.AddSingleton<IPharmacyReadAdapter>(Adapter);
        Provider = services.BuildServiceProvider();
    }

    public MissionCharter Charter(string pharmacyId = "pharm-test-001") =>
        MissionCharterLoader.BuildDefaultCharter(pharmacyId);

    public VerbContext Context(
        IVerb verb,
        IReadOnlyDictionary<string, object?> parameters,
        string pharmacyId = "pharm-test-001",
        string actor = "test-operator")
    {
        return new VerbContext(
            PharmacyId: pharmacyId,
            Charter: Charter(pharmacyId),
            Audit: Audit,
            InvocationId: Guid.NewGuid().ToString("D"),
            Actor: actor,
            Parameters: parameters,
            Services: Provider,
            DeadlineUtc: DateTimeOffset.UtcNow.AddMinutes(5));
    }

    public IVerb Verb<T>() where T : IVerb =>
        Provider.GetServices<IVerb>().OfType<T>().First();

    public void Dispose() => Provider.Dispose();
}
