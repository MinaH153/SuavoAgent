using System.Text.Json;
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

public class BehavioralPomExportTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;

    public BehavioralPomExportTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_behpom_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
        _db.CreateLearningSession("sess-beh", "pharm-beh");
    }

    [Fact]
    public void Export_IncludesBehavioralSection()
    {
        var export = PomExporter.Export(_db, "sess-beh");
        var doc = JsonDocument.Parse(export);
        Assert.True(doc.RootElement.TryGetProperty("behavioral", out _),
            "Export must include a 'behavioral' section");
    }

    [Fact]
    public void Export_BehavioralSection_HasRoutinesArray()
    {
        var export = PomExporter.Export(_db, "sess-beh");
        var doc = JsonDocument.Parse(export);
        var behavioral = doc.RootElement.GetProperty("behavioral");
        Assert.True(behavioral.TryGetProperty("routines", out var routines),
            "behavioral section must have 'routines' array");
        Assert.Equal(JsonValueKind.Array, routines.ValueKind);
    }

    [Fact]
    public void Export_BehavioralSection_HasWritebackCandidatesArray()
    {
        var export = PomExporter.Export(_db, "sess-beh");
        var doc = JsonDocument.Parse(export);
        var behavioral = doc.RootElement.GetProperty("behavioral");
        Assert.True(behavioral.TryGetProperty("writebackCandidates", out var wbc),
            "behavioral section must have 'writebackCandidates' array");
        Assert.Equal(JsonValueKind.Array, wbc.ValueKind);
    }

    [Fact]
    public void Export_NoNameHashInJson()
    {
        // Ensure no nameHash or name_hash leaks into export
        var export = PomExporter.Export(_db, "sess-beh");
        Assert.DoesNotContain("nameHash", export, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("name_hash", export, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Export_BehavioralSection_EmptyWhenNoData()
    {
        var export = PomExporter.Export(_db, "sess-beh");
        var doc = JsonDocument.Parse(export);
        var behavioral = doc.RootElement.GetProperty("behavioral");
        Assert.Equal(0, behavioral.GetProperty("routines").GetArrayLength());
        Assert.Equal(0, behavioral.GetProperty("writebackCandidates").GetArrayLength());
        Assert.Equal(0, behavioral.GetProperty("uniqueScreens").GetInt32());
        Assert.Equal(0, behavioral.GetProperty("totalInteractions").GetInt32());
    }

    [Fact]
    public void Export_IncludesFeedbackSection()
    {
        var db = new AgentStateDb(":memory:");
        db.CreateLearningSession("sess-pom-fb", "pharm-test");

        // Seed a write correlation
        db.UpsertCorrelatedAction("sess-pom-fb", "tree:elem:qshape", "tree", "elem",
            "Button", "qshape", true, "Prescription");

        // Seed a feedback event
        var evt = new FeedbackEvent("sess-pom-fb", "writeback_outcome", "writeback", "wb-001",
            "correlation_key", "tree:elem:qshape", null,
            DirectiveType.ConfidenceAdjust, """{"newConfidence":0.87}""", null)
        { AppliedAt = DateTimeOffset.UtcNow.ToString("o"), AppliedBy = "inline" };
        db.InsertFeedbackEvent(evt);

        var json = PomExporter.Export(db, "sess-pom-fb");
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("feedback", out var fb));
        Assert.True(fb.TryGetProperty("totalFeedbackEvents", out var total));
        Assert.Equal(1, total.GetInt32());
        Assert.True(fb.TryGetProperty("confidenceTrajectory", out var ct));
        Assert.Equal(1, ct.GetArrayLength());
        Assert.True(fb.TryGetProperty("windowOverrides", out _));
        Assert.True(fb.TryGetProperty("staleCorrelations", out _));

        db.Dispose();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
