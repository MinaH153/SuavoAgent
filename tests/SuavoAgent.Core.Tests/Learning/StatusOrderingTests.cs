using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

public class StatusOrderingTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;

    public StatusOrderingTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_status_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
        _db.CreateLearningSession("sess-1", "pharm-1");
    }

    [Theory]
    [InlineData("Waiting for Pick up", "ready_pickup")]
    [InlineData("Waiting for Delivery", "ready_pickup")]
    [InlineData("Out for Delivery", "in_transit")]
    [InlineData("Completed", "completed")]
    [InlineData("Cancelled", "cancelled")]
    [InlineData("Waiting for Fill", "in_progress")]
    [InlineData("Waiting for Data Entry", "queued")]
    [InlineData("RandomUnknownStatus", "unknown")]
    public void InferMeaning_ClassifiesCorrectly(string statusName, string expected)
    {
        Assert.Equal(expected, StatusOrderingEngine.InferMeaning(statusName));
    }

    [Fact]
    public void InferAndPersist_StoresStatuses()
    {
        var engine = new StatusOrderingEngine(_db);
        engine.InferAndPersist("sess-1", "Prescription.RxTransaction", "StatusTypeID",
            new[]
            {
                ("guid-1", "Waiting for Data Entry"),
                ("guid-2", "Waiting for Fill"),
                ("guid-3", "Waiting for Pick up"),
                ("guid-4", "Completed"),
            });

        var statuses = _db.GetDiscoveredStatuses("sess-1");
        Assert.Equal(4, statuses.Count);
        Assert.Equal("queued", statuses[0].InferredMeaning);
        Assert.Equal("ready_pickup", statuses[2].InferredMeaning);
    }

    [Fact]
    public void GetDeliveryReadyStatuses_FiltersCorrectly()
    {
        var engine = new StatusOrderingEngine(_db);
        engine.InferAndPersist("sess-1", "Prescription.RxTransaction", "StatusTypeID",
            new[]
            {
                ("guid-1", "Waiting for Data Entry"),
                ("guid-2", "Waiting for Pick up"),
                ("guid-3", "Out for Delivery"),
                ("guid-4", "Completed"),
            });

        var ready = StatusOrderingEngine.GetDeliveryReadyValues(
            _db.GetDiscoveredStatuses("sess-1"));
        Assert.Contains("guid-2", ready);
        Assert.DoesNotContain("guid-1", ready);
        Assert.DoesNotContain("guid-4", ready);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
