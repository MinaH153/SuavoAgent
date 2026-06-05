using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Core.Agentic;
using Xunit;

namespace SuavoAgent.Core.Tests.Agentic;

/// <summary>
/// Pure-unit suite for the general agentic orchestrator. Every port is faked; no UI,
/// no clock, no delay. Encodes the Codex-resolved semantics (deterministic deny,
/// settle-before-verify, no blind retry, cloud sub-budget, multi-action rejection).
/// </summary>
public sealed class AgenticLoopRunnerTests
{
    private static readonly AgentObjective Obj = AgenticTestKit.Objective;

    private static AgenticLoopOptions Opts(int maxSteps = 25, int maxCloud = 12, int noProgress = 3,
        int nullLimit = 3, int settleStable = 2, int settleMaxPolls = 5, bool dryRun = false, TimeSpan? deadline = null)
        => new()
        {
            MaxSteps = maxSteps,
            MaxCloudCalls = maxCloud,
            NoProgressWindow = noProgress,
            PerceiveNullStrikeLimit = nullLimit,
            SettleStableReads = settleStable,
            SettleMaxPolls = settleMaxPolls,
            DryRun = dryRun,
            Deadline = deadline,
        };

    // 1
    [Fact]
    public async Task HappyPath_PostconditionMet_ReasonerReturnsDone_TerminatesDone()
    {
        var perceiver = new FakePerceiver(AgenticTestKit.Screen("h"));
        var reasoner = new FakeReasoner
        {
            OnReason = (_, m, _, _) => m.History.Count == 0
                ? new ReasonResult(NextAction.Act("click"), false)
                : new ReasonResult(NextAction.Done, false),
            OnVerify = (_, _, _, _, _) => new VerifyResult(PostconditionVerdict.Met, false),
        };
        var actuator = new FakeActuator();
        var safety = new FakeSafetyGate();
        var runner = AgenticTestKit.Runner(perceiver, reasoner, actuator, safety);

        var result = await runner.RunAsync(Obj, Opts(), CancellationToken.None);

        Assert.Equal(TerminationReason.Done, result.Termination);
        Assert.Single(actuator.Calls);
        Assert.Equal(1, reasoner.VerifyCalls);
        Assert.False(result.EscalationEmitted);
    }

    // 2
    [Fact]
    public async Task StepBudgetExhausted_NeverMeetsPostcondition_EscalatesBudgetExhausted()
    {
        var perceiver = new FakePerceiver(AgenticTestKit.Screen("h"));
        var step = 0;
        var reasoner = new FakeReasoner
        {
            // distinct action each step ⇒ no-progress never trips; budget does
            OnReason = (_, _, _, _) => new ReasonResult(NextAction.Act($"click{step++}"), false),
            OnVerify = (_, _, _, _, _) => new VerifyResult(PostconditionVerdict.NotMet, false),
        };
        var actuator = new FakeActuator();
        var runner = AgenticTestKit.Runner(perceiver, reasoner, actuator, new FakeSafetyGate());

        var result = await runner.RunAsync(Obj, Opts(maxSteps: 5), CancellationToken.None);

        Assert.Equal(TerminationReason.BudgetExhausted, result.Termination);
        Assert.Equal(5, actuator.Calls.Count);
        Assert.True(result.EscalationEmitted);
    }

    // 3
    [Fact]
    public async Task SafetyGateDenied_OnPreflight_HaltsImmediately_NoPerceiveNoAct()
    {
        var perceiver = new FakePerceiver(AgenticTestKit.Screen("h"));
        var reasoner = new FakeReasoner();
        var actuator = new FakeActuator();
        var safety = new FakeSafetyGate { PreflightFn = _ => SafetyVerdict.Denied("kill_switch") };
        var runner = AgenticTestKit.Runner(perceiver, reasoner, actuator, safety);

        var result = await runner.RunAsync(Obj, Opts(), CancellationToken.None);

        Assert.Equal(TerminationReason.GateDenied, result.Termination);
        Assert.Equal(0, perceiver.Calls);
        Assert.Empty(actuator.Calls);
        Assert.Equal(0, reasoner.ReasonCalls);
    }

    // 4
    [Fact]
    public async Task SafetyGateDenied_OnGateAction_TerminatesGateDenied_NoActuation_NoReReason()
    {
        var perceiver = new FakePerceiver(AgenticTestKit.Screen("h"));
        var reasoner = new FakeReasoner
        {
            OnReason = (_, _, _, _) => new ReasonResult(NextAction.Act("type", new Dictionary<string, object?> { ["text"] = "x" }), false),
        };
        var actuator = new FakeActuator();
        var safety = new FakeSafetyGate { GateActionFn = (_, _) => SafetyVerdict.Denied("may_run_unsupervised=false") };
        var runner = AgenticTestKit.Runner(perceiver, reasoner, actuator, safety);

        var result = await runner.RunAsync(Obj, Opts(), CancellationToken.None);

        Assert.Equal(TerminationReason.GateDenied, result.Termination);
        Assert.Empty(actuator.Calls);
        Assert.Equal(1, reasoner.ReasonCalls); // no implicit re-reason after deny
        Assert.True(result.EscalationEmitted);
    }

