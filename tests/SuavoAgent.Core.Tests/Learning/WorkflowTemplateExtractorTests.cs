using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

public class WorkflowTemplateExtractorTests : IDisposable
{
    private readonly AgentStateDb _db;
    private readonly string _sessionId = "tmpl-xtor-session";
    private readonly string _pharmacy = "pharm-001";

    private static readonly PmsVersionFingerprint Fp = new(
        "PioneerRx", "schema-hash-abc", "dialect-hash-xyz", "2026.3.1");

    public WorkflowTemplateExtractorTests()
    {
        _db = new AgentStateDb(":memory:");
        _db.CreateLearningSession(_sessionId, _pharmacy);
    }

    public void Dispose() => _db.Dispose();

    // ──────────────────────── scaffolding ────────────────────────

    private void InsertInteraction(int seq, string treeHash, string elementId,
        string controlType, string? className, DateTimeOffset timestamp) =>
        _db.InsertBehavioralEvent(_sessionId, seq, "interaction", "invoked",
            treeHash, elementId, controlType, className, null, null,
            null, null, null, 1, timestamp.ToString("o"));

    private void SeedSixRoundTrips(string treeA, string elemA, string treeB, string elemB,
        string treeC, string elemC, string classA = "WinForms.Button",
        string? classB = null, string classC = "WinForms.Button")
    {
        var baseTime = DateTimeOffset.UtcNow;
        int seq = 1;
        for (int rep = 0; rep < 6; rep++)
        {
            var t0 = baseTime.AddMinutes(rep * 2);
            InsertInteraction(seq++, treeA, elemA, "Button", classA, t0);
            InsertInteraction(seq++, treeB, elemB, "Edit", classB, t0.AddSeconds(5));
            InsertInteraction(seq++, treeC, elemC, "Button", classC, t0.AddSeconds(10));
        }
    }

    private static PmsVersionFingerprint Provider() => Fp;

    // ──────────────────────── happy path ────────────────────────

    [Fact]
    public void Extract_ProducesTemplate_FromSimpleRoutine()
    {
        // Seed a 3-step DFG with 6 repetitions (same as RoutineDetector happy
        // path) so the routine detector persists a LearnedRoutine we can turn
        // into a template.
        SeedSixRoundTrips("treeA", "btnOpen", "treeB", "txtSearch", "treeC", "btnApprove");

        new SuavoAgent.Core.Behavioral.RoutineDetector(_db, _sessionId).DetectAndPersist();
        Assert.NotEmpty(_db.GetLearnedRoutines(_sessionId));

        var xtor = new WorkflowTemplateExtractor(
            _db, _sessionId, "learned", "PioneerPharmacy*", Provider,
            WorkflowTemplateThresholds.Default);
        var templates = xtor.ExtractAndPersist();

        Assert.NotEmpty(templates);
        var t = templates[0];
        Assert.Equal("learned", t.SkillId);
        Assert.Equal("PioneerPharmacy*", t.ProcessNameGlob);
        Assert.Equal(3, t.Steps.Count);
        Assert.Equal("btnOpen", t.Steps[0].Target.AutomationId);
        Assert.Equal("txtSearch", t.Steps[1].Target.AutomationId);
        Assert.Equal("btnApprove", t.Steps[2].Target.AutomationId);
        Assert.False(t.HasWriteback, "No correlated SQL writes → no writeback flag");
        Assert.Equal("1.0.0", t.TemplateVersion);
        Assert.Single(t.PmsVersionRange);
    }

    // ──────────────────────── idempotency ────────────────────────

    [Fact]
    public void Extract_Idempotent_RefreshesObservationCountWithoutBumpingVersion()
    {
        SeedSixRoundTrips("treeA", "btnOpen", "treeB", "txtSearch", "treeC", "btnApprove");
        new SuavoAgent.Core.Behavioral.RoutineDetector(_db, _sessionId).DetectAndPersist();

        var xtor = new WorkflowTemplateExtractor(
            _db, _sessionId, "learned", "PioneerPharmacy*", Provider,
            WorkflowTemplateThresholds.Default);

        var first = xtor.ExtractAndPersist();
        var second = xtor.ExtractAndPersist();

        Assert.Equal(first[0].TemplateId, second[0].TemplateId);
        Assert.Equal(first[0].TemplateVersion, second[0].TemplateVersion);
        Assert.Equal(first[0].StepsHash, second[0].StepsHash);
        // ObservationCount advances as the routine's frequency grows.
        Assert.True(second[0].ObservationCount >= first[0].ObservationCount);
    }

