using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.Agentic;
using SuavoAgent.Core.Agentic.Adapters;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Agentic;

/// <summary>
/// Capstone: the full ratchet round-trip — bank a verified skill to the SQLite store, load the
/// most-confirmed skill back via the DB, reconstruct it, and DETERMINISTICALLY replay it to Completed.
/// This is the moat's compounding payoff realized: a solved task replays with zero reasoning, no LLM.
/// </summary>
public class VerifiedSkillReplayRoundTripTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;
    private static readonly AgentObjective Obj = new("replay", "task.calc", "ph");

    public VerifiedSkillReplayRoundTripTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_replayrt_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
    }

    private static string Click(string label) =>
        NextAction.Act("click_by_label", new Dictionary<string, object?> { ["label"] = label, ["process_name"] = "calc.exe" }).Signature();

    private sealed class ScriptedApp
    {
        private readonly string[] _states;
        private readonly string[] _expected;
        public int Index;
        public ScriptedApp(VerifiedSkill skill)
        {
            var n = skill.Steps.Count;
            _states = new string[n + 1];
            _expected = new string[n];
            for (var i = 0; i < n; i++) { _states[i] = skill.Steps[i].StateHash; _expected[i] = skill.Steps[i].ActionSignature; }
            _states[n] = "calc:done";
        }
        public PerceivedScreen Screen() => new(_states[Index], true, new[] { "button:Seven", "button:Eight" }, "Calculator");
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

    [Fact]
    public async Task Bank_Load_Replay_Completes()
    {
        // 1. Bank a verified skill (what the harvester does post-run).
        var skill = VerifiedSkill.Create("ph", "task.calc", "calc.exe",
            new List<VerifiedStep> { new("s0", Click("Seven")), new("s1", Click("Eight")) });
        _db.UpsertVerifiedSkill(skill.SkillId, skill.PharmacyId, skill.TaskKey, skill.App, skill.SerializeSteps(), skill.StepsHash);

        // 2. Load the most-confirmed skill for the task straight from the DB, reconstruct it.
        var best = _db.GetBestVerifiedSkillForTask("ph", "task.calc", "calc.exe");
        Assert.NotNull(best);
        var loaded = new VerifiedSkill(best!.Value.SkillId, "ph", "task.calc", "calc.exe",
            VerifiedSkill.DeserializeSteps(best.Value.StepsJson), best.Value.StepsHash);
        Assert.Equal(skill.SkillId, loaded.SkillId);

        // 3. Deterministically replay it over a sim starting at the verified entry state → Completed.
        var app = new ScriptedApp(loaded);
        var gate = new SandboxExploreSafetyGate(
            () => new ActuationGateState(Enabled: true, DryRun: false, PausedUntilUtc: null, PauseReason: null, KillSwitchTrippedUtc: null));
        var replayer = new VerifiedSkillReplayer(new SimPerceiver(app), new SimActuator(app), gate, delay: (_, _) => Task.CompletedTask);
        var opts = new AgenticLoopOptions { DryRun = false, SettleMaxPolls = 2, SettleStableReads = 1, SettlePollInterval = TimeSpan.Zero };

        var result = await replayer.ReplayAsync(loaded, Obj, opts, default);

        Assert.Equal(SkillReplayOutcome.Completed, result.Outcome);
        Assert.Equal(2, result.StepsCompleted);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
