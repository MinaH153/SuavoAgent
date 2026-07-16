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
        Assert.Matches("^[a-f0-9]{64}$", ct[0].GetProperty("correlationToken").GetString());
        Assert.False(ct[0].TryGetProperty("correlationKey", out _));
        Assert.DoesNotContain("tree:elem:qshape", json, StringComparison.Ordinal);
        Assert.True(fb.TryGetProperty("windowOverrides", out _));
        Assert.True(fb.TryGetProperty("staleCorrelations", out _));

        db.Dispose();
    }

    [Fact]
    public void Export_RoutineAndWritebackMetadata_ContainsOnlyCloudTokens()
    {
        const string rawTree = "legacy-tree-1234";
        const string rawElement = "patient-search-box";
        const string rawControl = "Edit";
        const string rawCorrelation = "patient-search-correlation";
        const string rawQueryHash = "legacy-query-shape";

        var path = JsonSerializer.Serialize(new[]
        {
            new { treeHash = rawTree, elementId = rawElement, controlType = rawControl, queryShapeHash = (string?)rawQueryHash },
            new { treeHash = "tree-b", elementId = "element-b", controlType = "Button", queryShapeHash = (string?)null },
            new { treeHash = "tree-c", elementId = "element-c", controlType = "Text", queryShapeHash = (string?)null },
        });
        _db.UpsertLearnedRoutine("sess-beh", "legacy-routine", path, 3, 6, 0.9,
            rawElement, "element-c", JsonSerializer.Serialize(new[] { rawQueryHash }), true);
        _db.UpsertCorrelatedAction("sess-beh", rawCorrelation, rawTree, rawElement,
            rawControl, rawQueryHash, true, "Prescription");

        var json = PomExporter.Export(_db, "sess-beh", pmsVersionHash: "PioneerRx 6.1");
        var root = JsonDocument.Parse(json).RootElement;
        var behavioral = root.GetProperty("behavioral");
        var routine = behavioral.GetProperty("routines")[0];
        var step = routine.GetProperty("path")[0];
        var candidate = behavioral.GetProperty("writebackCandidates")[0];

        Assert.Matches("^[a-f0-9]{64}$", behavioral.GetProperty("pmsVersionHash").GetString());
        Assert.Matches("^[a-f0-9]{64}$", routine.GetProperty("routineHash").GetString());
        Assert.Matches("^[a-f0-9]{64}$", step.GetProperty("treeHash").GetString());
        Assert.Matches("^[a-f0-9]{64}$", step.GetProperty("elementToken").GetString());
        Assert.Matches("^[a-f0-9]{64}$", step.GetProperty("controlTypeToken").GetString());
        Assert.Matches("^[a-f0-9]{64}$", step.GetProperty("queryShapeHash").GetString());
        Assert.False(step.TryGetProperty("elementId", out _));
        Assert.False(step.TryGetProperty("controlType", out _));
        Assert.Matches("^[a-f0-9]{64}$", candidate.GetProperty("correlationToken").GetString());
        Assert.Matches("^[a-f0-9]{64}$", candidate.GetProperty("elementToken").GetString());
        Assert.False(candidate.TryGetProperty("correlationKey", out _));
        Assert.False(candidate.TryGetProperty("elementId", out _));
        Assert.DoesNotContain(rawTree, json, StringComparison.Ordinal);
        Assert.DoesNotContain(rawElement, json, StringComparison.Ordinal);
        Assert.DoesNotContain(rawControl, json, StringComparison.Ordinal);
        Assert.DoesNotContain(rawCorrelation, json, StringComparison.Ordinal);
        Assert.DoesNotContain(rawQueryHash, json, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
