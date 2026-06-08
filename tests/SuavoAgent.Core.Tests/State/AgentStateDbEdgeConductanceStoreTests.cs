using SuavoAgent.Core.Agentic;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.State;

/// <summary>
/// Pins the durable Physarum edge-conductance store: round-trips through the IEdgeConductanceStore
/// contract, scopes edges per (pharmacy, task), and SURVIVES restart (the trail memory lives outside
/// the model, in state.db). Also proves EdgeReinforcement's verified-only law drives it the same as the
/// in-memory store, so Phase-2 exploration sees identical dynamics whether cached or persisted.
/// </summary>
public class AgentStateDbEdgeConductanceStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;
    private readonly AgentStateDbEdgeConductanceStore _store;

    private static readonly ConductanceParams P = ConductanceParams.Default;
    private const string Pharmacy = "pharmacy.test";
    private const string Task = "task.pricing";

    public AgentStateDbEdgeConductanceStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_edge_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
        _store = new AgentStateDbEdgeConductanceStore(_db);
    }

    private static EdgeKey Edge(string state, string action) => new(Pharmacy, Task, state, action);

    [Fact]
    public void Get_AbsentEdge_ReturnsCallerDefault()
    {
        Assert.Equal(P.Floor, _store.Get(Edge("s1", "a1"), P.Floor));
        Assert.Equal(0.42, _store.Get(Edge("s1", "a1"), 0.42)); // never-seen ⇒ caller default, not 0
    }

    [Fact]
    public void SetThenGet_RoundTrips()
    {
        _store.Set(Edge("s1", "click(Save)"), 0.73);
        Assert.Equal(0.73, _store.Get(Edge("s1", "click(Save)"), P.Floor), precision: 9);
    }

    [Fact]
    public void Set_IsUpsert_NotDuplicate()
    {
        var e = Edge("s1", "a1");
        _store.Set(e, 0.5);
        _store.Set(e, 0.6);
        Assert.Equal(0.6, _store.Get(e, P.Floor), precision: 9);
        Assert.Single(_store.Edges(Pharmacy, Task)); // one row, updated in place
    }

    [Fact]
    public void Edges_ScopedToPharmacyAndTask()
    {
        _store.Set(new EdgeKey(Pharmacy, Task, "s1", "a1"), 0.5);
        _store.Set(new EdgeKey(Pharmacy, "task.other", "s2", "a2"), 0.5);
        _store.Set(new EdgeKey("pharmacy.other", Task, "s3", "a3"), 0.5);

        var edges = _store.Edges(Pharmacy, Task);
        Assert.Single(edges);
        Assert.Contains(new EdgeKey(Pharmacy, Task, "s1", "a1"), edges);
    }

    [Fact]
    public void Conductance_SurvivesRestart()
    {
        _store.Set(Edge("s1", "click(Save)"), 0.81);
        _db.Dispose();

        using var db2 = new AgentStateDb(_dbPath);
        var store2 = new AgentStateDbEdgeConductanceStore(db2);
        Assert.Equal(0.81, store2.Get(Edge("s1", "click(Save)"), P.Floor), precision: 9);
        Assert.Single(store2.Edges(Pharmacy, Task));
    }

    [Fact]
    public void AllPharmacyTaskPairs_ReturnsDistinctScopes()
    {
        _store.Set(new EdgeKey(Pharmacy, Task, "s1", "a1"), 0.5);
        _store.Set(new EdgeKey(Pharmacy, Task, "s2", "a2"), 0.5);   // same scope, different edge
        _store.Set(new EdgeKey(Pharmacy, "task.other", "s3", "a3"), 0.5);
        _store.Set(new EdgeKey("pharmacy.other", Task, "s4", "a4"), 0.5);

        var pairs = _store.AllPharmacyTaskPairs();
        Assert.Equal(3, pairs.Count); // (P,Task), (P,task.other), (pharmacy.other,Task) — deduped
        Assert.Contains((Pharmacy, Task), pairs);
        Assert.Contains((Pharmacy, "task.other"), pairs);
        Assert.Contains(("pharmacy.other", Task), pairs);
    }

    [Fact]
    public void ConcurrentReinforceAndEvaporate_NoCorruptionOrThrow()
    {
        // The navigate run (Task.Run) and the evaporation worker (timer) both hit edge_conductance on
        // the single non-thread-safe SqliteConnection. Stress them concurrently — the _edgeConductanceLock
        // must serialize so this completes without SQLITE_BUSY / corruption.
        var steps = Enumerable.Range(0, 40)
            .Select(i => new StepRecord($"s{i % 8}", $"click(b{i % 8})", ActStatus.Success, PostconditionVerdict.Met))
            .ToArray();

        var tasks = new List<System.Threading.Tasks.Task>();
        for (var t = 0; t < 4; t++)
            tasks.Add(System.Threading.Tasks.Task.Run(() =>
            {
                for (var r = 0; r < 25; r++)
                {
                    EdgeReinforcement.ApplyRun(_store, Pharmacy, Task, steps, P);
                    EdgeReinforcement.Evaporate(_store, Pharmacy, Task, P);
                }
            }));

        var ex = Record.Exception(() => System.Threading.Tasks.Task.WaitAll(tasks.ToArray()));
        Assert.Null(ex);
        Assert.NotEmpty(_store.Edges(Pharmacy, Task));
    }

    [Fact]
    public void Reinforcement_DrivesDurableStore_SameAsInMemory()
    {
        var edge = Edge("screenA", "click(Save)");
        var step = new StepRecord("screenA", "click(Save)", ActStatus.Success, PostconditionVerdict.Met);

        // Verified Met ⇒ reinforced above floor; persists; an evaporation sweep then decays it.
        EdgeReinforcement.ApplyRun(_store, Pharmacy, Task, new[] { step }, P);
        var reinforced = _store.Get(edge, P.Floor);
        Assert.True(reinforced > P.Floor);

        EdgeReinforcement.Evaporate(_store, Pharmacy, Task, P);
        Assert.True(_store.Get(edge, P.Floor) < reinforced);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
