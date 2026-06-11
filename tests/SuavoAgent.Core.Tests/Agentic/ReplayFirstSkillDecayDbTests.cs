using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Core.Agentic;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Agentic;

/// <summary>
/// MOAT Increment 2 — the design's DRIFT test against the REAL SQLite skill store: bank a skill
/// (same UpsertVerifiedSkill write the harvester uses), mutate the sim's state at the first point of
/// divergence, and prove the full slime-mold circuit replay-first plugs into:
///   drift → StateMismatch → loop fallback + consecutive_failures increments →
///   three consecutive drifts → RETIRED → GetBestVerifiedSkillForTask stops selecting it →
///   a Completed replay (healthy skill) re-thickens success_count and resets the streak.
/// </summary>
public class ReplayFirstSkillDecayDbTests : IDisposable
{
    private static readonly AgentObjective Obj = new("compute seven eight", "task.calc", "ph");

    private readonly string _dbPath;
    private readonly AgentStateDb _db;

    public ReplayFirstSkillDecayDbTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_replayfirst_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
    }

    private static AgenticLoopOptions Options() => new()
    {
        DryRun = false,
        SettleMaxPolls = 2,
        SettleStableReads = 1,
        SettlePollInterval = TimeSpan.Zero,
    };

    private static VerifiedStep ClickStep(string state, string label)
    {
        var action = NextAction.Act("click_by_label",
            new Dictionary<string, object?> { ["label"] = label, ["process_name"] = "calc.exe" });
        return new VerifiedStep(state, action.Signature(), "click_by_label",
            JsonSerializer.Serialize(new Dictionary<string, string> { ["label"] = label, ["process_name"] = "calc.exe" }));
    }

    private static VerifiedSkill TwoClickSkill() => VerifiedSkill.Create("ph", "task.calc", "calc.exe",
        new List<VerifiedStep> { ClickStep("s0", "Seven"), ClickStep("s1", "Eight") });

    private sealed class ScriptedApp(string[] states, string[] expectedSigs)
    {
        public int Index;
        public PerceivedScreen Screen() => new(states[Index], true, new[] { "button:Seven" }, "Calculator");
        public void Act(string sig)
        {
            if (Index < expectedSigs.Length && sig == expectedSigs[Index]) Index++;
        }
    }

    private sealed class SimPerceiver(ScriptedApp app) : IPerceiver
    {
        public Task<PerceivedScreen?> PerceiveAsync(CancellationToken ct) => Task.FromResult<PerceivedScreen?>(app.Screen());
    }

    private sealed class SimActuator(ScriptedApp app) : IActuator
    {
        public Task<ActOutcome> ActAsync(NextAction action, ActuationContext ctx, CancellationToken ct)
        {
            if (!ctx.DryRun) app.Act(action.Signature());
            return Task.FromResult(new ActOutcome(ActStatus.Success));
        }
    }

    /// <summary>Handler-shaped attempt: GetBest → reconstruct → TryReplayAsync with the REAL
    /// RecordSkillReplayOutcome as the hygiene delegate (what HeartbeatWorker.TryReplayFirstAsync wires).</summary>
    private async Task<(ReplayFirstResult Rf, (int SuccessCount, int ConsecutiveFailures, bool Retired)? Hygiene, int ReasonerCalls)>
        HandlerShapedAttempt(string[] simStates)
    {
        var best = _db.GetBestVerifiedSkillForTask("ph", "task.calc", "calc.exe");
        Assert.NotNull(best);
        var bv = best!.Value;
        var steps = VerifiedSkill.DeserializeSteps(bv.StepsJson);
        Assert.Equal(VerifiedSkill.ComputeStepsHash(steps), bv.StepsHash); // the hash-pin the worker enforces
        var skill = new VerifiedSkill(bv.SkillId, "ph", "task.calc", "calc.exe", steps, bv.StepsHash);

        var app = new ScriptedApp(simStates, [steps[0].ActionSignature, steps[1].ActionSignature]);
        var gate = new FakeSafetyGate();
        var runner = new ReplayFirstRunner(new SimPerceiver(app), new SimActuator(app), gate, AgenticTestKit.NoDelay);

        (int, int, bool)? hygiene = null;
        var rf = await runner.TryReplayAsync(skill, bv.SuccessCount, Obj, Options(), false,
            (id, ok) => hygiene = _db.RecordSkillReplayOutcome(id, ok), CancellationToken.None);

        var reasoner = new FakeReasoner();
        if (!rf.ReplayCompleted)
        {
            var loop = new AgenticLoopRunner(
                new FakePerceiver(AgenticTestKit.Screen("fresh")), reasoner, new FakeActuator(),
                new FakeSafetyGate(), null, AgenticTestKit.NoDelay);
            await loop.RunAsync(Obj, Options(), CancellationToken.None);
        }
        return (rf, hygiene, reasoner.ReasonCalls);
    }

    [Fact]
    public async Task Drift_FallsBackToLoop_IncrementsConsecutiveFailures_ThreeStrikesRetires()
    {
        // Bank the skill twice — the second verification is what makes it a replayable tube.
        var skill = TwoClickSkill();
        Assert.Equal(1, _db.UpsertVerifiedSkill(skill.SkillId, skill.PharmacyId, skill.TaskKey, skill.App, skill.SerializeSteps(), skill.StepsHash));
        Assert.Equal(2, _db.UpsertVerifiedSkill(skill.SkillId, skill.PharmacyId, skill.TaskKey, skill.App, skill.SerializeSteps(), skill.StepsHash));

        // Drift #1: entry matches, the post-step-0 state was MUTATED → StateMismatch mid-flow.
        var (rf1, hygiene1, reasonerCalls1) = await HandlerShapedAttempt(["s0", "MUTATED", "done"]);
        Assert.False(rf1.ReplayCompleted);
        Assert.Equal(SkillReplayOutcome.StateMismatch, rf1.Replay!.Outcome);
        Assert.Equal(1, hygiene1!.Value.ConsecutiveFailures);   // the drift DECAYED the tube
        Assert.False(hygiene1.Value.Retired);
        Assert.Equal(1, reasonerCalls1);                        // and the agentic loop took over, fresh

        // Drifts #2 and #3 → the stale tube is RETIRED (slime-mold pruning).
        var (_, hygiene2, _) = await HandlerShapedAttempt(["s0", "MUTATED", "done"]);
        Assert.Equal(2, hygiene2!.Value.ConsecutiveFailures);
        var (_, hygiene3, _) = await HandlerShapedAttempt(["s0", "MUTATED", "done"]);
        Assert.Equal(3, hygiene3!.Value.ConsecutiveFailures);
        Assert.True(hygiene3.Value.Retired);

        // A retired skill is no longer selectable — replay-first goes inert for the task, loop unaffected.
        Assert.Null(_db.GetBestVerifiedSkillForTask("ph", "task.calc", "calc.exe"));
    }

    [Fact]
    public async Task CompletedReplay_ReThickens_AndResetsFailureStreak()
    {
        var skill = TwoClickSkill();
        _db.UpsertVerifiedSkill(skill.SkillId, skill.PharmacyId, skill.TaskKey, skill.App, skill.SerializeSteps(), skill.StepsHash);
        _db.UpsertVerifiedSkill(skill.SkillId, skill.PharmacyId, skill.TaskKey, skill.App, skill.SerializeSteps(), skill.StepsHash);

        // One drift first, so the success has a streak to reset.
        var (_, hygieneDrift, _) = await HandlerShapedAttempt(["s0", "MUTATED", "done"]);
        Assert.Equal(1, hygieneDrift!.Value.ConsecutiveFailures);

        // Healthy replay: sim presents exactly the banked path → Completed, success_count++ (2→3), streak reset.
        var (rf, hygiene, reasonerCalls) = await HandlerShapedAttempt(["s0", "s1", "done"]);
        Assert.True(rf.ReplayCompleted);
        Assert.Equal(SkillReplayOutcome.Completed, rf.Replay!.Outcome);
        Assert.Equal(3, hygiene!.Value.SuccessCount);
        Assert.Equal(0, hygiene.Value.ConsecutiveFailures);
        Assert.False(hygiene.Value.Retired);
        Assert.Equal(0, reasonerCalls); // zero reasoning — the whole point
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