    // 5
    [Fact]
    public async Task GateActionAllowDryRun_DispatchesDryRun_ThenEscalates_NeverExecutesForReal()
    {
        var perceiver = new FakePerceiver(AgenticTestKit.Screen("h"));
        var reasoner = new FakeReasoner
        {
            OnReason = (_, _, _, _) => new ReasonResult(NextAction.Act("click"), false),
        };
        var actuator = new FakeActuator();
        var safety = new FakeSafetyGate { GateActionFn = (_, _) => SafetyVerdict.Dry("supervised") };
        var runner = AgenticTestKit.Runner(perceiver, reasoner, actuator, safety);

        var result = await runner.RunAsync(Obj, Opts(), CancellationToken.None);

        Assert.Equal(TerminationReason.GateDenied, result.Termination);
        Assert.Single(actuator.Calls);
        Assert.True(actuator.Calls[0].Ctx.DryRun);   // dispatched dry-run, audited not executed
        Assert.True(result.EscalationEmitted);
    }

    // 6
    [Fact]
    public async Task CancelMidLoop_TokenCancelledAfterFirstAct_StopsBeforeNextReason()
    {
        var cts = new CancellationTokenSource();
        var perceiver = new FakePerceiver(AgenticTestKit.Screen("h"));
        var reasoner = new FakeReasoner
        {
            OnReason = (_, _, _, _) => new ReasonResult(NextAction.Act("click"), false),
            OnVerify = (_, _, _, _, _) => new VerifyResult(PostconditionVerdict.NotMet, false),
        };
        var actuator = new FakeActuator { OnAct = (_, ctx) => { cts.Cancel(); return new ActOutcome(ActStatus.Success, DryRun: ctx.DryRun); } };
        var runner = AgenticTestKit.Runner(perceiver, reasoner, actuator, new FakeSafetyGate());

        var result = await runner.RunAsync(Obj, Opts(), cts.Token);

        Assert.Equal(TerminationReason.Cancelled, result.Termination);
        Assert.Equal(1, reasoner.ReasonCalls);
        Assert.Single(actuator.Calls);
    }

    // 7
    [Fact]
    public async Task PostconditionNotMet_ReReasons_DoesNotBlindRetrySameAction()
    {
        var perceiver = new FakePerceiver(AgenticTestKit.Screen("h"));
        var reasoner = new FakeReasoner
        {
            OnReason = (_, m, _, _) => m.History.Count == 0
                ? new ReasonResult(NextAction.Act("clickA"), false)
                : new ReasonResult(NextAction.Done, false),
            OnVerify = (_, _, _, _, _) => new VerifyResult(PostconditionVerdict.NotMet, false),
        };
        var actuator = new FakeActuator();
        var runner = AgenticTestKit.Runner(perceiver, reasoner, actuator, new FakeSafetyGate());

        var result = await runner.RunAsync(Obj, Opts(), CancellationToken.None);

        // NotMet routed back to reason (call 2 → Done); clickA actuated exactly ONCE (no blind retry).
        Assert.Equal(TerminationReason.Done, result.Termination);
        Assert.Single(actuator.Calls);
        Assert.Equal("clickA()", actuator.Calls[0].Action.Signature());
        Assert.Equal(2, reasoner.ReasonCalls);
    }

    // 8
    [Fact]
    public async Task NonIdempotentAction_NeverAutoReExecutedByOrchestrator()
    {
        var perceiver = new FakePerceiver(AgenticTestKit.Screen("h"));
        var typeAction = NextAction.Act("type", new Dictionary<string, object?> { ["text"] = "hi" });
        var reasonCount = 0;
        var reasoner = new FakeReasoner
        {
            // reasoner re-picks the SAME non-idempotent Type twice, then Done
            OnReason = (_, _, _, _) => ++reasonCount <= 2
                ? new ReasonResult(typeAction, false)
                : new ReasonResult(NextAction.Done, false),
            OnVerify = (_, _, _, _, _) => new VerifyResult(PostconditionVerdict.NotMet, false),
        };
        var actuator = new FakeActuator();
        var runner = AgenticTestKit.Runner(perceiver, reasoner, actuator, new FakeSafetyGate());

        var result = await runner.RunAsync(Obj, Opts(noProgress: 5), CancellationToken.None);

        // Exactly one ActAsync per reasoner Act-decision: no orchestrator-driven re-execution.
        Assert.Equal(2, actuator.Calls.Count);
        Assert.Equal(3, reasoner.ReasonCalls);
        Assert.Equal(TerminationReason.Done, result.Termination);
    }

