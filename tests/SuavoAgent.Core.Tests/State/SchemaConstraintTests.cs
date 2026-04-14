using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.State;

public class SchemaConstraintTests : IDisposable
{
    private readonly AgentStateDb _db;

    public SchemaConstraintTests()
    {
        _db = new AgentStateDb(":memory:");
        _db.CreateLearningSession("sess-1", "pharm-1");
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void SetCorrelatedActionSource_SeedWithNullDigest_Throws()
    {
        _db.UpsertCorrelatedAction("sess-1", "key-1", "t1", "btn1", "Button", "q1", true, "Tbl");
        Assert.Throws<ArgumentException>(() =>
            _db.SetCorrelatedActionSource("sess-1", "key-1", "seed", null, null));
    }

    [Fact]
    public void SetCorrelatedActionSource_SeedWithDigest_Succeeds()
    {
        _db.UpsertCorrelatedAction("sess-1", "key-1", "t1", "btn1", "Button", "q1", true, "Tbl");
        _db.SetCorrelatedActionSource("sess-1", "key-1", "seed", "digest-1", "2026-04-14T00:00:00Z");
        var source = _db.GetCorrelatedActionSource("sess-1", "key-1");
        Assert.Equal("seed", source.Source);
        Assert.Equal("digest-1", source.SeedDigest);
    }

    [Fact]
    public void SetCorrelatedActionSource_LocalWithNullDigest_Succeeds()
    {
        _db.UpsertCorrelatedAction("sess-1", "key-1", "t1", "btn1", "Button", "q1", true, "Tbl");
        _db.SetCorrelatedActionSource("sess-1", "key-1", "local", null, null);
        var source = _db.GetCorrelatedActionSource("sess-1", "key-1");
        Assert.Equal("local", source.Source);
    }

    [Fact]
    public void SeedItems_CannotHaveBothConfirmedAndRejected()
    {
        _db.InsertSeedItem("digest-1", "query_shape", "qs-1", "2026-04-14T00:00:00Z");
        _db.ConfirmSeedItem("digest-1", "query_shape", "qs-1", "2026-04-14T01:00:00Z");

        // RejectSeedItem has WHERE confirmed_at IS NULL, so this is a no-op
        _db.RejectSeedItem("digest-1", "query_shape", "qs-1", "2026-04-14T02:00:00Z");

        var items = _db.GetSeedItems("digest-1");
        var item = items.First();
        Assert.NotNull(item.ConfirmedAt);
        Assert.Null(item.RejectedAt);
    }

    [Fact]
    public void SeedItems_RejectBlocksConfirm()
    {
        _db.InsertSeedItem("digest-2", "query_shape", "qs-2", "2026-04-14T00:00:00Z");
        _db.RejectSeedItem("digest-2", "query_shape", "qs-2", "2026-04-14T01:00:00Z");

        // ConfirmSeedItem has WHERE rejected_at IS NULL, so this is a no-op
        _db.ConfirmSeedItem("digest-2", "query_shape", "qs-2", "2026-04-14T02:00:00Z");

        var items = _db.GetSeedItems("digest-2");
        var item = items.First();
        Assert.NotNull(item.RejectedAt);
        Assert.Null(item.ConfirmedAt);
    }

    [Fact]
    public void ApplyPatternSeeds_Atomic_AllOrNothing()
    {
        var applicator = new SeedApplicator(_db);
        var response = new SeedResponse("digest-atomic", 1, "pattern",
            new[] { "schema" }, null, null,
            new[] { new SeedQueryShape("qs-1", "SELECT 1", new[] { "T" }, 0.8, 5) },
            new[] { new SeedStatusMapping("ST", "g-1", "Done", 10) },
            null);

        applicator.ApplyPatternSeeds("sess-1", response);

        var items = _db.GetSeedItems("digest-atomic");
        var applied = _db.GetAppliedSeed("digest-atomic");
        Assert.Equal(2, items.Count);
        Assert.NotNull(applied);
        Assert.Equal(2, applied!.CorrelationsApplied);
    }

    [Fact]
    public void ApplyModelSeeds_Atomic_WithCorrelations()
    {
        var applicator = new SeedApplicator(_db);
        var response = new SeedResponse("digest-model", 1, "model",
            new[] { "schema" }, null,
            new[] { new SeedCorrelation("t1:btn1:q1", "t1", "btn1", "Button", "q1", 0.9, 0.8, 3, 0.5) },
            new[] { new SeedQueryShape("qs-1", "SELECT 1", new[] { "T" }, 0.8, 5) },
            new[] { new SeedStatusMapping("ST", "g-1", "Done", 10) },
            null);

        var result = applicator.ApplyModelSeeds("sess-1", response);

        Assert.Equal(1, result.CorrelationsApplied);
        Assert.False(result.AlreadyApplied);

        var items = _db.GetSeedItems("digest-model");
        var applied = _db.GetAppliedSeed("digest-model");
        Assert.Equal(3, items.Count); // qs + status + correlation
        Assert.NotNull(applied);
    }
}
