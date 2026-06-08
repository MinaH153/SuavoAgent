using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.Agentic;
using SuavoAgent.Core.Agentic.Adapters;
using Xunit;

namespace SuavoAgent.Core.Tests.Agentic;

/// <summary>
/// Capstone: the moat mechanic composed through the REAL <see cref="AgenticLoopRunner"/> — perceive →
/// PhysarumActionPolicy select → SandboxExploreSafetyGate allow → actuate → settle → real
/// <see cref="PostconditionEvaluator"/> verdict → record. After the run we feed the verdict trail through
/// <see cref="EdgeReinforcement.ApplyRun"/> (exactly as the explore_sandbox worker does) and prove: the
/// VERIFIED winning move thickened above Floor, a DEAD move stayed at Floor, and a fresh policy over the
/// reinforced store now picks the winning move outright — the learned speedup, end to end, no big LLM.
///
/// <para>This is the local proof; the on-box Calculator/Notepad run (real i5, real UIA) lands once Phase 0
/// unblocks OTA — marked PARTIAL until then.</para>
/// </summary>
public class PhysarumExploreEndToEndTests
{
    private const string Ph = "ph";
    private const string TaskKey = "task.calc";
    private const string Proc = "calc.exe"; // an allowlisted sandbox process value
    private const int Goal = 3;

    private static readonly AgentObjective Obj = new("advance to the goal", TaskKey, Ph);

    private static string ClickSig(string label) =>
        NextAction.Act("click_by_label", new Dictionary<string, object?> { ["label"] = label, ["process_name"] = Proc }).Signature();

    /// <summary>A trivial multi-state app: clicking "Next" advances the counter (screen hash CHANGES ⇒ Met);
    /// clicking "Bad" is a no-op (hash unchanged ⇒ NotMet). The goal is <see cref="Goal"/> Next-clicks.</summary>
    private sealed class SimApp
    {
        public int Progress;
        public bool Done => Progress >= Goal;
        public PerceivedScreen Screen() => Done
            ? new PerceivedScreen($"calc:done", true, new[] { "text:DONE" }, "Calculator")
            : new PerceivedScreen($"calc:{Progress}", true, new[] { "button:Next", "button:Bad" }, "Calculator");
        public void Click(string? label)
        {
            if (string.Equals(label, "Next", StringComparison.Ordinal) && !Done) Progress++;
            // "Bad" (or anything else) is a no-op — the screen hash does not change.
        }
    }

    private sealed class SimPerceiver : IPerceiver
    {
        private readonly SimApp _app;
        public SimPerceiver(SimApp app) => _app = app;
        public Task<PerceivedScreen?> PerceiveAsync(CancellationToken ct) => Task.FromResult<PerceivedScreen?>(_app.Screen());
    }

    private sealed class SimActuator : IActuator
    {
        private readonly SimApp _app;
        public SimActuator(SimApp app) => _app = app;
        public Task<ActOutcome> ActAsync(NextAction action, ActuationContext ctx, CancellationToken ct)
        {
            if (!ctx.DryRun && action.Kind == NextActionKind.Act &&
                action.Parameters is { } p && p.TryGetValue("label", out var l))
                _app.Click(l as string);
            return Task.FromResult(new ActOutcome(ActStatus.Success));
        }
    }

    /// <summary>Inner brain: signals Done at the goal, else proposes the winning "Next" click; verifies via the
    /// REAL PostconditionEvaluator (the same call TieredBrainReasoner makes).</summary>
    private sealed class SimReasoner : IReasoner
    {
        private readonly SimApp _app;
        public SimReasoner(SimApp app) => _app = app;

        public Task<ReasonResult> ReasonNextAsync(AgentObjective o, WorkingMemory m, IReadOnlySet<string> a, bool c, CancellationToken ct)
            => Task.FromResult(_app.Done
                ? new ReasonResult(NextAction.Done, false)
                : new ReasonResult(NextAction.Act("click_by_label",
                    new Dictionary<string, object?> { ["label"] = "Next", ["process_name"] = Proc }), false));

        public Task<VerifyResult> VerifyPostconditionAsync(AgentObjective o, NextAction la, PerceivedScreen b, PerceivedScreen af, bool c, CancellationToken ct)
            => Task.FromResult(new VerifyResult(PostconditionEvaluator.Evaluate(la, b, af), false));
    }

    private static SandboxExploreSafetyGate Gate() => new(
        () => new ActuationGateState(Enabled: true, DryRun: false, PausedUntilUtc: null, PauseReason: null, KillSwitchTrippedUtc: null));

    private static PhysarumActionPolicy Policy(IReasoner inner, IEdgeConductanceStore store) =>
        new(inner, store, ConductanceParams.Default,
            new PhysarumPolicyOptions { BrainTickInterval = 2, ExploreEpsilon = 0.03, ProcessName = Proc },
            NullLogger<PhysarumActionPolicy>.Instance, Proc);

    private static AgenticLoopRunner Runner(SimApp app, IReasoner reasoner) =>
        new(new SimPerceiver(app), reasoner, new SimActuator(app), Gate(),
            delay: (_, _) => Task.CompletedTask); // no real sleeps in the settle loop

    private static AgenticLoopOptions LoopOptions() => new()
    {
        MaxSteps = 40,
        DryRun = false,
        SettlePollInterval = TimeSpan.Zero,
        AllowedActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Click", "Type", "PressKey", "WaitForElement", "VerifyElement", "Log",
        },
    };

    [Fact]
    public async Task ExploreRun_ReinforcesVerifiedWinningPath_AndAFreshPolicyPrefersIt()
    {
        var store = new InMemoryEdgeConductanceStore();
        var app = new SimApp();
        var policy = Policy(new SimReasoner(app), store);
        var result = await Runner(app, policy).RunAsync(Obj, LoopOptions(), default);

        // Reached the goal through the real loop, driven by the explorer + real verifier.
        Assert.Equal(TerminationReason.Done, result.Termination);
        Assert.True(app.Done);

        // Worker-equivalent post-run reinforcement of the VERIFIED verdict trail.
        EdgeReinforcement.ApplyRun(store, Ph, TaskKey, result.FinalMemory.History, ConductanceParams.Default);

        var floor = ConductanceParams.Default.Floor;
        var nextAtStart = store.Get(new EdgeKey(Ph, TaskKey, "calc:0", ClickSig("Next")), floor);
        var badAtStart = store.Get(new EdgeKey(Ph, TaskKey, "calc:0", ClickSig("Bad")), floor);

        Assert.True(nextAtStart > floor, $"verified winning move should thicken; was {nextAtStart}");
        Assert.True(badAtStart <= floor, $"a dead (NotMet) move must never exceed Floor; was {badAtStart}");

        // The learned speedup: a fresh explorer over the reinforced store picks the winning move at the
        // start state on the very first (off-tick-equivalent) decision — no wasted exploration.
        var freshApp = new SimApp();
        var fresh = Policy(new SimReasoner(freshApp), store);
        // Step 0 is a brain-tick; advance one off-tick where selection is PURE conductance (no endorsement).
        await fresh.ReasonNextAsync(Obj, new WorkingMemory(Obj, app.Screen(), new List<StepRecord>(), 0, 0),
            LoopOptions().AllowedActions, false, default);
        var offTick = await fresh.ReasonNextAsync(Obj,
            new WorkingMemory(Obj, new PerceivedScreen("calc:0", true, new[] { "button:Next", "button:Bad" }, "Calculator"),
                new List<StepRecord>(), 0, 0),
            LoopOptions().AllowedActions, false, default);

        Assert.Equal(ClickSig("Next"), offTick.Action.Signature());
    }
}
