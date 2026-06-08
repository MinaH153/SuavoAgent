using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.Agentic;
using SuavoAgent.Core.Agentic.Adapters;
using Xunit;

namespace SuavoAgent.Core.Tests.Agentic;

/// <summary>
/// Pins the structured-params brick: the loop captures the action structurally, the harvester banks it,
/// and replay reconstructs the EXACT action from it — retiring the Codex Q2 residual (a label containing a
/// signature delimiter now replays correctly, where the legacy sig-only parse would have failed). Legacy
/// sig-only skills still replay via the parser fallback.
/// </summary>
public class VerifiedSkillStructuredParamsTests
{
    private const string Proc = "calc.exe";
    private static readonly AgentObjective Obj = new("calc", "task.calc", "ph");

    private static NextAction Click(string label) =>
        NextAction.Act("click_by_label", new Dictionary<string, object?> { ["label"] = label, ["process_name"] = Proc });

    // ── 1. The loop captures the action structurally ──

    [Fact]
    public void RecordStep_CapturesVerbAndParams()
    {
        var mem = ContextAccumulator.Start(Obj);
        mem = ContextAccumulator.RecordStep(mem, "s0", Click("Seven"), ActStatus.Success, PostconditionVerdict.Met, 8);
        var step = mem.History[^1];
        Assert.Equal("click_by_label", step.ActionVerb);
        Assert.NotNull(step.ActionParams);
        Assert.Equal("Seven", step.ActionParams!["label"]);
        Assert.Equal(Proc, step.ActionParams["process_name"]);
    }

    [Fact]
    public void RecordStep_TerminalAction_HasNoVerb()
    {
        var mem = ContextAccumulator.Start(Obj);
        mem = ContextAccumulator.RecordStep(mem, "", NextAction.Done, null, PostconditionVerdict.Met, 8);
        Assert.Null(mem.History[^1].ActionVerb);
    }

    // ── 2. The harvester banks the structured form ──

    [Fact]
    public void Harvester_BanksVerbAndParamsJson()
    {
        var p = new Dictionary<string, string> { ["label"] = "Seven", ["process_name"] = Proc };
        var step = new StepRecord("s0", Click("Seven").Signature(), ActStatus.Success, PostconditionVerdict.Met, "click_by_label", p);
        var result = new AgenticLoopResult(TerminationReason.Done, 1, 0, false, null,
            new WorkingMemory(Obj, null, new List<StepRecord> { step }, 0, 0));

        var skill = VerifiedTrajectoryHarvester.Harvest(Obj, Proc, result);

        Assert.NotNull(skill);
        var banked = skill!.Steps[0];
        Assert.Equal("click_by_label", banked.Verb);
        Assert.NotNull(banked.ParamsJson);
        Assert.Equal("Seven", JsonSerializer.Deserialize<Dictionary<string, string>>(banked.ParamsJson!)!["label"]);
    }

    [Fact]
    public void Harvester_RefusesPhiInParamValue()
    {
        var p = new Dictionary<string, string> { ["label"] = "123-45-6789", ["process_name"] = Proc }; // SSN shape
        var step = new StepRecord("s0", NextAction.Act("click_by_label",
            new Dictionary<string, object?> { ["label"] = "123-45-6789", ["process_name"] = Proc }).Signature(),
            ActStatus.Success, PostconditionVerdict.Met, "click_by_label", p);
        var result = new AgenticLoopResult(TerminationReason.Done, 1, 0, false, null,
            new WorkingMemory(Obj, null, new List<StepRecord> { step }, 0, 0));

        Assert.Null(VerifiedTrajectoryHarvester.Harvest(Obj, Proc, result)); // value-level PHI guard refuses
    }

    [Fact]
    public void Harvester_RefusesPhiInSignature_EvenWithNullParams()
    {
        // Legacy / uncaptured step: ActionParams is null, so the value-level scan can't run — the signature
        // string scan (Codex Q3) must still catch PHI banked verbatim in the signature.
        var sig = NextAction.Act("click_by_label",
            new Dictionary<string, object?> { ["label"] = "123-45-6789", ["process_name"] = Proc }).Signature();
        var step = new StepRecord("s0", sig, ActStatus.Success, PostconditionVerdict.Met); // ActionParams null
        var result = new AgenticLoopResult(TerminationReason.Done, 1, 0, false, null,
            new WorkingMemory(Obj, null, new List<StepRecord> { step }, 0, 0));
        Assert.Null(VerifiedTrajectoryHarvester.Harvest(Obj, Proc, result));
    }

