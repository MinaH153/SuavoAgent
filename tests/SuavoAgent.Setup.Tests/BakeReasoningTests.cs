using System.Collections.Generic;
using System.IO;
using SuavoAgent.Setup;
using SuavoAgent.Setup.Gui.Services;
using Xunit;

namespace SuavoAgent.Setup.Tests;

/// <summary>
/// The installer bakes the cloud-supplied brain config into Agent:Reasoning so a
/// fresh install boots reasoning-enabled and self-provisions on first run. Codex
/// flagged that the provisioners HARD-FAIL without on-box ModelPath /
/// NativeLibraryPath — these guard that they're always computed + present.
/// </summary>
public sealed class BakeReasoningTests
{
    private const string DataDir = @"C:\ProgramData\SuavoAgent";

    private static AgentReasoningConfig Provisionable() => new(
        Enabled: true,
        ModelId: "qwen3-1.7b",
        ModelUrl: "https://github.com/MinaH153/suavo-agent-models/releases/download/qwen3-1.7b-q4km-v1/qwen3-1.7b-q4_k_m.gguf",
        ModelSha256: new string('a', 64),
        ModelSizeBytes: 1282439584,
        NativeLibsUrl: "https://github.com/MinaH153/suavo-agent-models/releases/download/qwen3-1.7b-q4km-v1/llama-cpp-win-x64-noavx-0.24.0.zip",
        NativeLibsSha256: new string('b', 64),
        NativeLibsSizeBytes: 1053270,
        ContextSize: 4096,
        MaxOutputTokens: 512);

    [Fact]
    public void Bakes_ReasoningSection_WithComputedOnBoxPaths()
    {
        var agent = new Dictionary<string, object?>();
        InstallOrchestrator.BakeReasoning(agent, Provisionable(), DataDir);

        Assert.True(agent.ContainsKey("Reasoning"));
        var r = (Dictionary<string, object?>)agent["Reasoning"]!;
        Assert.Equal(true, r["Enabled"]);
        Assert.Equal("qwen3-1.7b", r["ModelId"]);
        // Paths are computed from the data dir (the Codex-flagged required fields).
        Assert.Equal(Path.Combine(DataDir, "models", "qwen3-1.7b-q4_k_m.gguf"), r["ModelPath"]);
        Assert.Equal(Path.Combine(DataDir, "native"), r["NativeLibraryPath"]);
        // URLs + SHAs flow through verbatim.
        Assert.Equal(Provisionable().ModelUrl, r["ModelUrl"]);
        Assert.Equal(Provisionable().ModelSha256, r["ModelSha256"]);
        Assert.Equal(Provisionable().NativeLibsUrl, r["NativeLibsUrl"]);
        Assert.Equal(Provisionable().NativeLibsSha256, r["NativeLibsSha256"]);
        Assert.Equal(4096, r["ContextSize"]);
        Assert.Equal(512, r["MaxOutputTokens"]);
        // Size powers the agent's download-percent telemetry (dashboard Brain card).
        Assert.Equal(1282439584L, r["ModelSizeBytes"]);
    }

    [Fact]
    public void NoReasoning_WhenConfigIsNull()
    {
        var agent = new Dictionary<string, object?>();
        InstallOrchestrator.BakeReasoning(agent, null, DataDir);
        Assert.False(agent.ContainsKey("Reasoning"));
    }

    [Fact]
    public void NoReasoning_WhenNotProvisionable()
    {
        var agent = new Dictionary<string, object?>();
        // enabled but missing the model URL => not provisionable => no bake (fail-soft).
        var bad = Provisionable() with { ModelUrl = "" };
        InstallOrchestrator.BakeReasoning(agent, bad, DataDir);
        Assert.False(agent.ContainsKey("Reasoning"));
    }

    [Fact]
    public void NoReasoning_WhenDisabled()
    {
        var agent = new Dictionary<string, object?>();
        var disabled = Provisionable() with { Enabled = false };
        InstallOrchestrator.BakeReasoning(agent, disabled, DataDir);
        Assert.False(agent.ContainsKey("Reasoning"));
    }
}
