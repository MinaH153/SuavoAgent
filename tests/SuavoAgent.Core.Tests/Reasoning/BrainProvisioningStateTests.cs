using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Reasoning;
using Xunit;

namespace SuavoAgent.Core.Tests.Reasoning;

/// <summary>
/// The provisioning lifecycle the heartbeat reports for the dashboard's
/// "Installing the brain… NN%" card. Derived purely from what's on disk —
/// these pin the state matrix + the temp-file percent math.
/// </summary>
public sealed class BrainProvisioningStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "suavo-brain-test-" + Guid.NewGuid().ToString("N"));

    private static readonly string[] RequiredDlls = { "llama.dll", "ggml.dll", "ggml-base.dll", "ggml-cpu.dll" };

    private sealed class StubModelManager : IModelManager
    {
        public Task<ModelVerificationResult> VerifyAsync(CancellationToken ct) =>
            Task.FromResult(new ModelVerificationResult(false, null, null, "stub"));
    }

    private DeferredLocalInference NewInference(string? modelPath, string? nativeDir, long? modelSizeBytes = null)
    {
        var options = Options.Create(new AgentOptions
        {
            Reasoning = new ReasoningOptions
            {
                Enabled = true,
                ModelId = "qwen3-1.7b",
                ModelPath = modelPath,
                NativeLibraryPath = nativeDir,
                ModelSizeBytes = modelSizeBytes,
                // No URLs → the constructor's background provisioning is a no-op
                // (logged + skipped), keeping these tests hermetic.
            },
        });
        return new DeferredLocalInference(
            options,
            new NativeLibProvisioner(options, NullLogger<NativeLibProvisioner>.Instance),
            new StubModelManager(),
            NullLogger<LLamaLocalInference>.Instance,
            NullLogger<DeferredLocalInference>.Instance);
    }

    private string NativeDirWithDlls()
    {
        var dir = Path.Combine(_root, "native");
        Directory.CreateDirectory(dir);
        foreach (var dll in RequiredDlls)
            File.WriteAllBytes(Path.Combine(dir, dll), new byte[] { 1 });
        return dir;
    }

    [Fact]
    public void NoModelPath_ReportsOff()
    {
        var sut = NewInference(modelPath: null, nativeDir: null);
        Assert.Equal(BrainProvisioningState.Off, sut.ProvisioningState);
        Assert.Null(sut.ProvisioningPercent);
    }

    [Fact]
    public void DllsAbsent_ReportsDownloadingLibs()
    {
        var sut = NewInference(Path.Combine(_root, "models", "m.gguf"), Path.Combine(_root, "native-missing"));
        Assert.Equal(BrainProvisioningState.DownloadingLibs, sut.ProvisioningState);
        Assert.Null(sut.ProvisioningPercent);
    }

    [Fact]
    public void DllsPresent_ModelAbsent_ReportsDownloadingModel_WithTempFilePercent()
    {
        var modelPath = Path.Combine(_root, "models", "m.gguf");
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        // Simulate the provisioner mid-download: 250 of 1000 bytes landed.
        File.WriteAllBytes(modelPath + ".download", new byte[250]);

        var sut = NewInference(modelPath, NativeDirWithDlls(), modelSizeBytes: 1000);
        Assert.Equal(BrainProvisioningState.DownloadingModel, sut.ProvisioningState);
        Assert.Equal(25, sut.ProvisioningPercent);
    }

    [Fact]
    public void DownloadingModel_NoTempFileYet_ReportsZeroPercent()
    {
        var modelPath = Path.Combine(_root, "models", "m.gguf");
        var sut = NewInference(modelPath, NativeDirWithDlls(), modelSizeBytes: 1000);
        Assert.Equal(BrainProvisioningState.DownloadingModel, sut.ProvisioningState);
        Assert.Equal(0, sut.ProvisioningPercent);
    }

    [Fact]
    public void DownloadingModel_UnknownTotalSize_ReportsNullPercent()
    {
        var modelPath = Path.Combine(_root, "models", "m.gguf");
        var sut = NewInference(modelPath, NativeDirWithDlls(), modelSizeBytes: null);
        Assert.Null(sut.ProvisioningPercent);
    }

    [Fact]
    public void AssetsAllPresent_ReportsReady_With100Percent()
    {
        var modelPath = Path.Combine(_root, "models", "m.gguf");
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        File.WriteAllBytes(modelPath, new byte[] { 1, 2, 3 });

        var sut = NewInference(modelPath, NativeDirWithDlls());
        Assert.Equal(BrainProvisioningState.Ready, sut.ProvisioningState);
        Assert.Equal(100, sut.ProvisioningPercent);
    }

    [Fact]
    public void PercentClampsTo99_WhileFinalFileAbsent()
    {
        // Temp file can momentarily exceed the recorded size (metadata drift) —
        // never show 100% until the verified file actually lands.
        var modelPath = Path.Combine(_root, "models", "m.gguf");
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        File.WriteAllBytes(modelPath + ".download", new byte[1200]);

        var sut = NewInference(modelPath, NativeDirWithDlls(), modelSizeBytes: 1000);
        Assert.Equal(99, sut.ProvisioningPercent);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
