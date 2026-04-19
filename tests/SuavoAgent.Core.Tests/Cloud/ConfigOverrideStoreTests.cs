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
