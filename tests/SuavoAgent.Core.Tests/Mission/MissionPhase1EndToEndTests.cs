using Microsoft.Extensions.DependencyInjection;
using SuavoAgent.Core.ActionGrammarV1.Verbs.LookupPatient;
using SuavoAgent.Core.ActionGrammarV1.Verbs.QueryTopNdcs;
using SuavoAgent.Core.Adapters;
using SuavoAgent.Core.Mission;
using Xunit;

namespace SuavoAgent.Core.Tests.Mission;

/// <summary>
/// End-to-end exercise of the Phase-1 Mission Loop against the mock adapter:
///
/// <list type="number">
///   <item>Operator-authored <see cref="MissionGoal"/> of type
///     <c>lookup_patient_top_ndcs</c>.</item>
///   <item><see cref="RuleBasedMissionPlanner"/> produces a two-step plan.</item>
///   <item><see cref="MissionExecutor"/> runs both steps through the
///     <see cref="SuavoAgent.Core.ActionGrammarV1.VerbDispatcher"/>.</item>
///   <item><see cref="MissionEvaluator"/> inspects the result and writes
///     <c>mission.evaluated</c> to the audit chain.</item>
///   <item>Hash-chain integrity verifies after the run.</item>
/// </list>
/// </summary>
public sealed class MissionPhase1EndToEndTests
{
    private const string PharmacyId = "pharm-phase1-e2e";

    [Fact]
    public async Task HappyPath_TopNdcsForPatient_EndToEnd_ProducesOrderedNdcsAndAuditChainIntact()
    {
        using var harness = new MissionTestHarness();

        harness.Adapter.SeedPatient(
            identifier: "PT-123-scoped",
            patientId: "PMS-PID-42",
            displayNameHash: "9a1c3bd2",
            lastActivityUtc: DateTimeOffset.UtcNow.AddHours(-4),
            history: new[]
            {
                new RxHistoryRecord("00093-5567-01", FillCount: 12, LastFillUtc: DateTimeOffset.UtcNow.AddDays(-14), LastQuantity: 30m),
                new RxHistoryRecord("00378-3773-10", FillCount: 4, LastFillUtc: DateTimeOffset.UtcNow.AddDays(-60), LastQuantity: 90m),
                new RxHistoryRecord("59746-0172-60", FillCount: 8, LastFillUtc: DateTimeOffset.UtcNow.AddDays(-30), LastQuantity: 10m),
                new RxHistoryRecord("50458-0140-10", FillCount: 2, LastFillUtc: DateTimeOffset.UtcNow.AddDays(-120), LastQuantity: 5m),
            });

        var goal = new MissionGoal(
            GoalId: Guid.NewGuid().ToString("D"),
            GoalType: MissionGoalTypes.LookupPatientTopNdcs,
            PharmacyId: PharmacyId,
            RequestedBy: "operator-test",
            Parameters: new Dictionary<string, object?>
            {
                ["patient_identifier"] = "PT-123-scoped",
                ["top_n"] = 3,
            },
            RequestedAt: DateTimeOffset.UtcNow,
            DeadlineUtc: DateTimeOffset.UtcNow.AddMinutes(5));

        var plan = await harness.Planner.PlanAsync(goal, CancellationToken.None);

        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal(LookupPatientVerb.VerbName, plan.Steps[0].VerbName);
        Assert.Equal(QueryTopNdcsForPatientVerb.VerbName, plan.Steps[1].VerbName);
        Assert.Contains("patient_id", plan.Steps[1].ParameterBindings.Keys);

        var result = await harness.Executor.RunAsync(goal, plan, harness.Charter(PharmacyId), harness.Audit, harness.Provider, CancellationToken.None);

        Assert.Equal(MissionOutcome.Success, result.Outcome);
        Assert.Equal(2, result.StepResults.Count);
        Assert.All(result.StepResults, r => Assert.Equal(SuavoAgent.Core.ActionGrammarV1.VerbDispatchOutcome.Success, r.Outcome));

        var ndcs = (IReadOnlyList<RxHistoryRecord>)result.FinalOutput["ndcs"]!;
        Assert.Equal(3, ndcs.Count);
        Assert.Equal("00093-5567-01", ndcs[0].Ndc); // highest fill count
        Assert.Equal(3, result.FinalOutput["result_count"]);

        var evaluation = harness.Evaluator.Evaluate(result, harness.Audit);
        Assert.True(evaluation.Healthy);
        Assert.Empty(evaluation.ShapeIssues);

        Assert.True(harness.Audit.VerifyChain());

        var events = harness.Audit.Snapshot().Select(e => e.EventType).ToList();
        Assert.Contains("mission.started", events);
        Assert.Contains("verb.rollback_captured", events);
        Assert.Contains("verb.executed", events);
        Assert.Contains("mission.completed", events);
        Assert.Contains("mission.evaluated", events);
        Assert.Equal(1, harness.Adapter.LookupInvocationCount);
        Assert.Equal(1, harness.Adapter.HistoryInvocationCount);
    }