    [Fact]
    public void Extract_DifferentSteps_BumpsVersionAndRetiresPrior()
    {
        // Version 1: A→B→C
        SeedSixRoundTrips("treeA", "btnOpen", "treeB", "txtSearch", "treeC", "btnApprove");
        new SuavoAgent.Core.Behavioral.RoutineDetector(_db, _sessionId).DetectAndPersist();
        var xtor = new WorkflowTemplateExtractor(
            _db, _sessionId, "learned", "PioneerPharmacy*", Provider,
            WorkflowTemplateThresholds.Default);
        var v1 = xtor.ExtractAndPersist();
        Assert.Single(v1);
        var v1Id = v1[0].TemplateId;
        var v1Screen = v1[0].ScreenSignatureV1;

        // Simulate a schema/UI change: swap the second step's elementId on the
        // SAME entry screen (treeA). The extractor sees the same
        // ScreenSignature (derived from Step[0].ExpectedVisible = treeA
        // elements) but a different StepsHash, so it must bump the major
        // version and retire the prior template.
        //
        // We rewrite the routine's path_json directly to isolate the
        // extractor's version-bump logic from RoutineDetector's tie-breaking.
        var newPath = "[" +
            "{\"treeHash\":\"treeA\",\"elementId\":\"btnOpen\",\"controlType\":\"Button\",\"queryShapeHash\":null}," +
            "{\"treeHash\":\"treeB\",\"elementId\":\"txtSearchV2\",\"controlType\":\"Edit\",\"queryShapeHash\":null}," +
            "{\"treeHash\":\"treeC\",\"elementId\":\"btnApprove\",\"controlType\":\"Button\",\"queryShapeHash\":null}" +
            "]";

        // Ensure the extractor can resolve structure for the new element.
        InsertInteraction(10_000, "treeB", "txtSearchV2", "Edit", null, DateTimeOffset.UtcNow);

        _db.UpsertLearnedRoutine(_sessionId,
            routineHash: v1[0].RoutineHashOrigin!,
            pathJson: newPath, pathLength: 3, frequency: 10, confidence: 0.9,
            startElementId: "btnOpen", endElementId: "btnApprove",
            correlatedWriteQueries: null, hasWritebackCandidate: false);

        var v2 = xtor.ExtractAndPersist();
        var active = _db.GetWorkflowTemplateByScreen("learned", v1Screen);
        Assert.NotNull(active);
        Assert.Equal("2.0.0", active!.TemplateVersion);
        Assert.NotEqual(v1Id, active.TemplateId);

        var prior = _db.GetWorkflowTemplate(v1Id);
        Assert.NotNull(prior);
        Assert.NotNull(prior!.RetiredAt);
        Assert.Equal("superseded", prior.RetirementReason);
    }

    // ──────────────────────── writeback safety ────────────────────────

    [Fact]
    public void Extract_WritebackStep_RequiresExpectedAfter_ElseSkipped()
    {
        // Seed 6 routines with a step whose correlated query is a write.
        // The extractor must either produce a template whose write step has
        // ExpectedAfter, or skip the routine entirely (fail closed) — never
        // emit a writeback template without a post-state.
        SeedSixRoundTrips("treeA", "btnOpen", "treeB", "txtSearch", "treeC", "btnApprove");
        new SuavoAgent.Core.Behavioral.RoutineDetector(_db, _sessionId).DetectAndPersist();

        // Mark treeC/btnApprove as a write via correlated_actions with a known write flag.
        _db.UpsertCorrelatedAction(_sessionId,
            correlationKey: "treeC:btnApprove:shape1",
            treeHash: "treeC", elementId: "btnApprove", controlType: "Button",
            queryShapeHash: "shape1", isWrite: true, tablesReferenced: "Prescription.Rx");
        // And add a "post-state" element on treeD so ExpectedAfter can be derived.
        _db.InsertBehavioralEvent(_sessionId, sequenceNum: 9999, eventType: "interaction",
            eventSubtype: "post", treeHash: "treeD_post", elementId: "wndApproved",
            controlType: "Window", className: "WinForms.Form", nameHash: null,
            boundingRect: null, keystrokeCategory: null, timingBucket: null,
            keystrokeCount: null, occurrenceCount: 5,
            helperTimestamp: DateTimeOffset.UtcNow.ToString("o"));

        var xtor = new WorkflowTemplateExtractor(
            _db, _sessionId, "learned", "PioneerPharmacy*", Provider,
            WorkflowTemplateThresholds.Default with
            {
                WritebackPostStateTreeHash = "treeD_post"
            });
        var templates = xtor.ExtractAndPersist();

        Assert.NotEmpty(templates);
        var t = templates[0];
        Assert.True(t.HasWriteback);
        var writeStep = t.Steps.First(s => s.IsWrite);
        Assert.NotNull(writeStep.ExpectedAfter);
        Assert.NotEmpty(writeStep.ExpectedAfter!);
    }