    // 9
    [Fact]
    public async Task SettleLoop_WaitsForStableScreenHash_BeforeVerify()
    {
        // main perceive = "before"; settle polls = changing then stable "after2"
        var perceiver = new FakePerceiver(
            AgenticTestKit.Screen("before"),   // main perceive
            AgenticTestKit.Screen("after1"),   // settle poll 1 (changing)
            AgenticTestKit.Screen("after2"),   // settle poll 2
            AgenticTestKit.Screen("after2"),   // settle poll 3 ⇒ stable (2 consecutive)
            AgenticTestKit.Screen("after2"));  // main perceive of step 2
        var reasoner = new FakeReasoner
        {
            OnReason = (_, m, _, _) => m.History.Count == 0
                ? new ReasonResult(NextAction.Act("click"), false)
                : new ReasonResult(NextAction.Done, false),
            OnVerify = (_, _, _, _, _) => new VerifyResult(PostconditionVerdict.Met, false),
        };
        var actuator = new FakeActuator();
        var runner = AgenticTestKit.Runner(perceiver, reasoner, actuator, new FakeSafetyGate());

        await runner.RunAsync(Obj, Opts(settleStable: 2, settleMaxPolls: 5), CancellationToken.None);

        Assert.Equal("after2", reasoner.LastVerifyAfter!.ContentHash); // verified on the SETTLED frame
        Assert.Equal("before", reasoner.LastVerifyBefore!.ContentHash);
    }

    // 10
    [Fact]
    public async Task SettleDeadline_Elapses_VerifiesOnLastFrame_NoHang()
    {
        // settle never stabilizes (every poll a new hash) ⇒ bounded by SettleMaxPolls
        var perceiver = new FakePerceiver(
            AgenticTestKit.Screen("before"),
            AgenticTestKit.Screen("p1"),
            AgenticTestKit.Screen("p2"),
            AgenticTestKit.Screen("p3"),
            AgenticTestKit.Screen("p4"),
            AgenticTestKit.Screen("p5"));   // last settle frame, then repeats p5
        var reasoner = new FakeReasoner
        {
            OnReason = (_, m, _, _) => m.History.Count == 0
                ? new ReasonResult(NextAction.Act("click"), false)
                : new ReasonResult(NextAction.Done, false),
            OnVerify = (_, _, _, _, _) => new VerifyResult(PostconditionVerdict.Met, false),
        };
        var actuator = new FakeActuator();
        var runner = AgenticTestKit.Runner(perceiver, reasoner, actuator, new FakeSafetyGate());

        var result = await runner.RunAsync(Obj, Opts(settleStable: 2, settleMaxPolls: 5), CancellationToken.None);

        Assert.Equal(1, reasoner.VerifyCalls);
        Assert.Equal("p5", reasoner.LastVerifyAfter!.ContentHash); // verified on the last polled frame
        Assert.Equal(TerminationReason.Done, result.Termination);
    }

    // 11
    [Fact]
    public async Task NoProgress_SameScreenHashAndChosenAction_TerminatesNoProgress()
    {
        var perceiver = new FakePerceiver(AgenticTestKit.Screen("h"));
        var sameAction = NextAction.Act("click");
        var reasoner = new FakeReasoner
        {
            OnReason = (_, _, _, _) => new ReasonResult(sameAction, false),
            OnVerify = (_, _, _, _, _) => new VerifyResult(PostconditionVerdict.NotMet, false),
        };
        var actuator = new FakeActuator { OnAct = (_, _) => new ActOutcome(ActStatus.Rejected, "label_not_found") };
        var runner = AgenticTestKit.Runner(perceiver, reasoner, actuator, new FakeSafetyGate());

        var result = await runner.RunAsync(Obj, Opts(maxSteps: 25, noProgress: 3), CancellationToken.None);

        Assert.Equal(TerminationReason.NoProgress, result.Termination);
        Assert.Equal(3, result.StepCount);
        Assert.True(result.EscalationEmitted);
    }

    // 12
    [Fact]
    public async Task ReasonerEscalates_TerminatesEscalated_NoActuation()
    {
        var perceiver = new FakePerceiver(AgenticTestKit.Screen("h"));
        var reasoner = new FakeReasoner
        {
            OnReason = (_, _, _, _) => new ReasonResult(NextAction.Escalate("multi-action decision rejected"), false),
        };
        var actuator = new FakeActuator();
        var runner = AgenticTestKit.Runner(perceiver, reasoner, actuator, new FakeSafetyGate());

        var result = await runner.RunAsync(Obj, Opts(), CancellationToken.None);

        Assert.Equal(TerminationReason.Escalated, result.Termination);
        Assert.Empty(actuator.Calls);
        Assert.True(result.EscalationEmitted);
    }

