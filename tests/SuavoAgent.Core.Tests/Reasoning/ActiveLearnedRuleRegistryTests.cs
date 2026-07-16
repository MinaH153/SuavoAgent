using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.Reasoning;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Reasoning;

public sealed class ActiveLearnedRuleRegistryTests : IDisposable
{
    private const string ApprovalId = "11111111-1111-4111-8111-111111111111";
    private const string ApprovedBy = "22222222-2222-4222-8222-222222222222";
    private const string CommandId = "33333333-3333-4333-8333-333333333333";

    private readonly AgentStateDb _db = new(":memory:");
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "suavo-active-rules-" + Guid.NewGuid().ToString("N"));
    private readonly YamlRuleLoader _loader = new(NullLogger<YamlRuleLoader>.Instance);

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void ApprovedTransition_AdmitsWithoutRestartAndRestartRehydrates()
    {
        var setup = SeedGeneratedRule();
        var registry = Registry();
        var prepared = registry.Prepare(
            ApprovalId, setup.RuleId, setup.Template.TemplateId, setup.YamlSha);
        Assert.True(_db.ApplyAutoRuleTransition(
            Transition(setup, CommandId), exactRuleValidated: true).Succeeded);

        registry.Admit(prepared);

        Assert.True(registry.TryGetExact(
            ApprovalId, setup.RuleId, setup.Template.TemplateId, setup.YamlSha, out var live));
        Assert.Equal(setup.RuleId, live!.Id);
        Assert.Equal(1, registry.Count);

        var restartedRegistry = Registry();
        Assert.True(restartedRegistry.TryGetExact(
            ApprovalId, setup.RuleId, setup.Template.TemplateId, setup.YamlSha, out _));
    }

    [Fact]
    public void FileChangeAndDbDemotionInvalidateLiveRuleImmediately()
    {
        var setup = SeedGeneratedRule();
        var registry = ApproveAndAdmit(setup);

        File.AppendAllText(setup.Path, "\n# changed after approval");
        Assert.False(registry.TryGetExact(
            ApprovalId, setup.RuleId, setup.Template.TemplateId, setup.YamlSha, out _));

        // Restore, re-admit under a fresh exact approval, then prove a local digest change removes
        // the durable row and the in-memory entry is lazily evicted on the next lookup.
        var fresh = SeedGeneratedRule();
        var freshRegistry = ApproveAndAdmit(fresh, "44444444-4444-4444-8444-444444444444");
        _db.UpsertAutoRuleApproval(
            fresh.RuleId, fresh.Template.TemplateId, new string('f', 64));
        Assert.False(freshRegistry.TryGetExact(
            ApprovalId, fresh.RuleId, fresh.Template.TemplateId, fresh.YamlSha, out _));
        Assert.Empty(_db.GetActiveAutoRuleBindings());
    }

    [Fact]
    public void RuleEngine_EvaluatesImmutableBuiltInBeforeActiveLearnedRule()
    {
        var setup = SeedGeneratedRule();
        var registry = ApproveAndAdmit(setup);
        var builtIn = new Rule
        {
            Id = "builtin.first",
            SkillId = "learned",
            Priority = 1,
            AutonomousOk = true,
            When = new RulePredicate { ProcessName = "PioneerPharmacy*" },
            Then = new[]
            {
                new RuleActionSpec
                {
                    Type = RuleActionType.AskOperator,
                    Parameters = new Dictionary<string, string>(),
                },
            },
        };
        var engine = new RuleEngine(
            new[] { builtIn }, NullLogger<RuleEngine>.Instance, registry);
        var context = new RuleContext
        {
            SkillId = "learned",
            ProcessName = "PioneerPharmacy.exe",
        };

        var result = engine.Evaluate(context);

        Assert.Equal(MatchOutcome.Matched, result.Outcome);
        Assert.Equal("builtin.first", result.MatchedRule!.Id);
    }

    private ActiveLearnedRuleRegistry ApproveAndAdmit(
        Setup setup,
        string commandId = CommandId)
    {
        var registry = Registry();
        var prepared = registry.Prepare(
            ApprovalId, setup.RuleId, setup.Template.TemplateId, setup.YamlSha);
        var transition = Transition(setup, commandId);
        var result = _db.ApplyAutoRuleTransition(transition, true);
        Assert.True(result.Succeeded);
        registry.Admit(prepared);
        return registry;
    }

    private Setup SeedGeneratedRule()
    {
        var target = new ElementSignature("Button", "btnOpen", "WinForms.Button");
        var visible = new[] { target };
        var steps = new[]
        {
            new TemplateStep(
                0, TemplateStepKind.Click, target, visible, 1, null,
                false, null, 0.99, null),
        };
        var stepsHash = WorkflowTemplate.ComputeStepsHash(steps);
        var screen = WorkflowTemplate.ComputeScreenSignature(visible);
        var template = new WorkflowTemplate(
            WorkflowTemplate.ComputeTemplateId(screen, stepsHash),
            "1.0.0", "learned", "PioneerPharmacy*", Array.Empty<PmsVersionFingerprint>(),
            screen, stepsHash, null, steps, 0.99, 20, false,
            "2026-07-10T12:00:00Z", "test", null, null);
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        _db.UpsertWorkflowTemplate(
            template.TemplateId, template.TemplateVersion, template.SkillId,
            template.ProcessNameGlob, JsonSerializer.Serialize(template.PmsVersionRange, json),
            template.ScreenSignatureV1, template.StepsHash, template.RoutineHashOrigin,
            JsonSerializer.Serialize(template.Steps, json), template.AggregateConfidence,
            template.ObservationCount, template.HasWriteback, template.ExtractedAt,
            template.ExtractedBy);

        var generator = new TemplateRuleGenerator(
            _db, _root, NullLogger<TemplateRuleGenerator>.Instance);
        var path = generator.EmitTemplateRule(template);
        var ruleId = $"auto.learned.{template.TemplateId[..12]}";
        var yaml = _db.GetAutoRuleApproval(ruleId)!.YamlSha256;
        _db.SetAutoRuleApprovalStatus(ruleId, AgentStateDb.AutoRuleStatus.Shadow);
        return new Setup(template, ruleId, yaml, path);
    }

    private ActiveLearnedRuleRegistry Registry() => new(
        _db, _root, _loader, NullLogger<ActiveLearnedRuleRegistry>.Instance);

    private static AutoRuleTransitionCommand Transition(Setup setup, string commandId) => new(
        1, ApprovalId, setup.RuleId, setup.Template.TemplateId, setup.YamlSha,
        AgentStateDb.AutoRuleStatus.Shadow, AgentStateDb.AutoRuleStatus.Approved,
        ApprovedBy, "2026-07-10T12:15:00.000Z", "human_approved", commandId);

    private sealed record Setup(
        WorkflowTemplate Template,
        string RuleId,
        string YamlSha,
        string Path);
}
