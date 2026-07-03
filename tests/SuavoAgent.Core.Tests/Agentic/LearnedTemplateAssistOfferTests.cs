using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Agentic;
using SuavoAgent.Core.Agentic.Adapters;
using SuavoAgent.Core.Reasoning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Agentic;

/// <summary>
/// Observe→assist bridge safety suite. Proves the ONLY new selection seam
/// (<see cref="TieredBrainReasoner"/> → learned-template offer) gates HARD and produces a TERMINAL,
/// NON-EXECUTING offer: Approved offers, Pending/Shadow-below-floor/writeback-in-Shadow do NOT, and the
/// offer never reaches the actuator. Everything read is local SQLite — no cloud, no UI.
/// </summary>
public sealed class LearnedTemplateAssistOfferTests : IDisposable
{
    private static readonly AgentObjective Obj = new("navigate somewhere", "task.assist", "ph.test");

    private static readonly IReadOnlySet<string> Allowed =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Click", "Type", "PressKey", "WaitForElement", "VerifyElement", "Log" };

    private static readonly PmsVersionFingerprint Fp =
        new("PioneerRx", "schema-hash", "dialect-hash", "2026.3.1");

    private static readonly JsonSerializerOptions CamelJson =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private const string SkillId = "pricing";

    private readonly string _dbPath;
    private readonly AgentStateDb _db;

    public LearnedTemplateAssistOfferTests()
    {
        // File-backed (not :memory:) so SeedShadowStats can reach the SAME db over a second connection.
        _dbPath = Path.Combine(Path.GetTempPath(), $"assist_offer_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var p in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(p); } catch { /* best-effort temp cleanup */ }
    }

    // ──────────────────────── fixtures ────────────────────────

    private TieredBrainReasoner Reasoner() =>
        new(new RuleEngine(Array.Empty<Rule>(), NullLogger<RuleEngine>.Instance),
            new NullLocalInference(),
            new ActionVerifier(autoExecuteDestructive: false),
            new NullCloudReasoning(),
            NullLogger<TieredBrain>.Instance,
            targetProcessName: null,
            stateDb: _db);

    private static WorkingMemory Memory(PerceivedScreen? screen) =>
        new(Obj, screen, Array.Empty<StepRecord>(), 0, 0);

    /// <summary>Screen whose GREEN-tier signatures structurally cover the template's 3-element entry step.</summary>
    private static PerceivedScreen MatchingScreen() =>
        new("hash-entry", Scrubbed: true, new[] { "el:entry" }, WindowTitle: null,
            Signatures: new[] { "Button|btnOpen", "Edit|txtSearch", "Button|btnApprove", "Text|noise" });

    private static TemplateStep Step(int ord, TemplateStepKind kind, ElementSignature target,
        IReadOnlyList<ElementSignature> visible, bool isWrite = false,
        IReadOnlyList<ElementSignature>? after = null) =>
        new(ord, kind, target, visible,
            MinElementsRequired: Math.Max(1, (int)Math.Ceiling(visible.Count * 0.8)),
            ExpectedAfter: after, IsWrite: isWrite, CorrelatedQueryShapeHash: null,
            StepConfidence: 0.9, Hint: null);

    /// <summary>Persists an active workflow_templates row and returns its (templateId, ruleId).</summary>
    private (string TemplateId, string RuleId) PersistTemplate(bool writeback)
    {
        var btnOpen = new ElementSignature("Button", "btnOpen", "WinForms.Button");
        var txtSearch = new ElementSignature("Edit", "txtSearch", "WinForms.TextBox");
        var btnApprove = new ElementSignature("Button", "btnApprove", "WinForms.Button");
        var wndApproved = new ElementSignature("Window", "wndApproved", "WinForms.Form");
        var entry = new[] { btnOpen, txtSearch, btnApprove };
        var after = new[] { wndApproved };

        var steps = new List<TemplateStep>
        {
            Step(0, TemplateStepKind.Click, btnOpen, entry),
            Step(1, TemplateStepKind.Type, txtSearch, new[] { txtSearch, btnApprove, btnOpen }),
            Step(2, TemplateStepKind.Click, btnApprove, new[] { btnApprove, txtSearch, btnOpen },
                isWrite: writeback, after: writeback ? after : null),
        };

        var stepsHash = WorkflowTemplate.ComputeStepsHash(steps);
        var screenSig = WorkflowTemplate.ComputeScreenSignature(entry);
        var templateId = WorkflowTemplate.ComputeTemplateId(screenSig, stepsHash);

        _db.UpsertWorkflowTemplate(
            templateId: templateId, templateVersion: "1.0.0", skillId: SkillId,
            processNameGlob: "Pioneer*", pmsVersionRangeJson: JsonSerializer.Serialize(new[] { Fp }, CamelJson),
            screenSignature: screenSig, stepsHash: stepsHash, routineHashOrigin: null,
            stepsJson: JsonSerializer.Serialize(steps, CamelJson),
            aggregateConfidence: 0.92, observationCount: 47, hasWriteback: writeback,
            extractedAt: "2026-04-19T00:00:00Z", extractedBy: "local-v3.12");

        var idSuffix = templateId.Length >= 12 ? templateId[..12] : templateId;
        return (templateId, $"auto.{SkillId}.{idSuffix}");
    }

    private void Approve(string ruleId, string templateId) =>
        SetStatus(ruleId, templateId, AgentStateDb.AutoRuleStatus.Approved, "operator", "2026-04-20T00:00:00Z");

    private void SetStatus(string ruleId, string templateId, AgentStateDb.AutoRuleStatus status,
        string? by = null, string? at = null)
    {
        _db.UpsertAutoRuleApproval(ruleId, templateId, "yaml-sha-256");
        _db.SetAutoRuleApprovalStatus(ruleId, status, approvedBy: by, approvedAt: at);
    }

    private void SeedShadowStats(string ruleId, int runs, int matches)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE auto_rule_approvals SET shadow_runs = @r, shadow_matches = @m WHERE rule_id = @id";
        cmd.Parameters.AddWithValue("@r", runs);
        cmd.Parameters.AddWithValue("@m", matches);
        cmd.Parameters.AddWithValue("@id", ruleId);
        cmd.ExecuteNonQuery();
    }

