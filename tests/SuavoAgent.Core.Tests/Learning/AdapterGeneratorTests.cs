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
        _db.CompleteLearnedTemplateEvidence("sess-1");

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
        _db.CompleteLearnedTemplateEvidence("sess-1");

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
        Assert.Contains("TOP (@pageSize)", pq.Query);
        Assert.Contains("@cursor IS NULL OR [RxNum] > @cursor", pq.Query);
        Assert.Contains("ORDER BY [RxNum] ASC", pq.Query);
        Assert.DoesNotContain("'Ready'", pq.Query);
        Assert.DoesNotContain("'InTransit'", pq.Query);
        Assert.Equal("Ready", pq.Parameters["@s0"]);
        Assert.Equal("InTransit", pq.Parameters["@s1"]);
    }

    [Fact]
    public void Describe_RequiresCompletedCurrentStatusEvidence()
    {
        SeedPioneerRxLikeSchema();
        SeedCandidateAndReadyStatus();
        var generator = new AdapterGenerator(_db);
        var before = generator.Describe("sess-1");
        Assert.NotNull(before);

        _db.InsertDiscoveredStatus(
            "sess-1",
            "Prescription.RxTransaction",
            "StatusTypeID",
            "guid-new",
            "ready_pickup",
            9,
            1,
            0.9);

        Assert.Null(generator.Describe("sess-1"));
        _db.CompleteLearnedTemplateEvidence("sess-1");
        var after = generator.Describe("sess-1");
        Assert.NotNull(after);
        Assert.NotEqual(before!.TemplateDigest, after!.TemplateDigest);
    }

    [Fact]
    public void BracketEscape_HandlesClosingBracket()
    {
        // A column name containing ] should be escaped as ]]
        var escaped = AdapterGenerator.BracketEscape("Col]Name");
        Assert.Equal("[Col]]Name]", escaped);
    }

    [Fact]
    public void Describe_ExactPatientForeignKey_BindsParameterizedPatientQueryIntoDigest()
    {
        SeedPioneerRxLikeSchema();
        SeedCandidateAndReadyStatus();
        var generator = new AdapterGenerator(_db);
        var before = generator.Describe("sess-1");
        Assert.NotNull(before);
        Assert.Null(before!.PatientLookupQuery);

        SeedExactPatientSchema();
        var after = generator.Describe("sess-1");

        Assert.NotNull(after);
        Assert.NotNull(after!.PatientLookupQuery);
        Assert.NotEqual(before.TemplateDigest, after.TemplateDigest);
        Assert.Contains("SELECT TOP 2", after.PatientLookupQuery);
        Assert.Contains("INNER JOIN [Person].[Patient] AS patient", after.PatientLookupQuery);
        Assert.Contains("rx.[PatientID] = patient.[PatientID]", after.PatientLookupQuery);
        Assert.Contains("WHERE rx.[RxNumber] = @rx", after.PatientLookupQuery);
        Assert.DoesNotContain("@s0", after.PatientLookupQuery);
    }

    [Fact]
    public void Describe_PatientColumnsWithoutExactForeignKey_DisablesPatientLookup()
    {
        SeedPioneerRxLikeSchema();
        SeedCandidateAndReadyStatus();
        SeedPatientColumnsOnly();

        var template = new AdapterGenerator(_db).Describe("sess-1");

        Assert.NotNull(template);
        Assert.Null(template!.PatientLookupQuery);
    }

    [Fact]
    public void Describe_AmbiguousPatientFieldMapping_DisablesPatientLookup()
    {
        SeedPioneerRxLikeSchema();
        SeedCandidateAndReadyStatus();
        SeedExactPatientSchema();
        _db.InsertDiscoveredSchema("sess-1", "svr", "TestDB",
            "Person", "Patient", "Mobile", "nvarchar", 30,
            true, false, false, null, null, "unknown");
        _db.CompleteDiscoveredSchemaSnapshot("sess-1");
        _db.CompleteLearnedTemplateEvidence("sess-1");

        var template = new AdapterGenerator(_db).Describe("sess-1");

        Assert.NotNull(template);
        Assert.Null(template!.PatientLookupQuery);
    }

    private void SeedCandidateAndReadyStatus()
    {
        _db.InsertRxQueueCandidate("sess-1", "Prescription.RxTransaction",
            "RxNumber", "StatusTypeID", "DateFilled", "PatientID",
            0.8, "[\"evidence\"]", null);
        new StatusOrderingEngine(_db).InferAndPersist(
            "sess-1",
            "Prescription.RxTransaction",
            "StatusTypeID",
            new[] { ("guid-pickup", "Waiting for Pick up") });
        _db.CompleteLearnedTemplateEvidence("sess-1");
    }

    private void SeedExactPatientSchema()
    {
        SeedPatientColumnsOnly();
        _db.BindDiscoveredForeignKey(
            "sess-1",
            "Prescription",
            "RxTransaction",
            "PatientID",
            "Person",
            "Patient",
            "PatientID");
        _db.InsertDiscoveredUniqueColumn("sess-1", "Person", "Patient", "PatientID");
        _db.CompleteDiscoveredSchemaSnapshot("sess-1");
        _db.CompleteLearnedTemplateEvidence("sess-1");
    }

    private void SeedPatientColumnsOnly()
    {
        var columns = new[]
        {
            ("PatientID", "uniqueidentifier", false),
            ("FirstName", "nvarchar", false),
            ("LastName", "nvarchar", false),
            ("Phone1", "nvarchar", true),
            ("Address1", "nvarchar", true),
            ("Address2", "nvarchar", true),
            ("City", "nvarchar", true),
            ("State", "nvarchar", true),
            ("Zip", "nvarchar", true),
        };
        foreach (var (column, type, nullable) in columns)
        {
            _db.InsertDiscoveredSchema("sess-1", "svr", "TestDB",
                "Person", "Patient", column, type, 200,
                nullable, column == "PatientID", false, null, null, "unknown");
        }
        _db.CompleteDiscoveredSchemaSnapshot("sess-1");
        _db.CompleteLearnedTemplateEvidence("sess-1");
    }

    private void SeedPioneerRxLikeSchema()
    {
        _db.BeginDiscoveredSchemaSnapshot("sess-1", new string('a', 64), "TestDB");
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
        _db.InsertDiscoveredUniqueColumn(
            "sess-1", "Prescription", "RxTransaction", "RxNumber");
        _db.CompleteDiscoveredSchemaSnapshot("sess-1");
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
