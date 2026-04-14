using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

public class SeedApplicatorTests : IDisposable
{
    private readonly AgentStateDb _db;
    private readonly SeedApplicator _applicator;
    private const string SessionId = "sess-1";

    public SeedApplicatorTests()
    {
        _db = new AgentStateDb(":memory:");
        _db.CreateLearningSession(SessionId, "pharm-1");
        _applicator = new SeedApplicator(_db);
    }

    public void Dispose() => _db.Dispose();

    // --- ApplyPatternSeeds ---

    [Fact]
    public void ApplyPatternSeeds_InsertsItemsCorrectly()
    {
        var response = MakePatternResponse("digest-1",
            queryShapes: new[] { new SeedQueryShape("qs-1", "UPDATE X SET Y=@p", new[] { "X" }, 0.88, 12) },
            statusMappings: new[] { new SeedStatusMapping("StatusTable", "guid-1", "Completed", 15) },
            workflowHints: new[] { new SeedWorkflowHint("wf-1", 4, 35.0, true, 8) });

        var result = _applicator.ApplyPatternSeeds(SessionId, response);

        Assert.Equal(3, result.ItemsApplied);
        Assert.False(result.AlreadyApplied);
        var items = _db.GetSeedItems("digest-1");
        Assert.Equal(3, items.Count);
        Assert.Contains(items, i => i.ItemType == "query_shape" && i.ItemKey == "qs-1");
        Assert.Contains(items, i => i.ItemType == "status_mapping" && i.ItemKey == "guid-1");
        Assert.Contains(items, i => i.ItemType == "workflow_hint" && i.ItemKey == "wf-1");
    }

    [Fact]
    public void ApplyPatternSeeds_RecordsAppliedSeed()
    {
        var response = MakePatternResponse("digest-1");

        _applicator.ApplyPatternSeeds(SessionId, response);

        var applied = _db.GetAppliedSeed("digest-1");
        Assert.NotNull(applied);
        Assert.Equal("pattern", applied!.Phase);
    }

    [Fact]
    public void ApplyPatternSeeds_AlreadyApplied_ReturnsEarly()
    {
        _db.InsertAppliedSeed("digest-1", "pattern", "2026-04-14T00:00:00Z", 3, 0);
        var response = MakePatternResponse("digest-1",
            queryShapes: new[] { new SeedQueryShape("qs-1", "SQL", new[] { "T" }, 0.8, 5) });

        var result = _applicator.ApplyPatternSeeds(SessionId, response);

        Assert.Equal(0, result.ItemsApplied);
        Assert.True(result.AlreadyApplied);
    }

    [Fact]
    public void ApplyPatternSeeds_NoWorkflowHints_CountsCorrectly()
    {
        var response = MakePatternResponse("digest-2",
            queryShapes: new[] { new SeedQueryShape("qs-1", "SELECT 1", new[] { "T" }, 0.9, 3) },
            workflowHints: null);

        var result = _applicator.ApplyPatternSeeds(SessionId, response);

        Assert.Equal(1, result.ItemsApplied);
    }

    // --- ApplyModelSeeds ---

    [Fact]
    public void ApplyModelSeeds_InsertsNewCorrelation()
    {
        var correlations = new[] {
            new SeedCorrelation("t1:btn1:q1", "t1", "btn1", "Button", "q1", 0.91, 0.94, 14, 0.6)
        };
        var response = MakeModelResponse("digest-2", correlations: correlations);

        var result = _applicator.ApplyModelSeeds(SessionId, response);

        Assert.Equal(1, result.CorrelationsApplied);
        Assert.Equal(0, result.CorrelationsSkipped);
        Assert.False(result.AlreadyApplied);

        var source = _db.GetCorrelatedActionSource(SessionId, "t1:btn1:q1");
        Assert.Equal("seed", source.Source);
        Assert.Equal("digest-2", source.SeedDigest);
    }

    [Fact]
    public void ApplyModelSeeds_SetsSeededConfidence()
    {
        var correlations = new[] {
            new SeedCorrelation("t1:btn1:q1", "t1", "btn1", "Button", "q1", 0.91, 0.94, 14, 0.6)
        };
        var response = MakeModelResponse("digest-3", correlations: correlations);

        _applicator.ApplyModelSeeds(SessionId, response);

        var actions = _db.GetCorrelatedActions(SessionId);
        Assert.Single(actions);
        Assert.Equal(0.6, actions[0].Confidence, 2);
    }

    [Fact]
    public void ApplyModelSeeds_LocalWins_SkipsExisting()
    {
        _db.UpsertCorrelatedAction(SessionId, "t1:btn1:q1", "t1", "btn1", "Button", "q1", true, "Tbl");

        var correlations = new[] {
            new SeedCorrelation("t1:btn1:q1", "t1", "btn1", "Button", "q1", 0.91, 0.94, 14, 0.6)
        };
        var response = MakeModelResponse("digest-2", correlations: correlations);

        var result = _applicator.ApplyModelSeeds(SessionId, response);

        Assert.Equal(0, result.CorrelationsApplied);
        Assert.Equal(1, result.CorrelationsSkipped);
        var source = _db.GetCorrelatedActionSource(SessionId, "t1:btn1:q1");
        Assert.Equal("local", source.Source);
    }