    [Fact]
    public async Task UnknownPatient_StepFailure_StopsPlanAndEmitsMissionFailedAudit()
    {
        using var harness = new MissionTestHarness();

        var goal = new MissionGoal(
            GoalId: Guid.NewGuid().ToString("D"),
            GoalType: MissionGoalTypes.LookupPatientTopNdcs,
            PharmacyId: PharmacyId,
            RequestedBy: "operator-test",
            Parameters: new Dictionary<string, object?>
            {
                ["patient_identifier"] = "PT-DOES-NOT-EXIST",
                ["top_n"] = 5,
            },
            RequestedAt: DateTimeOffset.UtcNow,
            DeadlineUtc: DateTimeOffset.UtcNow.AddMinutes(5));

        var plan = await harness.Planner.PlanAsync(goal, CancellationToken.None);
        var result = await harness.Executor.RunAsync(goal, plan, harness.Charter(PharmacyId), harness.Audit, harness.Provider, CancellationToken.None);

        Assert.Equal(MissionOutcome.StepFailed, result.Outcome);
        Assert.Single(result.StepResults);
        Assert.Equal(1, harness.Adapter.LookupInvocationCount);
        Assert.Equal(0, harness.Adapter.HistoryInvocationCount);
        Assert.True(harness.Audit.VerifyChain());
        Assert.Contains(harness.Audit.Snapshot(), e => e.EventType == "mission.failed");
    }

    [Fact]
    public async Task UnsupportedGoalType_PlannerThrows_NotAPartialPlan()
    {
        using var harness = new MissionTestHarness();

        var goal = new MissionGoal(
            GoalId: Guid.NewGuid().ToString("D"),
            GoalType: "unsupported_goal",
            PharmacyId: PharmacyId,
            RequestedBy: "operator-test",
            Parameters: new Dictionary<string, object?>(),
            RequestedAt: DateTimeOffset.UtcNow,
            DeadlineUtc: DateTimeOffset.UtcNow.AddMinutes(5));

        var ex = await Assert.ThrowsAsync<MissionPlanningException>(() =>
            harness.Planner.PlanAsync(goal, CancellationToken.None));
        Assert.Equal("unsupported_goal", ex.GoalType);
    }

    [Fact]
    public async Task MissingPlanParameter_PlannerRejects_BeforeExecution()
    {
        using var harness = new MissionTestHarness();

        var goal = new MissionGoal(
            GoalId: Guid.NewGuid().ToString("D"),
            GoalType: MissionGoalTypes.LookupPatientTopNdcs,
            PharmacyId: PharmacyId,
            RequestedBy: "operator-test",
            Parameters: new Dictionary<string, object?> { ["top_n"] = 3 }, // no patient_identifier
            RequestedAt: DateTimeOffset.UtcNow,
            DeadlineUtc: DateTimeOffset.UtcNow.AddMinutes(5));

        await Assert.ThrowsAsync<MissionPlanningException>(() =>
            harness.Planner.PlanAsync(goal, CancellationToken.None));
    }

    [Fact]
    public async Task TopNOutOfRange_PlannerRejects()
    {
        using var harness = new MissionTestHarness();

        var goal = new MissionGoal(
            GoalId: Guid.NewGuid().ToString("D"),
            GoalType: MissionGoalTypes.LookupPatientTopNdcs,
            PharmacyId: PharmacyId,
            RequestedBy: "operator-test",
            Parameters: new Dictionary<string, object?>
            {
                ["patient_identifier"] = "PT-X",
                ["top_n"] = 51,
            },
            RequestedAt: DateTimeOffset.UtcNow,
            DeadlineUtc: DateTimeOffset.UtcNow.AddMinutes(5));

        await Assert.ThrowsAsync<MissionPlanningException>(() =>
            harness.Planner.PlanAsync(goal, CancellationToken.None));
    }

    [Fact]
    public async Task PostconditionFail_ExceedingTopN_RejectedEvenIfAdapterReturnsMore()
    {
        // BadAdapter violates the contract: returns 5 rows when top_n=2. Postcondition must catch it.
        var bad = new BadAdapter_ReturnsMoreThanRequested();
        using var harness = new MissionTestHarness(adapter: new MockPharmacyReadAdapter()); // unused
        // Inject bad adapter via parameter override on the verb directly
        var verb = harness.Verb<QueryTopNdcsForPatientVerb>();
        var ctx = new SuavoAgent.Core.ActionGrammarV1.VerbContext(
            PharmacyId: PharmacyId,
            Charter: harness.Charter(PharmacyId),
            Audit: harness.Audit,
            InvocationId: Guid.NewGuid().ToString("D"),
            Actor: "test",
            Parameters: new Dictionary<string, object?> { ["patient_id"] = "pid-x", ["top_n"] = 2 },
            Services: new Microsoft.Extensions.DependencyInjection.ServiceCollection()
                .AddSingleton<IPharmacyReadAdapter>(bad)
                .BuildServiceProvider(),
            DeadlineUtc: DateTimeOffset.UtcNow.AddMinutes(5));

        var result = await harness.Dispatcher.DispatchAsync(verb, ctx, CancellationToken.None);
        Assert.Equal(SuavoAgent.Core.ActionGrammarV1.VerbDispatchOutcome.Failed, result.Outcome);
        Assert.Contains("ndcs_cardinality", result.FailureReason);
    }

    private sealed class BadAdapter_ReturnsMoreThanRequested : IPharmacyReadAdapter
    {
        public string AdapterType => "bad-test";
        public Task<PatientRecord?> LookupPatientAsync(string id, CancellationToken ct) =>
            Task.FromResult<PatientRecord?>(new PatientRecord(id, "x", DateTimeOffset.UtcNow));
        public Task<IReadOnlyList<RxHistoryRecord>> GetTopNdcsForPatientAsync(string patientId, int topN, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RxHistoryRecord>>(new[]
            {
                new RxHistoryRecord("A", 1, DateTimeOffset.UtcNow, 1m),
                new RxHistoryRecord("B", 1, DateTimeOffset.UtcNow, 1m),
                new RxHistoryRecord("C", 1, DateTimeOffset.UtcNow, 1m),
                new RxHistoryRecord("D", 1, DateTimeOffset.UtcNow, 1m),
                new RxHistoryRecord("E", 1, DateTimeOffset.UtcNow, 1m),
            });
    }
}
