using System.Text.Json;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Learning;

public sealed class SeedApplicator
{
    private readonly AgentStateDb _db;

    public SeedApplicator(AgentStateDb db) => _db = db;

    public record ApplyResult(int ItemsApplied, bool AlreadyApplied);
    public record ModelApplyResult(int CorrelationsApplied, int CorrelationsSkipped, bool AlreadyApplied);

    // Validate every seeded SQL shape through the tokenizer before storing.
    // A single failure rejects the entire batch (fail-closed).
    private static bool ValidateQueryShapes(IReadOnlyList<SeedQueryShape> shapes)
    {
        foreach (var qs in shapes)
        {
            if (SqlTokenizer.TryNormalize(qs.ParameterizedSql) is null)
                return false;
        }
        return true;
    }

    public ApplyResult ApplyPatternSeeds(string sessionId, SeedResponse response)
    {
        if (_db.GetAppliedSeed(response.SeedDigest) is not null)
            return new(0, AlreadyApplied: true);

        if (!ValidateQueryShapes(response.QueryShapes))
            throw new InvalidOperationException(
                $"Seed {response.SeedDigest} rejected — one or more QueryShapes failed SqlTokenizer validation");

        using var txn = _db.BeginTransaction();
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
        _db.CommitTransaction(txn);
        return new(applied, AlreadyApplied: false);
    }

    public ModelApplyResult ApplyModelSeeds(string sessionId, SeedResponse response)
    {
        if (_db.GetAppliedSeed(response.SeedDigest) is not null)
            return new(0, 0, AlreadyApplied: true);

        if (!ValidateQueryShapes(response.QueryShapes))
            throw new InvalidOperationException(
                $"Seed {response.SeedDigest} rejected — one or more QueryShapes failed SqlTokenizer validation");

        using var txn = _db.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToString("o");
        int applied = 0, skipped = 0;

        foreach (var qs in response.QueryShapes)
            _db.InsertSeedItem(response.SeedDigest, "query_shape", qs.QueryShapeHash, now);
        foreach (var sm in response.StatusMappings)
            _db.InsertSeedItem(response.SeedDigest, "status_mapping", sm.StatusGuid, now);

        if (response.Correlations is not { } correlations)
        {
            _db.InsertAppliedSeed(response.SeedDigest, "model", now, 0, 0);
            _db.CommitTransaction(txn);
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
        _db.CommitTransaction(txn);
        return new(applied, skipped, AlreadyApplied: false);
    }

    public IReadOnlyList<string> GetSeededShapeHashes(string seedDigest) =>
        _db.GetSeedItems(seedDigest)
            .Where(i => i.ItemType == "query_shape")
            .Select(i => i.ItemKey)
            .ToList();

    // ── v3.12 Workflow Template seeds (Spec-D template transfer) ──

    public record TemplateApplyResult(int TemplatesApplied, int TemplatesSkipped);

    /// <summary>
    /// Applies <see cref="SeedResponse.WorkflowTemplates"/> under three gates:
    ///   1. <see cref="PmsVersionFingerprint.Matches"/> against the local
    ///      installation — if the template's PmsVersionRange doesn't cover
    ///      the local fingerprint, the template is rejected.
    ///   2. Every <see cref="TemplateStep.CorrelatedQueryShapeHash"/> must be
    ///      known locally (either via prior seed apply or via local
    ///      correlation) — unknown query shapes mean the template would
    ///      reference a SQL we can't validate, so we refuse.
    ///   3. Tokenizer re-validation on every transferred query shape (defense
    ///      in depth — already runs in ApplyPatternSeeds / ApplyModelSeeds).
    ///
    /// Every emitted <see cref="WorkflowTemplate"/> carries
    /// <c>ExtractedBy = "seed:&lt;digest&gt;"</c>. Generated rules downstream
    /// inherit <c>autonomousOk=false</c> from the generator invariant.
    /// </summary>
    public TemplateApplyResult ApplyWorkflowTemplates(
        string sessionId, SeedResponse response, PmsVersionFingerprint localFingerprint)
    {
        if (response.WorkflowTemplates is null || response.WorkflowTemplates.Count == 0)
            return new(0, 0);

        // Known query shapes: this session's correlated_actions + seed-applied shapes.
        var knownShapes = new HashSet<string>(
            _db.GetCorrelatedActions(sessionId)
                .Where(c => c.QueryShapeHash is not null)
                .Select(c => c.QueryShapeHash!));
        foreach (var s in _db.GetSeedItems(response.SeedDigest)
            .Where(i => i.ItemType == "query_shape"))
        {
            knownShapes.Add(s.ItemKey);
        }

        int applied = 0, skipped = 0;
        var now = DateTimeOffset.UtcNow.ToString("o");

        foreach (var tmpl in response.WorkflowTemplates)
        {
            if (!PmsVersionIncluded(tmpl.PmsVersionRange, localFingerprint))
            {
                _db.RejectSeedItem(response.SeedDigest, "template", tmpl.TemplateId, now);
                skipped++;
                continue;
            }

            if (!AllShapesKnown(tmpl.Steps, knownShapes))
            {
                _db.RejectSeedItem(response.SeedDigest, "template", tmpl.TemplateId, now);
                skipped++;
                continue;
            }

            var rangeJson = JsonSerializer.Serialize(tmpl.PmsVersionRange);
            var stepsJson = JsonSerializer.Serialize(tmpl.Steps);
            _db.UpsertWorkflowTemplate(
                templateId: tmpl.TemplateId,
                templateVersion: tmpl.TemplateVersion,
                skillId: tmpl.SkillId,
                processNameGlob: tmpl.ProcessNameGlob,
                pmsVersionRangeJson: rangeJson,
                screenSignature: tmpl.ScreenSignatureV1,
                stepsHash: tmpl.StepsHash,
                routineHashOrigin: null,
                stepsJson: stepsJson,
                aggregateConfidence: tmpl.AggregateConfidence,
                observationCount: tmpl.ContributorCount,
                hasWriteback: tmpl.HasWriteback,
                extractedAt: now,
                extractedBy: $"seed:{response.SeedDigest}");
            _db.InsertSeedItem(response.SeedDigest, "template", tmpl.TemplateId, now);
            applied++;
        }

        return new(applied, skipped);
    }

    private static bool PmsVersionIncluded(
        IReadOnlyList<PmsVersionFingerprint> range, PmsVersionFingerprint local)
    {
        if (range is null || range.Count == 0) return false;
        foreach (var fp in range)
        {
            if (fp.Matches(local)) return true;
        }
        return false;
    }

    private static bool AllShapesKnown(IReadOnlyList<TemplateStep> steps, HashSet<string> known)
    {
        foreach (var s in steps)
        {
            if (s.CorrelatedQueryShapeHash is null) continue;
            if (!known.Contains(s.CorrelatedQueryShapeHash)) return false;
        }
        return true;
    }
}