    [Fact]
    public void ApplyModelSeeds_AlreadyApplied_ReturnsEarly()
    {
        _db.InsertAppliedSeed("digest-2", "model", "2026-04-14T00:00:00Z", 1, 0);
        var correlations = new[] {
            new SeedCorrelation("t1:btn1:q1", "t1", "btn1", "Button", "q1", 0.91, 0.94, 14, 0.6)
        };
        var response = MakeModelResponse("digest-2", correlations: correlations);

        var result = _applicator.ApplyModelSeeds(SessionId, response);

        Assert.Equal(0, result.CorrelationsApplied);
        Assert.Equal(0, result.CorrelationsSkipped);
        Assert.True(result.AlreadyApplied);
    }

    [Fact]
    public void ApplyModelSeeds_NoCorrelations_RecordsSeedOnly()
    {
        var response = MakeModelResponse("digest-4", correlations: null);

        var result = _applicator.ApplyModelSeeds(SessionId, response);

        Assert.Equal(0, result.CorrelationsApplied);
        Assert.Equal(0, result.CorrelationsSkipped);
        Assert.False(result.AlreadyApplied);
        Assert.NotNull(_db.GetAppliedSeed("digest-4"));
    }

    [Fact]
    public void ApplyModelSeeds_MixedNewAndExisting()
    {
        _db.UpsertCorrelatedAction(SessionId, "t1:btn1:q1", "t1", "btn1", "Button", "q1", true, null);

        var correlations = new[] {
            new SeedCorrelation("t1:btn1:q1", "t1", "btn1", "Button", "q1", 0.91, 0.94, 14, 0.6),
            new SeedCorrelation("t2:btn2:q2", "t2", "btn2", "TextBox", "q2", 0.85, 0.90, 10, 0.5),
        };
        var response = MakeModelResponse("digest-5", correlations: correlations);

        var result = _applicator.ApplyModelSeeds(SessionId, response);

        Assert.Equal(1, result.CorrelationsApplied);
        Assert.Equal(1, result.CorrelationsSkipped);
    }

    [Fact]
    public void ApplyModelSeeds_RejectedItemRecorded()
    {
        _db.UpsertCorrelatedAction(SessionId, "t1:btn1:q1", "t1", "btn1", "Button", "q1", true, null);

        var correlations = new[] {
            new SeedCorrelation("t1:btn1:q1", "t1", "btn1", "Button", "q1", 0.91, 0.94, 14, 0.6)
        };
        var response = MakeModelResponse("digest-6", correlations: correlations);

        _applicator.ApplyModelSeeds(SessionId, response);

        var items = _db.GetSeedItems("digest-6");
        var correlationItem = items.Single(i => i.ItemType == "correlation");
        Assert.NotNull(correlationItem.RejectedAt);
    }

    // --- GetSeededShapeHashes ---

    [Fact]
    public void GetSeededShapeHashes_FiltersToQueryShapes()
    {
        var response = MakePatternResponse("digest-7",
            queryShapes: new[] {
                new SeedQueryShape("qs-a", "SELECT 1", new[] { "T" }, 0.9, 5),
                new SeedQueryShape("qs-b", "SELECT 2", new[] { "T" }, 0.8, 3),
            },
            statusMappings: new[] { new SeedStatusMapping("Tbl", "guid-x", "Done", 2) });

        _applicator.ApplyPatternSeeds(SessionId, response);

        var hashes = _applicator.GetSeededShapeHashes("digest-7");
        Assert.Equal(2, hashes.Count);
        Assert.Contains("qs-a", hashes);
        Assert.Contains("qs-b", hashes);
    }

    [Fact]
    public void GetSeededShapeHashes_EmptyForUnknownDigest()
    {
        var hashes = _applicator.GetSeededShapeHashes("nonexistent");
        Assert.Empty(hashes);
    }

    // --- Helpers ---

    private static SeedResponse MakePatternResponse(string digest,
        SeedQueryShape[]? queryShapes = null,
        SeedStatusMapping[]? statusMappings = null,
        SeedWorkflowHint[]? workflowHints = null) =>
        new(digest, 1, "pattern", new[] { "schema" }, null, null,
            queryShapes ?? Array.Empty<SeedQueryShape>(),
            statusMappings ?? Array.Empty<SeedStatusMapping>(),
            workflowHints);

    private static SeedResponse MakeModelResponse(string digest,
        IReadOnlyList<SeedCorrelation>? correlations = null,
        SeedQueryShape[]? queryShapes = null,
        SeedStatusMapping[]? statusMappings = null) =>
        new(digest, 2, "model", new[] { "schema", "ui" },
            new UiOverlap(8, 10, 0.8), correlations,
            queryShapes ?? Array.Empty<SeedQueryShape>(),
            statusMappings ?? Array.Empty<SeedStatusMapping>(),
            null);
}
