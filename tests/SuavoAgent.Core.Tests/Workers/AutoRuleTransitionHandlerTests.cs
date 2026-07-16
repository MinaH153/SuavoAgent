using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.Reasoning;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public sealed class AutoRuleTransitionHandlerTests : IDisposable
{
    private const string ApprovalId = "11111111-1111-4111-8111-111111111111";
    private const string ApprovedBy = "22222222-2222-4222-8222-222222222222";
    private const string CommandId = "33333333-3333-4333-8333-333333333333";

    private readonly AgentStateDb _db = new(":memory:");
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "suavo-transition-handler-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ExactApprovedCommand_CommitsAndLiveAdmitsWithoutRestart()
    {
        var setup = SeedRule();
        var registry = new ActiveLearnedRuleRegistry(
            _db, _root, new YamlRuleLoader(NullLogger<YamlRuleLoader>.Instance),
            NullLogger<ActiveLearnedRuleRegistry>.Instance);
        var worker = Worker(registry);
        var payload = Payload(setup, includeExtra: false);

        await Invoke(worker, payload);
        await Invoke(worker, payload); // stable command redelivery is a no-op, not a second transition.

        var approval = _db.GetAutoRuleApproval(setup.RuleId)!;
        Assert.Equal(AgentStateDb.AutoRuleStatus.Approved, approval.Status);
        Assert.Equal(ApprovalId, approval.ApprovalId);
        Assert.Single(_db.GetActiveAutoRuleBindings());
        Assert.True(registry.TryGetExact(
            ApprovalId, setup.RuleId, setup.TemplateId, setup.YamlSha, out _));
    }

    [Fact]
    public async Task UnknownField_IsRejectedBeforeStateMutation()
    {
        var setup = SeedRule();
        var registry = new ActiveLearnedRuleRegistry(
            _db, _root, new YamlRuleLoader(NullLogger<YamlRuleLoader>.Instance),
            NullLogger<ActiveLearnedRuleRegistry>.Instance);

        await Invoke(Worker(registry), Payload(setup, includeExtra: true));

        Assert.Equal(AgentStateDb.AutoRuleStatus.Shadow,
            _db.GetAutoRuleApproval(setup.RuleId)!.Status);
        Assert.Empty(_db.GetActiveAutoRuleBindings());
        Assert.Equal(0, registry.Count);
    }

    private HeartbeatWorker Worker(IActiveLearnedRuleRegistry registry)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_db);
        services.AddSingleton(registry);
        return new HeartbeatWorker(
            NullLogger<HeartbeatWorker>.Instance,
            Options.Create(new AgentOptions
            {
                AgentId = "agent-test",
                MachineFingerprint = "machine-test",
                PharmacyId = "pharmacy-test",
            }),
            services.BuildServiceProvider(),
            _db);
    }

    private static async Task Invoke(HeartbeatWorker worker, JsonElement payload)
    {
        var handler = typeof(HeartbeatWorker).GetMethod(
            "HandleTransitionAutoRuleApprovalAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)handler.Invoke(worker, new object[] { payload, CancellationToken.None })!;
    }

    private JsonElement Payload(Setup setup, bool includeExtra)
    {
        var data = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["approvalId"] = ApprovalId,
            ["ruleId"] = setup.RuleId,
            ["templateId"] = setup.TemplateId,
            ["yamlSha256"] = setup.YamlSha,
            ["fromStatus"] = "Shadow",
            ["toStatus"] = "Approved",
            ["approvedBy"] = ApprovedBy,
            ["approvedAt"] = "2026-07-10T12:15:00.000Z",
            ["reasonCode"] = "human_approved",
            ["commandId"] = CommandId,
        };
        if (includeExtra) data["freeform"] = "not_allowed";
        return JsonSerializer.SerializeToElement(new { data });
    }

    private Setup SeedRule()
    {
        var target = new ElementSignature("Button", "btnOpen", "WinForms.Button");
        var steps = new[]
        {
            new TemplateStep(
                0, TemplateStepKind.Click, target, new[] { target }, 1, null,
                false, null, 0.99, null),
        };
        var stepsHash = WorkflowTemplate.ComputeStepsHash(steps);
        var screen = WorkflowTemplate.ComputeScreenSignature(new[] { target });
        var templateId = WorkflowTemplate.ComputeTemplateId(screen, stepsHash);
        var template = new WorkflowTemplate(
            templateId, "1.0.0", "learned", "PioneerPharmacy*",
            Array.Empty<PmsVersionFingerprint>(), screen, stepsHash, null, steps,
            0.99, 20, false, "2026-07-10T12:00:00Z", "test", null, null);
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        _db.UpsertWorkflowTemplate(
            templateId, "1.0.0", "learned", "PioneerPharmacy*", "[]", screen,
            stepsHash, null, JsonSerializer.Serialize(steps, json), 0.99, 20,
            false, "2026-07-10T12:00:00Z", "test");
        new TemplateRuleGenerator(
            _db, _root, NullLogger<TemplateRuleGenerator>.Instance).EmitTemplateRule(template);
        var ruleId = $"auto.learned.{templateId[..12]}";
        _db.SetAutoRuleApprovalStatus(ruleId, AgentStateDb.AutoRuleStatus.Shadow);
        return new Setup(templateId, ruleId, _db.GetAutoRuleApproval(ruleId)!.YamlSha256);
    }

    private sealed record Setup(string TemplateId, string RuleId, string YamlSha);
}
