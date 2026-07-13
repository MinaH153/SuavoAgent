using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Diagnostics.Maintenance;
using SuavoAgent.Setup.Maintenance;
using Xunit;

namespace SuavoAgent.Setup.Tests.Maintenance;

public sealed class NativeOtaCohortAssemblerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-ota-assembly-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _updateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Transition_or_full_manifest_assembles_exact_trusted_five_member_cohort(
        bool includeMaintenance)
    {
        var live = Path.Combine(_root, "ProgramFiles", "Agent");
        var data = Path.Combine(_root, "ProgramData", "SuavoAgent");
        var maintenanceRoot = Path.Combine(_root, "ProgramData", "SuavoAgent-Maintenance");
        var claimDirectory = Path.Combine(maintenanceRoot, "coordinator", new string('a', 64));
        var payload = Path.Combine(claimDirectory, "payload");
        Directory.CreateDirectory(live);
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(live, MaintenanceContract.ExecutableName), "old-maintenance");
        File.WriteAllText(
            Path.Combine(live, "appsettings.json"),
            JsonSerializer.Serialize(new
            {
                Agent = new
                {
                    AgentId = "agent-1",
                    MachineFingerprint = "fingerprint-1",
                    Version = "1.9.0",
                    ApiKey = "encrypted-secret-placeholder",
                },
            }));
        WriteReleaseReceipt(live, "old-maintenance");

        var bytes = new Dictionary<string, byte[]>
        {
            ["SuavoAgent.Core.exe"] = Encoding.UTF8.GetBytes("core-2"),
            ["SuavoAgent.Broker.exe"] = Encoding.UTF8.GetBytes("broker-2"),
            ["SuavoAgent.Helper.exe"] = Encoding.UTF8.GetBytes("helper-2"),
            ["SuavoAgent.Watchdog.exe"] = Encoding.UTF8.GetBytes("watchdog-2"),
            [MaintenanceContract.ExecutableName] = Encoding.UTF8.GetBytes("maintenance-2"),
        };
        var manifestText = BuildManifest(bytes, includeMaintenance);
        var signature = SignHex(manifestText);
        var manifest = UpdateActivationContract.ValidateManifest(
            manifestText,
            signature,
            PublicKey).Manifest!;
        foreach (var file in manifest.Files)
            File.WriteAllBytes(Path.Combine(payload, file.FileName), bytes[file.FileName]);
        var request = new UpdateActivationRequest(
            1,
            "update",
            "agent-1",
            "fingerprint-1",
            DateTimeOffset.UtcNow.ToString("O"),
            "nonce-1",
            "key-1",
            "signature",
            "{}",
            new string('b', 64),
            manifestText,
            signature,
            new string('a', 64),
            DateTimeOffset.UtcNow.ToString("O"));
        var validated = new ValidatedUpdateClaim(
            request,
            manifest,
            Encoding.UTF8.GetBytes("request"),
            new string('c', 64),
            payload);
        var claim = new DurableUpdateClaim(
            validated,
            claimDirectory,
            Path.Combine(claimDirectory, UpdateActivationContract.ActivationRequestFileName),
            payload,
            false);
        var assembler = new NativeOtaCohortAssembler(
            lockInstallDirectory: _ => { },
            verifyMaintenanceTrust: _ => new(
                true,
                MaintenanceTrustSource.SignedOtaManifest,
                "trusted"),
            verifyAuthenticode: _ => AuthenticodePublisherTrust.Trusted(
                AuthenticodePublisherVerifier.ExpectedPublisher),
            updatePublicKeyOverride: PublicKey);

        // Simulate a dead runner after assembly began but before the install
        // transaction journal existed. Resume must rebuild this exact
        // claim-derived stage instead of terminally rejecting the update.
        var expectedPreparation = NativeInstallCoordinator.CreatePreparation(
            live,
            data,
            maintenanceRoot,
            request.StagingId[..32]);
        Directory.CreateDirectory(expectedPreparation.StagingDirectory);
        File.WriteAllText(Path.Combine(expectedPreparation.StagingDirectory, "partial.bin"), "partial");
        Directory.CreateDirectory(Path.GetDirectoryName(expectedPreparation.PreparedManifestPath)!);
        File.WriteAllText(expectedPreparation.PreparedManifestPath, "partial-manifest");

        var result = assembler.Assemble(claim, live, data, maintenanceRoot);

        Assert.True(result.Succeeded, result.Code);
        var stage = result.Preparation!.StagingDirectory;
        Assert.False(File.Exists(Path.Combine(stage, "partial.bin")));
        Assert.Equal(
            BinaryDownloader.InstalledCohort.OrderBy(x => x),
            Directory.GetFiles(stage, "*.exe").Select(Path.GetFileName).OrderBy(x => x));
        Assert.Equal(
            includeMaintenance ? "maintenance-2" : "old-maintenance",
            File.ReadAllText(Path.Combine(stage, MaintenanceContract.ExecutableName)));
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(stage, "appsettings.json")));
        Assert.Equal("2.0.0", settings.RootElement.GetProperty("Agent").GetProperty("Version").GetString());
        Assert.False(settings.RootElement.GetProperty("Agent").TryGetProperty("ApiKey", out _));
        var cohort = MaintenanceCohortValidator.Validate(
            stage,
            result.Preparation.PreparedManifestPath,
            PublicKey,
            _ => AuthenticodePublisherTrust.Trusted(
                AuthenticodePublisherVerifier.ExpectedPublisher),
            _ => new(true, MaintenanceTrustSource.SignedOtaManifest, "trusted"));
        Assert.True(cohort.IsValid, cohort.Code);
    }

    private string PublicKey => Convert.ToBase64String(_updateKey.ExportSubjectPublicKeyInfo());

    private void WriteReleaseReceipt(string directory, string maintenanceBytes)
    {
        var hash = Hash(Encoding.UTF8.GetBytes(maintenanceBytes));
        var receipt = Encoding.UTF8.GetBytes(
            hash + "  " + MaintenanceContract.SignedSetupArtifactName + "\n");
        File.WriteAllBytes(
            Path.Combine(directory, MaintenanceContract.ReleaseChecksumsFileName),
            receipt);
        File.WriteAllBytes(
            Path.Combine(directory, MaintenanceContract.ReleaseChecksumsSignatureFileName),
            _updateKey.SignData(
                receipt,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence));
    }

    private string BuildManifest(IReadOnlyDictionary<string, byte[]> bytes, bool maintenance)
    {
        const string root = "https://github.com/SuavoLLC/MKM/releases/download/v2.0.0/";
        var value = $"{root}SuavoAgent.Core.exe|{Hash(bytes["SuavoAgent.Core.exe"])}|" +
                    $"{root}SuavoAgent.Broker.exe|{Hash(bytes["SuavoAgent.Broker.exe"])}|" +
                    $"{root}SuavoAgent.Helper.exe|{Hash(bytes["SuavoAgent.Helper.exe"])}|" +
                    $"2.0.0|net8.0|win-x64|" +
                    $"{root}SuavoAgent.Watchdog.exe|{Hash(bytes["SuavoAgent.Watchdog.exe"])}";
        return maintenance
            ? value + $"|{root}{MaintenanceContract.SignedSetupArtifactName}|{Hash(bytes[MaintenanceContract.ExecutableName])}"
            : value;
    }

    private string SignHex(string canonical) => Convert.ToHexString(
        _updateKey.SignData(
            Encoding.UTF8.GetBytes(canonical),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public void Dispose()
    {
        _updateKey.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }
}