    [Fact]
    public void Extract_WritebackWithoutPostState_SkipsTemplate()
    {
        SeedSixRoundTrips("treeA", "btnOpen", "treeB", "txtSearch", "treeC", "btnApprove");
        new SuavoAgent.Core.Behavioral.RoutineDetector(_db, _sessionId).DetectAndPersist();

        // Writeback correlated but NO post-state element on treeD_post.
        _db.UpsertCorrelatedAction(_sessionId,
            correlationKey: "treeC:btnApprove:shape1",
            treeHash: "treeC", elementId: "btnApprove", controlType: "Button",
            queryShapeHash: "shape1", isWrite: true, tablesReferenced: "Prescription.Rx");

        var xtor = new WorkflowTemplateExtractor(
            _db, _sessionId, "learned", "PioneerPharmacy*", Provider,
            WorkflowTemplateThresholds.Default with
            {
                WritebackPostStateTreeHash = "treeD_missing"
            });
        var templates = xtor.ExtractAndPersist();

        Assert.Empty(templates);
    }

    [Fact]
    public void Extract_NonTerminalWriteback_UsesNextStepTreeHashAsPostState()
    {
        // A→B→C where B is the writeback (non-terminal). Post-state anchor
        // for B must be derived from the NEXT step's tree (treeC), not from
        // any thresholds override. This is the path that unblocks v3.12
        // writeback templates in production where no override is plumbed.
        SeedSixRoundTrips("treeA", "btnOpen", "treeB", "btnCommit", "treeC", "btnNext");

        new SuavoAgent.Core.Behavioral.RoutineDetector(_db, _sessionId).DetectAndPersist();

        // Mark step B (treeB/btnCommit) as the writeback.
        _db.UpsertCorrelatedAction(_sessionId,
            correlationKey: "treeB:btnCommit:shape1",
            treeHash: "treeB", elementId: "btnCommit", controlType: "Button",
            queryShapeHash: "shape1", isWrite: true, tablesReferenced: "Prescription.Rx");

        // No WritebackPostStateTreeHash override — production-equivalent config.
        var xtor = new WorkflowTemplateExtractor(
            _db, _sessionId, "learned", "PioneerPharmacy*", Provider,
            WorkflowTemplateThresholds.Default);
        var templates = xtor.ExtractAndPersist();

        Assert.NotEmpty(templates);
        var t = templates[0];
        Assert.True(t.HasWriteback);
        var writeStep = t.Steps.First(s => s.IsWrite);
        Assert.NotNull(writeStep.ExpectedAfter);
        // ExpectedAfter for step B should reflect treeC's elements (btnNext).
        Assert.Contains(writeStep.ExpectedAfter!,
            e => e.AutomationId == "btnNext");
    }

    [Fact]
    public void Extract_TerminalWriteback_NoFallback_DropsWithWarning()
    {
        // Terminal writeback (step C) with no WritebackPostStateTreeHash.
        // The extractor must drop the routine (fail closed), not silently
        // succeed with an unverifiable template.
        SeedSixRoundTrips("treeA", "btnOpen", "treeB", "txtSearch", "treeC", "btnApprove");
        new SuavoAgent.Core.Behavioral.RoutineDetector(_db, _sessionId).DetectAndPersist();

        _db.UpsertCorrelatedAction(_sessionId,
            correlationKey: "treeC:btnApprove:shape1",
            treeHash: "treeC", elementId: "btnApprove", controlType: "Button",
            queryShapeHash: "shape1", isWrite: true, tablesReferenced: "Prescription.Rx");

        // No WritebackPostStateTreeHash — production default.
        var xtor = new WorkflowTemplateExtractor(
            _db, _sessionId, "learned", "PioneerPharmacy*", Provider,
            WorkflowTemplateThresholds.Default);
        var templates = xtor.ExtractAndPersist();

        Assert.Empty(templates);
    }

