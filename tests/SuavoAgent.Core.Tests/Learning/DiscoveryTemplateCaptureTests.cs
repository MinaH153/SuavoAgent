using System.Text.Json;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

public sealed class DiscoveryTemplateCaptureTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"suavo_discovery_capture_{Guid.NewGuid():N}.db");
    private readonly AgentStateDb _db;

    public DiscoveryTemplateCaptureTests() => _db = new AgentStateDb(_dbPath);

    [Fact]
    public void DiscoveryTemplate_IsStoredButExcludedFromEveryActiveLookup()
    {
        const string sessionId = "discovery-session";
        _db.CreateLearningSession(sessionId, "pharmacy-1");
        var target = new ElementSignature("Button", "btnSearch", "ButtonClass");
        var steps = new[]
        {
            new TemplateStep(
                Ordinal: 0,
                Kind: TemplateStepKind.Click,
                Target: target,
                ExpectedVisible: new[] { target },
                MinElementsRequired: 1,
                ExpectedAfter: null,
                IsWrite: false,
                CorrelatedQueryShapeHash: null,
                StepConfidence: 0.9,
                Hint: null),
        };
        var screen = WorkflowTemplate.ComputeScreenSignature(steps[0].ExpectedVisible);
        var stepsHash = WorkflowTemplate.ComputeStepsHash(steps);
        var templateId = WorkflowTemplate.ComputeTemplateId(screen, stepsHash);
        var now = DateTimeOffset.UtcNow.ToString("o");

        _db.UpsertWorkflowTemplate(
            templateId,
            "1.0.0",
            "learned",
            "PioneerPharmacy*",
            JsonSerializer.Serialize(Array.Empty<PmsVersionFingerprint>()),
            screen,
            stepsHash,
            "routine-1",
            JsonSerializer.Serialize(steps, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }),
            0.9,
            10,
            false,
            now,
            "local-v3.12",
            captureOnly: true,
            sourceSessionId: sessionId);

        var captured = _db.GetWorkflowTemplate(templateId);
        Assert.NotNull(captured);
        Assert.True(captured.CaptureOnly);
        Assert.Equal(sessionId, captured.SourceSessionId);
        Assert.Empty(_db.GetActiveWorkflowTemplates());
        Assert.Null(_db.FindActiveTemplateByScreenSignatures(new[] { "Button|btnSearch" }));
        Assert.Null(_db.GetAutoRuleApproval($"auto.learned.{templateId[..12]}"));

        _db.UpsertWorkflowTemplate(
            templateId,
            "1.0.0",
            "learned",
            "PioneerPharmacy*",
            JsonSerializer.Serialize(Array.Empty<PmsVersionFingerprint>()),
            screen,
            stepsHash,
            "routine-1",
            JsonSerializer.Serialize(steps, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }),
            0.9,
            11,
            false,
            now,
            "local-v3.12",
            captureOnly: false,
            sourceSessionId: sessionId);

        Assert.Single(_db.GetActiveWorkflowTemplates());
        Assert.NotNull(_db.FindActiveTemplateByScreenSignatures(new[] { "Button|btnSearch" }));
    }

    public void Dispose()
    {
        _db.Dispose();
        TryDelete(_dbPath);
        TryDelete(_dbPath + "-wal");
        TryDelete(_dbPath + "-shm");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
