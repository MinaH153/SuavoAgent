using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.ActionGrammarV1;
using SuavoAgent.Core.ActionGrammarV1.Policy;
using SuavoAgent.Core.ActionGrammarV1.Verbs.Actuation;
using SuavoAgent.Core.ActionGrammarV1.Workflows;
using SuavoAgent.Core.Audit;
using SuavoAgent.Core.Mission;
using Xunit;

namespace SuavoAgent.Core.Tests.Mission;

public sealed class WorkflowExecutorTests
{
    [Fact]
    public async Task SuccessfulRun_PostsAuditPerStep_ThenCompleted()
    {
        var harness = new ExecutorHarness();
        var def = new WorkflowDefinitionDto(
            WorkflowRunId: "run-1",
            WorkflowId: "wf-1",
            WorkflowName: "DEMO",
            WorkflowVersion: "1.0.0",
            ManifestSignature: "abc",
            DryRun: true,
            Tier: "sandbox",
            Steps: new[]
            {
                new WorkflowStepDto("launch_sandbox_app", "1.0.0", null, JsonDocument.Parse("{\"app_key\":\"notepad\"}").RootElement, null),
                new WorkflowStepDto("press_keys", "1.0.0", null, JsonDocument.Parse("{\"chords\":[\"Enter\"]}").RootElement, null),
            });

        var result = await harness.Executor.ExecuteAsync(def, harness.Provider, harness.Audit, harness.Charter, "ph-1", "test", CancellationToken.None);
        Assert.Equal(WorkflowRunOutcome.Completed, result.Outcome);
        Assert.Equal(2, harness.Audit_StepCalls);
        Assert.Equal(WorkflowRunOutcome.Completed, harness.Audit_RunOutcome);
    }

    [Fact]
    public async Task GateDisabled_AbortsBeforeFirstStep()
    {
        var harness = new ExecutorHarness();
        harness.Gateway.GateState = new ActuationGateState(Enabled: false, DryRun: true, PausedUntilUtc: null, PauseReason: null, KillSwitchTrippedUtc: null);

        var def = SimpleDef("run-2", new[] { Step("press_keys", "{\"chords\":[\"Enter\"]}") });
        var result = await harness.Executor.ExecuteAsync(def, harness.Provider, harness.Audit, harness.Charter, "ph-1", "test", CancellationToken.None);

        Assert.Equal(WorkflowRunOutcome.Aborted, result.Outcome);
        Assert.Equal("gate_disabled", result.AbortReason);
        Assert.Equal(0, harness.Audit_StepCalls);
        Assert.Equal(WorkflowRunOutcome.Aborted, harness.Audit_RunOutcome);
    }

    [Fact]
    public async Task KillSwitchTripped_AbortsImmediately()
    {
        var harness = new ExecutorHarness();
        harness.Gateway.GateState = new ActuationGateState(Enabled: false, DryRun: true, PausedUntilUtc: null, PauseReason: null, KillSwitchTrippedUtc: DateTimeOffset.UtcNow);

        var def = SimpleDef("run-3", new[] { Step("press_keys", "{\"chords\":[\"Enter\"]}") });
        var result = await harness.Executor.ExecuteAsync(def, harness.Provider, harness.Audit, harness.Charter, "ph-1", "test", CancellationToken.None);

        Assert.Equal(WorkflowRunOutcome.Aborted, result.Outcome);
        Assert.Equal("kill_switch_tripped", result.AbortReason);
    }

    [Fact]
    public async Task UnregisteredVerb_StepFailsWithManifestResolutionFailed()
    {
        var harness = new ExecutorHarness();
        var def = SimpleDef("run-4", new[] { Step("not_a_verb", "{}") });

        var result = await harness.Executor.ExecuteAsync(def, harness.Provider, harness.Audit, harness.Charter, "ph-1", "test", CancellationToken.None);
        Assert.Equal(WorkflowRunOutcome.Failed, result.Outcome);
        Assert.Equal(0, result.StepsCompleted);
        Assert.Equal(WorkflowRunOutcome.Failed, harness.Audit_RunOutcome);
    }