    // ── 3. Replay is airtight from structured params (Q2 retired) ──

    private sealed class ScriptedApp(VerifiedSkill skill)
    {
        private readonly string[] _states = BuildStates(skill);
        private readonly string[] _expected = skill.Steps.Select(s => s.ActionSignature).ToArray();
        public int Index;
        private static string[] BuildStates(VerifiedSkill s)
        {
            var arr = new string[s.Steps.Count + 1];
            for (var i = 0; i < s.Steps.Count; i++) arr[i] = s.Steps[i].StateHash;
            arr[^1] = "done";
            return arr;
        }
        public PerceivedScreen Screen() => new(_states[Index], true, new[] { "button:x" }, "Calculator");
        public void Act(string sig) { if (Index < _expected.Length && sig == _expected[Index]) Index++; }
    }

    private sealed class SimPerceiver(ScriptedApp app) : IPerceiver
    {
        public Task<PerceivedScreen?> PerceiveAsync(CancellationToken ct) => Task.FromResult<PerceivedScreen?>(app.Screen());
    }

    private sealed class SimActuator(ScriptedApp app) : IActuator
    {
        public Task<ActOutcome> ActAsync(NextAction action, ActuationContext ctx, CancellationToken ct)
        { if (!ctx.DryRun) app.Act(action.Signature()); return Task.FromResult(new ActOutcome(ActStatus.Success)); }
    }

    private static async Task<SkillReplayResult> Replay(VerifiedSkill skill)
    {
        var app = new ScriptedApp(skill);
        var gate = new SandboxExploreSafetyGate(
            () => new ActuationGateState(Enabled: true, DryRun: false, PausedUntilUtc: null, PauseReason: null, KillSwitchTrippedUtc: null));
        var replayer = new VerifiedSkillReplayer(new SimPerceiver(app), new SimActuator(app), gate, delay: (_, _) => Task.CompletedTask);
        var opts = new AgenticLoopOptions { DryRun = false, SettleMaxPolls = 2, SettleStableReads = 1, SettlePollInterval = TimeSpan.Zero };
        return await replayer.ReplayAsync(skill, Obj, opts, default);
    }

    private static VerifiedStep StructuredStep(string state, string label)
    {
        var p = new Dictionary<string, object?> { ["label"] = label, ["process_name"] = Proc };
        var json = JsonSerializer.Serialize(new Dictionary<string, string> { ["label"] = label, ["process_name"] = Proc });
        return new VerifiedStep(state, NextAction.Act("click_by_label", p).Signature(), "click_by_label", json);
    }

    [Fact]
    public async Task Replay_CommaInLabel_StructuredParams_IsAirtight()
    {
        // A label literally containing a comma: the unescaped signature is ambiguous, but the structured
        // ParamsJson rebuilds the EXACT action → replays correctly. This is the Q2 residual retired.
        var skill = new VerifiedSkill("id", "ph", "task.calc", Proc,
            new List<VerifiedStep> { StructuredStep("s0", "Save, As") }, "h");
        var r = await Replay(skill);
        Assert.Equal(SkillReplayOutcome.Completed, r.Outcome);
        Assert.Equal(1, r.StepsCompleted);
    }

    [Fact]
    public async Task Replay_CommaInLabel_LegacySigOnly_FailsClosed()
    {
        // The SAME comma-label as a legacy sig-only row (no structured params) can't reconstruct via the
        // parser → fail-closed. Proves structured params are what make it airtight.
        var sig = NextAction.Act("click_by_label",
            new Dictionary<string, object?> { ["label"] = "Save, As", ["process_name"] = Proc }).Signature();
        var skill = new VerifiedSkill("id", "ph", "task.calc", Proc,
            new List<VerifiedStep> { new("s0", sig) }, "h"); // Verb/ParamsJson null → legacy path
        var r = await Replay(skill);
        Assert.Equal(SkillReplayOutcome.Unparseable, r.Outcome);
    }

    [Fact]
    public async Task Replay_LegacySigOnly_CleanLabel_StillWorks()
    {
        // Back-compat: a clean (no-delimiter) legacy sig-only skill still replays via the parser fallback.
        var sig = NextAction.Act("click_by_label",
            new Dictionary<string, object?> { ["label"] = "Seven", ["process_name"] = Proc }).Signature();
        var skill = new VerifiedSkill("id", "ph", "task.calc", Proc,
            new List<VerifiedStep> { new("s0", sig) }, "h");
        var r = await Replay(skill);
        Assert.Equal(SkillReplayOutcome.Completed, r.Outcome);
    }
}
