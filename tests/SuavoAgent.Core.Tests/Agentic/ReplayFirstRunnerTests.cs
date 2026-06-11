using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Core.Agentic;
using Xunit;

namespace SuavoAgent.Core.Tests.Agentic;

/// <summary>
/// MOAT Increment 2 — the replay-first hit / miss / fallthrough matrix (design §Increment 2 validation),
/// driven by a <see cref="FakeSafetyGate"/> + a scripted sim app, with the agentic loop's scripted
/// <see cref="FakeReasoner"/> proving the loop is SKIPPED on a hit and REACHED on every miss/drift:
///   eligible + entry match            → replay, skip loop, record(true)
///   success_count &lt; 2              → loop (no IO at all)
///   type-bearing skill (flag off)     → loop (v1 hard rule)
///   entry StateHash mismatch          → loop, SILENT (no actuation, no decay)
///   mid-flow drift / dead button      → record(false), loop perceives fresh
///   supervised (dry-run / no autonomy)→ stand down to the loop's dry-run + escalate boundary
/// </summary>
public class ReplayFirstRunnerTests
{
    private static readonly AgentObjective Obj = new("compute seven eight", "task.calc", "ph");

    private static AgenticLoopOptions RealRunOptions(bool dryRun = false) => new()
    {
        DryRun = dryRun,
        SettleMaxPolls = 2,
        SettleStableReads = 1,
        SettlePollInterval = TimeSpan.Zero,
    };

    // ---- scripted sim (same shape as VerifiedSkillReplayRoundTripTests, plus counters) ----

    private sealed class ScriptedApp
    {
        private readonly string[] _states;
        private readonly string[] _expected;
        public int Index;
        public int RealActs;
        public bool Scrubbed = true;

        public ScriptedApp(string[] states, string[] expectedSigs)
        {
            _states = states;
            _expected = expectedSigs;
        }

        public PerceivedScreen Screen() => new(_states[Index], Scrubbed, new[] { "button:Seven" }, "Calculator");

        public void Act(string sig)
        {
            RealActs++;
            if (Index < _expected.Length && sig == _expected[Index]) Index++;
        }
    }

