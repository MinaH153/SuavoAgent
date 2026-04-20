using System;
using System.Collections.Generic;
using System.Linq;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

/// <summary>
/// Spec-D template transfer gates (v3.12 §6). Seed envelope is already
/// ECDSA-verified at the transport layer, so these tests focus on the
/// receiver-side safety checks: PMS fingerprint match, known-shape
/// validation, autonomousOk=false propagation, and rejection accounting.
/// </summary>
public class SeedApplicatorTemplateTransferTests : IDisposable
{
    private readonly AgentStateDb _db;
    private readonly string _sessionId = "seed-xfer-session";

    public SeedApplicatorTemplateTransferTests()
    {
        _db = new AgentStateDb(":memory:");
        _db.CreateLearningSession(_sessionId, "pharm-xfer");
    }

    public void Dispose() => _db.Dispose();

    private static readonly PmsVersionFingerprint LocalFp = new(
        "PioneerRx", "schema-local", "dialect-local", "2026.3.1");

    private static readonly PmsVersionFingerprint WrongFp = new(
        "PioneerRx", "schema-other", "dialect-other", "2025.1");

    private static ElementSignature Sig(string ctrl, string id, string? cls = null) => new(ctrl, id, cls);

    private static TemplateStep BasicStep(int ord, ElementSignature target,
        IReadOnlyList<ElementSignature> visible, string? shape = null,
        bool isWrite = false, IReadOnlyList<ElementSignature>? after = null) =>
        new(ord, TemplateStepKind.Click, target, visible,
            MinElementsRequired: Math.Max(1, (int)Math.Ceiling(visible.Count * 0.8)),
            ExpectedAfter: after, IsWrite: isWrite,
            CorrelatedQueryShapeHash: shape, StepConfidence: 0.9, Hint: null);

    private SeedResponse BuildResponse(SeedWorkflowTemplate[] templates, string digest = "seed-abc") =>
        new(SeedDigest: digest, SeedVersion: 1, Phase: "pattern",
            GatesPassed: new[] { "schema" }, UiOverlap: null, Correlations: null,
            QueryShapes: Array.Empty<SeedQueryShape>(),
            StatusMappings: Array.Empty<SeedStatusMapping>(),
            WorkflowHints: null,
            WorkflowTemplates: templates);

    private static SeedWorkflowTemplate BuildTemplate(
        IReadOnlyList<PmsVersionFingerprint> range,
        string? shape = null, string id = "tmpl-1")
    {
        var btnOpen = Sig("Button", "btnOpen");
        var txtSearch = Sig("Edit", "txtSearch");
        var btnApprove = Sig("Button", "btnApprove");
        var visibleEntry = new[] { btnOpen, txtSearch, btnApprove };
        var steps = new[]
        {
            BasicStep(0, btnOpen, visibleEntry, shape: shape),
            BasicStep(1, txtSearch, visibleEntry),
        };
        var stepsHash = WorkflowTemplate.ComputeStepsHash(steps);
        var screenSig = WorkflowTemplate.ComputeScreenSignature(visibleEntry);
        return new SeedWorkflowTemplate(
            TemplateId: id, TemplateVersion: "1.0.0", SkillId: "learned",
            ProcessNameGlob: "PioneerPharmacy*", PmsVersionRange: range,
            ScreenSignatureV1: screenSig, StepsHash: stepsHash,
            Steps: steps, AggregateConfidence: 0.9,
            ContributorCount: 3, FleetMatchCount: 9, FleetMismatchCount: 0,
            HasWriteback: false);
    }

    // ──────────────────────── happy path ────────────────────────

    [Fact]
    public void Apply_CompatibleTemplate_Persisted()
    {
        var applicator = new SeedApplicator(_db);
        var tmpl = BuildTemplate(new[] { LocalFp });
        var result = applicator.ApplyWorkflowTemplates(_sessionId, BuildResponse(new[] { tmpl }), LocalFp);

        Assert.Equal(1, result.TemplatesApplied);
        Assert.Equal(0, result.TemplatesSkipped);

        var stored = _db.GetWorkflowTemplate(tmpl.TemplateId);
        Assert.NotNull(stored);
        Assert.StartsWith("seed:", stored!.ExtractedBy);
    }

    // ──────────────────────── gate 1: PMS fingerprint ────────────────────────

    [Fact]
    public void Apply_PmsVersionMismatch_Rejected()
    {
        var applicator = new SeedApplicator(_db);
        var tmpl = BuildTemplate(new[] { WrongFp });
        var result = applicator.ApplyWorkflowTemplates(_sessionId, BuildResponse(new[] { tmpl }), LocalFp);

        Assert.Equal(0, result.TemplatesApplied);
        Assert.Equal(1, result.TemplatesSkipped);
        Assert.Null(_db.GetWorkflowTemplate(tmpl.TemplateId));
    }

