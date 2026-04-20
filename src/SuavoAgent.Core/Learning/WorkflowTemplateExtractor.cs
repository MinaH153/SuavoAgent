using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Learning;

/// <summary>
/// Consumes RoutineDetector output + ActionCorrelator mappings + behavioral
/// events and emits <see cref="WorkflowTemplate"/> records when confidence
/// thresholds cross. The v3.12 "agent writes its own playbooks" loop.
///
/// Safety rules (see spec §1.2, §3, §4):
///   • Every ElementSignature is built from the GREEN tier only — ControlType,
///     element_id (AutomationId), ClassName. Elements whose element_id is the
///     fallback "ClassName:Depth:ChildIndex" form are excluded (not stable
///     across pharmacies).
///   • A writeback step with no derivable ExpectedAfter → entire template is
///     skipped (fail closed), never emitted without a post-state assertion.
///   • Emission is idempotent by (ScreenSignatureV1 + StepsHash) → TemplateId.
///     Different StepsHash on the same ScreenSignature bumps the major version
///     and retires the prior template with reason <c>superseded</c>.
///   • Low-confidence routines on an existing template increment a counter and
///     retire with reason <c>confidence_drop</c> past the configured threshold.
/// </summary>
public sealed class WorkflowTemplateExtractor
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly AgentStateDb _db;
    private readonly string _sessionId;
    private readonly string _skillId;
    private readonly string _processNameGlob;
    private readonly Func<PmsVersionFingerprint> _fingerprint;
    private readonly WorkflowTemplateThresholds _thresholds;
    private readonly ILogger _logger;

    public WorkflowTemplateExtractor(
        AgentStateDb db,
        string sessionId,
        string skillId,
        string processNameGlob,
        Func<PmsVersionFingerprint> fingerprintProvider,
        WorkflowTemplateThresholds thresholds,
        ILogger<WorkflowTemplateExtractor>? logger = null)
    {
        _db = db;
        _sessionId = sessionId;
        _skillId = skillId;
        _processNameGlob = processNameGlob;
        _fingerprint = fingerprintProvider;
        _thresholds = thresholds;
        _logger = logger ?? NullLogger<WorkflowTemplateExtractor>.Instance;
    }

    public IReadOnlyList<WorkflowTemplate> ExtractAndPersist()
    {
        var emitted = new List<WorkflowTemplate>();

        var routines = _db.GetLearnedRoutines(_sessionId);
        var correlations = _db.GetCorrelatedActions(_sessionId);
        // Per-(tree, elementId) writeback flag: any correlated write on that
        // coordinate qualifies the step as destructive, independent of the
        // routine's captured queryShapeHash (the routine may have missed the
        // correlation if the observation ordering was split across sessions).
        var writeKeys = new HashSet<string>(
            correlations.Where(c => c.IsWrite).Select(c => $"{c.TreeHash}:{c.ElementId}"));
        // Per-(tree, elementId) → representative write query shape hash, used
        // to stamp the TemplateStep. Deterministic — first-by-occurrence wins.
        var writeShape = new Dictionary<string, string?>();
        foreach (var c in correlations.Where(c => c.IsWrite))
        {
            var key = $"{c.TreeHash}:{c.ElementId}";
            if (!writeShape.ContainsKey(key)) writeShape[key] = c.QueryShapeHash;
        }

        foreach (var routine in routines)
        {
            var low = routine.Confidence < _thresholds.MinRoutineConfidence;

            if (low)
            {
                // Existing template for this routine? Bump low-conf counter / retire.
                var existingScreenId = TryDeriveScreenSignatureFromRoutineHash(routine.RoutineHash);
                if (existingScreenId is not null)
                {
                    var runs = _db.IncrementTemplateLowConfidenceRuns(existingScreenId);
                    if (runs >= _thresholds.LowConfidenceRetirementAfter)
                    {
                        _db.RetireWorkflowTemplate(existingScreenId, Now(), "confidence_drop");
                    }
                }
                continue;
            }

            var wt = BuildTemplateForRoutine(routine, writeKeys, writeShape);
            if (wt is null) continue;

            emitted.Add(wt);
        }

        return emitted;
    }

    private string? TryDeriveScreenSignatureFromRoutineHash(string routineHash)
    {
        // A template carries RoutineHashOrigin, so reverse-lookup is O(n) in
        // active templates. That's fine in v3.12 — counts are small.
        var templates = _db.GetActiveWorkflowTemplates(_skillId);
        return templates.FirstOrDefault(t => t.RoutineHashOrigin == routineHash)?.TemplateId;
    }

    private WorkflowTemplate? BuildTemplateForRoutine(
        (string RoutineHash, string PathJson, int PathLength, int Frequency,
         double Confidence, string? StartElementId, string? EndElementId,
         string? CorrelatedWriteQueries, bool HasWritebackCandidate) routine,
        HashSet<string> writeKeys,
        Dictionary<string, string?> writeShape)
    {
        List<PathStepDto>? path;
        try
        {
            path = JsonSerializer.Deserialize<List<PathStepDto>>(routine.PathJson, JsonOpts);
        }
        catch { return null; }

        if (path is null || path.Count < _thresholds.MinStepCount) return null;

        var steps = new List<TemplateStep>(path.Count);
        var stepConfidences = new List<double>(path.Count);
        bool hasWriteback = false;

        for (int i = 0; i < path.Count; i++)
        {
            var p = path[i];
            if (string.IsNullOrWhiteSpace(p.TreeHash) || string.IsNullOrWhiteSpace(p.ElementId))
                return null;
            if (p.ElementId.Contains(':'))
                // Fallback-form element id — not cross-installation safe.
                return null;

            var structure = _db.GetElementStructure(_sessionId, p.TreeHash, p.ElementId);
            if (structure is null) return null;
            var (ctrl, cls) = structure.Value;
            if (string.IsNullOrWhiteSpace(ctrl)) return null;

            var target = new ElementSignature(ctrl!, p.ElementId, cls);

            // ExpectedVisible from this screen's observed elements.
            var screenElements = _db.GetDistinctElementsOnTree(_sessionId, p.TreeHash)
                .Take(_thresholds.MaxExpectedVisiblePerScreen)
                .ToList();
            if (screenElements.Count == 0) return null;

            var expectedVisible = screenElements
                .Select(e => new ElementSignature(e.ControlType, e.ElementId, e.ClassName))
                .ToList();
            int minRequired = Math.Max(1,
                (int)Math.Ceiling(expectedVisible.Count * _thresholds.MatchRatio));
            if (minRequired > expectedVisible.Count) minRequired = expectedVisible.Count;

            // Writeback is any correlated IsWrite on this (tree, element) —
            // independent of the routine's captured queryShapeHash.
            var coordKey = $"{p.TreeHash}:{p.ElementId}";
            bool isWrite = writeKeys.Contains(coordKey);
            string? shape = p.QueryShapeHash
                ?? (isWrite && writeShape.TryGetValue(coordKey, out var s) ? s : null);

            List<ElementSignature>? expectedAfter = null;
            if (isWrite)
            {
                // Per-writeback post-state: the tree observed at the start of
                // the next step is the natural anchor. Only if this writeback
                // is the routine's terminal step do we fall back to the
                // threshold-level override (typically Helper-captured).
                string? postTree = i + 1 < path.Count
                    ? path[i + 1].TreeHash
                    : _thresholds.WritebackPostStateTreeHash;

                if (string.IsNullOrWhiteSpace(postTree))
                {
                    _logger.LogWarning(
                        "WorkflowTemplateExtractor: template dropped — writeback step {Ordinal} for routine {Routine} has no post-state anchor (terminal writeback, WritebackPostStateTreeHash unset)",
                        i, routine.RoutineHash);
                    return null;
                }

                var postElements = _db.GetDistinctElementsOnTree(_sessionId, postTree);
                if (postElements.Count == 0)
                {
                    _logger.LogWarning(
                        "WorkflowTemplateExtractor: template dropped — writeback step {Ordinal} for routine {Routine} has post-state tree {Tree} with zero observed elements",
                        i, routine.RoutineHash, postTree);
                    return null;
                }
                expectedAfter = postElements
                    .Take(_thresholds.MaxExpectedVisiblePerScreen)
                    .Select(e => new ElementSignature(e.ControlType, e.ElementId, e.ClassName))
                    .ToList();
                hasWriteback = true;
            }

            double stepConfidence = Math.Clamp(routine.Confidence, 0.0, 1.0);
            stepConfidences.Add(stepConfidence);

            var step = new TemplateStep(
                Ordinal: i,
                Kind: InferKind(ctrl!, isWrite),
                Target: target,
                ExpectedVisible: expectedVisible,
                MinElementsRequired: minRequired,
                ExpectedAfter: expectedAfter,
                IsWrite: isWrite,
                CorrelatedQueryShapeHash: shape,
                StepConfidence: stepConfidence,
                Hint: null);
            steps.Add(step);
        }

        if (steps.Count < _thresholds.MinStepCount) return null;

        var stepsHash = WorkflowTemplate.ComputeStepsHash(steps);
        var screenSig = WorkflowTemplate.ComputeScreenSignature(steps[0].ExpectedVisible);
        var templateId = WorkflowTemplate.ComputeTemplateId(screenSig, stepsHash);

        // Version resolution:
        //   • No existing slot → 1.0.0.
        //   • Existing with same stepsHash → reuse version, refresh obs count.
        //   • Existing with different stepsHash → bump major, retire prior.
        var existing = _db.GetWorkflowTemplateByScreen(_skillId, screenSig);
        string version;
        string? routineOrigin = routine.RoutineHash;

        if (existing is null)
        {
            version = "1.0.0";
        }
        else if (string.Equals(existing.StepsHash, stepsHash, StringComparison.Ordinal))
        {
            version = existing.TemplateVersion;
        }
        else
        {
            _db.RetireWorkflowTemplate(existing.TemplateId, Now(), "superseded");
            version = BumpMajor(existing.TemplateVersion);
        }

        var pmsFp = _fingerprint();
        var pmsRangeJson = JsonSerializer.Serialize(new[] { pmsFp }, JsonOpts);
        var stepsJson = JsonSerializer.Serialize(steps, JsonOpts);

        double aggregate = stepConfidences.Count == 0 ? 0.0 : stepConfidences.Min();
        int obs = Math.Max(routine.Frequency, existing?.ObservationCount ?? 0);

        var now = Now();
        _db.UpsertWorkflowTemplate(
            templateId: templateId,
            templateVersion: version,
            skillId: _skillId,
            processNameGlob: _processNameGlob,
            pmsVersionRangeJson: pmsRangeJson,
            screenSignature: screenSig,
            stepsHash: stepsHash,
            routineHashOrigin: routineOrigin,
            stepsJson: stepsJson,
            aggregateConfidence: aggregate,
            observationCount: obs,
            hasWriteback: hasWriteback,
            extractedAt: now,
            extractedBy: "local-v3.12");
        _db.ResetTemplateLowConfidenceRuns(templateId);

        return new WorkflowTemplate(
            TemplateId: templateId,
            TemplateVersion: version,
            SkillId: _skillId,
            ProcessNameGlob: _processNameGlob,
            PmsVersionRange: new[] { pmsFp },
            ScreenSignatureV1: screenSig,
            StepsHash: stepsHash,
            RoutineHashOrigin: routineOrigin,
            Steps: steps,
            AggregateConfidence: aggregate,
            ObservationCount: obs,
            HasWriteback: hasWriteback,
            ExtractedAt: now,
            ExtractedBy: "local-v3.12",
            RetiredAt: null,
            RetirementReason: null);
    }

    private static TemplateStepKind InferKind(string controlType, bool isWrite) =>
        controlType switch
        {
            "Edit" => TemplateStepKind.Type,
            _ when isWrite => TemplateStepKind.Click,
            _ => TemplateStepKind.Click,
        };

    private static string Now() => DateTimeOffset.UtcNow.ToString("o");

    private static string BumpMajor(string semver)
    {
        var parts = semver.Split('.');
        if (parts.Length == 0 || !int.TryParse(parts[0], out var major))
            return "2.0.0";
        return $"{major + 1}.0.0";
    }

    private sealed record PathStepDto(string TreeHash, string ElementId,
        string? ControlType, string? QueryShapeHash);
}
