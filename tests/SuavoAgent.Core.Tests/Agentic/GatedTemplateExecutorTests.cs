using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Core.Agentic;
using SuavoAgent.Core.Agentic.Replication;
using Xunit;

namespace SuavoAgent.Core.Tests.Agentic;

/// <summary>
/// Safety suite for the operator-approved FSD-engage executor. Fakes only. Proves the hard invariants of
/// the EXECUTING path: click-family-only refusal, element-centric entry/drift/ExpectedAfter verification,
/// that an acting step can NEVER reach the actuator without a hard Allow from GateAction + AssertScrubbed,
/// that a dry-run verdict is a STOP (not a supervised dispatch), and that cancel halts between steps.
/// </summary>
public sealed class GatedTemplateExecutorTests
{
    private static readonly AgentObjective Obj = AgenticTestKit.Objective;
    private static readonly ActuationContext Ctx = new("ph.test", "task.run", DryRun: false);
    private static readonly TemplateReplayOptions Opts = new() { SettleStableReads = 1, SettleMaxPolls = 3 };

    private static GatedTemplateExecutor Exec(FakePerceiver p, FakeActuator a, FakeSafetyGate s) =>
        new(p, a, s, AgenticTestKit.NoDelay);

    // ── fixtures ──

    private static ElementSignature Sig(string auto) => new("Button", auto, null);

    private static ElementSignature[] Sigs(params string[] autos) => autos.Select(Sig).ToArray();

    /// <summary>A perceived screen carrying the given "Button|automationId" GREEN-tier signatures.</summary>
    private static PerceivedScreen Screen(params string[] autos) =>
        new("h", Scrubbed: true, Array.Empty<string>(), null,
            autos.Select(a => $"Button|{a}").ToArray());

    private static PerceivedScreen Unscrubbed(params string[] autos) =>
        new("h", Scrubbed: false, Array.Empty<string>(), null,
            autos.Select(a => $"Button|{a}").ToArray());

    private static TemplateStep Click(int ord, string target, string[] visible,
        string[]? after = null, bool isWrite = false) =>
        new(ord, TemplateStepKind.Click, Sig(target), Sigs(visible),
            MinElementsRequired: visible.Length,
            ExpectedAfter: after is null ? null : Sigs(after),
            IsWrite: isWrite, CorrelatedQueryShapeHash: null, StepConfidence: 0.9, Hint: null);

    private static TemplateStep NonClick(int ord, TemplateStepKind kind, string target, string[] visible) =>
        new(ord, kind, Sig(target), Sigs(visible), MinElementsRequired: visible.Length,
            ExpectedAfter: null, IsWrite: false, CorrelatedQueryShapeHash: null, StepConfidence: 0.9, Hint: null);

    private static WorkflowTemplate Template(params TemplateStep[] steps) =>
        new(TemplateId: "tid", TemplateVersion: "1.0.0", SkillId: "pricing",
            ProcessNameGlob: "notepad*", PmsVersionRange: Array.Empty<PmsVersionFingerprint>(),
            ScreenSignatureV1: "screen", StepsHash: "hash", RoutineHashOrigin: null,
            Steps: steps, AggregateConfidence: 0.9, ObservationCount: 10, HasWriteback: false,
            ExtractedAt: "2026-04-19T00:00:00Z", ExtractedBy: "test", RetiredAt: null, RetirementReason: null);

    // ──────────────────────── (b) Type / PressKey / IsWrite refused up front ────────────────────────

    [Theory]
    [InlineData(TemplateStepKind.Type)]
    [InlineData(TemplateStepKind.PressKey)]
    public async Task UnsafeKind_RefusesWholeTemplate_ActuatorAndPerceiverNeverTouched(TemplateStepKind kind)
    {
        var perceiver = new FakePerceiver(Screen("open"));
        var actuator = new FakeActuator();
        var tmpl = Template(Click(0, "open", new[] { "open" }), NonClick(1, kind, "field", new[] { "field" }));

        var result = await Exec(perceiver, actuator, new FakeSafetyGate())
            .ExecuteAsync(tmpl, Obj, Ctx, Opts, CancellationToken.None);

        Assert.Equal(GatedReplayOutcome.TypeStepsUnsupportedV1, result.Outcome);
        Assert.Equal(1, result.FailedOrdinal);
        Assert.Empty(actuator.Calls);
        Assert.Equal(0, perceiver.Calls); // refused BEFORE any perceive/actuate
    }

    [Fact]
    public async Task WritebackStep_RefusesWholeTemplate_ActuatorNeverCalled()
    {
        var actuator = new FakeActuator();
        // IsWrite step (ctor requires ExpectedAfter) — v1 refuses writebacks entirely.
        var tmpl = Template(Click(0, "save", new[] { "save" }, after: new[] { "done" }, isWrite: true));

        var result = await Exec(new FakePerceiver(Screen("save")), actuator, new FakeSafetyGate())
            .ExecuteAsync(tmpl, Obj, Ctx, Opts, CancellationToken.None);

        Assert.Equal(GatedReplayOutcome.TypeStepsUnsupportedV1, result.Outcome);
        Assert.Equal(0, result.FailedOrdinal);
        Assert.Empty(actuator.Calls);
    }

