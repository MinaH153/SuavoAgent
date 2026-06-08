using System.Collections.Generic;
using SuavoAgent.Core.Agentic;
using Xunit;

namespace SuavoAgent.Core.Tests.Agentic;

/// <summary>
/// Pins the verdict→conductance wiring: a VERIFIED (Met) step thickens its edge; a visited-but-
/// unverified step evaporates it; never-verified work is never rewarded; the periodic sweep decays
/// everything toward the floor. This is the slime-mold memory fed only by the real verifier.
/// </summary>
public class EdgeReinforcementTests
{
    private static readonly ConductanceParams P = ConductanceParams.Default;
    private const string Pharmacy = "pharmacy.test";
    private const string Task = "task.pricing";

    private static StepRecord Step(string state, string action, PostconditionVerdict? verdict) =>
        new(state, action, ActStatus.Success, verdict);

    private static EdgeKey Edge(string state, string action) => new(Pharmacy, Task, state, action);

    [Fact]
    public void MetStep_ReinforcesItsEdge_AboveFloor()
    {
        var store = new InMemoryEdgeConductanceStore();
        EdgeReinforcement.ApplyRun(store, Pharmacy, Task,
            new[] { Step("screenA", "click(Save)", PostconditionVerdict.Met) }, P);

        Assert.True(store.Get(Edge("screenA", "click(Save)"), P.Floor) > P.Floor);
    }

    [Fact]
    public void RepeatedMet_StrengthensTowardCeiling()
    {
        var store = new InMemoryEdgeConductanceStore();
        var edge = Edge("screenA", "click(Save)");
        double prev = P.Floor;
        for (var i = 0; i < 20; i++)
        {
            EdgeReinforcement.ApplyRun(store, Pharmacy, Task,
                new[] { Step("screenA", "click(Save)", PostconditionVerdict.Met) }, P);
            var now = store.Get(edge, P.Floor);
            Assert.True(now >= prev);          // monotonic up
            Assert.True(now <= P.Ceiling);     // clamped
            prev = now;
        }
        Assert.True(prev > 0.5);               // genuinely strengthened
    }

    [Fact]
    public void UnverifiedStep_NeverRewards_OnlyDecays()
    {
        var store = new InMemoryEdgeConductanceStore();
        var edge = Edge("screenA", "click(Save)");
        store.Set(edge, 0.6); // pretend it had been strengthened before

        foreach (var verdict in new PostconditionVerdict?[] { PostconditionVerdict.NotMet, PostconditionVerdict.Ambiguous, null })
        {
            var before = store.Get(edge, P.Floor);
            EdgeReinforcement.ApplyRun(store, Pharmacy, Task,
                new[] { Step("screenA", "click(Save)", verdict) }, P);
            Assert.True(store.Get(edge, P.Floor) < before); // strictly decayed, never rewarded
        }
    }

    [Fact]
    public void TerminalOrEmptySteps_AreSkipped()
    {
        var store = new InMemoryEdgeConductanceStore();
        EdgeReinforcement.ApplyRun(store, Pharmacy, Task, new[]
        {
            Step("", "", PostconditionVerdict.Met),          // terminal / no-action
            Step("screenA", "", PostconditionVerdict.Met),   // no action sig
        }, P);
        Assert.Empty(store.Edges(Pharmacy, Task));
    }

    [Fact]
    public void Evaporate_DecaysAllEdges_TowardFloor()
    {
        var store = new InMemoryEdgeConductanceStore();
        store.Set(Edge("s1", "a1"), 0.9);
        store.Set(Edge("s2", "a2"), 0.7);

        EdgeReinforcement.Evaporate(store, Pharmacy, Task, P);

        Assert.True(store.Get(Edge("s1", "a1"), P.Floor) < 0.9);
        Assert.True(store.Get(Edge("s2", "a2"), P.Floor) < 0.7);
    }
}