    private sealed class SimPerceiver(ScriptedApp app) : IPerceiver
    {
        public int Calls;
        public Task<PerceivedScreen?> PerceiveAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<PerceivedScreen?>(app.Screen());
        }
    }

    private sealed class SimActuator(ScriptedApp app) : IActuator
    {
        public Task<ActOutcome> ActAsync(NextAction action, ActuationContext ctx, CancellationToken ct)
        {
            if (!ctx.DryRun) app.Act(action.Signature());
            return Task.FromResult(new ActOutcome(ActStatus.Success, DryRun: ctx.DryRun));
        }
    }

    // ---- banked-step builders ----

    private static VerifiedStep ClickStep(string state, string label)
    {
        var ps = new Dictionary<string, object?> { ["label"] = label, ["process_name"] = "calc.exe" };
        var action = NextAction.Act("click_by_label", ps);
        var paramsJson = JsonSerializer.Serialize(
            new Dictionary<string, string> { ["label"] = label, ["process_name"] = "calc.exe" });
        return new VerifiedStep(state, action.Signature(), "click_by_label", paramsJson);
    }

    private static VerifiedStep TypeStep(string state, string text)
    {
        var action = NextAction.Act("type_into_field", new Dictionary<string, object?> { ["text"] = text });
        return new VerifiedStep(state, action.Signature(), "type_into_field",
            JsonSerializer.Serialize(new Dictionary<string, string> { ["text"] = text }));
    }

    private static VerifiedSkill TwoClickSkill() => VerifiedSkill.Create("ph", "task.calc", "calc.exe",
        new List<VerifiedStep> { ClickStep("s0", "Seven"), ClickStep("s1", "Eight") });

    private static string[] HappySigs(VerifiedSkill skill)
        => [skill.Steps[0].ActionSignature, skill.Steps[1].ActionSignature];

    // ---- the handler-shaped contract: replay Completed ⇒ skip the loop; ANYTHING else ⇒ run it ----

    private static async Task<(ReplayFirstResult Rf, int ReasonerCalls)> RunHandlerShaped(
        ReplayFirstRunner runner, VerifiedSkill skill, int successCount, AgenticLoopOptions options,
        List<(string SkillId, bool Ok)> recorded, FakeSafetyGate loopGate,
        bool allowTypeSteps = false, CancellationToken ct = default)
    {
        var rf = await runner.TryReplayAsync(
            skill, successCount, Obj, options, allowTypeSteps,
            (id, ok) => recorded.Add((id, ok)), ct);

        var reasoner = new FakeReasoner(); // scripted sim reasoner: returns Done on first consult
        if (!rf.ReplayCompleted)
        {
            var loop = new AgenticLoopRunner(
                new FakePerceiver(AgenticTestKit.Screen("loop-screen")),
                reasoner, new FakeActuator(), loopGate, null, AgenticTestKit.NoDelay);
            await loop.RunAsync(Obj, options, CancellationToken.None);
        }
        return (rf, reasoner.ReasonCalls);
    }

    // =====================================================================================
    // HIT: eligible + healthy + entry fingerprint match → deterministic replay, loop skipped
    // =====================================================================================

    [Fact]
    public async Task EligibleAndEntryMatch_Replays_SkipsLoop_RecordsSuccess()
    {
        var skill = TwoClickSkill();
        var app = new ScriptedApp(["s0", "s1", "done"], HappySigs(skill));
        var perceiver = new SimPerceiver(app);
        var gate = new FakeSafetyGate();
        var runner = new ReplayFirstRunner(perceiver, new SimActuator(app), gate, AgenticTestKit.NoDelay);
        var recorded = new List<(string, bool)>();

        var (rf, reasonerCalls) = await RunHandlerShaped(runner, skill, successCount: 2, RealRunOptions(), recorded, gate);

        Assert.True(rf.ReplayCompleted);
        Assert.Null(rf.SkipReason);
        Assert.Equal(SkillReplayOutcome.Completed, rf.Replay!.Outcome);
        Assert.Equal(2, rf.Replay.StepsCompleted);
        Assert.Equal(2, app.Index);                              // both banked clicks really executed
        Assert.Equal(0, reasonerCalls);                          // ZERO LLM/reasoning — the loop never ran
        Assert.Equal(new[] { (skill.SkillId, true) }, recorded);
    }

    // =====================================================================================
    // MISSES: each one must reach the agentic loop and must not actuate or decay anything
    // =====================================================================================

    [Fact]
    public async Task SuccessCountBelowThreshold_SkipsBeforeAnyIo_FallsToLoop()
    {
        var skill = TwoClickSkill();
        var app = new ScriptedApp(["s0", "s1", "done"], HappySigs(skill));
        var perceiver = new SimPerceiver(app);
        var gate = new FakeSafetyGate();
        var runner = new ReplayFirstRunner(perceiver, new SimActuator(app), gate, AgenticTestKit.NoDelay);
        var recorded = new List<(string, bool)>();

        var (rf, reasonerCalls) = await RunHandlerShaped(runner, skill, successCount: 1, RealRunOptions(), recorded, gate);

        Assert.False(rf.ReplayCompleted);
        Assert.Equal("success_count_below_threshold", rf.SkipReason);
        Assert.Equal(0, perceiver.Calls);   // eligibility refused before a single IPC perceive
        Assert.Equal(0, app.RealActs);
        Assert.Empty(recorded);             // a skip is never a skill fault
        Assert.Equal(1, reasonerCalls);     // the loop ran
    }

    [Fact]
    public async Task TypeBearingSkill_FlagOff_SkipsReplay_FallsToLoop()
    {
        // v1 HARD RULE: a banked literal would type the OLD value — never auto-replay type/press.
        var skill = VerifiedSkill.Create("ph", "task.calc", "calc.exe",
            new List<VerifiedStep> { ClickStep("s0", "Seven"), TypeStep("s1", "12.99") });
        var app = new ScriptedApp(["s0", "s1", "done"], HappySigs(skill));
        var perceiver = new SimPerceiver(app);
        var gate = new FakeSafetyGate();
        var runner = new ReplayFirstRunner(perceiver, new SimActuator(app), gate, AgenticTestKit.NoDelay);
        var recorded = new List<(string, bool)>();

        var (rf, reasonerCalls) = await RunHandlerShaped(runner, skill, successCount: 5, RealRunOptions(), recorded, gate);

        Assert.False(rf.ReplayCompleted);
        Assert.Equal("type_bearing_step:type_into_field", rf.SkipReason);
        Assert.Equal(0, perceiver.Calls);
        Assert.Equal(0, app.RealActs);
        Assert.Empty(recorded);
        Assert.Equal(1, reasonerCalls);
    }

    [Fact]
    public async Task EntryFingerprintMismatch_SilentSkip_NoActuation_NoDecay_FallsToLoop()
    {
        var skill = TwoClickSkill();
        var app = new ScriptedApp(["somewhere-else", "s1", "done"], HappySigs(skill));
        var perceiver = new SimPerceiver(app);
        var gate = new FakeSafetyGate();
        var runner = new ReplayFirstRunner(perceiver, new SimActuator(app), gate, AgenticTestKit.NoDelay);
        var recorded = new List<(string, bool)>();

        var (rf, reasonerCalls) = await RunHandlerShaped(runner, skill, successCount: 2, RealRunOptions(), recorded, gate);

        Assert.False(rf.ReplayCompleted);
        Assert.Equal("state_fingerprint_mismatch", rf.SkipReason);
        Assert.Null(rf.Replay);
        Assert.Equal(1, perceiver.Calls);   // exactly the one precheck perceive was spent
        Assert.Equal(0, app.RealActs);      // NEVER act on a screen we don't recognise
        Assert.Empty(recorded);             // an unrelated screen is not the skill's fault — no decay
        Assert.Equal(1, reasonerCalls);
    }

    // =====================================================================================
    // DRIFT / SKILL FAULTS: replay started, reality diverged → record(false) + loop reasons fresh
    // =====================================================================================

    [Fact]
    public async Task MidFlowDrift_StateMismatch_RecordsFailure_FallsToLoop()
    {
        var skill = TwoClickSkill();
        // Entry state matches; the state AFTER step 0 was mutated (UI changed since banking).
        var app = new ScriptedApp(["s0", "MUTATED", "done"], HappySigs(skill));
        var perceiver = new SimPerceiver(app);
        var gate = new FakeSafetyGate();
        var runner = new ReplayFirstRunner(perceiver, new SimActuator(app), gate, AgenticTestKit.NoDelay);
        var recorded = new List<(string, bool)>();

        var (rf, reasonerCalls) = await RunHandlerShaped(runner, skill, successCount: 3, RealRunOptions(), recorded, gate);

        Assert.False(rf.ReplayCompleted);
        Assert.Equal(SkillReplayOutcome.StateMismatch, rf.Replay!.Outcome);
        Assert.Equal(1, rf.Replay.StepsCompleted);               // step 0 executed; step 1 refused on drift
        Assert.Equal("replay_outcome:StateMismatch", rf.SkipReason);
        Assert.Equal(new[] { (skill.SkillId, false) }, recorded);
        Assert.Equal(1, reasonerCalls);                          // the loop reasons from the CURRENT screen
    }

    [Fact]
    public async Task DeadButton_PostconditionFailed_RecordsFailure_FallsToLoop()
    {
        var skill = TwoClickSkill();
        // Actuator reports Success but the screen never changes (dead button) → postcondition fails.
        var app = new ScriptedApp(["s0", "s1", "done"], ["never-matching-signature", "x"]);
        var perceiver = new SimPerceiver(app);
        var gate = new FakeSafetyGate();
        var runner = new ReplayFirstRunner(perceiver, new SimActuator(app), gate, AgenticTestKit.NoDelay);
        var recorded = new List<(string, bool)>();

        var (rf, reasonerCalls) = await RunHandlerShaped(runner, skill, successCount: 2, RealRunOptions(), recorded, gate);

        Assert.False(rf.ReplayCompleted);
        Assert.Equal(SkillReplayOutcome.PostconditionFailed, rf.Replay!.Outcome);
        Assert.Equal("replay_outcome:PostconditionFailed", rf.SkipReason);
        Assert.Equal(new[] { (skill.SkillId, false) }, recorded);
        Assert.Equal(1, reasonerCalls);
    }

    [Fact]
    public async Task CancelledReplay_EnvironmentalOutcome_NoRecord_FallsThrough()
    {
        var skill = TwoClickSkill();
        var app = new ScriptedApp(["s0", "s1", "done"], HappySigs(skill));
        var gate = new FakeSafetyGate();
        var runner = new ReplayFirstRunner(new SimPerceiver(app), new SimActuator(app), gate, AgenticTestKit.NoDelay);
        var recorded = new List<(string, bool)>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var rf = await runner.TryReplayAsync(skill, 2, Obj, RealRunOptions(), false,
            (id, ok) => recorded.Add((id, ok)), cts.Token);

        Assert.False(rf.ReplayCompleted);
        Assert.Equal(SkillReplayOutcome.Cancelled, rf.Replay!.Outcome);
        Assert.Empty(recorded);             // cancellation is environmental — never decays the skill
        Assert.Equal(0, app.RealActs);
    }

    // =====================================================================================
    // SUPERVISED / GATED: replay is still actuation — it must stand down to the loop's
    // dry-run + escalate operator boundary, never execute around it, never decay the skill.
    // =====================================================================================

    [Fact]
    public async Task DryRunRun_SkipsReplay_NoIo_FallsToLoop()
    {
        var skill = TwoClickSkill();
        var app = new ScriptedApp(["s0", "s1", "done"], HappySigs(skill));
        var perceiver = new SimPerceiver(app);
        var gate = new FakeSafetyGate();
        var runner = new ReplayFirstRunner(perceiver, new SimActuator(app), gate, AgenticTestKit.NoDelay);
        var recorded = new List<(string, bool)>();

        var (rf, reasonerCalls) = await RunHandlerShaped(runner, skill, 2, RealRunOptions(dryRun: true), recorded, gate);

        Assert.False(rf.ReplayCompleted);
        Assert.Equal("run_is_dry_run", rf.SkipReason);
        Assert.Equal(0, perceiver.Calls);   // a replay that cannot legally complete spends nothing
        Assert.Equal(0, app.RealActs);
        Assert.Empty(recorded);             // and must NOT decay a healthy skill
        Assert.Equal(1, reasonerCalls);
    }

    [Fact]
    public async Task AutonomyNotEarned_GateDryRun_SkipsReplay_NoActuation_NoDecay()
    {
        var skill = TwoClickSkill();
        var app = new ScriptedApp(["s0", "s1", "done"], HappySigs(skill));
        var perceiver = new SimPerceiver(app);
        var gate = new FakeSafetyGate
        {
            GateActionFn = (_, _) => SafetyVerdict.Dry("supervised_autonomy_not_earned"),
        };
        var runner = new ReplayFirstRunner(perceiver, new SimActuator(app), gate, AgenticTestKit.NoDelay);
        var recorded = new List<(string, bool)>();

        var rf = await runner.TryReplayAsync(skill, 2, Obj, RealRunOptions(), false,
            (id, ok) => recorded.Add((id, ok)), CancellationToken.None);

        Assert.False(rf.ReplayCompleted);
        Assert.Equal("supervised_dry_run:supervised_autonomy_not_earned", rf.SkipReason);
        Assert.Equal(1, perceiver.Calls);   // fingerprint matched, then the autonomy boundary held
        Assert.Equal(0, app.RealActs);      // nothing executed without earned autonomy
        Assert.Empty(recorded);
    }

    [Fact]
    public async Task GateDeny_SkipsReplay_NoActuation()
    {
        var skill = TwoClickSkill();
        var app = new ScriptedApp(["s0", "s1", "done"], HappySigs(skill));
        var gate = new FakeSafetyGate { GateActionFn = (_, _) => SafetyVerdict.Denied("verb_not_allowed") };
        var runner = new ReplayFirstRunner(new SimPerceiver(app), new SimActuator(app), gate, AgenticTestKit.NoDelay);
        var recorded = new List<(string, bool)>();

        var rf = await runner.TryReplayAsync(skill, 2, Obj, RealRunOptions(), false,
            (id, ok) => recorded.Add((id, ok)), CancellationToken.None);

        Assert.Equal("gate_denied:verb_not_allowed", rf.SkipReason);
        Assert.Equal(0, app.RealActs);
        Assert.Empty(recorded);
    }

    [Fact]
    public async Task PreflightDeny_SkipsBeforeAnyPerceive()
    {
        var skill = TwoClickSkill();
        var app = new ScriptedApp(["s0", "s1", "done"], HappySigs(skill));
        var perceiver = new SimPerceiver(app);
        var gate = new FakeSafetyGate { PreflightFn = _ => SafetyVerdict.Denied("kill_switch") };
        var runner = new ReplayFirstRunner(perceiver, new SimActuator(app), gate, AgenticTestKit.NoDelay);

        var rf = await runner.TryReplayAsync(skill, 2, Obj, RealRunOptions(), false, (_, _) => { }, CancellationToken.None);

        Assert.Equal("preflight_denied:kill_switch", rf.SkipReason);
        Assert.Equal(0, perceiver.Calls);   // no IPC under a tripped kill-switch
        Assert.Equal(0, app.RealActs);
    }

    [Fact]
    public async Task UnscrubbedPrecheckFrame_Skips_NoActuation()
    {
        var skill = TwoClickSkill();
        var app = new ScriptedApp(["s0", "s1", "done"], HappySigs(skill)) { Scrubbed = false };
        var gate = new FakeSafetyGate();
        var runner = new ReplayFirstRunner(new SimPerceiver(app), new SimActuator(app), gate, AgenticTestKit.NoDelay);

        var rf = await runner.TryReplayAsync(skill, 2, Obj, RealRunOptions(), false, (_, _) => { }, CancellationToken.None);

        Assert.Equal("precheck_unscrubbed_frame", rf.SkipReason);
        Assert.Equal(0, app.RealActs);
    }

    [Fact]
    public async Task Step0Unreconstructable_RecordsFault_SkipsWithoutActuation()
    {
        // Structured verb with corrupt ParamsJson: the replayer would refuse it at step 0 (Unparseable)
        // — the pre-check records the same fault class without dispatching anything.
        var bad = new VerifiedStep("s0", "click_by_label(label=Seven)", "click_by_label", "{not json");
        var skill = VerifiedSkill.Create("ph", "task.calc", "calc.exe", new List<VerifiedStep> { bad });
        var app = new ScriptedApp(["s0", "done"], [bad.ActionSignature]);
        var gate = new FakeSafetyGate();
        var runner = new ReplayFirstRunner(new SimPerceiver(app), new SimActuator(app), gate, AgenticTestKit.NoDelay);
        var recorded = new List<(string, bool)>();

        var rf = await runner.TryReplayAsync(skill, 2, Obj, RealRunOptions(), false,
            (id, ok) => recorded.Add((id, ok)), CancellationToken.None);

        Assert.Equal("step0_unreconstructable", rf.SkipReason);
        Assert.Equal(new[] { (skill.SkillId, false) }, recorded);
        Assert.Equal(0, app.RealActs);
    }

    // =====================================================================================
    // Eligibility unit facts — the v1 click-only hard rule, fail-closed verb resolution
    // =====================================================================================

    [Fact]
    public void Eligibility_ClickFamilyIncludingLaunch_IsEligible()
    {
        var steps = new List<VerifiedStep>
        {
            new("s0", "launch_sandbox_app(app_key=calculator)", "launch_sandbox_app",
                JsonSerializer.Serialize(new Dictionary<string, string> { ["app_key"] = "calculator" })),
            ClickStep("s1", "Seven"),
            new("s2", "click_by_signature(signature=Button|Eight)", "click_by_signature",
                JsonSerializer.Serialize(new Dictionary<string, string> { ["signature"] = "Button|Eight" })),
        };

        Assert.True(ReplayFirstRunner.IsEligible(steps, 2, allowTypeSteps: false, out var reason));
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("type_into_field")]
    [InlineData("press_keys")]
    public void Eligibility_TypeFamily_FlagOff_Refuses_FlagOn_Allows(string verb)
    {
        var steps = new List<VerifiedStep>
        {
            ClickStep("s0", "Seven"),
            new("s1", $"{verb}(x=y)", verb, JsonSerializer.Serialize(new Dictionary<string, string> { ["x"] = "y" })),
        };

        Assert.False(ReplayFirstRunner.IsEligible(steps, 9, allowTypeSteps: false, out var reason));
        Assert.Equal($"type_bearing_step:{verb}", reason);

        // Agent:ReplayFirstAllowTypeSteps is declared (OFF in v1) — this pins its intended semantics.
        Assert.True(ReplayFirstRunner.IsEligible(steps, 9, allowTypeSteps: true, out reason));
        Assert.Null(reason);
    }

    [Fact]
    public void Eligibility_UnknownVerb_Refuses()
    {
        var steps = new List<VerifiedStep> { new("s0", "run_macro(name=m1)", "run_macro", "{}") };

        Assert.False(ReplayFirstRunner.IsEligible(steps, 9, false, out var reason));
        Assert.Equal("non_click_step:run_macro", reason);
    }

    [Fact]
    public void Eligibility_LegacySigOnlyRows_ResolveVerbFromSignaturePrefix()
    {
        var legacyClick = new List<VerifiedStep> { new("s0", "click_by_label(label=Seven,process_name=calc.exe)") };
        Assert.True(ReplayFirstRunner.IsEligible(legacyClick, 2, false, out _));

        var legacyType = new List<VerifiedStep> { new("s0", "type_into_field(text=hello)") };
        Assert.False(ReplayFirstRunner.IsEligible(legacyType, 2, false, out var reason));
        Assert.Equal("type_bearing_step:type_into_field", reason);

        var garbage = new List<VerifiedStep> { new("s0", "no-parens-here") };
        Assert.False(ReplayFirstRunner.IsEligible(garbage, 2, false, out reason));
        Assert.Equal("unresolvable_step_verb", reason);
    }

    [Fact]
    public void Eligibility_EmptyOrThin_Refuses()
    {
        Assert.False(ReplayFirstRunner.IsEligible(new List<VerifiedStep>(), 9, false, out var reason));
        Assert.Equal("no_steps", reason);

        var steps = new List<VerifiedStep> { ClickStep("s0", "Seven") };
        Assert.False(ReplayFirstRunner.IsEligible(steps, ReplayFirstRunner.MinSuccessCount - 1, false, out reason));
        Assert.Equal("success_count_below_threshold", reason);
    }
}
