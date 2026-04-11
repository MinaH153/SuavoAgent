using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

public class RxQueueInferenceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;

    public RxQueueInferenceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_rxinfer_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
        _db.CreateLearningSession("sess-1", "pharm-1");
    }

    [Fact]
    public void ScoreTable_WithRxNumberAndStatus_HighConfidence()
    {
        // Table with RxNumber + StatusTypeID + DateFilled = strong Rx queue candidate
        SeedSchema("Prescription", "RxTransaction", new[]
        {
            ("RxTransactionID", "uniqueidentifier", "identifier"),
            ("RxNumber", "int", "identifier"),
            ("RxTransactionStatusTypeID", "uniqueidentifier", "status"),
            ("DateFilled", "datetime", "temporal"),
            ("PatientID", "uniqueidentifier", "identifier"),
        });

        var engine = new RxQueueInferenceEngine(_db);
        var candidates = engine.InferCandidates("sess-1");

        Assert.NotEmpty(candidates);
        var top = candidates[0];
        Assert.Equal("Prescription.RxTransaction", top.PrimaryTable);
        Assert.True(top.Confidence >= 0.6);
        Assert.Equal("RxNumber", top.RxNumberColumn);
        Assert.Equal("RxTransactionStatusTypeID", top.StatusColumn);
    }

    [Fact]
    public void ScoreTable_NoRxColumn_LowConfidence()
    {
        SeedSchema("dbo", "Users", new[]
        {
            ("UserID", "int", "identifier"),
            ("UserName", "varchar", "name"),
            ("CreatedAt", "datetime", "temporal"),
        });

        var engine = new RxQueueInferenceEngine(_db);
        var candidates = engine.InferCandidates("sess-1");

        Assert.All(candidates, c => Assert.True(c.Confidence < 0.6));
    }

    [Fact]
    public void ScoreTable_PatientFK_MarkedAsPhiFence()
    {
        SeedSchema("Prescription", "Rx", new[]
        {
            ("RxID", "uniqueidentifier", "identifier"),
            ("RxNumber", "int", "identifier"),
            ("PatientID", "uniqueidentifier", "identifier"),
            ("StatusID", "int", "status"),
        });

        var engine = new RxQueueInferenceEngine(_db);
        var candidates = engine.InferCandidates("sess-1");

        var rx = candidates.FirstOrDefault(c => c.PrimaryTable == "Prescription.Rx");
        Assert.NotNull(rx);
        Assert.Equal("PatientID", rx.PatientFkColumn);
    }

    [Fact]
    public void InferCandidates_PersistsToDb()
    {
        SeedSchema("Prescription", "RxTransaction", new[]
        {
            ("RxTransactionID", "uniqueidentifier", "identifier"),
            ("RxNumber", "int", "identifier"),
            ("StatusTypeID", "uniqueidentifier", "status"),
            ("DateFilled", "datetime", "temporal"),
        });

        var engine = new RxQueueInferenceEngine(_db);
        engine.InferAndPersist("sess-1");

        var stored = _db.GetRxQueueCandidates("sess-1");
        Assert.NotEmpty(stored);
    }

    private void SeedSchema(string schema, string table, (string col, string type, string purpose)[] columns)
    {
        foreach (var (col, type, purpose) in columns)
        {
            _db.InsertDiscoveredSchema("sess-1", "svr", "TestDB",
                schema, table, col, type, null,
                false, col.EndsWith("ID"), false, null, null, purpose);
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
