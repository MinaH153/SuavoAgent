using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.Agentic;
using SuavoAgent.Core.Agentic.Adapters;
using Xunit;

namespace SuavoAgent.Core.Tests.Agentic;

/// <summary>
/// Pins deterministic skill replay (the ratchet cashing in): a banked skill replays end-to-end when the
/// live screen matches the verified path, and STOPS fail-closed on every divergence — drift (state
/// mismatch), a no-op step (postcondition not met), an unparseable signature, a denied gate, or an empty
/// skill. Replay must NEVER act on a screen it doesn't recognise.
/// </summary>
public class VerifiedSkillReplayerTests
{
    private static readonly AgentObjective Obj = new("replay calc", "task.calc", "ph");

    private static string Click(string label) =>
        NextAction.Act("click_by_label", new Dictionary<string, object?> { ["label"] = label, ["process_name"] = "calc.exe" }).Signature();

    private static VerifiedSkill TwoStep() => VerifiedSkill.Create("ph", "task.calc", "calc.exe",
        new List<VerifiedStep> { new("s0", Click("Seven")), new("s1", Click("Eight")) });

    private static AgenticLoopOptions Opts => new()
    {
        DryRun = false,
        SettleMaxPolls = 2,
        SettleStableReads = 1,
        SettlePollInterval = TimeSpan.Zero,
    };

    private static SandboxExploreSafetyGate Gate(ActuationGateState? gs = null) =>
        new(() => gs ?? new ActuationGateState(Enabled: true, DryRun: false, PausedUntilUtc: null, PauseReason: null, KillSwitchTrippedUtc: null));

    /// <summary>Drives the perceived state forward exactly as the verified path prescribes (or refuses to,
    /// per the flags) so we can exercise each replay outcome deterministically.</summary>
    private sealed class ScriptedApp
    {
        private readonly string[] _states;            // s0..s_n (last = terminal-after-final-step)
        private readonly string[] _expected;          // expected action sig per state index
        private readonly bool _noOp;                  // never advance (postcondition-failed case)
        private readonly string? _startOverride;      // wrong starting screen (drift case)
        public int Index;

        public ScriptedApp(VerifiedSkill skill, bool noOp = false, string? startOverride = null)
        {
            var n = skill.Steps.Count;
            _states = new string[n + 1];
            _expected = new string[n];
            for (var i = 0; i < n; i++) { _states[i] = skill.Steps[i].StateHash; _expected[i] = skill.Steps[i].ActionSignature; }
            _states[n] = "calc:done";
            _noOp = noOp;
            _startOverride = startOverride;
        }

        public PerceivedScreen Screen()
        {
            var hash = Index == 0 && _startOverride is not null ? _startOverride : _states[Index];
            return new PerceivedScreen(hash, Scrubbed: true, new[] { "button:Seven", "button:Eight" }, "Calculator");
        }

        public void Act(string actionSig)
        {
            if (_noOp) return;
            if (Index < _expected.Length && actionSig == _expected[Index]) Index++;
        }
    }

    private sealed class SimPerceiver(ScriptedApp app) : IPerceiver
    {
        public Task<PerceivedScreen?> PerceiveAsync(CancellationToken ct) => Task.FromResult<PerceivedScreen?>(app.Screen());
    }

    private sealed class NullPerceiver : IPerceiver
    {
        public Task<PerceivedScreen?> PerceiveAsync(CancellationToken ct) => Task.FromResult<PerceivedScreen?>(null);
    }

    private sealed class SimActuator(ScriptedApp app) : IActuator
    {
        public Task<ActOutcome> ActAsync(NextAction action, ActuationContext ctx, CancellationToken ct)
        {
            if (!ctx.DryRun) app.Act(action.Signature());
            return Task.FromResult(new ActOutcome(ActStatus.Success));
        }
    }

    private static VerifiedSkillReplayer Replayer(ScriptedApp app, ISafetyGate gate) =>
        new(new SimPerceiver(app), new SimActuator(app), gate, delay: (_, _) => Task.CompletedTask);

