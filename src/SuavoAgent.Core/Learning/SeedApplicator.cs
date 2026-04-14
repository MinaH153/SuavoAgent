using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Learning;

public sealed class SeedApplicator
{
    private readonly AgentStateDb _db;

    public SeedApplicator(AgentStateDb db) => _db = db;

    public record ApplyResult(int ItemsApplied, bool AlreadyApplied);
    public record ModelApplyResult(int CorrelationsApplied, int CorrelationsSkipped, bool AlreadyApplied);

    public ApplyResult ApplyPatternSeeds(string sessionId, SeedResponse response)
    {
        if (_db.GetAppliedSeed(response.SeedDigest) is not null)
            return new(0, AlreadyApplied: true);

        var now = DateTimeOffset.UtcNow.ToString("o");
        int applied = 0;

        foreach (var qs in response.QueryShapes)
        {
            _db.InsertSeedItem(response.SeedDigest, "query_shape", qs.QueryShapeHash, now);
            applied++;
        }

        foreach (var sm in response.StatusMappings)
        {
            _db.InsertSeedItem(response.SeedDigest, "status_mapping", sm.StatusGuid, now);
            applied++;
        }

        if (response.WorkflowHints is { } hints)
        {
            foreach (var wh in hints)
            {
                _db.InsertSeedItem(response.SeedDigest, "workflow_hint", wh.RoutineHash, now);
                applied++;
            }
        }

        _db.InsertAppliedSeed(response.SeedDigest, "pattern", now, applied, 0);
        return new(applied, AlreadyApplied: false);
    }

    public ModelApplyResult ApplyModelSeeds(string sessionId, SeedResponse response)
    {
        if (_db.GetAppliedSeed(response.SeedDigest) is not null)
            return new(0, 0, AlreadyApplied: true);

        var now = DateTimeOffset.UtcNow.ToString("o");
        int applied = 0, skipped = 0;

        foreach (var qs in response.QueryShapes)
            _db.InsertSeedItem(response.SeedDigest, "query_shape", qs.QueryShapeHash, now);
        foreach (var sm in response.StatusMappings)
            _db.InsertSeedItem(response.SeedDigest, "status_mapping", sm.StatusGuid, now);

        if (response.Correlations is not { } correlations)
        {
            _db.InsertAppliedSeed(response.SeedDigest, "model", now, 0, 0);
            return new(0, 0, AlreadyApplied: false);
        }

        var existing = _db.GetCorrelatedActions(sessionId);
        var existingKeys = new HashSet<string>(existing.Select(e => e.CorrelationKey));

        foreach (var c in correlations)
        {
            _db.InsertSeedItem(response.SeedDigest, "correlation", c.CorrelationKey, now);

            if (existingKeys.Contains(c.CorrelationKey))
            {
                _db.RejectSeedItem(response.SeedDigest, "correlation", c.CorrelationKey, now);
                skipped++;
                continue;
            }

            _db.UpsertCorrelatedAction(sessionId, c.CorrelationKey, c.TreeHash, c.ElementId,
                c.ControlType, c.QueryShapeHash, true, null);
            _db.SetCorrelatedActionSource(sessionId, c.CorrelationKey, "seed", response.SeedDigest, now);
            var clampedConfidence = Math.Clamp(c.SeededConfidence, 0.0, 0.6);
            _db.UpdateCorrelationConfidence(sessionId, c.CorrelationKey, clampedConfidence);
            applied++;
        }

        _db.InsertAppliedSeed(response.SeedDigest, "model", now, applied, skipped);
        return new(applied, skipped, AlreadyApplied: false);
    }

    public IReadOnlyList<string> GetSeededShapeHashes(string seedDigest) =>
        _db.GetSeedItems(seedDigest)
            .Where(i => i.ItemType == "query_shape")
            .Select(i => i.ItemKey)
            .ToList();
}