    // ──────────────────────── cross-installation safety ────────────────────────

    [Fact]
    public void Extract_AnonymousElementId_Skipped()
    {
        // element_id of "WinForms.Button:3:0" is the AutomationId-fallback form
        // from UiaPropertyScrubber.BuildElementId — not cross-installation safe.
        // Extractor must skip routines built from such elements.
        var baseTime = DateTimeOffset.UtcNow;
        int seq = 1;
        for (int rep = 0; rep < 6; rep++)
        {
            var t0 = baseTime.AddMinutes(rep * 2);
            InsertInteraction(seq++, "treeA", "WinForms.Button:3:0", "Button", null, t0);
            InsertInteraction(seq++, "treeB", "WinForms.Edit:3:1", "Edit", null, t0.AddSeconds(5));
            InsertInteraction(seq++, "treeC", "WinForms.Button:3:2", "Button", null, t0.AddSeconds(10));
        }

        new SuavoAgent.Core.Behavioral.RoutineDetector(_db, _sessionId).DetectAndPersist();

        var xtor = new WorkflowTemplateExtractor(
            _db, _sessionId, "learned", "PioneerPharmacy*", Provider,
            WorkflowTemplateThresholds.Default);
        var templates = xtor.ExtractAndPersist();

        Assert.Empty(templates);
    }

    // ──────────────────────── retirement ────────────────────────

    [Fact]
    public void Extract_LowConfidence_IncrementsRetirementCounter()
    {
        SeedSixRoundTrips("treeA", "btnOpen", "treeB", "txtSearch", "treeC", "btnApprove");
        new SuavoAgent.Core.Behavioral.RoutineDetector(_db, _sessionId).DetectAndPersist();

        var xtor = new WorkflowTemplateExtractor(
            _db, _sessionId, "learned", "PioneerPharmacy*", Provider,
            WorkflowTemplateThresholds.Default);
        var first = xtor.ExtractAndPersist();
        var templateId = first[0].TemplateId;

        // Drop the routine's confidence below threshold by rewriting it.
        _db.UpsertLearnedRoutine(_sessionId,
            routineHash: first[0].RoutineHashOrigin!,
            pathJson: "[]", pathLength: 0, frequency: 1, confidence: 0.2,
            startElementId: null, endElementId: null,
            correlatedWriteQueries: null, hasWritebackCandidate: false);

        // Run extractor three times — each low-conf run increments the counter.
        for (int i = 0; i < 3; i++) xtor.ExtractAndPersist();

        var row = _db.GetWorkflowTemplate(templateId);
        Assert.NotNull(row);
        Assert.True(row!.ConsecutiveLowConfRuns >= 3);
        Assert.Null(row.RetiredAt); // not yet retired
    }

    [Fact]
    public void Extract_LowConfidencePastThreshold_RetiresTemplate()
    {
        SeedSixRoundTrips("treeA", "btnOpen", "treeB", "txtSearch", "treeC", "btnApprove");
        new SuavoAgent.Core.Behavioral.RoutineDetector(_db, _sessionId).DetectAndPersist();

        var thresholds = WorkflowTemplateThresholds.Default with { LowConfidenceRetirementAfter = 2 };
        var xtor = new WorkflowTemplateExtractor(
            _db, _sessionId, "learned", "PioneerPharmacy*", Provider, thresholds);
        var first = xtor.ExtractAndPersist();
        var templateId = first[0].TemplateId;

        _db.UpsertLearnedRoutine(_sessionId,
            routineHash: first[0].RoutineHashOrigin!,
            pathJson: "[]", pathLength: 0, frequency: 1, confidence: 0.2,
            startElementId: null, endElementId: null,
            correlatedWriteQueries: null, hasWritebackCandidate: false);

        for (int i = 0; i < 5; i++) xtor.ExtractAndPersist();

        var row = _db.GetWorkflowTemplate(templateId);
        Assert.NotNull(row);
        Assert.NotNull(row!.RetiredAt);
        Assert.Equal("confidence_drop", row.RetirementReason);
    }
}
