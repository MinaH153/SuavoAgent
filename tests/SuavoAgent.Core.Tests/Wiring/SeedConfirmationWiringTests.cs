using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Wiring;

public class SeedConfirmationWiringTests : IDisposable
{
    private readonly AgentStateDb _db;
    private const string SessionId = "sess-1";

    public SeedConfirmationWiringTests()
    {
        _db = new AgentStateDb(":memory:");
        _db.CreateLearningSession(SessionId, "pharm-1");
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void TryCorrelateWithSql_SeededShape_ConfirmsQueryShapeSeedItem()
    {
        _db.InsertSeedItem("digest-1", "query_shape", "seeded-hash", "2026-04-14T00:00:00Z");

        var correlator = new ActionCorrelator(_db, SessionId);
        correlator.RegisterSeededShapes(new[] { "seeded-hash" });
        correlator.SetActiveSeedDigest("digest-1");

        var uiTime = DateTimeOffset.UtcNow;
        correlator.RecordUiEvent("tree1", "btn1", "Button", uiTime);
        correlator.TryCorrelateWithSql("seeded-hash", uiTime.AddSeconds(0.5).ToString("o"), true, "Tbl");

        var items = _db.GetSeedItems("digest-1");
        var item = items.First(i => i.ItemKey == "seeded-hash");
        Assert.NotNull(item.ConfirmedAt);
        Assert.Equal(1, item.LocalMatchCount);
    }

    [Fact]
    public void TryCorrelateWithSql_SeededShape_ConfirmsCorrelationSeedItem()
    {
        _db.InsertSeedItem("digest-2", "correlation", "tree1:btn1:seeded-hash", "2026-04-14T00:00:00Z");

        var correlator = new ActionCorrelator(_db, SessionId);
        correlator.RegisterSeededShapes(new[] { "seeded-hash" });
        correlator.SetActiveSeedDigest("digest-2");

        var uiTime = DateTimeOffset.UtcNow;
        correlator.RecordUiEvent("tree1", "btn1", "Button", uiTime);
        correlator.TryCorrelateWithSql("seeded-hash", uiTime.AddSeconds(0.5).ToString("o"), true, "Tbl");

        var items = _db.GetSeedItems("digest-2");
        var item = items.First(i => i.ItemType == "correlation");
        Assert.NotNull(item.ConfirmedAt);
    }

    [Fact]
    public void TryCorrelateWithSql_NoActiveSeedDigest_NoConfirmation()
    {
        _db.InsertSeedItem("digest-3", "query_shape", "some-hash", "2026-04-14T00:00:00Z");

        var correlator = new ActionCorrelator(_db, SessionId);
        // No SetActiveSeedDigest called

        var uiTime = DateTimeOffset.UtcNow;
        correlator.RecordUiEvent("tree1", "btn1", "Button", uiTime);
        correlator.TryCorrelateWithSql("some-hash", uiTime.AddSeconds(0.5).ToString("o"), true, "Tbl");

        var items = _db.GetSeedItems("digest-3");
        Assert.All(items, i => Assert.Null(i.ConfirmedAt));
    }
}
