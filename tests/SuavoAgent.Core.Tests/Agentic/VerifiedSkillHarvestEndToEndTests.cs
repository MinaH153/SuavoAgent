using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.Agentic;
using SuavoAgent.Core.Agentic.Adapters;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Agentic;

/// <summary>
/// Capstone for the amortize ratchet: drive the REAL AgenticLoopRunner + PhysarumActionPolicy over a
/// Calculator-style sim to a verified Done, harvest the trajectory, bank it — then do it AGAIN and prove
/// the SAME verified path thickens the SAME skill (success_count 1 → 2), not a duplicate. End to end:
/// real loop → real Met verdicts → banked replayable skill → reinforced on re-verification. No big LLM.
/// </summary>
public class VerifiedSkillHarvestEndToEndTests : IDisposable
{
    private const string Proc = "calc.exe";
    private const int Goal = 3;
    private static readonly AgentObjective Obj = new("advance to the goal", "task.calc", "ph");

    private readonly string _dbPath;
    private readonly AgentStateDb _db;

    public VerifiedSkillHarvestEndToEndTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_skillrun_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
    }

    private sealed class SimApp
    {
        public int Progress;
        public bool Done => Progress >= Goal;
        public PerceivedScreen Screen() => Done
            ? new PerceivedScreen("calc:done", true, new[] { "text:DONE" }, "Calculator")
            : new PerceivedScreen($"calc:{Progress}", true, new[] { "button:Next", "button:Bad" }, "Calculator");
        public void Click(string? label) { if (label == "Next" && !Done) Progress++; }
    }

    private sealed class SimPerceiver(SimApp app) : IPerceiver
    {
        public Task<PerceivedScreen?> PerceiveAsync(CancellationToken ct) => Task.FromResult<PerceivedScreen?>(app.Screen());
    }

    private sealed class SimActuator(SimApp app) : IActuator
    {
        public Task<ActOutcome> ActAsync(NextAction action, ActuationContext ctx, CancellationToken ct)
        {
            if (!ctx.DryRun && action.Parameters is { } p && p.TryGetValue("label", out var l)) app.Click(l as string);
            return Task.FromResult(new ActOutcome(ActStatus.Success));
        }
    }

    private sealed class SimReasoner(SimApp app) : IReasoner
    {
        public Task<ReasonResult> ReasonNextAsync(AgentObjective o, WorkingMemory m, IReadOnlySet<string> a, bool c, CancellationToken ct)
            => Task.FromResult(app.Done
                ? new ReasonResult(NextAction.Done, false)
                : new ReasonResult(NextAction.Act("click_by_label",
                    new Dictionary<string, object?> { ["label"] = "Next", ["process_name"] = Proc }), false));
        public Task<VerifyResult> VerifyPostconditionAsync(AgentObjective o, NextAction la, PerceivedScreen b, PerceivedScreen af, bool c, CancellationToken ct)
            => Task.FromResult(new VerifyResult(PostconditionEvaluator.Evaluate(la, b, af), false));
    }

    private async Task<AgenticLoopResult> RunOnce(IEdgeConductanceStore store)
    {
        var app = new SimApp();
        var policy = new PhysarumActionPolicy(new SimReasoner(app), store, ConductanceParams.Default,
            new PhysarumPolicyOptions { BrainTickInterval = 2, ExploreEpsilon = 0.0, ProcessName = Proc },
            NullLogger<PhysarumActionPolicy>.Instance, Proc);
        var gate = new SandboxExploreSafetyGate(
            () => new ActuationGateState(Enabled: true, DryRun: false, PausedUntilUtc: null, PauseReason: null, KillSwitchTrippedUtc: null));
        var runner = new AgenticLoopRunner(new SimPerceiver(app), policy, new SimActuator(app), gate,
            delay: (_, _) => Task.CompletedTask);
        var opts = new AgenticLoopOptions
        {
            MaxSteps = 40,
            DryRun = false,
            SettlePollInterval = TimeSpan.Zero,
            AllowedActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Click", "WaitForElement", "VerifyElement", "Log" },
        };
        return await runner.RunAsync(Obj, opts, default);
    }

    private int Bank(AgenticLoopResult result)
    {
        var skill = VerifiedTrajectoryHarvester.Harvest(Obj, Proc, result);
        Assert.NotNull(skill); // a successful verified run MUST yield a bankable skill
        return _db.UpsertVerifiedSkill(skill!.SkillId, skill.PharmacyId, skill.TaskKey, skill.App, skill.SerializeSteps(), skill.StepsHash);
    }

    [Fact]
    public async Task TwoVerifiedRuns_BankOneSkill_ThatThickens()
    {
        var store = new InMemoryEdgeConductanceStore(); // shared across both runs (conductance carries over)

        var r1 = await RunOnce(store);
        Assert.Equal(TerminationReason.Done, r1.Termination);
        Assert.Equal(1, Bank(r1)); // first banking → success_count 1

        var r2 = await RunOnce(store);
        Assert.Equal(TerminationReason.Done, r2.Termination);
        Assert.Equal(2, Bank(r2)); // SAME verified path re-confirmed → thickened to 2, not duplicated

        Assert.Equal(1, _db.GetVerifiedSkillCountForTask("ph", "task.calc")); // exactly one skill banked
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
