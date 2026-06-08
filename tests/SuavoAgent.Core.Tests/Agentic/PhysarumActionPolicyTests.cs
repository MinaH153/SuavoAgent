using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Core.Agentic;
using Xunit;

namespace SuavoAgent.Core.Tests.Agentic;

/// <summary>
/// Pins the slime-mold frontier explorer: on off-ticks it selects the highest-CONDUCTANCE candidate with
/// zero LLM cost (conductance is load-bearing, not advisory); brain Done/Escalate is honored; the store is
/// never written during a run; verification passes straight through to the real verifier; and when
/// conductance is flat the brain's endorsement breaks the tie (semantic grounding).
/// </summary>
public class PhysarumActionPolicyTests
{
    private const string Ph = "ph";
    private const string TaskKey = "task.explore";
    private const string Proc = "calc";
    private static readonly IReadOnlySet<string> ClickAllowed =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Click" };

    private static AgentObjective Objective => new("learn calc", TaskKey, Ph);

    private static WorkingMemory Memory(params string[] elements) =>
        new(Objective, new PerceivedScreen("hash", true, elements, "Calculator", null),
            new List<StepRecord>(), 0, 0);

    private static string Sig(string label) =>
        NextAction.Act("click_by_label", new Dictionary<string, object?> { ["label"] = label, ["process_name"] = Proc }).Signature();

    private static PhysarumPolicyOptions Deterministic(int brainTick = 2) =>
        new() { BrainTickInterval = brainTick, BrainEndorsementBonus = 0.15, TrailPenalty = 0.2, ExploreEpsilon = 0.0 };

    private sealed class FakeReasoner : IReasoner
    {
        public int ReasonCalls;
        public int VerifyCalls;
        public Func<ReasonResult> OnReason = () => new ReasonResult(NextAction.Escalate("none"), false);
        public VerifyResult Verify = new(PostconditionVerdict.Met, false);

        public Task<ReasonResult> ReasonNextAsync(AgentObjective o, WorkingMemory m, IReadOnlySet<string> a, bool c, CancellationToken ct)
        { ReasonCalls++; return Task.FromResult(OnReason()); }

        public Task<VerifyResult> VerifyPostconditionAsync(AgentObjective o, NextAction la, PerceivedScreen b, PerceivedScreen af, bool c, CancellationToken ct)
        { VerifyCalls++; return Task.FromResult(Verify); }
    }

    private sealed class RecordingStore : IEdgeConductanceStore
    {
        private readonly InMemoryEdgeConductanceStore _inner = new();
        public int SetCalls;
        public void Seed(EdgeKey e, double v) => _inner.Set(e, v);
        public double Get(EdgeKey e, double absent) => _inner.Get(e, absent);
        public void Set(EdgeKey e, double v) { SetCalls++; _inner.Set(e, v); }
        public IReadOnlyCollection<EdgeKey> Edges(string p, string t) => _inner.Edges(p, t);
        public IReadOnlyCollection<(string PharmacyId, string TaskKey)> AllPharmacyTaskPairs() => _inner.AllPharmacyTaskPairs();
    }

    private static PhysarumActionPolicy Policy(IReasoner inner, IEdgeConductanceStore store, PhysarumPolicyOptions opts) =>
        new(inner, store, ConductanceParams.Default, opts, NullLogger<PhysarumActionPolicy>.Instance, Proc);

    [Fact]
    public async Task OffTick_PicksHighestConductance_WithoutCallingBrain()
    {
        var store = new RecordingStore();
        store.Seed(new EdgeKey(Ph, TaskKey, "hash", Sig("High")), 0.9); // a verified-thickened tube
        var inner = new FakeReasoner
        {
            OnReason = () => new ReasonResult(
                NextAction.Act("click_by_label", new Dictionary<string, object?> { ["label"] = "Low", ["process_name"] = Proc }), false),
        };
        var policy = Policy(inner, store, Deterministic(brainTick: 2));

        var r0 = await policy.ReasonNextAsync(Objective, Memory("button:High", "button:Low"), ClickAllowed, false, default);
        var r1 = await policy.ReasonNextAsync(Objective, Memory("button:High", "button:Low"), ClickAllowed, false, default);

        Assert.Equal(1, inner.ReasonCalls);              // step0 brain-tick only; step1 off-tick = no LLM
        Assert.Equal(Sig("High"), r0.Action.Signature()); // conductance(0.9) beats brain-endorsed Low(0.25)
        Assert.Equal(Sig("High"), r1.Action.Signature());
    }

