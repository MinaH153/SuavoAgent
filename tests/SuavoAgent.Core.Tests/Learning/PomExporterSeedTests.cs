using System.Text.Json;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

public class PomExporterSeedTests : IDisposable
{
    private readonly AgentStateDb _db;
    private const string SessionId = "sess-1";
    private const string PharmacyId = "pharm-1";

    public PomExporterSeedTests()
    {
        _db = new AgentStateDb(":memory:");
        _db.CreateLearningSession(SessionId, PharmacyId);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Export_IncludesPmsVersionHash_InBehavioral()
    {
        var exporter = new PomExporter(_db, SessionId, PharmacyId, pmsVersionHash: "ver-hash-1");
        var (json, _) = exporter.Export();
        var doc = JsonDocument.Parse(json);
        var behavioral = doc.RootElement.GetProperty("behavioral");
        Assert.Equal("ver-hash-1", behavioral.GetProperty("pmsVersionHash").GetString());
    }

    [Fact]
    public void Export_ConfidenceTrajectory_IncludesSeedProvenance()
    {
        _db.UpsertCorrelatedAction(SessionId, "t1:btn1:q1", "t1", "btn1", "Button", "q1", true, "Tbl");
        _db.SetCorrelatedActionSource(SessionId, "t1:btn1:q1", "seed", "digest-abc", "2026-04-14T00:00:00Z");

        var exporter = new PomExporter(_db, SessionId, PharmacyId, pmsVersionHash: "ver-1");
        var (json, _) = exporter.Export();
        var doc = JsonDocument.Parse(json);
        var trajectory = doc.RootElement.GetProperty("feedback").GetProperty("confidenceTrajectory");

        var item = trajectory.EnumerateArray().First();
        Assert.Equal("seed", item.GetProperty("origin").GetString());
        Assert.Equal("digest-abc", item.GetProperty("firstSeedDigest").GetString());
        Assert.Equal("2026-04-14T00:00:00Z", item.GetProperty("seededAt").GetString());
    }

    [Fact]
    public void Export_LocalCorrelation_OriginIsLocal()
    {
        _db.UpsertCorrelatedAction(SessionId, "t1:btn1:q1", "t1", "btn1", "Button", "q1", true, "Tbl");

        var exporter = new PomExporter(_db, SessionId, PharmacyId, pmsVersionHash: "ver-1");
        var (json, _) = exporter.Export();
        var doc = JsonDocument.Parse(json);
        var trajectory = doc.RootElement.GetProperty("feedback").GetProperty("confidenceTrajectory");

        var item = trajectory.EnumerateArray().First();
        Assert.Equal("local", item.GetProperty("origin").GetString());
    }
}