    [Fact]
    public async Task OnPath_ReplaysAllSteps_Completed()
    {
        var skill = TwoStep();
        var app = new ScriptedApp(skill);
        var r = await Replayer(app, Gate()).ReplayAsync(skill, Obj, Opts, default);
        Assert.Equal(SkillReplayOutcome.Completed, r.Outcome);
        Assert.Equal(2, r.StepsCompleted);
    }

    [Fact]
    public async Task WrongStartState_StopsImmediately_StateMismatch()
    {
        var skill = TwoStep();
        var app = new ScriptedApp(skill, startOverride: "calc:unexpected");
        var r = await Replayer(app, Gate()).ReplayAsync(skill, Obj, Opts, default);
        Assert.Equal(SkillReplayOutcome.StateMismatch, r.Outcome);
        Assert.Equal(0, r.StepsCompleted); // never acted on an unrecognised screen
    }

    [Fact]
    public async Task StepThatDoesNotMoveTheScreen_StopsFailClosed_PostconditionFailed()
    {
        var skill = TwoStep();
        var app = new ScriptedApp(skill, noOp: true);
        var r = await Replayer(app, Gate()).ReplayAsync(skill, Obj, Opts, default);
        Assert.Equal(SkillReplayOutcome.PostconditionFailed, r.Outcome);
        Assert.Equal(0, r.StepsCompleted);
    }

    [Fact]
    public async Task UnparseableSignature_StopsFailClosed()
    {
        // a banked step whose signature is a terminal kind (no verb()) can't reconstruct → fail-closed
        var skill = VerifiedSkill.Create("ph", "task.calc", "calc.exe",
            new List<VerifiedStep> { new("s0", "Done") });
        var app = new ScriptedApp(skill);
        var r = await Replayer(app, Gate()).ReplayAsync(skill, Obj, Opts, default);
        Assert.Equal(SkillReplayOutcome.Unparseable, r.Outcome);
        Assert.Equal(0, r.StepsCompleted);
    }

    [Fact]
    public async Task KillSwitch_DeniesPreflight_GateDenied()
    {
        var skill = TwoStep();
        var killed = new ActuationGateState(Enabled: true, DryRun: false, PausedUntilUtc: null, PauseReason: null, KillSwitchTrippedUtc: DateTimeOffset.UtcNow);
        var r = await Replayer(new ScriptedApp(skill), Gate(killed)).ReplayAsync(skill, Obj, Opts, default);
        Assert.Equal(SkillReplayOutcome.GateDenied, r.Outcome);
        Assert.Equal(0, r.StepsCompleted);
    }

    [Fact]
    public async Task NonAllowlistedProcess_DeniedPerAction()
    {
        // a skill whose action targets a non-allowlisted process must be denied at the per-action gate
        var skill = VerifiedSkill.Create("ph", "task.x", "pms.exe", new List<VerifiedStep>
        {
            new("s0", NextAction.Act("click_by_label", new Dictionary<string, object?> { ["label"] = "X", ["process_name"] = "pms.exe" }).Signature()),
        });
        var app = new ScriptedApp(skill);
        var r = await Replayer(app, Gate()).ReplayAsync(skill, Obj, Opts, default);
        Assert.Equal(SkillReplayOutcome.GateDenied, r.Outcome);
    }

    [Fact]
    public async Task NullPerception_StopsFailClosed_PerceiveFailed()
    {
        var skill = TwoStep();
        var replayer = new VerifiedSkillReplayer(new NullPerceiver(), new SimActuator(new ScriptedApp(skill)), Gate(), delay: (_, _) => Task.CompletedTask);
        var r = await replayer.ReplayAsync(skill, Obj, Opts, default);
        Assert.Equal(SkillReplayOutcome.PerceiveFailed, r.Outcome);
    }

    [Fact]
    public async Task EmptySkill_NoSteps()
    {
        var empty = new VerifiedSkill("id", "ph", "task.calc", "calc.exe", new List<VerifiedStep>(), "h");
        var r = await Replayer(new ScriptedApp(TwoStep()), Gate()).ReplayAsync(empty, Obj, Opts, default);
        Assert.Equal(SkillReplayOutcome.NoSteps, r.Outcome);
    }
}
