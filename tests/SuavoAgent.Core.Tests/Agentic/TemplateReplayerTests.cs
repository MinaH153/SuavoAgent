using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Core.Agentic;
using SuavoAgent.Core.Agentic.Replication;
using Xunit;

namespace SuavoAgent.Core.Tests.Agentic;

/// <summary>
/// Deterministic template replay through the fail-closed actuator/safety seam. Fakes only; proves
/// gate→act→verify control flow, fail-closed stops with the offending ordinal, and signature-based checks.
/// </summary>
public sealed class TemplateReplayerTests
{
    private static readonly AgentObjective Obj = AgenticTestKit.Objective;
    private static readonly ActuationContext Ctx = new("ph.test", "task.replay", DryRun: false);
    private static readonly TemplateReplayOptions Opts = new() { SettleStableReads = 1, SettleMaxPolls = 3 };

    private static ElementSignature Sig(string auto) => new("Button", auto, null);

    private static PerceivedScreen ScreenWith(params string[] signatures) =>
        new("h", Scrubbed: true, Array.Empty<string>(), null, signatures);

    private static CompiledStep Click(int ord) =>
        new(ord, NextAction.Act("click_by_signature", new Dictionary<string, object?> { ["automationId"] = "b" }),
            IsCheck: false, IsWrite: false, new[] { Sig("b") }, null);

    private static CompiledStep Check(int ord, ElementSignature expect) =>
        new(ord, null, IsCheck: true, IsWrite: false, new[] { expect }, null);

    private static CompiledStep Writeback(int ord, ElementSignature after) =>
        new(ord, NextAction.Act("click_by_signature", new Dictionary<string, object?> { ["automationId"] = "save" }),
            IsCheck: false, IsWrite: true, new[] { Sig("save") }, new[] { after });

    private static TemplateReplayer Replayer(FakePerceiver p, FakeActuator a, FakeSafetyGate s) =>
        new(p, a, s, AgenticTestKit.NoDelay);

    [Fact]
    public async Task Replay_AllActuatingStepsSucceed_Completed()
    {
        var actuator = new FakeActuator();
        var replayer = Replayer(new FakePerceiver(), actuator, new FakeSafetyGate());

        var result = await replayer.ReplayAsync(new[] { Click(0), Click(1) }, Obj, Ctx, Opts, CancellationToken.None);

        Assert.Equal(ReplayOutcome.Completed, result.Outcome);
        Assert.Equal(2, result.StepsCompleted);
        Assert.Equal(2, actuator.Calls.Count);
    }

    [Fact]
    public async Task Replay_GateDenies_StopsGateDenied_WithOrdinal_NoFurtherActs()
    {
        var actuator = new FakeActuator();
        var safety = new FakeSafetyGate { GateActionFn = (_, _) => SafetyVerdict.Denied("kill_switch") };
        var replayer = Replayer(new FakePerceiver(), actuator, safety);

        var result = await replayer.ReplayAsync(new[] { Click(0), Click(1) }, Obj, Ctx, Opts, CancellationToken.None);

        Assert.Equal(ReplayOutcome.GateDenied, result.Outcome);
        Assert.Equal(0, result.FailedOrdinal);
        Assert.Empty(actuator.Calls);
    }

    [Fact]
    public async Task Replay_ActionRejected_StopsActionFailed_AtThatOrdinal()
    {
        var actuator = new FakeActuator { OnAct = (_, _) => new ActOutcome(ActStatus.Rejected, "label_not_found") };
        var replayer = Replayer(new FakePerceiver(), actuator, new FakeSafetyGate());

        var result = await replayer.ReplayAsync(new[] { Click(0), Click(1) }, Obj, Ctx, Opts, CancellationToken.None);

        Assert.Equal(ReplayOutcome.ActionFailed, result.Outcome);
        Assert.Equal(0, result.FailedOrdinal);
        Assert.Equal("label_not_found", result.Detail);
        Assert.Single(actuator.Calls); // stopped after the first failed step
    }

    [Fact]
    public async Task Replay_CheckStep_ExpectedSignaturePresent_Passes()
    {
        var perceiver = new FakePerceiver(ScreenWith("Button|target"));
        var replayer = Replayer(perceiver, new FakeActuator(), new FakeSafetyGate());

        var result = await replayer.ReplayAsync(new[] { Check(0, Sig("target")) }, Obj, Ctx, Opts, CancellationToken.None);

        Assert.Equal(ReplayOutcome.Completed, result.Outcome);
    }

    [Fact]
    public async Task Replay_CheckStep_ExpectedSignatureMissing_CheckFailed()
    {
        var perceiver = new FakePerceiver(ScreenWith("Button|somethingElse"));
        var replayer = Replayer(perceiver, new FakeActuator(), new FakeSafetyGate());

        var result = await replayer.ReplayAsync(new[] { Check(0, Sig("target")) }, Obj, Ctx, Opts, CancellationToken.None);

        Assert.Equal(ReplayOutcome.CheckFailed, result.Outcome);
        Assert.Equal(0, result.FailedOrdinal);
    }

    [Fact]
    public async Task Replay_Writeback_ExpectedAfterPresent_Completes()
    {
        var perceiver = new FakePerceiver(ScreenWith("Button|confirmed")); // the post-action settle frame
        var replayer = Replayer(perceiver, new FakeActuator(), new FakeSafetyGate());

        var result = await replayer.ReplayAsync(new[] { Writeback(0, Sig("confirmed")) }, Obj, Ctx, Opts, CancellationToken.None);

        Assert.Equal(ReplayOutcome.Completed, result.Outcome);
    }

    [Fact]
    public async Task Replay_Writeback_ExpectedAfterMissing_PostconditionFailed()
    {
        var perceiver = new FakePerceiver(ScreenWith("Button|nope"));
        var replayer = Replayer(perceiver, new FakeActuator(), new FakeSafetyGate());

        var result = await replayer.ReplayAsync(new[] { Writeback(0, Sig("confirmed")) }, Obj, Ctx, Opts, CancellationToken.None);

        Assert.Equal(ReplayOutcome.PostconditionFailed, result.Outcome);
        Assert.Equal(0, result.FailedOrdinal);
    }

    [Fact]
    public async Task Replay_Cancelled_StopsCancelled()
    {
        var cts = new CancellationTokenSource();
        var actuator = new FakeActuator { OnAct = (_, c) => { cts.Cancel(); return new ActOutcome(ActStatus.Success, DryRun: c.DryRun); } };
        var replayer = Replayer(new FakePerceiver(), actuator, new FakeSafetyGate());

        var result = await replayer.ReplayAsync(new[] { Click(0), Click(1) }, Obj, Ctx, Opts, cts.Token);

        Assert.Equal(ReplayOutcome.Cancelled, result.Outcome);
        Assert.Single(actuator.Calls); // cancelled before the second step
    }

    [Fact]
    public async Task Replay_SupervisedDryRunVerdict_ActuatesDryRun()
    {
        var actuator = new FakeActuator();
        var safety = new FakeSafetyGate { GateActionFn = (_, _) => SafetyVerdict.Dry("supervised") };
        var replayer = Replayer(new FakePerceiver(), actuator, safety);

        await replayer.ReplayAsync(new[] { Click(0) }, Obj, Ctx, Opts, CancellationToken.None);

        Assert.True(actuator.Calls[0].Ctx.DryRun);
    }
}