    // ──────────────────────── (c) entry mismatch → actuate nothing ────────────────────────

    [Fact]
    public async Task EntryScreenDoesNotMatch_EntryMismatch_ActuatorNeverCalled()
    {
        var actuator = new FakeActuator();
        var perceiver = new FakePerceiver(Screen("somethingElse")); // no "Button|open"
        var tmpl = Template(Click(0, "open", new[] { "open" }));

        var result = await Exec(perceiver, actuator, new FakeSafetyGate())
            .ExecuteAsync(tmpl, Obj, Ctx, Opts, CancellationToken.None);

        Assert.Equal(GatedReplayOutcome.EntryMismatch, result.Outcome);
        Assert.Equal(0, result.FailedOrdinal);
        Assert.Empty(actuator.Calls);
    }

    // ──────────────────────── (d) click happy-path → gated + actuated + ExpectedAfter verified ────────────────────────

    [Fact]
    public async Task TwoClicks_EachGatedActuatedAndPostVerified_Completed()
    {
        var actuator = new FakeActuator();
        var safety = new FakeSafetyGate();
        // per step: 1 settle to verify current screen + 1 settle for ExpectedAfter.
        var perceiver = new FakePerceiver(
            Screen("open"), Screen("mid"),   // step0 verify, step0 ExpectedAfter
            Screen("next"), Screen("done")); // step1 verify, step1 ExpectedAfter
        var tmpl = Template(
            Click(0, "open", new[] { "open" }, after: new[] { "mid" }),
            Click(1, "next", new[] { "next" }, after: new[] { "done" }));

        var result = await Exec(perceiver, actuator, safety)
            .ExecuteAsync(tmpl, Obj, Ctx, Opts, CancellationToken.None);

        Assert.Equal(GatedReplayOutcome.Completed, result.Outcome);
        Assert.Equal(2, result.StepsCompleted);
        Assert.Equal(2, actuator.Calls.Count);
        Assert.Equal(2, safety.GateActionCalls);
        Assert.All(actuator.Calls, c => Assert.Equal("click_by_signature", c.Action.Verb));
        Assert.All(actuator.Calls, c => Assert.False(c.Ctx.DryRun)); // operator-commanded ⇒ live
    }

    [Fact]
    public async Task NonClickCheckStep_VerifiesVisibleWithoutActuating()
    {
        var actuator = new FakeActuator();
        var perceiver = new FakePerceiver(Screen("panel")); // VerifyElement check settle
        var tmpl = Template(NonClick(0, TemplateStepKind.VerifyElement, "panel", new[] { "panel" }));

        var result = await Exec(perceiver, actuator, new FakeSafetyGate())
            .ExecuteAsync(tmpl, Obj, Ctx, Opts, CancellationToken.None);

        Assert.Equal(GatedReplayOutcome.Completed, result.Outcome);
        Assert.Empty(actuator.Calls); // check step never actuates
    }

    // ──────────────────────── (e) ExpectedAfter NotMet → PostconditionFailed + stop ────────────────────────

    [Fact]
    public async Task ExpectedAfterNotMet_PostconditionFailed_StopsNoFurtherSteps()
    {
        var actuator = new FakeActuator();
        var perceiver = new FakePerceiver(
            Screen("open"),   // step0 verify
            Screen("wrong")); // step0 ExpectedAfter settle — does NOT contain "Button|mid"
        var tmpl = Template(
            Click(0, "open", new[] { "open" }, after: new[] { "mid" }),
            Click(1, "next", new[] { "next" }, after: new[] { "done" }));

        var result = await Exec(perceiver, actuator, new FakeSafetyGate())
            .ExecuteAsync(tmpl, Obj, Ctx, Opts, CancellationToken.None);

        Assert.Equal(GatedReplayOutcome.PostconditionFailed, result.Outcome);
        Assert.Equal(0, result.FailedOrdinal);
        Assert.Single(actuator.Calls); // step0 actuated, step1 never reached
    }

    // ──────────────────────── (f) GateAction Deny → GateDenied, actuator not called ────────────────────────

    [Fact]
    public async Task GateActionDenies_GateDenied_ActuatorNeverCalled()
    {
        var actuator = new FakeActuator();
        var safety = new FakeSafetyGate { GateActionFn = (_, _) => SafetyVerdict.Denied("supervised_autonomy_not_earned") };
        var perceiver = new FakePerceiver(Screen("open")); // passes entry verify, REACHES the gate
        var tmpl = Template(Click(0, "open", new[] { "open" }));

        var result = await Exec(perceiver, actuator, safety)
            .ExecuteAsync(tmpl, Obj, Ctx, Opts, CancellationToken.None);

        Assert.Equal(GatedReplayOutcome.GateDenied, result.Outcome);
        Assert.Empty(actuator.Calls);
    }

