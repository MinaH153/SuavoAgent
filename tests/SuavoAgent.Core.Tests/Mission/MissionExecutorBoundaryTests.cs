using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SuavoAgent.Core.ActionGrammarV1;
using SuavoAgent.Core.ActionGrammarV1.Verbs.LookupPatient;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Mission;
using Xunit;

namespace SuavoAgent.Core.Tests.Mission;

public sealed class MissionExecutorBoundaryTests
{
    [Theory]
    [InlineData(false, false, "Agent.AutoExecution.Enabled=false")]
    [InlineData(true, true, "Agent.AutoExecution.RequireConfirmation=true")]
    public async Task AutoExecutionPolicy_BlocksBeforeAnyVerbDispatch(
        bool enabled,
        bool requireConfirmation,
        string expectedReason)
    {
        using var harness = new MissionTestHarness();
        var options = Options.Create(new AgentOptions
        {
            AutoExecution = new AutoExecutionOptions
            {
                Enabled = enabled,
                RequireConfirmation = requireConfirmation,
            },
        });
        var executor = new MissionExecutor(
            harness.Dispatcher,
            harness.Provider.GetServices<IVerb>(),
            options);

        var result = await executor.RunAsync(
            Goal(), Plan(Step("step-1", "not_registered", "1.0.0")),
            harness.Charter(), harness.Audit, harness.Provider, CancellationToken.None);

        Assert.Equal(MissionOutcome.PlanFailed, result.Outcome);
        Assert.Equal(expectedReason, result.FailureReason);
        Assert.Empty(result.StepResults);
        Assert.Contains(harness.Audit.Snapshot(),
            entry => entry.EventType == "mission.blocked_auto_execution");
        Assert.True(harness.Audit.VerifyChain());
    }

    [Fact]
    public async Task UnknownVerb_FailsPlanWithoutDispatch()
    {
        using var harness = new MissionTestHarness();

        var result = await harness.Executor.RunAsync(
            Goal(), Plan(Step("unknown", "future_verb", "1.0.0")),
            harness.Charter(), harness.Audit, harness.Provider, CancellationToken.None);

        Assert.Equal(MissionOutcome.PlanFailed, result.Outcome);
        Assert.Contains("not registered", result.FailureReason);
        Assert.Empty(result.StepResults);
        Assert.Contains(harness.Audit.Snapshot(),
            entry => entry.EventType == "mission.step_unknown_verb");
    }

    [Fact]
    public async Task VersionMismatch_FailsClosedBeforeVerbExecution()
    {
        using var harness = new MissionTestHarness();
        var verb = harness.Verb<LookupPatientVerb>();

        var result = await harness.Executor.RunAsync(
            Goal(), Plan(Step("version", verb.Metadata.Name, "999.0.0")),
            harness.Charter(), harness.Audit, harness.Provider, CancellationToken.None);

        Assert.Equal(MissionOutcome.PlanFailed, result.Outcome);
        Assert.Contains("version mismatch", result.FailureReason);
        Assert.Empty(result.StepResults);
        Assert.Contains(harness.Audit.Snapshot(),
            entry => entry.EventType == "mission.step_version_mismatch");
    }

    [Fact]
    public async Task BindingToFutureStep_FailsWithoutLeakingBindingDetail()
    {
        using var harness = new MissionTestHarness();
        var verb = harness.Verb<LookupPatientVerb>();
        var step = Step(
            "consumer", verb.Metadata.Name, verb.Metadata.Version,
            bindings: new Dictionary<string, ParameterBinding>
            {
                ["patient_identifier"] = new("future", "patient_id"),
            });

        var result = await harness.Executor.RunAsync(
            Goal(), Plan(step), harness.Charter(), harness.Audit, harness.Provider,
            CancellationToken.None);

        Assert.Equal(MissionOutcome.PlanFailed, result.Outcome);
        Assert.Equal("mission_binding_failed:MissionExecutionException", result.FailureReason);
        Assert.Empty(result.StepResults);
        Assert.Contains(harness.Audit.Snapshot(),
            entry => entry.EventType == "mission.step_binding_failed");
    }

    [Fact]
    public async Task BindingToMissingOutputKey_FailsAfterPriorSuccessfulStep()
    {
        using var harness = new MissionTestHarness();
        harness.Adapter.SeedPatient(
            "patient-1", "pms-patient-1", "display-hash", DateTimeOffset.UtcNow,
            Array.Empty<SuavoAgent.Core.Adapters.RxHistoryRecord>());
        var verb = harness.Verb<LookupPatientVerb>();
        var producer = Step(
            "producer", verb.Metadata.Name, verb.Metadata.Version,
            new Dictionary<string, object?> { ["patient_identifier"] = "patient-1" });
        var consumer = Step(
            "consumer", verb.Metadata.Name, verb.Metadata.Version,
            bindings: new Dictionary<string, ParameterBinding>
            {
                ["patient_identifier"] = new("producer", "not_emitted"),
            });

        var result = await harness.Executor.RunAsync(
            Goal(), Plan(producer, consumer), harness.Charter(), harness.Audit,
            harness.Provider, CancellationToken.None);

        Assert.Equal(MissionOutcome.PlanFailed, result.Outcome);
        Assert.Equal("mission_binding_failed:MissionExecutionException", result.FailureReason);
        Assert.Single(result.StepResults);
        Assert.Equal(VerbDispatchOutcome.Success, result.StepResults[0].Outcome);
        Assert.Equal(1, harness.Adapter.LookupInvocationCount);
    }

    [Fact]
    public async Task EmptyPlan_CompletesWithEmptyOutputAndAuditableResult()
    {
        using var harness = new MissionTestHarness();

        var result = await harness.Executor.RunAsync(
            Goal(), Plan(), harness.Charter(), harness.Audit, harness.Provider,
            CancellationToken.None);

        Assert.Equal(MissionOutcome.Success, result.Outcome);
        Assert.Empty(result.StepResults);
        Assert.Empty(result.FinalOutput);
        Assert.Null(result.FailureReason);
        Assert.Contains(harness.Audit.Snapshot(),
            entry => entry.EventType == "mission.completed");
    }

    private static MissionGoal Goal() => new(
        "goal-boundary", MissionGoalTypes.LookupPatientTopNdcs, "pharm-test-001",
        "operator-test", new Dictionary<string, object?>(), DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddMinutes(5));

    private static MissionPlan Plan(params MissionPlanStep[] steps) =>
        new("plan-boundary", "goal-boundary", steps);

    private static MissionPlanStep Step(
        string id,
        string verb,
        string version,
        IReadOnlyDictionary<string, object?>? parameters = null,
        IReadOnlyDictionary<string, ParameterBinding>? bindings = null) =>
        new(
            id, verb, version,
            parameters ?? new Dictionary<string, object?>(),
            bindings ?? new Dictionary<string, ParameterBinding>());
}
