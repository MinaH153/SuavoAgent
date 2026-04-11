using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

public class AdapterGeneratorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;

    public AdapterGeneratorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_adaptergen_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
        _db.CreateLearningSession("sess-1", "pharm-1");
    }

    [Fact]
    public void Generate_WithValidCandidate_ReturnsAdapter()
    {
        SeedPioneerRxLikeSchema();

        _db.InsertRxQueueCandidate("sess-1", "Prescription.RxTransaction",
            "RxNumber", "StatusTypeID", "DateFilled", "PatientID",
            0.8, "[\"evidence\"]", null);

        var statusEngine = new StatusOrderingEngine(_db);
        statusEngine.InferAndPersist("sess-1", "Prescription.RxTransaction", "StatusTypeID",
            new[]
            {
                ("guid-pickup", "Waiting for Pick up"),
                ("guid-delivery", "Waiting for Delivery"),
                ("guid-complete", "Completed"),
            });

        var generator = new AdapterGenerator(_db);
        var adapter = generator.Generate("sess-1");

        Assert.NotNull(adapter);
        Assert.Equal("Learned-Prescription.RxTransaction", adapter.PmsName);
    }

    [Fact]
    public void Generate_NoCandidate_ReturnsNull()
    {
        var generator = new AdapterGenerator(_db);
        var adapter = generator.Generate("sess-1");
        Assert.Null(adapter);
    }

    [Fact]
    public void Generate_LowConfidenceCandidate_ReturnsNull()
    {
        _db.InsertRxQueueCandidate("sess-1", "dbo.SomeTable",
            null, null, null, null, 0.3, "[\"weak\"]", null);

        var generator = new AdapterGenerator(_db);
        var adapter = generator.Generate("sess-1");
        Assert.Null(adapter);
    }

    [Fact]
    public void GeneratedAdapter_BuildsCorrectQuery()
    {
        SeedPioneerRxLikeSchema();

        _db.InsertRxQueueCandidate("sess-1", "Prescription.RxTransaction",
            "RxNumber", "StatusTypeID", "DateFilled", "PatientID",
            0.8, "[\"evidence\"]", null);

        var statusEngine = new StatusOrderingEngine(_db);
        statusEngine.InferAndPersist("sess-1", "Prescription.RxTransaction", "StatusTypeID",
            new[]
            {
                ("guid-pickup", "Waiting for Pick up"),
                ("guid-complete", "Completed"),
            });

        var generator = new AdapterGenerator(_db);
        var adapter = generator.Generate("sess-1");
        var query = adapter!.DetectionQuery;

        Assert.Contains("Prescription.RxTransaction", query);
        Assert.Contains("StatusTypeID", query);
        Assert.Contains("guid-pickup", query);
        Assert.DoesNotContain("guid-complete", query); // completed is not delivery-ready
    }

    private void SeedPioneerRxLikeSchema()
    {
        var columns = new[]
        {
            ("RxTransactionID", "uniqueidentifier", "identifier"),
            ("RxNumber", "int", "identifier"),
            ("StatusTypeID", "uniqueidentifier", "status"),
            ("DateFilled", "datetime", "temporal"),
            ("PatientID", "uniqueidentifier", "identifier"),
            ("DispensedQuantity", "decimal", "amount"),
        };
        foreach (var (col, type, purpose) in columns)
        {
            _db.InsertDiscoveredSchema("sess-1", "svr", "TestDB",
                "Prescription", "RxTransaction", col, type, null,
                false, col.EndsWith("ID"), false, null, null, purpose);
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