    [Fact]
    public void Apply_RangeIncludesLocal_Accepted()
    {
        // Range contains both our version and another; at least one match → accept.
        var applicator = new SeedApplicator(_db);
        var tmpl = BuildTemplate(new[] { WrongFp, LocalFp });
        var result = applicator.ApplyWorkflowTemplates(_sessionId, BuildResponse(new[] { tmpl }), LocalFp);

        Assert.Equal(1, result.TemplatesApplied);
        Assert.Equal(0, result.TemplatesSkipped);
    }

    // ──────────────────────── gate 2: shapes must be locally known ────────────────────────

    [Fact]
    public void Apply_TemplateReferencesUnknownShape_Rejected()
    {
        var applicator = new SeedApplicator(_db);
        var tmpl = BuildTemplate(new[] { LocalFp }, shape: "unknown-shape-xyz");
        var result = applicator.ApplyWorkflowTemplates(_sessionId, BuildResponse(new[] { tmpl }), LocalFp);

        Assert.Equal(0, result.TemplatesApplied);
        Assert.Equal(1, result.TemplatesSkipped);
        Assert.Null(_db.GetWorkflowTemplate(tmpl.TemplateId));
    }

    [Fact]
    public void Apply_ShapeProvidedBySameSeedResponse_Accepted()
    {
        // Seed_items records include the query_shapes applied from this very
        // response — those count as "known" for template gate purposes.
        var now = DateTimeOffset.UtcNow.ToString("o");
        _db.InsertSeedItem("seed-abc", "query_shape", "known-shape-1", now);

        var applicator = new SeedApplicator(_db);
        var tmpl = BuildTemplate(new[] { LocalFp }, shape: "known-shape-1");
        var result = applicator.ApplyWorkflowTemplates(_sessionId, BuildResponse(new[] { tmpl }), LocalFp);

        Assert.Equal(1, result.TemplatesApplied);
        Assert.Equal(0, result.TemplatesSkipped);
    }

    [Fact]
    public void Apply_ShapeFromLocalCorrelations_Accepted()
    {
        _db.UpsertCorrelatedAction(_sessionId,
            correlationKey: "treeA:btnOpen:local-shape",
            treeHash: "treeA", elementId: "btnOpen", controlType: "Button",
            queryShapeHash: "local-shape", isWrite: false, tablesReferenced: null);

        var applicator = new SeedApplicator(_db);
        var tmpl = BuildTemplate(new[] { LocalFp }, shape: "local-shape");
        var result = applicator.ApplyWorkflowTemplates(_sessionId, BuildResponse(new[] { tmpl }), LocalFp);

        Assert.Equal(1, result.TemplatesApplied);
    }

    // ──────────────────────── empty payload ────────────────────────

    [Fact]
    public void Apply_NoWorkflowTemplates_ZeroActivity()
    {
        var applicator = new SeedApplicator(_db);
        var response = new SeedResponse(
            SeedDigest: "seed-nil", SeedVersion: 1, Phase: "pattern",
            GatesPassed: Array.Empty<string>(), UiOverlap: null,
            Correlations: null, QueryShapes: Array.Empty<SeedQueryShape>(),
            StatusMappings: Array.Empty<SeedStatusMapping>(),
            WorkflowHints: null, WorkflowTemplates: null);
        var result = applicator.ApplyWorkflowTemplates(_sessionId, response, LocalFp);
        Assert.Equal(0, result.TemplatesApplied);
        Assert.Equal(0, result.TemplatesSkipped);
    }

    // ──────────────────────── retire-first on screen-signature collision ────────────────────────

