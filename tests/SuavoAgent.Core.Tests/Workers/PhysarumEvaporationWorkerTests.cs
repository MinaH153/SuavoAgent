using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Core.Agentic;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

/// <summary>
/// Pins the periodic decay sweep: one tick decays every edge in every known scope toward the Floor, and an
/// empty store is a no-op. This is the always-on evaporation that prunes stale paths (drift handling).
/// </summary>
public class PhysarumEvaporationWorkerTests
{
    private static readonly double Floor = ConductanceParams.Default.Floor;

    private static PhysarumEvaporationWorker Worker(IEdgeConductanceStore store) =>
        new(store, NullLogger<PhysarumEvaporationWorker>.Instance);

    [Fact]
    public void EvaporateOnce_DecaysEveryScope_TowardFloor()
    {
        var store = new InMemoryEdgeConductanceStore();
        store.Set(new EdgeKey("p1", "t1", "s1", "a1"), 0.9);
        store.Set(new EdgeKey("p1", "t1", "s2", "a2"), 0.8); // same scope, second edge
        store.Set(new EdgeKey("p2", "t2", "s3", "a3"), 0.7); // a second scope

        var swept = Worker(store).EvaporateOnce();

        Assert.Equal(2, swept); // two DISTINCT (pharmacy, task) scopes
        Assert.True(store.Get(new EdgeKey("p1", "t1", "s1", "a1"), Floor) < 0.9);
        Assert.True(store.Get(new EdgeKey("p1", "t1", "s2", "a2"), Floor) < 0.8);
        Assert.True(store.Get(new EdgeKey("p2", "t2", "s3", "a3"), Floor) < 0.7);
    }

    [Fact]
    public void EvaporateOnce_EmptyStore_NoThrow_ZeroSwept()
    {
        Assert.Equal(0, Worker(new InMemoryEdgeConductanceStore()).EvaporateOnce());
    }
}
