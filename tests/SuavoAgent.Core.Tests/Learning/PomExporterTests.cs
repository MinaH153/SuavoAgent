using System.Text.Json;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

public class PomExporterTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;

    public PomExporterTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_pomexp_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
        _db.CreateLearningSession("sess-1", "pharm-1");
    }

    [Fact]
    public void Export_ContainsSessionMetadata()
    {
        var export = PomExporter.Export(_db, "sess-1");
        var doc = JsonDocument.Parse(export);
        Assert.Equal("sess-1", doc.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal("pharm-1", doc.RootElement.GetProperty("pharmacyId").GetString());
    }

    [Fact]
    public void Export_ContainsProcessCatalog()
    {
        _db.UpsertObservedProcess("sess-1", "PioneerPharmacy.exe",
            @"C:\PioneerRx\PioneerPharmacy.exe", isPmsCandidate: true);

        var export = PomExporter.Export(_db, "sess-1");
        var doc = JsonDocument.Parse(export);
        var procs = doc.RootElement.GetProperty("processes");
        Assert.Equal(1, procs.GetArrayLength());
        Assert.Equal("PioneerPharmacy.exe", procs[0].GetProperty("processName").GetString());
    }

    [Fact]
    public void Export_StripsHashes()
    {
        _db.UpsertObservedProcess("sess-1", "Test.exe", @"C:\Test.exe",
            windowTitleHash: "abc123hash");

        var export = PomExporter.Export(_db, "sess-1");
        Assert.DoesNotContain("abc123hash", export);
    }

    [Fact]
    public void Export_StripsExePaths()
    {
        _db.UpsertObservedProcess("sess-1", "Test.exe",
            @"C:\Program Files\Secret\Test.exe");

        var export = PomExporter.Export(_db, "sess-1");
        Assert.DoesNotContain(@"C:\Program Files\Secret", export);
    }

    [Fact]
    public void Export_ContainsSchemaStructure()
    {
        _db.InsertDiscoveredSchema("sess-1", "svr-hash", "TestDB",
            "Prescription", "Rx", "RxID", "uniqueidentifier",
            16, false, true, false, null, null, "identifier");

        var export = PomExporter.Export(_db, "sess-1");
        var doc = JsonDocument.Parse(export);
        var schemas = doc.RootElement.GetProperty("schemas");
        Assert.True(schemas.GetArrayLength() > 0);

        // Server hash must be stripped
        Assert.DoesNotContain("svr-hash", export);
    }

    [Fact]
    public void Export_ContainsRxCandidates()
    {
        _db.InsertRxQueueCandidate("sess-1", "Prescription.RxTransaction",
            "RxNumber", "StatusTypeID", "DateFilled", "PatientID",
            0.8, "[\"evidence\"]", null);

        var export = PomExporter.Export(_db, "sess-1");
        var doc = JsonDocument.Parse(export);
        var candidates = doc.RootElement.GetProperty("rxQueueCandidates");
        Assert.Equal(1, candidates.GetArrayLength());
    }

    [Fact]
    public void ComputeDigest_Deterministic()
    {
        var json = "{\"test\": true}";
        var d1 = PomExporter.ComputeDigest("pharm-1", "sess-1", json);
        var d2 = PomExporter.ComputeDigest("pharm-1", "sess-1", json);
        Assert.Equal(d1, d2);
    }

    [Fact]
    public void ComputeDigest_DifferentInputs_DifferentDigest()
    {
        var d1 = PomExporter.ComputeDigest("pharm-1", "sess-1", "{\"a\":1}");
        var d2 = PomExporter.ComputeDigest("pharm-1", "sess-1", "{\"a\":2}");
        Assert.NotEqual(d1, d2);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
