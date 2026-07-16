using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Reasoning;
using Xunit;

namespace SuavoAgent.Core.Tests.Reasoning;

public sealed class DeferredLocalInferenceCohortSecurityTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
    private const string ModelKeyId = "brain-model-runtime-test-v1";
    private const string NativeKeyId = "brain-native-runtime-test-v1";
    private readonly ECDsa _modelKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _nativeKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _data = Path.Combine(
        Path.GetTempPath(),
        "suavo-runtime-brain-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Concurrent_first_inference_waits_for_full_cohort_proof_and_activates_once()
    {
        var fixture = CreateInstalledCohort();
        var factoryCalls = 0;
        await using var inference = CreateInference(
            fixture.Options,
            fixture.Keys,
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return new FakeInference();
            });

        var replies = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => inference.ChatAsync("hello", CancellationToken.None)));

        Assert.All(replies, reply => Assert.Equal("verified", reply));
        Assert.Equal(1, factoryCalls);
        Assert.True(inference.IsReady);
    }

    [Fact]
    public async Task Retired_schema_v2_can_only_boot_after_exact_installed_flat_cohort_proof()
    {
        var fixture = CreateInstalledCohort(retired: true);
        await using var inference = CreateInference(
            fixture.Options,
            fixture.Keys,
            _ => new FakeInference());

        Assert.Equal("verified", await inference.ChatAsync(
            "upgrade compatibility",
            CancellationToken.None));
        Assert.True(inference.IsReady);
    }

    [Fact]
    public async Task Tampered_model_cannot_win_constructor_background_verification_race()
    {
        var fixture = CreateInstalledCohort();
        var tampered = Enumerable.Repeat((byte)0xA5, fixture.ModelBytes.Length).ToArray();
        await File.WriteAllBytesAsync(fixture.Options.Value.Reasoning.ModelPath!, tampered);
        var factoryCalls = 0;
        var modelManager = new AlwaysSuccessfulModelManager();
        await using var inference = CreateInference(
            fixture.Options,
            fixture.Keys,
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return new FakeInference();
            },
            modelManager);

        var replies = await Task.WhenAll(
            inference.ChatAsync("one", CancellationToken.None),
            inference.ChatAsync("two", CancellationToken.None));

        Assert.All(replies, Assert.Null);
        Assert.Equal(0, factoryCalls);
        Assert.True(modelManager.Calls > 0);
        Assert.False(inference.IsReady);
    }

    [Fact]
    public async Task Tampered_dll_and_matching_local_inventory_cannot_override_retained_package()
    {
        var fixture = CreateInstalledCohort();
        var dll = Path.Combine(fixture.Options.Value.Reasoning.NativeLibraryPath!, "llama.dll");
        var attacker = new byte[] { 9, 9, 9, 9 };
        await File.WriteAllBytesAsync(dll, attacker);
        var manifestPath = Path.Combine(fixture.CohortRoot, InstalledBrainCohortVerifier.ManifestFileName);
        var local = JsonSerializer.Deserialize<InstalledBrainCohortManifest>(
            await File.ReadAllTextAsync(manifestPath),
            InstalledBrainCohortVerifier.ManifestJson)!;
        var changed = local.NativeFiles!.Select(file => file.Path == "llama.dll"
            ? file with { SizeBytes = attacker.Length, Sha256 = Sha(attacker) }
            : file).ToArray();
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(
                local with { NativeFiles = changed },
                InstalledBrainCohortVerifier.ManifestJson));
        var factoryCalls = 0;
        await using var inference = CreateInference(
            fixture.Options,
            fixture.Keys,
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return new FakeInference();
            });

        Assert.Null(await inference.ChatAsync("hello", CancellationToken.None));
        Assert.Equal(0, factoryCalls);
        Assert.False(inference.IsReady);
    }

    [Fact]
    public async Task Publisher_expiry_disables_an_already_constructed_inner_at_call_time()
    {
        var fixture = CreateInstalledCohort();
        var clock = Now;
        await using var inference = CreateInference(
            fixture.Options,
            fixture.Keys,
            _ => new FakeInference(),
            clock: () => clock);

        Assert.Equal(
            "verified",
            await inference.ChatAsync("before expiry", CancellationToken.None));
        Assert.True(inference.IsReady);

        clock = Now.AddDays(2);

        Assert.False(inference.IsReady);
        Assert.Null(await inference.ChatAsync("after expiry", CancellationToken.None));
        Assert.Equal(BrainProvisioningState.Failed, inference.ProvisioningState);
    }

    private DeferredLocalInference CreateInference(
        IOptions<AgentOptions> options,
        IReadOnlyDictionary<string, string> keys,
        Func<string, ILocalInference> factory,
        IModelManager? modelManager = null,
        Func<DateTimeOffset>? clock = null) =>
        new(
            options,
            new NativeLibProvisioner(options, NullLogger<NativeLibProvisioner>.Instance),
            modelManager ?? new AlwaysSuccessfulModelManager(),
            NullLogger<LLamaLocalInference>.Instance,
            NullLogger<DeferredLocalInference>.Instance,
            _data,
            keys,
            clock ?? (() => Now),
            factory);

    private CohortFixture CreateInstalledCohort(bool retired = false)
    {
        var model = new byte[] { 10, 20, 30, 40, 50 };
        IReadOnlyDictionary<string, byte[]> nativeFiles = retired
            ? new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["ggml-base.dll"] = [1, 2, 3],
                ["ggml-cpu.dll"] = [4, 5, 6],
                ["ggml.dll"] = [7, 8, 9],
                ["llama.dll"] = [10, 11, 12],
            }
            : new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["ggml-base.dll"] = [1, 2, 3],
                ["ggml-cpu.dll"] = [4, 5, 6],
                ["ggml.dll"] = [7, 8, 9],
                ["llama.dll"] = [10, 11, 12],
                ["llava_shared.dll"] = [13, 14, 15],
            };
        var package = retired ? FlatZip(nativeFiles) : NuGetPackage(nativeFiles);
        var unsigned = new BrainCohortPublisherManifest(
            retired
                ? BrainCohortContract.RetiredInstalledSchemaVersion
                : BrainCohortContract.SchemaVersion,
            new string('0', 64),
            "test-model",
            "https://assets.example/model.gguf",
            Sha(model),
            model.Length,
            retired
                ? "https://assets.example/native.zip"
                : "https://assets.example/native.nupkg",
            Sha(package),
            package.Length,
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
            retired
                ? string.Empty
                : BrainNativePackageExtractor.OfficialNuGetPackageKind);
        var manifest = Sign(unsigned);
        var root = BrainCohortContract.GetCohortRoot(_data, manifest.CohortId);
        var modelPath = BrainCohortContract.GetModelPath(_data, manifest);
        var nativeRoot = BrainCohortContract.GetNativeDirectory(_data, manifest);
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        Directory.CreateDirectory(nativeRoot);
        File.WriteAllBytes(modelPath, model);
        File.WriteAllBytes(
            Path.Combine(
                root,
                retired
                    ? InstalledBrainCohortVerifier.RetiredNativePackageFileName
                    : InstalledBrainCohortVerifier.NativePackageFileName),
            package);
        foreach (var file in nativeFiles)
            File.WriteAllBytes(Path.Combine(nativeRoot, file.Key), file.Value);
        var inventory = nativeFiles
            .Select(file => new InstalledBrainFileManifest(
                file.Key,
                file.Value.Length,
                Sha(file.Value)))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();
        var local = new InstalledBrainCohortManifest(
            retired
                ? InstalledBrainCohortVerifier.RetiredManifestSchemaVersion
                : InstalledBrainCohortVerifier.ManifestSchemaVersion,
            manifest.CohortId,
            BrainCohortContract.BuildCanonical(manifest),
            manifest,
            Path.GetFileName(modelPath),
            model.Length,
            manifest.ModelSha256,
            package.Length,
            manifest.NativeLibsSha256,
            inventory,
            manifest.NativePackageKind);
        File.WriteAllText(
            Path.Combine(root, InstalledBrainCohortVerifier.ManifestFileName),
            JsonSerializer.Serialize(local, InstalledBrainCohortVerifier.ManifestJson));

        var options = Options.Create(new AgentOptions
        {
            Reasoning = new ReasoningOptions
            {
                Enabled = true,
                SchemaVersion = manifest.SchemaVersion,
                CohortId = manifest.CohortId,
                ModelId = manifest.ModelId,
                ModelUrl = manifest.ModelUrl,
                ModelSha256 = manifest.ModelSha256,
                ModelSizeBytes = manifest.ModelSizeBytes,
                ModelPath = modelPath,
                NativeLibsUrl = manifest.NativeLibsUrl,
                NativeLibsSha256 = manifest.NativeLibsSha256,
                NativeLibsSizeBytes = manifest.NativeLibsSizeBytes,
                NativePackageKind = manifest.NativePackageKind,
                NativeLibraryPath = nativeRoot,
                ContextSize = manifest.ContextSize,
                MaxOutputTokens = manifest.MaxOutputTokens,
                IssuedAtUtc = manifest.IssuedAtUtc,
                ExpiresAtUtc = manifest.ExpiresAtUtc,
                KeyId = manifest.KeyId,
                Signature = manifest.Signature,
                ModelKeyId = manifest.ModelKeyId,
                ModelSignature = manifest.ModelSignature,
                NativeKeyId = manifest.NativeKeyId,
                NativeSignature = manifest.NativeSignature,
            },
        });
        var keys = new Dictionary<string, string>
        {
            [ModelKeyId] = Convert.ToBase64String(_modelKey.ExportSubjectPublicKeyInfo()),
            [NativeKeyId] = Convert.ToBase64String(_nativeKey.ExportSubjectPublicKeyInfo()),
        };
        return new(root, model, options, keys);
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

    private static byte[] NuGetPackage(IReadOnlyDictionary<string, byte[]> files)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var nuspec = archive.CreateEntry("LLamaSharp.Backend.Cpu.nuspec");
            using (var target = nuspec.Open())
            using (var writer = new StreamWriter(target, Encoding.UTF8, leaveOpen: false))
                writer.Write($$"""
                    <?xml version="1.0" encoding="utf-8"?>
                    <package xmlns="http://schemas.microsoft.com/packaging/2011/08/nuspec.xsd">
                      <metadata>
                        <id>{{BrainNativePackageExtractor.PackageId}}</id>
                        <version>{{BrainNativePackageExtractor.PackageVersion}}</version>
                        <license type="expression">MIT</license>
                      </metadata>
                    </package>
                    """);
            var signature = archive.CreateEntry(".signature.p7s");
            using (var target = signature.Open())
                target.Write([42, 43, 44]);
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(
                    BrainNativePackageExtractor.NuGetPrefix + file.Key,
                    CompressionLevel.Fastest);
                using var target = entry.Open();
                target.Write(file.Value);
            }
            var ignored = archive.CreateEntry("runtimes/win-x64/native/avx2/llama.dll");
            using (var target = ignored.Open())
                target.Write([99]);
        }
        return output.ToArray();
    }

    private static byte[] FlatZip(IReadOnlyDictionary<string, byte[]> files)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Key, CompressionLevel.Fastest);
                using var target = entry.Open();
                target.Write(file.Value);
            }
        }
        return output.ToArray();
    }

    private static string Sha(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Utc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    private sealed record CohortFixture(
        string CohortRoot,
        byte[] ModelBytes,
        IOptions<AgentOptions> Options,
        IReadOnlyDictionary<string, string> Keys);

    private sealed class AlwaysSuccessfulModelManager : IModelManager
    {
        public int Calls;

        public Task<ModelVerificationResult> VerifyAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new ModelVerificationResult(true, null, null, "test-success"));
        }
    }

    private sealed class FakeInference : ILocalInference
    {
        public string ModelId => "fake";
        public bool IsReady => true;

        public Task<InferenceProposal?> ProposeAsync(InferenceRequest request, CancellationToken ct) =>
            Task.FromResult<InferenceProposal?>(null);

        public Task<string?> ChatAsync(string userMessage, CancellationToken ct) =>
            Task.FromResult<string?>("verified");
    }

    public void Dispose()
    {
        _modelKey.Dispose();
        _nativeKey.Dispose();
        try { Directory.Delete(_data, recursive: true); } catch { }
    }
}
