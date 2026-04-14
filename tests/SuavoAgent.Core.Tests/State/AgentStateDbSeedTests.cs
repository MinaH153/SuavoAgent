using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.State;

public class AgentStateDbSeedTests : IDisposable
{
    private readonly AgentStateDb _db;

    public AgentStateDbSeedTests()
    {
        _db = new AgentStateDb(":memory:");
        _db.CreateLearningSession("sess-1", "pharm-1");
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void CorrelatedAction_HasSourceColumn_DefaultsToLocal()
    {
        _db.UpsertCorrelatedAction("sess-1", "t1:btn1:q1", "t1", "btn1", "Button", "q1", true, "Tbl");
        var source = _db.GetCorrelatedActionSource("sess-1", "t1:btn1:q1");
        Assert.Equal("local", source.Source);
        Assert.Null(source.SeedDigest);
        Assert.Null(source.SeededAt);
    }

    [Fact]
    public void InsertAppliedSeed_RoundTrips()
    {
        _db.InsertAppliedSeed("digest-abc", "pattern", "2026-04-14T00:00:00Z", 5, 2);
        var seed = _db.GetAppliedSeed("digest-abc");
        Assert.NotNull(seed);
        Assert.Equal("pattern", seed!.Phase);
        Assert.Equal(5, seed.CorrelationsApplied);
        Assert.Equal(2, seed.CorrelationsSkipped);
    }

    [Fact]
    public void InsertAppliedSeed_DuplicateDigest_Ignored()
    {
        _db.InsertAppliedSeed("digest-abc", "pattern", "2026-04-14T00:00:00Z", 5, 2);
        _db.InsertAppliedSeed("digest-abc", "model", "2026-04-14T01:00:00Z", 3, 1);
        var seed = _db.GetAppliedSeed("digest-abc");
        Assert.Equal("pattern", seed!.Phase);
    }

    [Fact]
    public void InsertSeedItem_And_Confirm()
    {
        _db.InsertSeedItem("digest-abc", "query_shape", "shape-hash-1", "2026-04-14T00:00:00Z");
        _db.InsertSeedItem("digest-abc", "correlation", "t1:btn1:q1", "2026-04-14T00:00:00Z");

        var items = _db.GetSeedItems("digest-abc");
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Null(i.ConfirmedAt));

        _db.ConfirmSeedItem("digest-abc", "query_shape", "shape-hash-1", "2026-04-14T01:00:00Z");

        var confirmed = _db.GetSeedItems("digest-abc");
        var qs = confirmed.First(i => i.ItemType == "query_shape");
        Assert.NotNull(qs.ConfirmedAt);
        Assert.Equal(1, qs.LocalMatchCount);
    }

    [Fact]
    public void RejectSeedItem_ExcludedFromConfirmationRatio()
    {
        _db.InsertSeedItem("digest-abc", "correlation", "key-1", "2026-04-14T00:00:00Z");
        _db.InsertSeedItem("digest-abc", "correlation", "key-2", "2026-04-14T00:00:00Z");
        _db.InsertSeedItem("digest-abc", "correlation", "key-3", "2026-04-14T00:00:00Z");

        _db.ConfirmSeedItem("digest-abc", "correlation", "key-1", "2026-04-14T01:00:00Z");
        _db.RejectSeedItem("digest-abc", "correlation", "key-2", "2026-04-14T01:00:00Z");

        var ratio = _db.GetSeedConfirmationRatio("digest-abc");
        Assert.Equal(0.5, ratio, precision: 2);
    }

    [Fact]
    public void SetCorrelatedActionSource_UpdatesFields()
    {
        _db.UpsertCorrelatedAction("sess-1", "t1:btn1:q1", "t1", "btn1", "Button", "q1", true, "Tbl");
        _db.SetCorrelatedActionSource("sess-1", "t1:btn1:q1", "seed", "digest-abc", "2026-04-14T00:00:00Z");

        var source = _db.GetCorrelatedActionSource("sess-1", "t1:btn1:q1");
        Assert.Equal("seed", source.Source);
        Assert.Equal("digest-abc", source.SeedDigest);
        Assert.Equal("2026-04-14T00:00:00Z", source.SeededAt);
    }
}
