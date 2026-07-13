using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Setup;
using SuavoAgent.Setup.Gui.Services;
using Xunit;

namespace SuavoAgent.Setup.Tests;

public sealed class BrainInstallerTests
{
    [Fact]
    public async Task Complete_pair_activates_once_and_never_mutates_legacy_live_paths()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.Data, "native"));
        Directory.CreateDirectory(Path.Combine(fixture.Data, "models"));
        File.WriteAllText(Path.Combine(fixture.Data, "native", "llama.dll"), "old-native");
        File.WriteAllText(Path.Combine(fixture.Data, "models", "model.gguf"), "old-model");

        var installed = await InstallAsync(fixture);

        Assert.True(installed);
        Assert.Equal(fixture.Model, await File.ReadAllBytesAsync(fixture.Config.GetModelPath(fixture.Data)));
        Assert.Equal(
            fixture.NativeFile,
            await File.ReadAllBytesAsync(Path.Combine(
                fixture.Config.GetNativeLibsDir(fixture.Data),
                "llama.dll")));
        Assert.Equal(
            new[] { "ggml-base.dll", "ggml-cpu.dll", "ggml.dll", "llama.dll", "llava_shared.dll" },
            Directory.GetFiles(fixture.Config.GetNativeLibsDir(fixture.Data))
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal));
        Assert.Empty(Directory.GetDirectories(fixture.Config.GetNativeLibsDir(fixture.Data)));
        Assert.Equal("old-native", File.ReadAllText(Path.Combine(fixture.Data, "native", "llama.dll")));
        Assert.Equal("old-model", File.ReadAllText(Path.Combine(fixture.Data, "models", "model.gguf")));
        Assert.Empty(Directory.GetDirectories(
            Path.Combine(fixture.Data, "reasoning", "cohorts"),
            "*.staging-*"));
    }

    [Fact]
    public async Task Existing_content_addressed_cohort_is_fully_reproved_without_network()
    {
        using var fixture = new Fixture();
        Assert.True(await InstallAsync(fixture));
        var offline = new StaticHandler(new Dictionary<string, byte[]>(), failOnRequest: true);

        var installed = await InstallAsync(fixture, handler: offline);

        Assert.True(installed);
        Assert.Equal(0, offline.Requests);
    }

    [Fact]
    public async Task Interrupted_empty_final_cohort_is_quarantined_and_repaired_from_signed_artifacts()
    {
        using var fixture = new Fixture();
        var cohort = fixture.Config.GetBrainCohortRoot(fixture.Data);
        Directory.CreateDirectory(cohort);

        var installed = await InstallAsync(fixture);

        Assert.True(installed);
        Assert.Equal(2, fixture.Handler.Requests);
        Assert.Equal(fixture.Model, await File.ReadAllBytesAsync(
            fixture.Config.GetModelPath(fixture.Data)));
        Assert.Contains(
            "invalid_cohort_removed",
            await File.ReadAllTextAsync(RepairReceipt(fixture)));
        Assert.Empty(Directory.GetFileSystemEntries(
            Path.GetDirectoryName(cohort)!,
            "*.quarantine-*"));
    }

    [Theory]
    [InlineData("model")]
    [InlineData("native")]
    [InlineData("package")]
    [InlineData("manifest")]
    public async Task Corrupt_cohort_is_never_activated_and_is_repaired_by_full_redownload(
        string corrupt)
    {
        using var fixture = new Fixture();
        Assert.True(await InstallAsync(fixture));
        var cohort = fixture.Config.GetBrainCohortRoot(fixture.Data);
        switch (corrupt)
        {
            case "model":
                await File.WriteAllBytesAsync(
                    fixture.Config.GetModelPath(fixture.Data),
                    new byte[fixture.Model.Length]);
                break;
            case "native":
                await File.WriteAllBytesAsync(
                    Path.Combine(cohort, "native", "llama.dll"),
                    new byte[fixture.NativeFile.Length]);
                break;
            case "package":
                await File.WriteAllBytesAsync(
                    Path.Combine(cohort, InstalledBrainCohortVerifier.NativePackageFileName),
                    new byte[fixture.NativePackage.Length]);
                break;
            case "manifest":
                await File.WriteAllTextAsync(
                    Path.Combine(cohort, "brain.manifest.json"),
                    "{\"interrupted\":true}");
                break;
        }
        var requestsBeforeRepair = fixture.Handler.Requests;

        Assert.True(await InstallAsync(fixture));
        Assert.Equal(requestsBeforeRepair + 2, fixture.Handler.Requests);
        Assert.Equal(fixture.Model, await File.ReadAllBytesAsync(
            fixture.Config.GetModelPath(fixture.Data)));
        Assert.Equal(fixture.NativeFile, await File.ReadAllBytesAsync(
            Path.Combine(cohort, "native", "llama.dll")));
        Assert.Contains(
            "invalid_cohort_removed",
            await File.ReadAllTextAsync(RepairReceipt(fixture)));
    }

    [Fact]
    public async Task Invalid_cohort_is_preserved_when_full_repair_capacity_cannot_be_proved()
    {
        using var fixture = new Fixture();
        var cohort = fixture.Config.GetBrainCohortRoot(fixture.Data);
        Directory.CreateDirectory(cohort);
        await File.WriteAllTextAsync(Path.Combine(cohort, "partial.bin"), "partial");

        var installed = await InstallAsync(fixture, availableBytes: _ => 0);

        Assert.False(installed);
        Assert.True(Directory.Exists(cohort));
        Assert.Equal("partial", await File.ReadAllTextAsync(
            Path.Combine(cohort, "partial.bin")));
        Assert.Equal(0, fixture.Handler.Requests);
        Assert.False(File.Exists(RepairReceipt(fixture)));
    }

    [Fact]
    public async Task Invalid_cohort_repair_requires_explicit_administrator_authority()
    {
        using var fixture = new Fixture();
        var cohort = fixture.Config.GetBrainCohortRoot(fixture.Data);
        Directory.CreateDirectory(cohort);

        var installed = await InstallAsync(fixture, repairAuthorized: () => false);

        Assert.False(installed);
        Assert.True(Directory.Exists(cohort));
        Assert.Equal(0, fixture.Handler.Requests);
    }

    [Fact]
    public async Task Traversal_entry_is_rejected_without_escape_or_final_cohort()
    {
        using var fixture = new Fixture(nativePackage: Zip(("../escape.dll", [1, 2, 3])));

        var installed = await InstallAsync(fixture);

        Assert.False(installed);
        Assert.False(File.Exists(Path.Combine(fixture.Data, "reasoning", "escape.dll")));
        Assert.False(Directory.Exists(fixture.Config.GetBrainCohortRoot(fixture.Data)));
        Assert.Empty(Directory.GetDirectories(
            Path.Combine(fixture.Data, "reasoning", "cohorts"),
            "*.staging-*"));
    }

    [Fact]
    public async Task Native_package_symlink_entry_is_rejected_before_extraction()
    {
        using var fixture = new Fixture(nativePackage: ZipSymlink("llama.dll", "outside.dll"));

        var installed = await InstallAsync(fixture);

        Assert.False(installed);
        Assert.False(Directory.Exists(fixture.Config.GetBrainCohortRoot(fixture.Data)));
        Assert.Empty(Directory.GetDirectories(
            Path.Combine(fixture.Data, "reasoning", "cohorts"),
            "*.staging-*"));
    }

    [Fact]
    public async Task Declared_asset_over_hard_cap_is_rejected_before_network_or_disk_mutation()
    {
        using var fixture = new Fixture();
        var oversized = fixture.Sign(fixture.Config with
        {
            ModelSizeBytes = BrainInstaller.MaxModelBytes + 1,
        });

        var installed = await InstallAsync(fixture, oversized);

        Assert.False(installed);
        Assert.Equal(0, fixture.Handler.Requests);
        Assert.False(Directory.Exists(oversized.GetBrainCohortRoot(fixture.Data)));
    }

    [Fact]
    public async Task Hash_failure_cleans_private_stage_and_preserves_existing_brain_files()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.Data, "native"));
        File.WriteAllText(Path.Combine(fixture.Data, "native", "llama.dll"), "current");
        var wrongHash = fixture.Sign(
            fixture.Config with { ModelSha256 = new string('0', 64) });

        var installed = await InstallAsync(fixture, wrongHash);

        Assert.False(installed);
        Assert.Equal("current", File.ReadAllText(Path.Combine(fixture.Data, "native", "llama.dll")));
        Assert.False(Directory.Exists(wrongHash.GetBrainCohortRoot(fixture.Data)));
        Assert.Empty(Directory.GetDirectories(
            Path.Combine(fixture.Data, "reasoning", "cohorts"),
            "*.staging-*"));
    }

    [Fact]
    public async Task Next_run_reclaims_exact_abandoned_brain_stage_before_activation()
    {
        using var fixture = new Fixture();
        var abandoned = fixture.Config.GetBrainCohortRoot(fixture.Data) +
                        ".staging-" + new string('a', 32);
        Directory.CreateDirectory(abandoned);
        File.WriteAllText(Path.Combine(abandoned, "partial.bin"), "partial");
        var victim = Path.Combine(fixture.Data, "do-not-delete");
        Directory.CreateDirectory(victim);
        File.WriteAllText(Path.Combine(victim, "keep.txt"), "keep");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(abandoned, "nested-link"), victim);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or
                                   IOException or
                                   PlatformNotSupportedException)
        {
            // The cleanup proof still covers the exact abandoned stage on hosts
            // where creating a test link requires an unavailable privilege.
        }

        var installed = await InstallAsync(fixture);

        Assert.True(installed);
        Assert.False(Directory.Exists(abandoned));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(victim, "keep.txt")));
    }

    [Fact]
    public async Task Next_run_reclaims_exact_crash_left_authorization_temp_only()
    {
        using var fixture = new Fixture();
        var cohort = fixture.Config.GetBrainCohortRoot(fixture.Data);
        Directory.CreateDirectory(Path.GetDirectoryName(cohort)!);
        var abandoned = cohort + ".manifest-new-" + new string('a', 32);
        await File.WriteAllTextAsync(abandoned, "partial authorization");
        var unrelated = Path.Combine(
            Path.GetDirectoryName(cohort)!,
            "unrelated.manifest-new-" + new string('b', 32));
        await File.WriteAllTextAsync(unrelated, "keep");

        Assert.True(await InstallAsync(fixture));
        Assert.False(File.Exists(abandoned));
        Assert.Equal("keep", await File.ReadAllTextAsync(unrelated));
    }

    [Fact]
    public async Task Missing_unknown_or_expired_publisher_authority_is_rejected_before_network()
    {
        using var fixture = new Fixture();
        var candidates = new[]
        {
            fixture.Config with { ModelSignature = "" },
            fixture.Config with { NativeKeyId = "brain-native-unknown-v1" },
            fixture.Sign(fixture.Config with
            {
                IssuedAtUtc = Utc(fixture.Now.AddDays(-2)),
                ExpiresAtUtc = Utc(fixture.Now.AddDays(-1)),
            }),
        };

        foreach (var candidate in candidates)
            Assert.False(await InstallAsync(fixture, candidate));

        Assert.Equal(0, fixture.Handler.Requests);
        Assert.False(Directory.Exists(Path.Combine(fixture.Data, "reasoning")));
    }

    [Fact]
    public async Task Existing_cohort_repairs_tampered_persisted_authorization_from_valid_request_offline()
    {
        using var fixture = new Fixture();
        Assert.True(await InstallAsync(fixture));
        var manifestPath = Path.Combine(
            fixture.Config.GetBrainCohortRoot(fixture.Data),
            "brain.manifest.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        manifest["publisherManifest"]!["signature"] = new string('0', 128);
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
        var offline = new StaticHandler(new Dictionary<string, byte[]>(), failOnRequest: true);

        Assert.True(await InstallAsync(fixture, handler: offline));
        Assert.Equal(0, offline.Requests);
        var repaired = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        Assert.Equal(
            fixture.Config.Signature,
            repaired["publisherManifest"]!["signature"]!.GetValue<string>());
    }

    [Fact]
    public async Task Tampered_native_inventory_is_quarantined_and_offline_repair_never_activates()
    {
        using var fixture = new Fixture();
        Assert.True(await InstallAsync(fixture));
        var cohort = fixture.Config.GetBrainCohortRoot(fixture.Data);
        var dllPath = Path.Combine(cohort, "native", "llama.dll");
        var attackerBytes = new byte[] { 1, 3, 3, 7 };
        await File.WriteAllBytesAsync(dllPath, attackerBytes);
        var manifestPath = Path.Combine(cohort, "brain.manifest.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        var localFile = manifest["nativeFiles"]!.AsArray()[0]!.AsObject();
        localFile["sizeBytes"] = attackerBytes.Length;
        localFile["sha256"] = Sha(attackerBytes);
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
        var offline = new StaticHandler(new Dictionary<string, byte[]>(), failOnRequest: true);

        Assert.False(await InstallAsync(fixture, handler: offline));
        Assert.Equal(1, offline.Requests);
        Assert.False(Directory.Exists(cohort));
        Assert.True(File.Exists(RepairReceipt(fixture)));
    }

    [Fact]
    public async Task Same_artifacts_with_renewed_validity_refresh_authorization_without_network()
    {
        using var fixture = new Fixture();
        Assert.True(await InstallAsync(fixture));
        var renewed = fixture.Sign(fixture.Config with
        {
            IssuedAtUtc = Utc(fixture.Now.AddMinutes(-1)),
            ExpiresAtUtc = Utc(fixture.Now.AddDays(30)),
        });
        Assert.Equal(fixture.Config.CohortId, renewed.CohortId);
        var offline = new StaticHandler(new Dictionary<string, byte[]>(), failOnRequest: true);

        Assert.True(await InstallAsync(fixture, renewed, offline));
        Assert.Equal(0, offline.Requests);
        var manifestPath = Path.Combine(
            renewed.GetBrainCohortRoot(fixture.Data),
            "brain.manifest.json");
        var persisted = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        Assert.Equal(
            renewed.ExpiresAtUtc,
            persisted["publisherManifest"]!["expiresAtUtc"]!.GetValue<string>());
        Assert.Equal(
            renewed.Signature,
            persisted["publisherManifest"]!["signature"]!.GetValue<string>());
    }

    [Fact]
    public async Task Expired_persisted_authorization_is_recovered_by_valid_renewal()
    {
        using var fixture = new Fixture();
        var old = fixture.Sign(fixture.Config with
        {
            IssuedAtUtc = Utc(fixture.Now.AddDays(-3)),
            ExpiresAtUtc = Utc(fixture.Now.AddDays(-1)),
        });
        Assert.True(await InstallAsync(
            fixture,
            old,
            verificationTime: fixture.Now.AddDays(-2)));
        var renewed = fixture.Sign(fixture.Config with
        {
            IssuedAtUtc = Utc(fixture.Now.AddHours(-1)),
            ExpiresAtUtc = Utc(fixture.Now.AddDays(7)),
        });
        var offline = new StaticHandler(new Dictionary<string, byte[]>(), failOnRequest: true);

        Assert.True(await InstallAsync(fixture, renewed, offline));
        Assert.Equal(0, offline.Requests);
    }

    [Fact]
    public async Task Artifact_url_size_or_tuning_change_cannot_alias_existing_cohort()
    {
        using var fixture = new Fixture();
        Assert.True(await InstallAsync(fixture));
        var candidates = new[]
        {
            fixture.Sign(fixture.Config with
            {
                ModelUrl = "https://assets.example/other.gguf",
            }),
            fixture.Sign(fixture.Config with
            {
                ModelSizeBytes = fixture.Config.ModelSizeBytes + 1,
            }),
            fixture.Sign(fixture.Config with { ContextSize = 8192 }),
        };

        foreach (var candidate in candidates)
        {
            Assert.NotEqual(fixture.Config.CohortId, candidate.CohortId);
            var offline = new StaticHandler(
                new Dictionary<string, byte[]>(),
                failOnRequest: true);
            Assert.False(await InstallAsync(fixture, candidate, offline));
        }
    }

    private static Task<bool> InstallAsync(
        Fixture fixture,
        AgentReasoningConfig? config = null,
        HttpMessageHandler? handler = null,
        DateTimeOffset? verificationTime = null,
        Func<bool>? repairAuthorized = null,
        Func<string, long>? availableBytes = null) =>
        BrainInstaller.InstallAsync(
            config ?? fixture.Config,
            fixture.Data,
            percent: null,
            CancellationToken.None,
            handler ?? fixture.Handler,
            fixture.Keys,
            verificationTime ?? fixture.Now,
            repairAuthorized,
            availableBytes);

    private static string RepairReceipt(Fixture fixture) => Path.Combine(
        Path.GetDirectoryName(fixture.Config.GetBrainCohortRoot(fixture.Data))!,
        "repair-receipts",
        fixture.Config.BrainCohortId() + ".json");

    private sealed class Fixture : IDisposable
    {
        private const string ModelKeyId = "brain-model-installer-test-v1";
        private const string NativeKeyId = "brain-native-installer-test-v1";
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "suavo-brain-install-" + Guid.NewGuid().ToString("N"));
        private readonly ECDsa _modelKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly ECDsa _nativeKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public Fixture(byte[]? nativePackage = null)
        {
            Model = [10, 20, 30, 40, 50];
            NativeFile = [60, 70, 80, 90];
            NativePackage = nativePackage ?? OfficialNuGetPackage(NativeFile);
            var unsigned = new AgentReasoningConfig(
                Enabled: true,
                ModelId: "test-model",
                ModelUrl: "https://assets.example/model.gguf",
                ModelSha256: Sha(Model),
                ModelSizeBytes: Model.Length,
                NativeLibsUrl: "https://assets.example/native.nupkg",
                NativeLibsSha256: Sha(NativePackage),
                NativeLibsSizeBytes: NativePackage.Length,
                NativePackageKind: BrainNativePackageExtractor.OfficialNuGetPackageKind,
                ContextSize: 4096,
                MaxOutputTokens: 512,
                SchemaVersion: BrainCohortContract.SchemaVersion,
                CohortId: new string('0', 64),
                IssuedAtUtc: Utc(Now.AddHours(-1)),
                ExpiresAtUtc: Utc(Now.AddDays(1)),
                KeyId: string.Empty,
                Signature: string.Empty,
                ModelKeyId: ModelKeyId,
                ModelSignature: string.Empty,
                NativeKeyId: NativeKeyId,
                NativeSignature: string.Empty);
            Config = Sign(unsigned);
            Handler = new StaticHandler(new Dictionary<string, byte[]>
            {
                ["/model.gguf"] = Model,
                ["/native.nupkg"] = NativePackage,
            });
        }

        public string Data => Path.Combine(_root, "data");
        public DateTimeOffset Now { get; } =
            new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
        public byte[] Model { get; }
        public byte[] NativeFile { get; }
        public byte[] NativePackage { get; }
        public AgentReasoningConfig Config { get; }
        public StaticHandler Handler { get; }
        public IReadOnlyDictionary<string, string> Keys =>
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ModelKeyId] = Convert.ToBase64String(_modelKey.ExportSubjectPublicKeyInfo()),
                [NativeKeyId] = Convert.ToBase64String(_nativeKey.ExportSubjectPublicKeyInfo()),
            };

        public AgentReasoningConfig Sign(AgentReasoningConfig config)
        {
            var identified = config with
            {
                CohortId = BrainCohortContract.ComputeCohortId(config.PublisherManifest()),
                ModelSignature = string.Empty,
                NativeSignature = string.Empty,
            };
            var modelSignature = _modelKey.SignData(
                Encoding.UTF8.GetBytes(
                    BrainCohortContract.BuildModelCanonical(identified.PublisherManifest())),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            var nativeSignature = _nativeKey.SignData(
                Encoding.UTF8.GetBytes(
                    BrainCohortContract.BuildNativeCanonical(identified.PublisherManifest())),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return identified with
            {
                ModelSignature = Convert.ToHexString(modelSignature).ToLowerInvariant(),
                NativeSignature = Convert.ToHexString(nativeSignature).ToLowerInvariant(),
            };
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
            _modelKey.Dispose();
            _nativeKey.Dispose();
        }
    }

    private sealed class StaticHandler(
        IReadOnlyDictionary<string, byte[]> responses,
        bool failOnRequest = false) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            if (failOnRequest)
                throw new HttpRequestException("network must not be reached");
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (!responses.TryGetValue(path, out var body))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            });
        }
    }

    private static string Sha(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Utc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    private static byte[] Zip(params (string Name, byte[] Bytes)[] files)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Name, CompressionLevel.Fastest);
                using var target = entry.Open();
                target.Write(file.Bytes);
            }
        }
        return stream.ToArray();
    }

    private static byte[] OfficialNuGetPackage(byte[] llama)
    {
        var nuspec = Encoding.UTF8.GetBytes($$"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2011/08/nuspec.xsd">
              <metadata>
                <id>{{BrainNativePackageExtractor.PackageId}}</id>
                <version>{{BrainNativePackageExtractor.PackageVersion}}</version>
                <license type="expression">MIT</license>
              </metadata>
            </package>
            """);
        return Zip(
            ("LLamaSharp.Backend.Cpu.nuspec", nuspec),
            (".signature.p7s", [42, 43, 44]),
            (BrainNativePackageExtractor.NuGetPrefix + "llama.dll", llama),
            (BrainNativePackageExtractor.NuGetPrefix + "ggml.dll", [1, 2, 3]),
            (BrainNativePackageExtractor.NuGetPrefix + "ggml-base.dll", [4, 5, 6]),
            (BrainNativePackageExtractor.NuGetPrefix + "ggml-cpu.dll", [7, 8, 9]),
            (BrainNativePackageExtractor.NuGetPrefix + "llava_shared.dll", [10, 11, 12]),
            ("runtimes/win-x64/native/avx2/llama.dll", [99]),
            ("icon512.png", [98]));
    }

    private static byte[] ZipSymlink(string name, string target)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(name);
            entry.ExternalAttributes = (0xA000 | 0x1FF) << 16;
            using var output = entry.Open();
            using var writer = new StreamWriter(output);
            writer.Write(target);
        }
        return stream.ToArray();
    }
}