    // 13
    [Fact]
    public async Task CloudCallSubBudgetExhausted_FallsBackToLocalVerify_ThenBudgetExhausted()
    {
        var perceiver = new FakePerceiver(AgenticTestKit.Screen("h"));
        var reasoner = new FakeReasoner
        {
            // reason needs cloud; when denied (allowCloud=false) it can't decide ⇒ CloudDenied
            OnReason = (_, _, _, allowCloud) => allowCloud
                ? new ReasonResult(NextAction.Act("click"), UsedCloud: true)
                : new ReasonResult(NextAction.Escalate("needs cloud"), UsedCloud: false, CloudDenied: true),
            // verify uses cloud when allowed, else local fallback
            OnVerify = (_, _, _, _, allowCloud) => allowCloud
                ? new VerifyResult(PostconditionVerdict.NotMet, UsedCloud: true)
                : new VerifyResult(PostconditionVerdict.NotMet, UsedCloud: false),
        };
        var actuator = new FakeActuator();
        var runner = AgenticTestKit.Runner(perceiver, reasoner, actuator, new FakeSafetyGate());

        // budget = 1 cloud call: step1 reason consumes it; step1 verify must fall back local; step2 reason denied
        var result = await runner.RunAsync(Obj, Opts(maxCloud: 1), CancellationToken.None);

        Assert.Equal(TerminationReason.BudgetExhausted, result.Termination);
        Assert.Equal(1, result.CloudCallsUsed);
        Assert.Contains(false, reasoner.VerifyAllowCloud); // verify fell back to local
        Assert.True(result.EscalationEmitted);
    }

    // 14
    [Fact]
    public async Task PhiNeverEgressesUnscrubbed_AssertScrubbedGuardsEveryFrame()
    {
        var perceiver = new FakePerceiver(AgenticTestKit.Screen("h", scrubbed: false));
        var reasoner = new FakeReasoner();
        var actuator = new FakeActuator();
        var safety = new FakeSafetyGate { AssertScrubbedFn = s => s.Scrubbed };
        var runner = AgenticTestKit.Runner(perceiver, reasoner, actuator, safety);

        var result = await runner.RunAsync(Obj, Opts(), CancellationToken.None);

        Assert.Equal(TerminationReason.GateDenied, result.Termination);
        Assert.Equal(0, reasoner.ReasonCalls);   // unscrubbed frame NEVER reached reasoning
        Assert.Empty(actuator.Calls);
    }

    // 15
    [Fact]
    public async Task PerceiveReturnsNull_CountsAsStrike_EscalatesAfterThreshold()
    {
        var perceiver = new FakePerceiver(null, null, null);
        var reasoner = new FakeReasoner();
        var actuator = new FakeActuator();
        var runner = AgenticTestKit.Runner(perceiver, reasoner, actuator, new FakeSafetyGate());

        var result = await runner.RunAsync(Obj, Opts(maxSteps: 25, nullLimit: 3), CancellationToken.None);

        Assert.Equal(TerminationReason.NoProgress, result.Termination);
        Assert.Equal(0, reasoner.ReasonCalls);
        Assert.True(result.EscalationEmitted);
    }

    // 16 — see ContextAccumulatorTests (immutability + bounds + no-progress covered there)

    // 17
    [Fact]
    public async Task ReasonerReceivesObjectiveAndPriorActions_InWorkingMemory()
    {
        var perceiver = new FakePerceiver(AgenticTestKit.Screen("h"));
        var reasoner = new FakeReasoner
        {
            OnReason = (_, m, _, _) => m.History.Count == 0
                ? new ReasonResult(NextAction.Act("clickFirst"), false)
                : new ReasonResult(NextAction.Done, false),
            OnVerify = (_, _, _, _, _) => new VerifyResult(PostconditionVerdict.Met, false),
        };
        var actuator = new FakeActuator();
        var runner = AgenticTestKit.Runner(perceiver, reasoner, actuator, new FakeSafetyGate());

        await runner.RunAsync(Obj, Opts(), CancellationToken.None);

        // second reason call saw the objective verbatim + the prior step's action
        var secondMemory = reasoner.ReasonMemories[1];
        Assert.Equal(Obj, secondMemory.Objective);
        Assert.Single(secondMemory.History);
        Assert.Equal("clickFirst()", secondMemory.History[0].ActionSignature);
        Assert.Equal(PostconditionVerdict.Met, secondMemory.History[0].Verdict);
    }
}