    private Task<ReasonResult> Reason(PerceivedScreen? screen) =>
        Reasoner().ReasonNextAsync(Obj, Memory(screen), Allowed, allowCloud: false, CancellationToken.None);

    // ──────────────────────── (a) Approved + matching screen → offers ────────────────────────

    [Fact]
    public async Task Approved_MatchingScreen_OffersAssist_PhiSafeStructuralSummary()
    {
        var (templateId, ruleId) = PersistTemplate(writeback: false);
        Approve(ruleId, templateId);

        var result = await Reason(MatchingScreen());

        Assert.Equal(NextActionKind.Assist, result.Action.Kind);
        Assert.False(result.UsedCloud);
        Assert.Null(result.Action.Verb); // terminal offer carries NO verb ⇒ no actuation shape

        var offer = result.Action.Offer;
        Assert.NotNull(offer);
        Assert.Equal(templateId, offer!.TemplateId);
        Assert.Equal(47, offer.ObservationCount);
        Assert.Equal(0.92, offer.Confidence, 3);
        Assert.Contains("Click Button x2", offer.StepsSummary);
        Assert.Contains("Type Edit x1", offer.StepsSummary);
        // PHI firewall: the summary is structural ONLY — never an element AutomationId/Name.
        Assert.All(offer.StepsSummary, s =>
        {
            Assert.DoesNotContain("btnOpen", s);
            Assert.DoesNotContain("txtSearch", s);
            Assert.DoesNotContain("btnApprove", s);
        });
    }

    // ──────────────────────── (b) Pending → NO offer ────────────────────────

    [Fact]
    public async Task Pending_DoesNotOffer()
    {
        var (templateId, ruleId) = PersistTemplate(writeback: false);
        _db.UpsertAutoRuleApproval(ruleId, templateId, "yaml-sha-256"); // defaults to Pending

        var result = await Reason(MatchingScreen());

        Assert.NotEqual(NextActionKind.Assist, result.Action.Kind);
        Assert.Null(result.Action.Offer);
    }

    // ──────────────────────── (c) Shadow, confidence < 0.85 → NO offer ────────────────────────

