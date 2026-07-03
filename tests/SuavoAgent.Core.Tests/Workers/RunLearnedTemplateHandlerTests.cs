using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

/// <summary>
/// Approval-gate suite for the run_learned_template handler. Fail-closed: the handler must REFUSE a
/// template whose auto-rule is not Approved (Pending / Shadow / Rejected / missing) BEFORE it constructs
/// or drives any executor. Observed via the audit side-effect: the handler writes a
/// "run_learned_template_received" entry only AFTER the approval gate passes, so its absence proves the
/// run never reached execution. Everything read is local SQLite; no cloud client wired ⇒ acks no-op.
/// </summary>
public sealed class RunLearnedTemplateHandlerTests : IDisposable
{
    private const string SkillId = "pricing";
    private static readonly JsonSerializerOptions CamelJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _dbPath;
    private readonly AgentStateDb _db;
    private readonly HeartbeatWorker _worker;
    private readonly MethodInfo _handler;

    public RunLearnedTemplateHandlerTests()
    {
        _dbPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"run_tmpl_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);

        var services = new ServiceCollection();
        services.AddSingleton(_db);
        var sp = services.BuildServiceProvider();

        var options = Options.Create(new AgentOptions
        {
            AgentId = "agent-run-tmpl",
            MachineFingerprint = "fp-run-tmpl",
            PharmacyId = "ph.test",
            PricingExecutor = PricingExecutorMode.SqlFirst,
        });

        _worker = new HeartbeatWorker(NullLogger<HeartbeatWorker>.Instance, options, sp, _db);
        _handler = typeof(HeartbeatWorker)
            .GetMethod("HandleRunLearnedTemplateAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var p in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { System.IO.File.Delete(p); } catch { /* best-effort */ }
    }

    // ── helpers ──

    private static TemplateStep ClickStep(int ord, ElementSignature target, IReadOnlyList<ElementSignature> visible) =>
        new(ord, TemplateStepKind.Click, target, visible,
            MinElementsRequired: Math.Max(1, visible.Count), ExpectedAfter: null, IsWrite: false,
            CorrelatedQueryShapeHash: null, StepConfidence: 0.9, Hint: null);

    /// <summary>Persists a click-only template and returns (templateId, ruleId).</summary>
    private (string TemplateId, string RuleId) PersistTemplate()
    {
        var open = new ElementSignature("Button", "btnOpen", null);
        var entry = new[] { open };
        var steps = new List<TemplateStep> { ClickStep(0, open, entry) };

        var stepsHash = WorkflowTemplate.ComputeStepsHash(steps);
        var screenSig = WorkflowTemplate.ComputeScreenSignature(entry);
        var templateId = WorkflowTemplate.ComputeTemplateId(screenSig, stepsHash);

        _db.UpsertWorkflowTemplate(
            templateId: templateId, templateVersion: "1.0.0", skillId: SkillId,
            processNameGlob: "Pioneer*", pmsVersionRangeJson: "[]",
            screenSignature: screenSig, stepsHash: stepsHash, routineHashOrigin: null,
            stepsJson: JsonSerializer.Serialize(steps, CamelJson),
            aggregateConfidence: 0.9, observationCount: 10, hasWriteback: false,
            extractedAt: "2026-04-19T00:00:00Z", extractedBy: "test");

        var idSuffix = templateId.Length >= 12 ? templateId[..12] : templateId;
        return (templateId, $"auto.{SkillId}.{idSuffix}");
    }

    private void SetStatus(string ruleId, string templateId, AgentStateDb.AutoRuleStatus status)
    {
        _db.UpsertAutoRuleApproval(ruleId, templateId, "yaml-sha-256");
        _db.SetAutoRuleApprovalStatus(ruleId, status, approvedBy: "operator", approvedAt: "2026-04-20T00:00:00Z");
    }

    private async Task InvokeAsync(string templateId)
    {
        var payload = JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(new { data = new { templateId, commandId = "cid-1" } }));
        var cmd = new SignedCommand("run_learned_template", "agent-run-tmpl", "fp-run-tmpl",
            DateTimeOffset.UtcNow.ToString("o"), Guid.NewGuid().ToString(), "k", "sig", "hash");
        await (Task)_handler.Invoke(_worker, new object[] { payload, cmd, CancellationToken.None })!;
    }

    private int ReceivedAuditCount()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM audit_entries WHERE event_type = 'run_learned_template_received'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // ──────────────────────── (a) non-Approved ⇒ refused before execution ────────────────────────

    [Theory]
    [InlineData(AgentStateDb.AutoRuleStatus.Pending)]
    [InlineData(AgentStateDb.AutoRuleStatus.Shadow)]
    [InlineData(AgentStateDb.AutoRuleStatus.Rejected)]
    public async Task NonApprovedStatus_Refused_NoExecutionAudit(AgentStateDb.AutoRuleStatus status)
    {
        var (templateId, ruleId) = PersistTemplate();
        SetStatus(ruleId, templateId, status);

        await InvokeAsync(templateId);

        Assert.Equal(0, ReceivedAuditCount()); // refused at the approval gate — never reached execution
    }

    [Fact]
    public async Task MissingApproval_Refused_NoExecutionAudit()
    {
        var (templateId, _) = PersistTemplate(); // no approval row at all

        await InvokeAsync(templateId);

        Assert.Equal(0, ReceivedAuditCount());
    }

    [Fact]
    public async Task UnknownTemplateId_Refused_NoExecutionAudit()
    {
        await InvokeAsync("does-not-exist");

        Assert.Equal(0, ReceivedAuditCount());
    }

    [Fact]
    public async Task ApprovedStatus_PassesGate_WritesExecutionAudit()
    {
        // Positive control: an Approved template PASSES the gate and reaches the execution setup (which then
        // no-ops here because the actuation DI — VerbRegistry — is intentionally unwired). The audit entry is
        // written before that, so its presence proves the gate distinguishes Approved from every other state.
        var (templateId, ruleId) = PersistTemplate();
        SetStatus(ruleId, templateId, AgentStateDb.AutoRuleStatus.Approved);

        await InvokeAsync(templateId);

        Assert.True(ReceivedAuditCount() >= 1);
    }
}
