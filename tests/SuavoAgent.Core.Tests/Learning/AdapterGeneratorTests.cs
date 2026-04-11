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

        // Table name should be bracket-escaped
        Assert.Contains("[Prescription].[RxTransaction]", query);
        Assert.Contains("[StatusTypeID]", query);
        // Status values are now parameterized — should NOT appear as inline literals
        Assert.DoesNotContain("'guid-pickup'", query);
        // Parameters should contain the values
        Assert.True(adapter.StatusParameters.ContainsKey("@s0"));
        Assert.Equal("guid-pickup", adapter.StatusParameters["@s0"]);
    }

    [Fact]
    public void BuildDetectionQuery_InvalidTableName_ReturnsNull()
    {
        // SQL injection attempt via table name
        var result = AdapterGenerator.BuildDetectionQuery(
            "dbo.Table; DROP TABLE--", "Col1", "Col2", null, new[] { "val" });
        Assert.Null(result);
    }

    [Fact]
    public void BuildDetectionQuery_NoSchemaQualifier_ReturnsNull()
    {
        var result = AdapterGenerator.BuildDetectionQuery(
            "JustATable", "Col1", "Col2", null, new[] { "val" });
        Assert.Null(result);
    }

    [Fact]
    public void BuildDetectionQuery_ValidTable_ReturnsParameterizedQuery()
    {
        var result = AdapterGenerator.BuildDetectionQuery(
            "dbo.Prescriptions", "RxNum", "Status", "DateFilled",
            new[] { "Ready", "InTransit" });

        Assert.NotNull(result);
        var pq = result!.Value;

        Assert.Contains("[dbo].[Prescriptions]", pq.Query);
        Assert.Contains("[RxNum]", pq.Query);
        Assert.Contains("[DateFilled]", pq.Query);
        Assert.Contains("@s0", pq.Query);
        Assert.Contains("@s1", pq.Query);
        Assert.DoesNotContain("'Ready'", pq.Query);
        Assert.DoesNotContain("'InTransit'", pq.Query);
        Assert.Equal("Ready", pq.Parameters["@s0"]);
        Assert.Equal("InTransit", pq.Parameters["@s1"]);
    }

    [Fact]
    public void BracketEscape_HandlesClosingBracket()
    {
        // A column name containing ] should be escaped as ]]
        var escaped = AdapterGenerator.BracketEscape("Col]Name");
        Assert.Equal("[Col]]Name]", escaped);
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