    [Fact]
    public async Task Shadow_ConfidenceBelowFloor_DoesNotOffer()
    {
        var (templateId, ruleId) = PersistTemplate(writeback: false);
        SetStatus(ruleId, templateId, AgentStateDb.AutoRuleStatus.Shadow);
        SeedShadowStats(ruleId, runs: 10, matches: 5); // 0.50 < 0.85

        var result = await Reason(MatchingScreen());

        Assert.NotEqual(NextActionKind.Assist, result.Action.Kind);
    }

    [Fact]
    public async Task Shadow_ZeroRuns_DoesNotOffer_InsufficientEvidence()
    {
        var (templateId, ruleId) = PersistTemplate(writeback: false);
        SetStatus(ruleId, templateId, AgentStateDb.AutoRuleStatus.Shadow); // shadow_runs stays 0

        var result = await Reason(MatchingScreen());

        Assert.NotEqual(NextActionKind.Assist, result.Action.Kind);
    }

    [Fact]
    public async Task Shadow_ConfidenceAtOrAboveFloor_OffersWithShadowConfidence()
    {
        var (templateId, ruleId) = PersistTemplate(writeback: false);
        SetStatus(ruleId, templateId, AgentStateDb.AutoRuleStatus.Shadow);
        SeedShadowStats(ruleId, runs: 10, matches: 9); // 0.90 ≥ 0.85

        var result = await Reason(MatchingScreen());

        Assert.Equal(NextActionKind.Assist, result.Action.Kind);
        Assert.Equal(0.90, result.Action.Offer!.Confidence, 3);
    }

    // ──────────────────────── (d) HasWriteback + Shadow → NO offer (even at conf 1.0) ────────────────────────

    [Fact]
    public async Task Writeback_InShadow_NeverOffers_EvenAtPerfectConfidence()
    {
        var (templateId, ruleId) = PersistTemplate(writeback: true);
        SetStatus(ruleId, templateId, AgentStateDb.AutoRuleStatus.Shadow);
        SeedShadowStats(ruleId, runs: 10, matches: 10); // 1.0 — still blocked: writes need Approved

        var result = await Reason(MatchingScreen());

        Assert.NotEqual(NextActionKind.Assist, result.Action.Kind);
    }

    [Fact]
    public async Task Writeback_Approved_MayOffer()
    {
        var (templateId, ruleId) = PersistTemplate(writeback: true);
        Approve(ruleId, templateId);

        var result = await Reason(MatchingScreen());

        Assert.Equal(NextActionKind.Assist, result.Action.Kind);
        Assert.Equal(templateId, result.Action.Offer!.TemplateId);
    }

    // ──────────────────────── (e) no screen → NO offer ────────────────────────

    [Fact]
    public async Task NoScreen_DoesNotOffer()
    {
        var (templateId, ruleId) = PersistTemplate(writeback: false);
        Approve(ruleId, templateId);

        var result = await Reason(screen: null);

        Assert.NotEqual(NextActionKind.Assist, result.Action.Kind);
        Assert.Null(result.Action.Offer);
    }

    // ──────────────────────── (f) offer is terminal — never actuates ────────────────────────

    [Fact]
    public async Task AssistOffer_IsTerminal_LoopNeverCallsActuator_AndCarriesOfferOut()
    {
        var (templateId, ruleId) = PersistTemplate(writeback: false);
        Approve(ruleId, templateId);

        var perceiver = new FakePerceiver(MatchingScreen());
        var actuator = new FakeActuator();
        var safety = new FakeSafetyGate();
        var runner = new AgenticLoopRunner(perceiver, Reasoner(), actuator, safety, null, AgenticTestKit.NoDelay);

        var result = await runner.RunAsync(Obj, new AgenticLoopOptions { MaxSteps = 5 }, CancellationToken.None);

        Assert.Equal(TerminationReason.Escalated, result.Termination);
        Assert.True(result.EscalationEmitted);
        Assert.Empty(actuator.Calls);                          // NON-EXECUTING: actuator never touched
        Assert.Equal(0, safety.GateActionCalls);               // never reached the per-action gate
        Assert.NotNull(result.AssistedTemplate);
        Assert.Equal(templateId, result.AssistedTemplate!.TemplateId);
        Assert.Equal("assist_offer", result.Detail);
    }
}
