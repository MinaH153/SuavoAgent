using System.Text.Json;
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

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
