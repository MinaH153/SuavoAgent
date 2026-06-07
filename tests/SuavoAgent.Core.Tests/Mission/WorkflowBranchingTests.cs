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

/// <summary>
/// Phase 5.4 branching coverage: conditional steps, goto, on_failure
/// retry/end, cycle limit. The Phase 5.5 cooperative cancel path lives
/// here too because it shares the same harness.
/// </summary>
public sealed class WorkflowBranchingTests
{
    [Fact]
    public async Task ConditionFalse_StepSkipped_AndAuditRecorded()
    {
        var harness = new BranchHarness();
        var def = MakeDef(
            Step("press_keys", "{\"chords\":[\"Enter\"]}", stepId: "first"),
            Step(
                "press_keys",
                "{\"chords\":[\"Tab\"]}",
                stepId: "second",
                condition: new WorkflowConditionDto("never", null, null, null)));

        var result = await harness.Execute(def);
        Assert.Equal(WorkflowRunOutcome.Completed, result.Outcome);
        Assert.Equal(2, harness.Audit.StepCount);
        Assert.Contains(harness.Audit.AuditEntries, e => e.Outcome == "skipped");
    }

    [Fact]
    public async Task PreviousOutcomeCondition_RoutesAroundFailedSteps()
    {
        var harness = new BranchHarness();
        // First step succeeds; second runs only if previous == success → executes.
        var def = MakeDef(
            Step("press_keys", "{\"chords\":[\"Enter\"]}"),
            Step(
                "press_keys",
                "{\"chords\":[\"Tab\"]}",
                condition: new WorkflowConditionDto("previous_outcome", null, null, "success")));

        var result = await harness.Execute(def);
        Assert.Equal(WorkflowRunOutcome.Completed, result.Outcome);
        Assert.Equal(2, harness.Audit.StepCount);
    }

    [Fact]
    public async Task GotoControlFlow_JumpsToTargetStepId()
    {
        var harness = new BranchHarness();
        var def = MakeDef(
            Step(
                "press_keys",
                "{\"chords\":[\"Enter\"]}",
                stepId: "start",
                onSuccess: new WorkflowControlFlowDto("goto", "tail", null, null, null)),
            Step("press_keys", "{\"chords\":[\"Tab\"]}", stepId: "skipped"),
            Step("press_keys", "{\"chords\":[\"Esc\"]}", stepId: "tail"));

        var result = await harness.Execute(def);
        Assert.Equal(WorkflowRunOutcome.Completed, result.Outcome);
        Assert.Equal(2, harness.Audit.StepCount); // start + tail
        Assert.DoesNotContain(harness.Audit.AuditEntries, e => e.StepIndex == 1);
    }

    [Fact]
    public async Task GotoToUnknownStepId_FailsWithGotoTargetUnresolved()
    {
        var harness = new BranchHarness();
        var def = MakeDef(
            Step(
                "press_keys",
                "{\"chords\":[\"Enter\"]}",
                stepId: "start",
                onSuccess: new WorkflowControlFlowDto("goto", "does-not-exist", null, null, null)));

        var result = await harness.Execute(def);
        Assert.Equal(WorkflowRunOutcome.Failed, result.Outcome);
        Assert.Contains("goto_target_unresolved", result.AbortReason ?? "");
    }

    [Fact]
    public async Task CycleLimit_AbortsAfterMaxRevisits()
    {
        var harness = new BranchHarness();
        var def = MakeDef(
            Step(
                "press_keys",
                "{\"chords\":[\"Enter\"]}",
                stepId: "looper",
                onSuccess: new WorkflowControlFlowDto("goto", "looper", null, null, null)));

        var result = await harness.Execute(def);
        Assert.Equal(WorkflowRunOutcome.Aborted, result.Outcome);
        Assert.Equal("cycle_limit_exceeded", result.AbortReason);
        Assert.True(harness.Audit.StepCount <= WorkflowExecutor.MaxStepRevisits + 1,
            $"audit count {harness.Audit.StepCount} should not exceed cycle limit");
    }

    [Fact]
    public async Task OnFailureRetry_RetriesUpToLimit_ThenFails()
    {
        var harness = new BranchHarness { ForceVerbFailure = true };
        var def = MakeDef(
            Step(
                "press_keys",
                "{\"chords\":[\"Enter\"]}",
                onFailure: new WorkflowControlFlowDto("retry", null, 2, null, null)));

        var result = await harness.Execute(def);
        Assert.Equal(WorkflowRunOutcome.Failed, result.Outcome);
        // 1 initial + 2 retries = 3 attempts.
        Assert.Equal(3, harness.Audit.StepCount);
    }

    [Fact]
    public async Task OnFailureEnd_TerminatesEarly_WithCustomReason()
    {
        var harness = new BranchHarness { ForceVerbFailure = true };
        var def = MakeDef(
            Step(
                "press_keys",
                "{\"chords\":[\"Enter\"]}",
                onFailure: new WorkflowControlFlowDto("end", null, null, "aborted", "operator_handled_failure")),
            Step("press_keys", "{\"chords\":[\"Tab\"]}"));

        var result = await harness.Execute(def);
        Assert.Equal(WorkflowRunOutcome.Aborted, result.Outcome);
        Assert.Equal("operator_handled_failure", result.AbortReason);
        Assert.Equal(1, harness.Audit.StepCount);
    }

