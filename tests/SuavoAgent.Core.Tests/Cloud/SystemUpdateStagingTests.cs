using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Core.Cloud;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public sealed class SystemUpdateStagingTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 19, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-system-stage-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _commandKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _updateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public SystemUpdateStagingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _commandKey.Dispose();
        _updateKey.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Theory]
    [InlineData(false, 4)]
    [InlineData(true, 5)]
    public async Task Stage_ValidSignedCommand_WritesOnlyIncomingAreaAndAtomicRequest(
        bool includeMaintenance,
        int expectedFiles)
    {
        var fixture = CreateFixture(includeMaintenance);
        var installDir = Path.Combine(_root, "program-files-readonly-sentinel");
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, "SuavoAgent.Core.exe"), "installed-core");
        var updateRoot = Path.Combine(_root, "program-data", "updates");

        var result = await SelfUpdater.TryStagePackageUpdateAsync(
            fixture.Manifest,
            fixture.ManifestSignature,
            fixture.Command,
            fixture.DataJson,
            updateRoot,
            (url, path, ct) => File.WriteAllBytesAsync(path, fixture.BytesByUrl[url], ct),
            NullLogger.Instance,
            CancellationToken.None,
            Now,
            fixture.CommandKeys,
            fixture.UpdatePublicKey);

        Assert.True(result);
        Assert.Equal("installed-core", File.ReadAllText(Path.Combine(installDir, "SuavoAgent.Core.exe")));
        Assert.Empty(Directory.GetFiles(installDir, "*.new", SearchOption.AllDirectories));

        var requestPath = Path.Combine(updateRoot, UpdateActivationContract.ActivationRequestFileName);
        Assert.True(File.Exists(requestPath));
        Assert.True(UpdateActivationContract.TryDeserialize(
            File.ReadAllText(requestPath), out var request, out var code), code);
        var validation = UpdateActivationContract.Validate(
            request!, fixture.CommandKeys, fixture.UpdatePublicKey, Now,
            fixture.Command.AgentId, fixture.Command.MachineFingerprint);
        Assert.True(validation.IsValid, validation.Code);

        var staging = UpdateActivationContract.GetIncomingStagingDirectory(updateRoot, request!.StagingId);
        Assert.Equal(expectedFiles, Directory.GetFiles(staging).Length);
        Assert.DoesNotContain(Directory.GetFiles(staging), path => path.EndsWith(".partial", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stage_HashMismatch_DeletesIncomingCohortAndPublishesNoRequest()
    {
        var fixture = CreateFixture(includeMaintenance: true);
        var updateRoot = Path.Combine(_root, "updates");

        var result = await SelfUpdater.TryStagePackageUpdateAsync(
            fixture.Manifest,
            fixture.ManifestSignature,
            fixture.Command,
            fixture.DataJson,
            updateRoot,
            (url, path, ct) => File.WriteAllTextAsync(path, "tampered", ct),
            NullLogger.Instance,
            CancellationToken.None,
            Now,
            fixture.CommandKeys,
            fixture.UpdatePublicKey);

        Assert.False(result);
        Assert.False(File.Exists(Path.Combine(updateRoot, UpdateActivationContract.ActivationRequestFileName)));
        var incoming = Path.Combine(updateRoot, UpdateActivationContract.IncomingDirectoryName);
        Assert.True(!Directory.Exists(incoming) || Directory.GetDirectories(incoming).Length == 0);
    }

    [Fact]
    public async Task Stage_ExistingRequest_IsNeverOverwrittenOrDownloadedAgain()
    {
        var fixture = CreateFixture(includeMaintenance: false);
        var updateRoot = Path.Combine(_root, "updates");
        Directory.CreateDirectory(updateRoot);
        var requestPath = Path.Combine(updateRoot, UpdateActivationContract.ActivationRequestFileName);
        File.WriteAllText(requestPath, "existing-request");
        var downloadCalls = 0;

        var result = await SelfUpdater.TryStagePackageUpdateAsync(
            fixture.Manifest,
            fixture.ManifestSignature,
            fixture.Command,
            fixture.DataJson,
            updateRoot,
            (_, _, _) => { downloadCalls++; return Task.CompletedTask; },
            NullLogger.Instance,
            CancellationToken.None,
            Now,
            fixture.CommandKeys,
            fixture.UpdatePublicKey);

        Assert.False(result);
        Assert.Equal(0, downloadCalls);
        Assert.Equal("existing-request", File.ReadAllText(requestPath));
    }

    [Fact]
    public async Task Stage_NineFieldLegacyManifest_IsRejectedBeforeDownloaderRuns()
    {
        var fixture = CreateFixture(includeMaintenance: false, legacyNineField: true);
        var downloadCalls = 0;

        var result = await SelfUpdater.TryStagePackageUpdateAsync(
            fixture.Manifest,
            fixture.ManifestSignature,
            fixture.Command,
            fixture.DataJson,
            Path.Combine(_root, "updates"),
            (_, _, _) => { downloadCalls++; return Task.CompletedTask; },
            NullLogger.Instance,
            CancellationToken.None,
            Now,
            fixture.CommandKeys,
            fixture.UpdatePublicKey);

        Assert.False(result);
        Assert.Equal(0, downloadCalls);
    }

    private Fixture CreateFixture(bool includeMaintenance, bool legacyNineField = false)
    {
        var bytes = new Dictionary<string, byte[]>
        {
            ["SuavoAgent.Core.exe"] = Encoding.UTF8.GetBytes("new-core"),
            ["SuavoAgent.Broker.exe"] = Encoding.UTF8.GetBytes("new-broker"),
            ["SuavoAgent.Helper.exe"] = Encoding.UTF8.GetBytes("new-helper"),
            ["SuavoAgent.Watchdog.exe"] = Encoding.UTF8.GetBytes("new-watchdog"),
            [MaintenanceContract.SignedSetupArtifactName] = Encoding.UTF8.GetBytes("new-maintenance"),
        };
        const string root = "https://github.com/SuavoLLC/MKM/releases/download/v2.0.0/";
        var manifest = new UpdateManifest(
            root + "SuavoAgent.Core.exe", Hash(bytes["SuavoAgent.Core.exe"]),
            root + "SuavoAgent.Broker.exe", Hash(bytes["SuavoAgent.Broker.exe"]),
            root + "SuavoAgent.Helper.exe", Hash(bytes["SuavoAgent.Helper.exe"]),
            "v2.0.0", "net8.0", "win-x64",
            legacyNineField ? null : root + "SuavoAgent.Watchdog.exe",
            legacyNineField ? null : Hash(bytes["SuavoAgent.Watchdog.exe"]),
            includeMaintenance ? root + MaintenanceContract.SignedSetupArtifactName : null,
            includeMaintenance ? Hash(bytes[MaintenanceContract.SignedSetupArtifactName]) : null);
        var canonical = manifest.ToCanonical();
        var manifestSignature = SignHex(_updateKey, canonical);
        var dataJson = JsonSerializer.Serialize(new
        {
            manifest = canonical,
            manifestSignature,
            channel = "stable",
        });
        var dataHash = RemoteCommandTrust.ComputeSha256Hex(dataJson);
        var command = new SignedCommand(
            UpdateActivationContract.CommandName,
            "agent-0001",
            "fingerprint-0001",
            Now.ToString("O"),
            "nonce-0001",
            "test-command-key",
            "",
            dataHash);
        command = command with
        {
            Signature = SignBase64(
                _commandKey,
                RemoteCommandTrust.BuildCommandCanonical(
                    command.Command,
                    command.AgentId,
                    command.MachineFingerprint,
                    command.Timestamp,
                    command.Nonce,
                    command.DataHash)),
        };
        var bytesByUrl = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (name, payload) in bytes)
            bytesByUrl[root + name] = payload;

        return new Fixture(
            manifest,
            manifestSignature,
            command,
            dataJson,
            new Dictionary<string, string>
            {
                [command.KeyId] = Convert.ToBase64String(_commandKey.ExportSubjectPublicKeyInfo()),
            },
            Convert.ToBase64String(_updateKey.ExportSubjectPublicKeyInfo()),
            bytesByUrl);
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string SignHex(ECDsa key, string canonical) => Convert.ToHexString(
        key.SignData(
            Encoding.UTF8.GetBytes(canonical),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    private static string SignBase64(ECDsa key, string canonical) => Convert.ToBase64String(
        key.SignData(
            Encoding.UTF8.GetBytes(canonical),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    private sealed record Fixture(
        UpdateManifest Manifest,
        string ManifestSignature,
        SignedCommand Command,
        string DataJson,
        IReadOnlyDictionary<string, string> CommandKeys,
        string UpdatePublicKey,
        IReadOnlyDictionary<string, byte[]> BytesByUrl);
}
