using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.Reasoning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

public class TemplateRuleGeneratorTests : IDisposable
{
    private readonly AgentStateDb _db;
    private readonly string _rulesRoot;

    public TemplateRuleGeneratorTests()
    {
        _db = new AgentStateDb(":memory:");
        _rulesRoot = Path.Combine(Path.GetTempPath(), $"tmpl-rule-gen-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_rulesRoot)) Directory.Delete(_rulesRoot, recursive: true);
    }

    private static readonly PmsVersionFingerprint Fp = new(
        "PioneerRx", "schema-hash", "dialect-hash", "2026.3.1");

    private static TemplateStep Step(int ord, TemplateStepKind kind, ElementSignature target,
        IReadOnlyList<ElementSignature> visible, bool isWrite = false,
        IReadOnlyList<ElementSignature>? after = null, string? shape = null, KeyHint? hint = null) =>
        new(ord, kind, target, visible,
            MinElementsRequired: Math.Max(1, (int)Math.Ceiling(visible.Count * 0.8)),
            ExpectedAfter: after,
            IsWrite: isWrite,
            CorrelatedQueryShapeHash: shape,
            StepConfidence: 0.9,
            Hint: hint);

    private static WorkflowTemplate BuildTemplate(bool writeback)
    {
        var btnOpen = new ElementSignature("Button", "btnOpen", "WinForms.Button");
        var txtSearch = new ElementSignature("Edit", "txtSearch", "WinForms.TextBox");
        var btnApprove = new ElementSignature("Button", "btnApprove", "WinForms.Button");
        var wndApproved = new ElementSignature("Window", "wndApproved", "WinForms.Form");

        var visibleEntry = new[] { btnOpen, txtSearch, btnApprove };
        var visibleSearch = new[] { txtSearch, btnApprove, btnOpen };
        var visibleApprove = new[] { btnApprove, txtSearch, btnOpen };
        var after = new[] { wndApproved };

        var steps = new List<TemplateStep>
        {
            Step(0, TemplateStepKind.Click, btnOpen, visibleEntry),
            Step(1, TemplateStepKind.Type, txtSearch, visibleSearch,
                hint: new KeyHint(null, KeyHintPlaceholder.RxNumberEchoed)),
            Step(2, TemplateStepKind.Click, btnApprove, visibleApprove,
                isWrite: writeback, after: writeback ? after : null,
                shape: writeback ? "shape-hash-123" : null),
        };

        var stepsHash = WorkflowTemplate.ComputeStepsHash(steps);
        var screenSig = WorkflowTemplate.ComputeScreenSignature(visibleEntry);
        var templateId = WorkflowTemplate.ComputeTemplateId(screenSig, stepsHash);

        return new WorkflowTemplate(
            TemplateId: templateId,
            TemplateVersion: "1.0.0",
            SkillId: "learned",
            ProcessNameGlob: "PioneerPharmacy*",
            PmsVersionRange: new[] { Fp },
            ScreenSignatureV1: screenSig,
            StepsHash: stepsHash,
            RoutineHashOrigin: "routine-hash-xyz",
            Steps: steps,
            AggregateConfidence: 0.9,
            ObservationCount: 12,
            HasWriteback: writeback,
            ExtractedAt: "2026-04-19T00:00:00Z",
            ExtractedBy: "local-v3.12",
            RetiredAt: null,
            RetirementReason: null);
    }

    // ──────────────────────── invariants ────────────────────────

    [Fact]
    public void Emit_RoundTripsThroughYamlRuleLoader()
    {
        var gen = new TemplateRuleGenerator(_db, _rulesRoot, NullLogger<TemplateRuleGenerator>.Instance);
        var tmpl = BuildTemplate(writeback: false);
        var path = gen.EmitTemplateRule(tmpl);
        Assert.True(File.Exists(path));

        var loader = new YamlRuleLoader(NullLogger<YamlRuleLoader>.Instance);
        var rules = loader.ParseYaml(File.ReadAllText(path), source: path);
        Assert.Single(rules);
        Assert.False(rules[0].AutonomousOk, "All auto-generated rules MUST be autonomousOk=false");
        Assert.Equal("learned", rules[0].SkillId);
        Assert.StartsWith("auto.learned.", rules[0].Id);
    }

    [Fact]
    public void Emit_AutonomousOk_HardcodedFalse()
    {
        var gen = new TemplateRuleGenerator(_db, _rulesRoot, NullLogger<TemplateRuleGenerator>.Instance);
        var tmpl = BuildTemplate(writeback: false);
        var path = gen.EmitTemplateRule(tmpl);
        var text = File.ReadAllText(path);
        Assert.Contains("autonomousOk: false", text);
    }

    [Fact]
    public void Emit_NonWriteback_Priority200()
    {
        var gen = new TemplateRuleGenerator(_db, _rulesRoot, NullLogger<TemplateRuleGenerator>.Instance);
        var tmpl = BuildTemplate(writeback: false);
        var path = gen.EmitTemplateRule(tmpl);
        var loader = new YamlRuleLoader(NullLogger<YamlRuleLoader>.Instance);
        var rule = loader.ParseYaml(File.ReadAllText(path))[0];
        Assert.Equal(200, rule.Priority);
    }

    [Fact]
    public void Emit_Writeback_Priority300()
    {
        var gen = new TemplateRuleGenerator(_db, _rulesRoot, NullLogger<TemplateRuleGenerator>.Instance);
        var tmpl = BuildTemplate(writeback: true);
        var path = gen.EmitTemplateRule(tmpl);
        var loader = new YamlRuleLoader(NullLogger<YamlRuleLoader>.Instance);
        var rule = loader.ParseYaml(File.ReadAllText(path))[0];
        Assert.Equal(300, rule.Priority);
    }

    [Fact]
    public void Emit_MinRequiredCount_RoundTripsFromTemplate()
    {
        // 3-element ExpectedVisible * 0.8 ratio → ceil = 3 (K=M here). To
        // exercise the K<M path we synthesise a 5-element visible list and
        // assert MinRequiredCount lands in the YAML at 4.
        var a = new ElementSignature("Button", "btn-a", null);
        var b = new ElementSignature("Button", "btn-b", null);
        var c = new ElementSignature("Button", "btn-c", null);
        var d = new ElementSignature("Button", "btn-d", null);
        var e = new ElementSignature("Button", "btn-e", null);
        var visible = new[] { a, b, c, d, e };

        var step = new TemplateStep(
            Ordinal: 0, Kind: TemplateStepKind.Click, Target: a,
            ExpectedVisible: visible,
            MinElementsRequired: 4,
            ExpectedAfter: null, IsWrite: false,
            CorrelatedQueryShapeHash: null, StepConfidence: 0.9, Hint: null);
        var step2 = new TemplateStep(
            Ordinal: 1, Kind: TemplateStepKind.Click, Target: b,
            ExpectedVisible: visible, MinElementsRequired: 4,
            ExpectedAfter: null, IsWrite: false,
            CorrelatedQueryShapeHash: null, StepConfidence: 0.9, Hint: null);
        var steps = new[] { step, step2 };
        var stepsHash = WorkflowTemplate.ComputeStepsHash(steps);
        var screenSig = WorkflowTemplate.ComputeScreenSignature(visible);
        var tmpl = new WorkflowTemplate(
            TemplateId: WorkflowTemplate.ComputeTemplateId(screenSig, stepsHash),
            TemplateVersion: "1.0.0", SkillId: "learned",
            ProcessNameGlob: "PioneerPharmacy*", PmsVersionRange: new[] { Fp },
            ScreenSignatureV1: screenSig, StepsHash: stepsHash,
            RoutineHashOrigin: null, Steps: steps,
            AggregateConfidence: 0.9, ObservationCount: 10, HasWriteback: false,
            ExtractedAt: "2026-04-20T00:00:00Z", ExtractedBy: "local-v3.12",
            RetiredAt: null, RetirementReason: null);

        var gen = new TemplateRuleGenerator(_db, _rulesRoot, NullLogger<TemplateRuleGenerator>.Instance);
        var path = gen.EmitTemplateRule(tmpl);
        var yamlText = File.ReadAllText(path);

        // The emitted YAML must carry minRequiredCount — without it, cross-
        // installation UIA drift would defeat the whole template.
        Assert.Contains("minRequiredCount: 4", yamlText);

        var loader = new YamlRuleLoader(NullLogger<YamlRuleLoader>.Instance);
        var rule = loader.ParseYaml(yamlText, source: path)[0];
        Assert.Equal(4, rule.When.MinRequiredCount);
    }

    [Fact]
    public void Emit_WritebackVerifyAfter_IncludesMinRequiredCount()
    {
        var gen = new TemplateRuleGenerator(_db, _rulesRoot, NullLogger<TemplateRuleGenerator>.Instance);
        var tmpl = BuildTemplate(writeback: true);
        var path = gen.EmitTemplateRule(tmpl);
        var yamlText = File.ReadAllText(path);

        var loader = new YamlRuleLoader(NullLogger<YamlRuleLoader>.Instance);
        var rule = loader.ParseYaml(yamlText, source: path)[0];
        var writeAction = rule.Then.Single(a => a.VerifyAfter != null);
        Assert.NotNull(writeAction.VerifyAfter);
        Assert.NotNull(writeAction.VerifyAfter!.MinRequiredCount);
        Assert.True(writeAction.VerifyAfter.MinRequiredCount >= 1);
        Assert.True(writeAction.VerifyAfter.MinRequiredCount
            <= writeAction.VerifyAfter.ElementFingerprints.Count);
    }

    [Fact]
    public void Emit_Writeback_VerifyAfterRequired()
    {
        var gen = new TemplateRuleGenerator(_db, _rulesRoot, NullLogger<TemplateRuleGenerator>.Instance);
        var tmpl = BuildTemplate(writeback: true);
        var path = gen.EmitTemplateRule(tmpl);
        var loader = new YamlRuleLoader(NullLogger<YamlRuleLoader>.Instance);
        var rule = loader.ParseYaml(File.ReadAllText(path))[0];

        // Writeback step MUST have a VerifyAfter with structural fingerprints.
        var writeAction = rule.Then[^1]; // last step
        Assert.NotNull(writeAction.VerifyAfter);
        Assert.NotEmpty(writeAction.VerifyAfter!.ElementFingerprints);
    }

    [Fact]
    public void Emit_Writeback_RejectsIfFewerThan3Fingerprints()
    {
        // Codex Area 2: writeback steps must carry at least 3 structural fingerprints
        // in the predicate. Fewer = not enough discrimination.
        var btnApprove = new ElementSignature("Button", "btnApprove", null);
        var wndApproved = new ElementSignature("Window", "wndApproved", null);
        var steps = new List<TemplateStep>
        {
            Step(0, TemplateStepKind.Click, btnApprove, new[] { btnApprove }, // only 1 visible
                isWrite: true, after: new[] { wndApproved }, shape: "s1"),
        };
        // Building the template should succeed; generator should throw.
        var stepsHash = WorkflowTemplate.ComputeStepsHash(steps);
        var screenSig = WorkflowTemplate.ComputeScreenSignature(new[] { btnApprove });
        var templateId = WorkflowTemplate.ComputeTemplateId(screenSig, stepsHash);
        var tmpl = new WorkflowTemplate(
            TemplateId: templateId, TemplateVersion: "1.0.0", SkillId: "learned",
            ProcessNameGlob: "PioneerPharmacy*", PmsVersionRange: new[] { Fp },
            ScreenSignatureV1: screenSig, StepsHash: stepsHash,
            RoutineHashOrigin: "r", Steps: steps, AggregateConfidence: 0.9,
            ObservationCount: 10, HasWriteback: true, ExtractedAt: "2026-04-19T00:00:00Z",
            ExtractedBy: "local-v3.12", RetiredAt: null, RetirementReason: null);

        var gen = new TemplateRuleGenerator(_db, _rulesRoot, NullLogger<TemplateRuleGenerator>.Instance);
        var ex = Assert.Throws<InvalidOperationException>(() => gen.EmitTemplateRule(tmpl));
        Assert.Contains("ElementFingerprints", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Emit_UpsertsApprovalRow_PendingStatus()
    {
        var gen = new TemplateRuleGenerator(_db, _rulesRoot, NullLogger<TemplateRuleGenerator>.Instance);
        var tmpl = BuildTemplate(writeback: false);
        gen.EmitTemplateRule(tmpl);
        var ruleId = $"auto.learned.{tmpl.TemplateId[..12]}";
        var approval = _db.GetAutoRuleApproval(ruleId);
        Assert.NotNull(approval);
        Assert.Equal(tmpl.TemplateId, approval!.TemplateId);
        Assert.Equal(AgentStateDb.AutoRuleStatus.Pending, approval.Status);
        Assert.Equal(0, approval.ShadowRuns);
    }

    [Fact]
    public void Emit_Predicate_CarriesElementFingerprints()
    {
        // Codex Area 2 fix: the predicate must include structural fingerprints,
        // not just a name list, so RuleEngine.PredicateMatches enforces the
        // structural gate.
        var gen = new TemplateRuleGenerator(_db, _rulesRoot, NullLogger<TemplateRuleGenerator>.Instance);
        var tmpl = BuildTemplate(writeback: false);
        var path = gen.EmitTemplateRule(tmpl);
        var loader = new YamlRuleLoader(NullLogger<YamlRuleLoader>.Instance);
        var rule = loader.ParseYaml(File.ReadAllText(path))[0];
        Assert.NotEmpty(rule.When.ElementFingerprints);
        Assert.Contains(rule.When.ElementFingerprints, f => f.AutomationId == "btnOpen");
    }

    [Fact]
    public void Emit_ProcessNameGlob_MappedToWhenClause()
    {
        var gen = new TemplateRuleGenerator(_db, _rulesRoot, NullLogger<TemplateRuleGenerator>.Instance);
        var tmpl = BuildTemplate(writeback: false);
        var path = gen.EmitTemplateRule(tmpl);
        var loader = new YamlRuleLoader(NullLogger<YamlRuleLoader>.Instance);
        var rule = loader.ParseYaml(File.ReadAllText(path))[0];
        Assert.Equal("PioneerPharmacy*", rule.When.ProcessName);
    }

    // ──────────────────────── pending templates batch ────────────────────────

    [Fact]
    public void EmitPendingRules_EmitsForAllActiveTemplates()
    {
        var gen = new TemplateRuleGenerator(_db, _rulesRoot, NullLogger<TemplateRuleGenerator>.Instance);

        var a = BuildTemplate(writeback: false);
        _db.UpsertWorkflowTemplate(
            a.TemplateId, a.TemplateVersion, a.SkillId, a.ProcessNameGlob,
            pmsVersionRangeJson: "[]", screenSignature: a.ScreenSignatureV1,
            stepsHash: a.StepsHash, routineHashOrigin: a.RoutineHashOrigin,
            stepsJson: System.Text.Json.JsonSerializer.Serialize(a.Steps),
            aggregateConfidence: a.AggregateConfidence, observationCount: a.ObservationCount,
            hasWriteback: a.HasWriteback, extractedAt: a.ExtractedAt, extractedBy: a.ExtractedBy);

        var count = gen.EmitPendingRules();
        Assert.Equal(1, count);

        var ruleFile = Path.Combine(_rulesRoot, a.SkillId, $"{a.TemplateId}.yaml");
        Assert.True(File.Exists(ruleFile));
    }
}
