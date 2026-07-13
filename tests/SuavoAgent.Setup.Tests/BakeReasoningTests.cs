using System.Collections.Generic;
using System.IO;
using SuavoAgent.Contracts.Reasoning;
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

    private static AgentReasoningConfig Provisionable()
    {
        var config = new AgentReasoningConfig(
            Enabled: true,
            ModelId: "qwen3-1.7b",
            ModelUrl: "https://huggingface.co/Qwen/Qwen3-1.7B-GGUF/resolve/e8e713d99f327fd01e47f1992a910a9e4cacf312/Qwen3-1.7B-Q4_K_M.gguf",
            ModelSha256: "228fb5627f7510b8b3516cdb6435e4b0d2a2bf330fe5b0ab19284a3570a8bb1f",
            ModelSizeBytes: 1107408544,
            NativeLibsUrl: "https://api.nuget.org/v3-flatcontainer/llamasharp.backend.cpu/0.24.0/llamasharp.backend.cpu.0.24.0.nupkg",
            NativeLibsSha256: "47120fed200482ab364b9d225271172ccbf2ac7713ad388e4e7fe7d89fdedb0a",
            NativeLibsSizeBytes: 21485108,
            NativePackageKind: BrainNativePackageExtractor.OfficialNuGetPackageKind,
            ContextSize: 4096,
            MaxOutputTokens: 512,
            SchemaVersion: BrainCohortContract.SchemaVersion,
            CohortId: new string('0', 64),
            IssuedAtUtc: "2026-07-11T00:00:00.000Z",
            ExpiresAtUtc: "2027-07-11T00:00:00.000Z",
            KeyId: string.Empty,
            Signature: string.Empty,
            ModelKeyId: "brain-model-test-v1",
            ModelSignature: new string('1', 128),
            NativeKeyId: "brain-native-test-v1",
            NativeSignature: new string('2', 128));
        return config with
        {
            CohortId = BrainCohortContract.ComputeCohortId(config.PublisherManifest()),
        };
    }

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
        Assert.Equal(Provisionable().GetModelPath(DataDir), r["ModelPath"]);
        Assert.Equal(Provisionable().GetNativeLibsDir(DataDir), r["NativeLibraryPath"]);
        Assert.Contains(Path.Combine("reasoning", "cohorts"), (string)r["ModelPath"]!);
        // URLs + SHAs flow through verbatim.
        Assert.Equal(Provisionable().ModelUrl, r["ModelUrl"]);
        Assert.Equal(Provisionable().ModelSha256, r["ModelSha256"]);
        Assert.Equal(Provisionable().NativeLibsUrl, r["NativeLibsUrl"]);
        Assert.Equal(Provisionable().NativeLibsSha256, r["NativeLibsSha256"]);
        Assert.Equal(4096, r["ContextSize"]);
        Assert.Equal(512, r["MaxOutputTokens"]);
        // Size powers the agent's download-percent telemetry (dashboard Brain card).
        Assert.Equal(1107408544L, r["ModelSizeBytes"]);
        Assert.Equal(21485108L, r["NativeLibsSizeBytes"]);
        Assert.Equal(
            BrainNativePackageExtractor.OfficialNuGetPackageKind,
            r["NativePackageKind"]);
        Assert.Equal(BrainCohortContract.SchemaVersion, r["SchemaVersion"]);
        Assert.Equal(Provisionable().CohortId, r["CohortId"]);
        Assert.Equal("brain-model-test-v1", r["ModelKeyId"]);
        Assert.Equal(new string('1', 128), r["ModelSignature"]);
        Assert.Equal("brain-native-test-v1", r["NativeKeyId"]);
        Assert.Equal(new string('2', 128), r["NativeSignature"]);
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

    [Fact]
    public void Different_verified_asset_pair_gets_a_different_immutable_cohort()
    {
        var first = Provisionable();
        var second = first with { NativeLibsSha256 = new string('c', 64) };

        Assert.NotEqual(first.GetModelPath(DataDir), second.GetModelPath(DataDir));
        Assert.NotEqual(first.GetNativeLibsDir(DataDir), second.GetNativeLibsDir(DataDir));
    }

    [Fact]
    public void Same_bytes_with_different_model_filename_cannot_alias_one_cohort_path()
    {
        var first = Provisionable();
        var renamed = first with { ModelUrl = "https://assets.example/renamed-model.gguf" };

        Assert.NotEqual(first.GetModelPath(DataDir), renamed.GetModelPath(DataDir));
        Assert.NotEqual(first.GetNativeLibsDir(DataDir), renamed.GetNativeLibsDir(DataDir));
    }
}
