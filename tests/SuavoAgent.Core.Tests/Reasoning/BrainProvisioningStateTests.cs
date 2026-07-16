using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SuavoAgent.Contracts.Reasoning;
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
    private static readonly DateTimeOffset Now =
        new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
    private const string ModelKeyId = "brain-model-provisioning-test-v1";
    private const string NativeKeyId = "brain-native-provisioning-test-v1";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "suavo-brain-test-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _modelKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _nativeKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static readonly string[] RequiredDlls = { "llama.dll", "ggml.dll", "ggml-base.dll", "ggml-cpu.dll" };

    private sealed class StubModelManager : IModelManager
    {
        public Task<ModelVerificationResult> VerifyAsync(CancellationToken ct) =>
            Task.FromResult(new ModelVerificationResult(false, null, null, "stub"));
    }

    private DeferredLocalInference NewInference(
        string? modelPath,
        string? nativeDir,
        long? modelSizeBytes = 1_000,
        bool publisherAuthorized = true)
    {
        var unsigned = new BrainCohortPublisherManifest(
            BrainCohortContract.SchemaVersion,
            new string('0', 64),
            "qwen3-1.7b",
            "https://assets.example/model.gguf",
            new string('a', 64),
            modelSizeBytes ?? 0,
            "https://assets.example/native.nupkg",
            new string('b', 64),
            4_096,
            4_096,
            512,
            Utc(Now.AddHours(-1)),
            Utc(Now.AddDays(1)),
            string.Empty,
            string.Empty,
            ModelKeyId,
            string.Empty,
            NativeKeyId,
            string.Empty,
            BrainNativePackageExtractor.OfficialNuGetPackageKind);
        var manifest = Sign(unsigned);
        var options = Options.Create(new AgentOptions
        {
            Reasoning = new ReasoningOptions
            {
                Enabled = true,
                SchemaVersion = publisherAuthorized ? manifest.SchemaVersion : 0,
                CohortId = publisherAuthorized ? manifest.CohortId : null,
                ModelId = manifest.ModelId,
                ModelPath = modelPath,
                NativeLibraryPath = nativeDir,
                ModelSizeBytes = modelSizeBytes,
                ModelUrl = publisherAuthorized ? manifest.ModelUrl : null,
                ModelSha256 = publisherAuthorized ? manifest.ModelSha256 : null,
                NativeLibsUrl = publisherAuthorized ? manifest.NativeLibsUrl : null,
                NativeLibsSha256 = publisherAuthorized ? manifest.NativeLibsSha256 : null,
                NativeLibsSizeBytes = publisherAuthorized ? manifest.NativeLibsSizeBytes : null,
                NativePackageKind = publisherAuthorized ? manifest.NativePackageKind : null,
                ContextSize = manifest.ContextSize,
                MaxOutputTokens = manifest.MaxOutputTokens,
                IssuedAtUtc = publisherAuthorized ? manifest.IssuedAtUtc : null,
                ExpiresAtUtc = publisherAuthorized ? manifest.ExpiresAtUtc : null,
                ModelKeyId = publisherAuthorized ? manifest.ModelKeyId : null,
                ModelSignature = publisherAuthorized ? manifest.ModelSignature : null,
                NativeKeyId = publisherAuthorized ? manifest.NativeKeyId : null,
                NativeSignature = publisherAuthorized ? manifest.NativeSignature : null,
                // The publisher manifest is real and signed in-process. Tests
                // arrange present native files (or no path) during construction,
                // so its background provisioner never reaches the network.
            },
        });
        var keys = new Dictionary<string, string>
        {
            [ModelKeyId] = Convert.ToBase64String(_modelKey.ExportSubjectPublicKeyInfo()),
            [NativeKeyId] = Convert.ToBase64String(_nativeKey.ExportSubjectPublicKeyInfo()),
        };
        return new DeferredLocalInference(
            options,
            new NativeLibProvisioner(options, NullLogger<NativeLibProvisioner>.Instance),
            new StubModelManager(),
            NullLogger<LLamaLocalInference>.Instance,
            NullLogger<DeferredLocalInference>.Instance,
            _root,
            keys,
            () => Now);
    }

    private BrainCohortPublisherManifest Sign(BrainCohortPublisherManifest manifest)
    {
        var identified = manifest with
        {
            CohortId = BrainCohortContract.ComputeCohortId(manifest),
            ModelSignature = string.Empty,
            NativeSignature = string.Empty,
        };
        return identified with
        {
            ModelSignature = Convert.ToHexString(_modelKey.SignData(
                Encoding.UTF8.GetBytes(BrainCohortContract.BuildModelCanonical(identified)),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation)).ToLowerInvariant(),
            NativeSignature = Convert.ToHexString(_nativeKey.SignData(
                Encoding.UTF8.GetBytes(BrainCohortContract.BuildNativeCanonical(identified)),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation)).ToLowerInvariant(),
        };
    }

    private static string Utc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

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
        var nativeDir = NativeDirWithDlls();
        var sut = NewInference(Path.Combine(_root, "models", "m.gguf"), nativeDir);
        File.Delete(Path.Combine(nativeDir, RequiredDlls[0]));
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
    public void MissingSignedModelSize_FailsAuthorization()
    {
        var modelPath = Path.Combine(_root, "models", "m.gguf");
        var sut = NewInference(modelPath, NativeDirWithDlls(), modelSizeBytes: null);
        Assert.Equal(BrainProvisioningState.Failed, sut.ProvisioningState);
        Assert.Null(sut.ProvisioningPercent);
    }

    [Fact]
    public void Unsigned_assets_present_are_failed_not_ready()
    {
        var modelPath = Path.Combine(_root, "models", "m.gguf");
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        File.WriteAllBytes(modelPath, new byte[] { 1, 2, 3 });

        var sut = NewInference(modelPath, NativeDirWithDlls(), publisherAuthorized: false);
        Assert.Equal(BrainProvisioningState.Failed, sut.ProvisioningState);
        Assert.Null(sut.ProvisioningPercent);
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
        _modelKey.Dispose();
        _nativeKey.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
