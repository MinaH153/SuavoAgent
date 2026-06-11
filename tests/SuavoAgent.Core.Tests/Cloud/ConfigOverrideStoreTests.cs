using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Cloud;
using SuavoAgent.Core.Cloud;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public class ConfigOverrideStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _path;

    public ConfigOverrideStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "suavo-agent-config-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _path = Path.Combine(_tempDir, "config-overrides.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Apply_DottedPath_NestsUnderKeys()
    {
        var store = NewStore();
        var ov = Override("Reasoning.PricingBrainEnabled", JsonDocument.Parse("true").RootElement);

        store.Apply(new[] { ov });

        var json = JsonDocument.Parse(File.ReadAllText(_path));
        Assert.True(
            json.RootElement.GetProperty("Reasoning").GetProperty("PricingBrainEnabled").GetBoolean());
    }

    [Fact]
    public void Apply_MultipleOverrides_CoexistInNestedTree()
    {
        var store = NewStore();
        var overrides = new[]
        {
            Override("Reasoning.PricingBrainEnabled", JsonDocument.Parse("true").RootElement),
            Override("Reasoning.CloudEnabled",        JsonDocument.Parse("true").RootElement),
            Override("Agent.HeartbeatIntervalSeconds", JsonDocument.Parse("60").RootElement),
        };

        store.Apply(overrides);

        var root = JsonDocument.Parse(File.ReadAllText(_path)).RootElement;
        Assert.True(root.GetProperty("Reasoning").GetProperty("PricingBrainEnabled").GetBoolean());
        Assert.True(root.GetProperty("Reasoning").GetProperty("CloudEnabled").GetBoolean());
        Assert.Equal(60, root.GetProperty("Agent").GetProperty("HeartbeatIntervalSeconds").GetInt32());
    }

    [Fact]
    public void Apply_ScalarTypes_RoundTripThroughJson()
    {
        var store = NewStore();
        var overrides = new[]
        {
            Override("A.S", JsonDocument.Parse("\"hello\"").RootElement),
            Override("A.N", JsonDocument.Parse("42").RootElement),
            Override("A.F", JsonDocument.Parse("3.5").RootElement),
            Override("A.B", JsonDocument.Parse("false").RootElement),
            Override("A.Nul", JsonDocument.Parse("null").RootElement),
        };

        store.Apply(overrides);

        var a = JsonDocument.Parse(File.ReadAllText(_path)).RootElement.GetProperty("A");
        Assert.Equal("hello", a.GetProperty("S").GetString());
        Assert.Equal(42, a.GetProperty("N").GetInt32());
        Assert.Equal(3.5, a.GetProperty("F").GetDouble());
        Assert.False(a.GetProperty("B").GetBoolean());
        Assert.Equal(JsonValueKind.Null, a.GetProperty("Nul").ValueKind);
    }

    [Fact]
    public void Apply_UnchangedContent_ReturnsFalseAndPreservesTimestamp()
    {
        var store = NewStore();
        var ov = new[] { Override("A.B", JsonDocument.Parse("true").RootElement) };

        Assert.True(store.Apply(ov));
        var firstMtime = File.GetLastWriteTimeUtc(_path);

        Thread.Sleep(50);
        Assert.False(store.Apply(ov));
        Assert.Equal(firstMtime, File.GetLastWriteTimeUtc(_path));
    }

    [Fact]
    public void Apply_EmptyOverrides_WritesEmptyJsonObject()
    {
        var store = NewStore();
        store.Apply(Array.Empty<ConfigOverride>());

        Assert.True(File.Exists(_path));
        var json = JsonDocument.Parse(File.ReadAllText(_path));
        Assert.Equal(JsonValueKind.Object, json.RootElement.ValueKind);
        Assert.Empty(json.RootElement.EnumerateObject());
    }

    [Fact]
    public void Apply_IgnoresBlankPath_NeverCrashes()
    {
        var store = NewStore();
        var overrides = new[]
        {
            Override("", JsonDocument.Parse("1").RootElement),
            Override("   ", JsonDocument.Parse("1").RootElement),
            Override("Good.Flag", JsonDocument.Parse("true").RootElement),
        };

        store.Apply(overrides);

        var root = JsonDocument.Parse(File.ReadAllText(_path)).RootElement;
        Assert.True(root.GetProperty("Good").GetProperty("Flag").GetBoolean());
        Assert.Equal(1, root.EnumerateObject().Count());
    }

    [Fact]
    public void Apply_BlocksSecretAndIdentityOverrides()
    {
        var store = NewStore();
        var overrides = new[]
        {
            Override("Agent.ApiKey", JsonDocument.Parse("\"leak\"").RootElement),
            Override("Agent.AgentId", JsonDocument.Parse("\"agent-other\"").RootElement),
            Override("Agent.PharmacyId", JsonDocument.Parse("\"pharm-other\"").RootElement),
            Override("Agent.MachineFingerprint", JsonDocument.Parse("\"fp-other\"").RootElement),
            Override("Agent.SqlPassword", JsonDocument.Parse("\"pw\"").RootElement),
            Override("Agent.HmacSalt", JsonDocument.Parse("\"salt\"").RootElement),
            Override("Agent.CloudCertPin", JsonDocument.Parse("\"pin\"").RootElement),
            Override("Agent.Pharmacies", JsonDocument.Parse("[{\"SqlPassword\":\"pw\"}]").RootElement),
            Override("Agent.Pharmacies.0.SqlPassword", JsonDocument.Parse("\"pw\"").RootElement),
            Override("Agent.LearningMode", JsonDocument.Parse("true").RootElement),
        };

        store.Apply(overrides);

        var root = JsonDocument.Parse(File.ReadAllText(_path)).RootElement;
        var agent = root.GetProperty("Agent");
        Assert.True(agent.GetProperty("LearningMode").GetBoolean());
        Assert.False(agent.TryGetProperty("ApiKey", out _));
        Assert.False(agent.TryGetProperty("AgentId", out _));
        Assert.False(agent.TryGetProperty("PharmacyId", out _));
        Assert.False(agent.TryGetProperty("MachineFingerprint", out _));
        Assert.False(agent.TryGetProperty("SqlPassword", out _));
        Assert.False(agent.TryGetProperty("HmacSalt", out _));
        Assert.False(agent.TryGetProperty("CloudCertPin", out _));
        Assert.False(agent.TryGetProperty("Pharmacies", out _));
    }

    [Fact]
    public void Apply_BlocksHighRiskWritebackOverrides()
    {
        var store = NewStore();
        var overrides = new[]
        {
            Override("Agent.ReceiptOnlyMode", JsonDocument.Parse("false").RootElement),
            Override("Agent.AutoExecution.Enabled", JsonDocument.Parse("true").RootElement),
            Override("Agent.AutoExecution.WritebackEnabled", JsonDocument.Parse("true").RootElement),
            Override("Agent.LearningMode", JsonDocument.Parse("true").RootElement),
        };

        store.Apply(overrides);

        var root = JsonDocument.Parse(File.ReadAllText(_path)).RootElement;
        var agent = root.GetProperty("Agent");
        Assert.True(agent.GetProperty("LearningMode").GetBoolean());
        Assert.False(agent.TryGetProperty("ReceiptOnlyMode", out _));
        Assert.False(agent.TryGetProperty("AutoExecution", out _));
    }

    [Fact]
    public void Apply_BlocksReplayFirstTypeStepUnlock_AllowsRolloutSwitch()
    {
        // v1 HARD RULE: the type-step auto-replay unlock is local-only (a banked literal types the OLD
        // value) — the cloud must not be able to flip it. Agent.ReplayFirst IS the per-box rollout
        // switch and stays overridable.
        var store = NewStore();
        store.Apply(new[]
        {
            Override("Agent.ReplayFirst", JsonDocument.Parse("true").RootElement),
            Override("Agent.ReplayFirstAllowTypeSteps", JsonDocument.Parse("true").RootElement),
        });

        var root = JsonDocument.Parse(File.ReadAllText(_path)).RootElement;
        var agent = root.GetProperty("Agent");
        Assert.True(agent.GetProperty("ReplayFirst").GetBoolean());
        Assert.False(agent.TryGetProperty("ReplayFirstAllowTypeSteps", out _));
    }

    [Fact]
    public void Apply_AllowsTestHooksEnabled_AsBooleanOverride()
    {
        var store = NewStore();
        store.Apply(new[]
        {
            Override("Agent.TestHooks.Enabled", JsonDocument.Parse("true").RootElement),
        });

        var root = JsonDocument.Parse(File.ReadAllText(_path)).RootElement;
        var agent = root.GetProperty("Agent");
        Assert.True(agent.GetProperty("TestHooks").GetProperty("Enabled").GetBoolean());
    }

    [Fact]
    public void Apply_RejectsOutOfBoundsRuntimeTunables()
    {
        var store = NewStore();
        var overrides = new[]
        {
            Override("Agent.HeartbeatIntervalSeconds", JsonDocument.Parse("0").RootElement),
            Override("Agent.HeartbeatJitterSeconds", JsonDocument.Parse("-1").RootElement),
            Override("Agent.MaxDetectionBatchSize", JsonDocument.Parse("10000").RootElement),
            Override("Vision.PeriodicCapture.IntervalSeconds", JsonDocument.Parse("0").RootElement),
            Override("Vision.MaxStoredScreens", JsonDocument.Parse("0").RootElement),
            Override("Vision.Tesseract.MinConfidence", JsonDocument.Parse("101").RootElement),
            Override("Good.Flag", JsonDocument.Parse("true").RootElement),
        };

        store.Apply(overrides);

        var root = JsonDocument.Parse(File.ReadAllText(_path)).RootElement;
        Assert.True(root.GetProperty("Good").GetProperty("Flag").GetBoolean());
        Assert.False(root.TryGetProperty("Agent", out _));
        Assert.False(root.TryGetProperty("Vision", out _));
    }

    [Fact]
    public void Apply_AllowsBoundedRuntimeTunables()
    {
        var store = NewStore();
        var overrides = new[]
        {
            Override("Agent.HeartbeatIntervalSeconds", JsonDocument.Parse("30").RootElement),
            Override("Agent.HeartbeatJitterSeconds", JsonDocument.Parse("5").RootElement),
            Override("Agent.MaxDetectionBatchSize", JsonDocument.Parse("250").RootElement),
            Override("Vision.PeriodicCapture.IntervalSeconds", JsonDocument.Parse("30").RootElement),
            Override("Vision.Tesseract.MinConfidence", JsonDocument.Parse("60").RootElement),
            Override("Vision.PeriodicCapture.Enabled", JsonDocument.Parse("true").RootElement),
        };

        store.Apply(overrides);

        var root = JsonDocument.Parse(File.ReadAllText(_path)).RootElement;
        Assert.Equal(30, root.GetProperty("Agent").GetProperty("HeartbeatIntervalSeconds").GetInt32());
        Assert.Equal(5, root.GetProperty("Agent").GetProperty("HeartbeatJitterSeconds").GetInt32());
        Assert.Equal(250, root.GetProperty("Agent").GetProperty("MaxDetectionBatchSize").GetInt32());
        var vision = root.GetProperty("Vision");
        Assert.Equal(30, vision.GetProperty("PeriodicCapture").GetProperty("IntervalSeconds").GetInt32());
        Assert.True(vision.GetProperty("PeriodicCapture").GetProperty("Enabled").GetBoolean());
        Assert.Equal(60, vision.GetProperty("Tesseract").GetProperty("MinConfidence").GetInt32());
    }

    [Fact]
    public void Apply_RejectsNonBooleanValuesForBooleanTunables()
    {
        var store = NewStore();
        var overrides = new[]
        {
            Override("Vision.Enabled", JsonDocument.Parse("\"true\"").RootElement),
            Override("Agent.LearningMode", JsonDocument.Parse("1").RootElement),
            Override("Good.Flag", JsonDocument.Parse("true").RootElement),
        };

        store.Apply(overrides);

        var root = JsonDocument.Parse(File.ReadAllText(_path)).RootElement;
        Assert.True(root.GetProperty("Good").GetProperty("Flag").GetBoolean());
        Assert.False(root.TryGetProperty("Vision", out _));
        Assert.False(root.TryGetProperty("Agent", out _));
    }

    [Fact]
    public void Unwrap_ArrayElement_ReturnsArray()
    {
        var el = JsonDocument.Parse("[1, 2, 3]").RootElement;
        var result = (object?[])ConfigOverrideStore.Unwrap(el)!;
        Assert.Equal(3, result.Length);
        Assert.Equal(1L, result[0]);
    }

    private ConfigOverrideStore NewStore() =>
        new(_path, NullLogger<ConfigOverrideStore>.Instance);

    private static ConfigOverride Override(string path, JsonElement value) => new()
    {
        Path = path,
        Value = value,
        Scope = "pharmacy",
        UpdatedAt = "2026-04-19T00:00:00Z",
    };
}