    [Fact]
    public void Apply_SeedCollidesWithLocalActiveTemplate_RetiresLocalFirst()
    {
        // Local extractor populated an active template on (skill, screen).
        // A seeded template arrives with a DIFFERENT template_id on the same
        // (skill, screen) — the partial unique index uniq_wt_active_skill_screen
        // would reject the INSERT. Applier must retire the local one first.
        var btnOpen = Sig("Button", "btnOpen");
        var txtSearch = Sig("Edit", "txtSearch");
        var btnApprove = Sig("Button", "btnApprove");
        var visibleEntry = new[] { btnOpen, txtSearch, btnApprove };
        var screenSig = WorkflowTemplate.ComputeScreenSignature(visibleEntry);

        // Seed a local-extracted active template on the same screen.
        var now = DateTimeOffset.UtcNow.ToString("o");
        _db.UpsertWorkflowTemplate(
            templateId: "local-123",
            templateVersion: "1.0.0",
            skillId: "learned",
            processNameGlob: "PioneerPharmacy*",
            pmsVersionRangeJson: System.Text.Json.JsonSerializer.Serialize(new[] { LocalFp }),
            screenSignature: screenSig,
            stepsHash: "local-steps-hash",
            routineHashOrigin: "routine-local",
            stepsJson: "[]",
            aggregateConfidence: 0.9,
            observationCount: 6,
            hasWriteback: false,
            extractedAt: now,
            extractedBy: "local-v3.12");

        var applicator = new SeedApplicator(_db);
        var tmpl = BuildTemplate(new[] { LocalFp }, id: "seeded-456");
        var result = applicator.ApplyWorkflowTemplates(
            _sessionId, BuildResponse(new[] { tmpl }), LocalFp);

        Assert.Equal(1, result.TemplatesApplied);
        var priorRow = _db.GetWorkflowTemplate("local-123");
        Assert.NotNull(priorRow);
        Assert.NotNull(priorRow!.RetiredAt);
        Assert.Equal("superseded-by-seed", priorRow.RetirementReason);

        var newRow = _db.GetWorkflowTemplate("seeded-456");
        Assert.NotNull(newRow);
        Assert.Null(newRow!.RetiredAt);
    }

    [Fact]
    public void Apply_Idempotent_ReapplyIsNoOp()
    {
        var applicator = new SeedApplicator(_db);
        var tmpl = BuildTemplate(new[] { LocalFp });
        var first = applicator.ApplyWorkflowTemplates(
            _sessionId, BuildResponse(new[] { tmpl }), LocalFp);
        Assert.Equal(1, first.TemplatesApplied);

        var second = applicator.ApplyWorkflowTemplates(
            _sessionId, BuildResponse(new[] { tmpl }), LocalFp);
        Assert.Equal(0, second.TemplatesApplied);
        Assert.Equal(0, second.TemplatesSkipped);
    }

    // ──────────────────────── provenance / generator interop ────────────────────────

    [Fact]
    public void Apply_EmittedTemplate_GeneratesAutonomousOkFalseRule()
    {
        var applicator = new SeedApplicator(_db);
        var tmpl = BuildTemplate(new[] { LocalFp });
        var result = applicator.ApplyWorkflowTemplates(_sessionId, BuildResponse(new[] { tmpl }), LocalFp);
        Assert.Equal(1, result.TemplatesApplied);
        Assert.NotEmpty(_db.GetActiveWorkflowTemplates());

        // Rehydrate directly to prove round-trip works from the applier's stored
        // form (PascalCase JSON) to a valid WorkflowTemplate.
        var row = _db.GetActiveWorkflowTemplates()[0];
        var opts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var rehydratedSteps = System.Text.Json.JsonSerializer.Deserialize<
            System.Collections.Generic.List<TemplateStep>>(row.StepsJson, opts);
        Assert.NotNull(rehydratedSteps);
        Assert.NotEmpty(rehydratedSteps!);

        // Now emit via the generator — the full pipeline must produce exactly
        // one YAML file flagged autonomousOk=false.
        var rulesRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"seed-gen-{Guid.NewGuid():N}");
        try
        {
            var gen = new TemplateRuleGenerator(_db, rulesRoot,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<TemplateRuleGenerator>.Instance);

            // Direct-emit path must also work — if it throws, we want the
            // real exception, not EmitPendingRules' swallowed warning.
            var rehydrated = new WorkflowTemplate(
                TemplateId: row.TemplateId, TemplateVersion: row.TemplateVersion,
                SkillId: row.SkillId, ProcessNameGlob: row.ProcessNameGlob,
                PmsVersionRange: new[] { LocalFp },
                ScreenSignatureV1: row.ScreenSignature, StepsHash: row.StepsHash,
                RoutineHashOrigin: row.RoutineHashOrigin,
                Steps: rehydratedSteps!,
                AggregateConfidence: row.AggregateConfidence,
                ObservationCount: row.ObservationCount, HasWriteback: row.HasWriteback,
                ExtractedAt: row.ExtractedAt, ExtractedBy: row.ExtractedBy,
                RetiredAt: null, RetirementReason: null);
            gen.EmitTemplateRule(rehydrated);

            var count = gen.EmitPendingRules();
            Assert.Equal(1, count);

            var files = System.IO.Directory.GetFiles(rulesRoot, "*.yaml",
                System.IO.SearchOption.AllDirectories);
            Assert.Single(files);
            var yaml = System.IO.File.ReadAllText(files[0]);
            Assert.Contains("autonomousOk: false", yaml);
        }
        finally
        {
            if (System.IO.Directory.Exists(rulesRoot))
                System.IO.Directory.Delete(rulesRoot, recursive: true);
        }
    }
}