    [Fact]
    public async Task GatewayRejection_FailsRun_AfterFirstStep()
    {
        var harness = new ExecutorHarness();
        harness.Gateway.NextResult = ActuationResult.Reject("gate_paused", "user_input_detected", dryRun: false);

        var def = SimpleDef("run-5", new[] { Step("press_keys", "{\"chords\":[\"Ctrl+S\"]}") });
        var result = await harness.Executor.ExecuteAsync(def, harness.Provider, harness.Audit, harness.Charter, "ph-1", "test", CancellationToken.None);

        Assert.Equal(WorkflowRunOutcome.Failed, result.Outcome);
        Assert.NotNull(harness.Audit_RunOutcome);
        Assert.Equal(WorkflowRunOutcome.Failed, harness.Audit_RunOutcome);
    }

    private static WorkflowStepDto Step(string verb, string paramsJson) =>
        new(verb, "1.0.0", null, JsonDocument.Parse(paramsJson).RootElement, null);

    private static WorkflowDefinitionDto SimpleDef(string runId, IReadOnlyList<WorkflowStepDto> steps) =>
        new(runId, "wf", "DEMO", "1.0.0", "abc", true, "sandbox", steps);

    private sealed class ExecutorHarness
    {
        public StubGateway Gateway { get; } = new();
        public StubAuditClient AuditClient { get; } = new();
        public ServiceProvider Provider { get; }
        public WorkflowExecutor Executor { get; }
        public AuditChain Audit { get; } = new();
        public MissionCharter Charter { get; }

        public int Audit_StepCalls => AuditClient.StepCount;
        public WorkflowRunOutcome? Audit_RunOutcome => AuditClient.LastRunOutcome;

        public ExecutorHarness()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IActuationGateway>(Gateway);
            services.AddSingleton<IAuthzPolicy>(new CharterDrivenAuthzPolicy());
            services.AddSingleton<VerbDispatcher>();
            services.AddSingleton(NullLogger<WorkflowExecutor>.Instance);
            Provider = services.BuildServiceProvider();

            var registry = VerbRegistry.Build(
                new[] { typeof(IVerb).Assembly },
                NullLogger<VerbRegistry>.Instance);

            Executor = new WorkflowExecutor(
                registry,
                Provider.GetRequiredService<VerbDispatcher>(),
                Gateway,
                AuditClient,
                NullLogger<WorkflowExecutor>.Instance);

            Charter = new MissionCharter(
                CharterId: Guid.Empty,
                PharmacyId: "ph-1",
                Version: 1,
                EffectiveFrom: DateTimeOffset.UtcNow,
                Objectives: new List<MissionObjective> { new("o1", "test", 1) },
                Constraints: Array.Empty<MissionConstraint>(),
                PriorityOrdering: new MissionPriorityOrdering(new[] { "o1" }),
                Tolerance: new MissionToleranceThresholds(60, 3, 0.5),
                SignedByOperator: "test",
                SignedAt: DateTimeOffset.UtcNow);
        }
    }

    private sealed class StubGateway : IActuationGateway
    {
        public ActuationGateState GateState { get; set; } =
            new(Enabled: true, DryRun: true, PausedUntilUtc: null, PauseReason: null, KillSwitchTrippedUtc: null);
        public ActuationResult? NextResult { get; set; }

        public Task<ActuationGateState> GetStateAsync(CancellationToken ct) => Task.FromResult(GateState);
        public Task<ActuationResult> ClickByLabelAsync(ClickByLabelRequest req, CancellationToken ct) => Task.FromResult(NextResult ?? ActuationResult.Success(1, true, "x"));
        public Task<ActuationResult> TypeTextAsync(TypeTextRequest req, CancellationToken ct) => Task.FromResult(NextResult ?? ActuationResult.Success(1, true, "x"));
        public Task<ActuationResult> PressKeysAsync(PressKeysRequest req, CancellationToken ct) => Task.FromResult(NextResult ?? ActuationResult.Success(1, true, "x"));
        public Task<ActuationResult> LaunchSandboxAppAsync(LaunchSandboxAppRequest req, CancellationToken ct) => Task.FromResult(NextResult ?? ActuationResult.Success(1, true, "x"));
    }

    private sealed class StubAuditClient : IWorkflowAuditClient
    {
        public int StepCount;
        public WorkflowRunOutcome? LastRunOutcome;
        public string? LastAbortReason;

        public Task PostStepAuditAsync(WorkflowStepAuditEntry entry, CancellationToken ct)
        {
            StepCount++;
            return Task.CompletedTask;
        }

        public Task PostRunCompletedAsync(string workflowRunId, WorkflowRunOutcome outcome, string? abortReason, CancellationToken ct)
        {
            LastRunOutcome = outcome;
            LastAbortReason = abortReason;
            return Task.CompletedTask;
        }
    }
}