    [Fact]
    public async Task GateActionDryRun_IsAStop_NeverActuatesDryRunLive()
    {
        // The safety-critical difference from the harvest TemplateReplayer: AllowDryRun is a STOP here, not a
        // supervised dry-run dispatch. An unearned/forced-dry-run step must NEVER reach the actuator.
        var actuator = new FakeActuator();
        var safety = new FakeSafetyGate { GateActionFn = (_, _) => SafetyVerdict.Dry("gate_dry_run") };
        var perceiver = new FakePerceiver(Screen("open"));
        var tmpl = Template(Click(0, "open", new[] { "open" }));

        var result = await Exec(perceiver, actuator, safety)
            .ExecuteAsync(tmpl, Obj, Ctx, Opts, CancellationToken.None);

        Assert.Equal(GatedReplayOutcome.GateDenied, result.Outcome);
        Assert.Empty(actuator.Calls);
    }

    [Fact]
    public async Task PreflightDenies_GateDenied_BeforeAnyPerceiveOrActuate()
    {
        var actuator = new FakeActuator();
        var perceiver = new FakePerceiver(Screen("open"));
        var safety = new FakeSafetyGate { PreflightFn = _ => SafetyVerdict.Denied("kill_switch") };
        var tmpl = Template(Click(0, "open", new[] { "open" }));

        var result = await Exec(perceiver, actuator, safety)
            .ExecuteAsync(tmpl, Obj, Ctx, Opts, CancellationToken.None);

        Assert.Equal(GatedReplayOutcome.GateDenied, result.Outcome);
        Assert.Equal("kill_switch", result.Detail);
        Assert.Empty(actuator.Calls);
        Assert.Equal(0, perceiver.Calls);
    }

    [Fact]
    public async Task UnscrubbedFrame_GateDenied_ActuatorNeverCalled()
    {
        var actuator = new FakeActuator();
        var perceiver = new FakePerceiver(Unscrubbed("open"));
        var tmpl = Template(Click(0, "open", new[] { "open" }));

        var result = await Exec(perceiver, actuator, new FakeSafetyGate())
            .ExecuteAsync(tmpl, Obj, Ctx, Opts, CancellationToken.None);

        Assert.Equal(GatedReplayOutcome.GateDenied, result.Outcome);
        Assert.Equal("unscrubbed_frame", result.Detail);
        Assert.Empty(actuator.Calls);
    }

    // ──────────────────────── (g) cancellation between steps → Cancelled ────────────────────────

    [Fact]
    public async Task Cancelled_BetweenSteps_HaltsCancelled()
    {
        var cts = new CancellationTokenSource();
        // Cancel the moment the first click actuates; the loop's top-of-step check then halts before step1.
        var actuator = new FakeActuator { OnAct = (_, c) => { cts.Cancel(); return new ActOutcome(ActStatus.Success, DryRun: c.DryRun); } };
        var perceiver = new FakePerceiver(Screen("open"), Screen("next"));
        var tmpl = Template(
            Click(0, "open", new[] { "open" }),   // no ExpectedAfter ⇒ advances straight to step1
            Click(1, "next", new[] { "next" }));

        var result = await Exec(perceiver, actuator, new FakeSafetyGate())
            .ExecuteAsync(tmpl, Obj, Ctx, Opts, cts.Token);

        Assert.Equal(GatedReplayOutcome.Cancelled, result.Outcome);
        Assert.Equal(1, result.StepsCompleted);
        Assert.Single(actuator.Calls); // step1 never actuated
    }

    [Fact]
    public async Task ActuatorRejects_StepRejected_StopsAtThatOrdinal()
    {
        var actuator = new FakeActuator { OnAct = (_, _) => new ActOutcome(ActStatus.Rejected, "target_not_found") };
        var perceiver = new FakePerceiver(Screen("open"));
        var tmpl = Template(Click(0, "open", new[] { "open" }));

        var result = await Exec(perceiver, actuator, new FakeSafetyGate())
            .ExecuteAsync(tmpl, Obj, Ctx, Opts, CancellationToken.None);

        Assert.Equal(GatedReplayOutcome.StepRejected, result.Outcome);
        Assert.Equal(0, result.FailedOrdinal);
        Assert.Equal("target_not_found", result.Detail);
    }

    [Fact]
    public async Task EmptyTemplate_NoSteps()
    {
        // WorkflowTemplate with an empty step list — executor refuses with NoSteps, actuates nothing.
        var actuator = new FakeActuator();
        var tmpl = Template();

        var result = await Exec(new FakePerceiver(), actuator, new FakeSafetyGate())
            .ExecuteAsync(tmpl, Obj, Ctx, Opts, CancellationToken.None);

        Assert.Equal(GatedReplayOutcome.NoSteps, result.Outcome);
        Assert.Empty(actuator.Calls);
    }
}