    [Fact]
    public async Task BrainDone_IsHonored()
    {
        var inner = new FakeReasoner { OnReason = () => new ReasonResult(NextAction.Done, false) };
        var policy = Policy(inner, new RecordingStore(), Deterministic());
        var r = await policy.ReasonNextAsync(Objective, Memory("button:High"), ClickAllowed, false, default);
        Assert.Equal(NextActionKind.Done, r.Action.Kind);
    }

    [Fact]
    public async Task BrainEscalate_IsHonored()
    {
        var inner = new FakeReasoner { OnReason = () => new ReasonResult(NextAction.Escalate("stuck"), false) };
        var policy = Policy(inner, new RecordingStore(), Deterministic());
        var r = await policy.ReasonNextAsync(Objective, Memory("button:High"), ClickAllowed, false, default);
        Assert.Equal(NextActionKind.Escalate, r.Action.Kind);
    }

    [Fact]
    public async Task NeverWritesStore_DuringRun()
    {
        var store = new RecordingStore();
        var inner = new FakeReasoner
        {
            OnReason = () => new ReasonResult(
                NextAction.Act("click_by_label", new Dictionary<string, object?> { ["label"] = "High", ["process_name"] = Proc }), false),
        };
        var policy = Policy(inner, store, Deterministic(brainTick: 2));
        for (var i = 0; i < 5; i++)
            await policy.ReasonNextAsync(Objective, Memory("button:High", "button:Low"), ClickAllowed, false, default);
        Assert.Equal(0, store.SetCalls); // read-only during the run; reinforcement is post-run only
    }

    [Fact]
    public async Task VerifyPostcondition_PassesThroughToInner()
    {
        var inner = new FakeReasoner { Verify = new VerifyResult(PostconditionVerdict.NotMet, false) };
        var policy = Policy(inner, new RecordingStore(), Deterministic());
        var screen = new PerceivedScreen("h", true, new[] { "button:X" });
        var v = await policy.VerifyPostconditionAsync(Objective, NextAction.Done, screen, screen, false, default);
        Assert.Equal(1, inner.VerifyCalls);
        Assert.Equal(PostconditionVerdict.NotMet, v.Verdict);
    }

    [Fact]
    public async Task FlatConductance_BrainEndorsementBreaksTie()
    {
        var store = new RecordingStore(); // empty — every edge reads Floor
        var inner = new FakeReasoner
        {
            OnReason = () => new ReasonResult(
                NextAction.Act("click_by_label", new Dictionary<string, object?> { ["label"] = "Low", ["process_name"] = Proc }), false),
        };
        var policy = Policy(inner, store, Deterministic());
        var r = await policy.ReasonNextAsync(Objective, Memory("button:High", "button:Low"), ClickAllowed, false, default);
        Assert.Equal(Sig("Low"), r.Action.Signature()); // brain's pick wins when conductance is flat
    }

    [Fact]
    public async Task NoCandidates_FallsBackToInner()
    {
        var inner = new FakeReasoner
        {
            OnReason = () => new ReasonResult(NextAction.Escalate("no targets"), false),
            ReasonCalls = 0,
        };
        var policy = Policy(inner, new RecordingStore(), Deterministic());
        var r = await policy.ReasonNextAsync(Objective, Memory("text:Display 0"), ClickAllowed, false, default); // only text → no clicks
        Assert.Equal(1, inner.ReasonCalls);
        Assert.Equal(NextActionKind.Escalate, r.Action.Kind);
    }
}