    [Fact]
    public async Task CooperativeCancel_AbortsRun_AfterCurrentStep()
    {
        var harness = new BranchHarness { CancelAfterFirstStep = true };
        var def = MakeDef(
            Step("press_keys", "{\"chords\":[\"Enter\"]}"),
            Step("press_keys", "{\"chords\":[\"Tab\"]}"));

        var result = await harness.Execute(def);
        Assert.Equal(WorkflowRunOutcome.Aborted, result.Outcome);
        Assert.Equal("cooperative_cancel", result.AbortReason);
    }

    private static WorkflowStepDto Step(
        string verb,
        string paramsJson,
        string? stepId = null,
        WorkflowConditionDto? condition = null,
        WorkflowControlFlowDto? onSuccess = null,
        WorkflowControlFlowDto? onFailure = null) =>
        new(verb, "1.0.0", null, JsonDocument.Parse(paramsJson).RootElement, null, stepId, condition, onSuccess, onFailure);

    private static WorkflowDefinitionDto MakeDef(params WorkflowStepDto[] steps) =>
        new("run-x", "wf", "BRANCH-DEMO", "1.0.0", "abc", true, "sandbox", steps);

    private sealed class BranchHarness
    {
        public StubGateway Gateway { get; } = new();
        public StubAuditClient Audit { get; } = new();
        public ServiceProvider Provider { get; }
        public WorkflowExecutor Executor { get; }
        public AuditChain Chain { get; } = new();
        public MissionCharter Charter { get; }
        public bool ForceVerbFailure { get; init; }
        public bool CancelAfterFirstStep { get; init; }

        public BranchHarness()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IActuationGateway>(Gateway);
            services.AddSingleton<IAuthzPolicy>(new CharterDrivenAuthzPolicy());
            services.AddSingleton<VerbDispatcher>();
            Provider = services.BuildServiceProvider();

            var registry = VerbRegistry.Build(
                new[] { typeof(IVerb).Assembly },
                NullLogger<VerbRegistry>.Instance);

            Executor = new WorkflowExecutor(
                registry,
                Provider.GetRequiredService<VerbDispatcher>(),
                Gateway,
                Audit,
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

        public async Task<WorkflowExecutor.WorkflowExecutionResult> Execute(WorkflowDefinitionDto def)
        {
            if (ForceVerbFailure)
            {
                Gateway.NextResult = ActuationResult.Reject("synthetic_failure", "test", true);
            }
            using var cts = new CancellationTokenSource();
            if (CancelAfterFirstStep)
            {
                Audit.OnStepRecorded = _ => cts.Cancel();
            }
            return await Executor.ExecuteAsync(def, Provider, Chain, Charter, "ph-1", "test", cts.Token);
        }
    }

    private sealed class StubGateway : IActuationGateway
    {
        public ActuationGateState GateState { get; set; } =
            new(true, true, null, null, null);
        public ActuationResult? NextResult { get; set; }

        public Task<ActuationGateState> GetStateAsync(CancellationToken ct) => Task.FromResult(GateState);
        public Task<ActuationResult> ClickByLabelAsync(ClickByLabelRequest req, CancellationToken ct) => Task.FromResult(NextResult ?? ActuationResult.Success(1, true, "x"));
        public Task<ActuationResult> ClickBySignatureAsync(ClickBySignatureRequest req, CancellationToken ct) => Task.FromResult(NextResult ?? ActuationResult.Success(1, true, "x"));
        public Task<ActuationResult> TypeTextAsync(TypeTextRequest req, CancellationToken ct) => Task.FromResult(NextResult ?? ActuationResult.Success(1, true, "x"));
        public Task<ActuationResult> PressKeysAsync(PressKeysRequest req, CancellationToken ct) => Task.FromResult(NextResult ?? ActuationResult.Success(1, true, "x"));
        public Task<ActuationResult> LaunchSandboxAppAsync(LaunchSandboxAppRequest req, CancellationToken ct) => Task.FromResult(NextResult ?? ActuationResult.Success(1, true, "x"));
        public Task<ActuationResult> ReloadAllowlistAsync(CancellationToken ct) => Task.FromResult(ActuationResult.Success(0, true, "reload"));
    }

    private sealed class StubAuditClient : IWorkflowAuditClient
    {
        public List<WorkflowStepAuditEntry> AuditEntries { get; } = new();
        public WorkflowRunOutcome? LastRunOutcome;
        public string? LastAbortReason;
        public Action<WorkflowStepAuditEntry>? OnStepRecorded { get; set; }
        public int StepCount => AuditEntries.Count;

        public Task PostStepAuditAsync(WorkflowStepAuditEntry entry, CancellationToken ct)
        {
            AuditEntries.Add(entry);
            OnStepRecorded?.Invoke(entry);
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
