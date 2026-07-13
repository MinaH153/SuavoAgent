using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.ActionGrammarV1;
using SuavoAgent.Core.ActionGrammarV1.Policy;
using SuavoAgent.Core.Agentic;
using SuavoAgent.Core.Agentic.Adapters;
using SuavoAgent.Core.Agentic.Replication;
using SuavoAgent.Core.Audit;
using SuavoAgent.Core.Autonomy;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.Mission;
using SuavoAgent.Core.Reasoning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Agentic;

/// <summary>
/// Regression coverage for the production composition roots. A unit test of the gate type alone cannot
/// detect a factory silently replacing unavailable Helper truth with a synthetic clean snapshot, which was
/// the original trust-boundary defect. These cases build every production Navigate/Replay factory and test
/// the exact safety instance it wires into the runner.
/// </summary>
public sealed class ProductionFactoryHelperGateTests : IDisposable
{
    private static readonly AgentObjective Objective =
        new("click Save", "factory-gate-task", "factory-gate-pharmacy");

    private static readonly NextAction AllowlistedClick = NextAction.Act(
        "click_by_label",
        new Dictionary<string, object?>
        {
            ["process_name"] = "calc.exe",
            ["label"] = "Save",
        });

    private readonly AgentStateDb _db = new(":memory:");
    private readonly ServiceProvider _services;

    public ProductionFactoryHelperGateTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IIpcCommandClient>(new NoOpIpcClient());
        services.AddSingleton(_db);
        services.AddSingleton(new TaskAutonomyLedger(_db, cleanRunsThreshold: 1));
        services.AddSingleton(new RuleEngine(
            Array.Empty<Rule>(),
            NullLogger<RuleEngine>.Instance));
        services.AddSingleton<ILocalInference>(new NullLocalInference());
        services.AddSingleton<ICloudReasoning>(new NullCloudReasoning());
        services.AddSingleton(new ActionVerifier());
        services.AddSingleton(VerbRegistry.Build(
            Array.Empty<Assembly>(),
            NullLogger<VerbRegistry>.Instance));
        services.AddSingleton(new VerbDispatcher(new CharterDrivenAuthzPolicy()));
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<TieredBrain>>(
            NullLogger<TieredBrain>.Instance);
        _services = services.BuildServiceProvider();
    }

    public static TheoryData<string, ActuationGateState?, string> ClosedSnapshots => new()
    {
        { "navigate_loop", null, "gate_state_unavailable" },
        { "navigate_replay", null, "gate_state_unavailable" },
        { "replay_skill", null, "gate_state_unavailable" },
        { "template_replay", null, "gate_state_unavailable" },
        { "gated_template_executor", null, "gate_state_unavailable" },

        { "navigate_loop", Closed(enabled: false), "kill_switch" },
        { "navigate_replay", Closed(enabled: false), "kill_switch" },
        { "replay_skill", Closed(enabled: false), "kill_switch" },
        { "template_replay", Closed(enabled: false), "kill_switch" },
        { "gated_template_executor", Closed(enabled: false), "kill_switch" },

        { "navigate_loop", Closed(killed: true), "kill_switch" },
        { "navigate_replay", Closed(killed: true), "kill_switch" },
        { "replay_skill", Closed(killed: true), "kill_switch" },
        { "template_replay", Closed(killed: true), "kill_switch" },
        { "gated_template_executor", Closed(killed: true), "kill_switch" },

        { "navigate_loop", Closed(paused: true), "paused_user_active" },
        { "navigate_replay", Closed(paused: true), "paused_user_active" },
        { "replay_skill", Closed(paused: true), "paused_user_active" },
        { "template_replay", Closed(paused: true), "paused_user_active" },
        { "gated_template_executor", Closed(paused: true), "paused_user_active" },

        { "navigate_loop", Closed(compromised: true), "compromise_detected" },
        { "navigate_replay", Closed(compromised: true), "compromise_detected" },
        { "replay_skill", Closed(compromised: true), "compromise_detected" },
        { "template_replay", Closed(compromised: true), "compromise_detected" },
        { "gated_template_executor", Closed(compromised: true), "compromise_detected" },
    };

    public static TheoryData<string> FactoryKinds => new()
    {
        "navigate_loop",
        "navigate_replay",
        "replay_skill",
        "template_replay",
        "gated_template_executor",
    };

    [Theory]
    [MemberData(nameof(ClosedSnapshots))]
    public void ProductionFactory_ClosedOrUnavailableHelperSnapshot_DeniesPreflight(
        string factoryKind,
        ActuationGateState? helperGateState,
        string expectedReason)
    {
        var safety = FactorySafety(factoryKind, helperGateState);

        var verdict = safety.Preflight(Objective);

        Assert.Equal(SafetyDecision.Deny, verdict.Decision);
        Assert.Equal(expectedReason, verdict.Reason);
    }

    [Theory]
    [MemberData(nameof(FactoryKinds))]
    public void ProductionFactory_ExplicitlyOpenHelperSnapshot_PermitsPreflightAndAuthorizedAction(
        string factoryKind)
    {
        var safety = FactorySafety(factoryKind, Open());

        var preflight = safety.Preflight(Objective);
        var action = safety.GateAction(AllowlistedClick, Objective);

        Assert.Equal(SafetyDecision.Allow, preflight.Decision);
        Assert.Equal(SafetyDecision.Allow, action.Decision);
    }

    [Theory]
    [MemberData(nameof(FactoryKinds))]
    public void ProductionFactory_HelperDryRunSnapshot_CannotAuthorizeLiveAction(string factoryKind)
    {
        var safety = FactorySafety(factoryKind, Open(dryRun: true));

        var preflight = safety.Preflight(Objective);
        var action = safety.GateAction(AllowlistedClick, Objective);

        Assert.Equal(SafetyDecision.Allow, preflight.Decision);
        Assert.Equal(SafetyDecision.AllowDryRun, action.Decision);
        Assert.Equal("gate_dry_run", action.Reason);
    }

    private ISafetyGate FactorySafety(string factoryKind, ActuationGateState? helperGateState)
    {
        var charter = new MissionCharter(
            Guid.NewGuid(),
            Objective.PharmacyId,
            1,
            DateTimeOffset.UtcNow,
            new[] { new MissionObjective("factory-gate", "Factory gate regression", 1) },
            Array.Empty<MissionConstraint>(),
            new MissionPriorityOrdering(new[] { "factory-gate" }),
            new MissionToleranceThresholds(60, 1, 1),
            "test-operator",
            DateTimeOffset.UtcNow);
        var audit = new AuditChain();
        var safetyOptions = new NavigateSafetyOptions(
            EnableTaskAutonomy: true,
            ExecutorMode: PricingExecutorMode.UiaFirst,
            AllowLiveActuation: true,
            OperatorApprovedScopes: new HashSet<string>(StringComparer.Ordinal)
            {
                TaskAutonomyScope.Build(
                    Objective.TaskKey,
                    "calc.exe",
                    "click_by_label",
                    PricingExecutorMode.UiaFirst),
            });
        var deadline = DateTimeOffset.UtcNow.AddMinutes(1);

        object product = factoryKind switch
        {
            "navigate_loop" => NavigateLoopFactory.Create(
                _services, safetyOptions, charter, audit, deadline, helperGateState),
            "navigate_replay" => NavigateReplayFactory.Create(
                _services, safetyOptions, charter, audit, deadline, helperGateState),
            "replay_skill" => ReplaySkillFactory.Create(
                _services, charter, audit, deadline, helperGateState),
            "template_replay" => ReplayFactory.Create(
                _services, safetyOptions, charter, audit, deadline, helperGateState),
            "gated_template_executor" => ReplayFactory.CreateExecutor(
                _services, safetyOptions, charter, audit, deadline, () => helperGateState),
            _ => throw new ArgumentOutOfRangeException(nameof(factoryKind), factoryKind, null),
        };

        var field = product.GetType().GetField(
            "_safety",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsAssignableFrom<ISafetyGate>(field!.GetValue(product));
    }

    private static ActuationGateState Open(bool dryRun = false) =>
        new(true, dryRun, null, null, null);

    private static ActuationGateState Closed(
        bool enabled = true,
        bool killed = false,
        bool paused = false,
        bool compromised = false) =>
        new(
            enabled,
            false,
            paused ? DateTimeOffset.UtcNow.AddMinutes(5) : null,
            paused ? "operator_active" : null,
            killed ? DateTimeOffset.UtcNow : null,
            CompromiseDetected: compromised);

    public void Dispose()
    {
        _services.Dispose();
        _db.Dispose();
    }

    private sealed class NoOpIpcClient : IIpcCommandClient
    {
        public bool IsConnected => false;

        public Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(false);

        public Task<IpcResponse?> SendAsync(
            IpcRequest request,
            TimeSpan timeout,
            CancellationToken ct) =>
            Task.FromResult<IpcResponse?>(null);
    }
}
