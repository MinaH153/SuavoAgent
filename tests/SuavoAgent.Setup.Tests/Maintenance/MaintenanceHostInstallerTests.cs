using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Diagnostics.Maintenance;
using SuavoAgent.Setup.Maintenance;
using Xunit;

namespace SuavoAgent.Setup.Tests.Maintenance;

public sealed class MaintenanceHostInstallerTests
{
    [Fact]
    public void Stage_copies_exact_setup_bytes_under_fixed_name()
    {
        using var fixture = new MaintenanceFixture();
        var source = Path.Combine(fixture.Root, "SuavoSetup-token.exe");
        File.WriteAllBytes(source, [1, 3, 3, 7, 9]);

        var staged = MaintenanceHostInstaller.Stage(source, fixture.InstallDir);

        Assert.Equal(
            Path.Combine(fixture.InstallDir, MaintenanceContract.ExecutableName),
            staged.Path);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(staged.Path));
        Assert.Equal(
            MaintenanceHostInstaller.ComputeSha256(source),
            staged.Sha256);
        Assert.Empty(Directory.GetFiles(fixture.InstallDir, "*.staging-*"));
    }

    [Fact]
    public void WriteInstallState_binds_fixed_host_and_immutable_service_cohort()
    {
        using var fixture = MaintenanceFixture.CreateValid();
        var installedAt = DateTimeOffset.Parse("2026-07-10T12:00:00Z");

        var statePath = MaintenanceHostInstaller.WriteInstallState(
            fixture.InstallDir,
            fixture.ManifestPath,
            "v3.92.1",
            installedAt);

        using var document = JsonDocument.Parse(File.ReadAllText(statePath));
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("native-maintenance-bridge", root.GetProperty("installerKind").GetString());
        Assert.Equal("3.92.1", root.GetProperty("version").GetString());
        Assert.Equal(MaintenanceContract.ExecutableName, root.GetProperty("maintenanceExecutable").GetString());
        Assert.Equal(
            BinaryDownloader.InstalledCohort,
            root.GetProperty("installedCohort").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Equal(installedAt, root.GetProperty("installedAtUtc").GetDateTimeOffset());
    }

    [Fact]
    public void Validator_rejects_tampered_maintenance_host()
    {
        using var fixture = MaintenanceFixture.CreateValid(writeState: true);
        File.AppendAllText(
            Path.Combine(fixture.InstallDir, MaintenanceContract.ExecutableName),
            "tampered");

        var result = MaintenanceCohortValidator.Validate(
            fixture.InstallDir,
            fixture.ManifestPath,
            fixture.PublicKeyDer,
            _ => AuthenticodePublisherTrust.Trusted(AuthenticodePublisherVerifier.ExpectedPublisher),
            _ => new(true, MaintenanceTrustSource.SignedReleaseChecksums, "trusted"));

        Assert.False(result.IsValid);
        Assert.Equal(
            "binary_hash_mismatch:" + MaintenanceContract.ExecutableName,
            result.Code);
    }

    [Fact]
    public void Validator_rejects_missing_or_hash_mismatched_service_binary()
    {
        using var fixture = MaintenanceFixture.CreateValid(writeState: true);
        File.AppendAllText(Path.Combine(fixture.InstallDir, "SuavoAgent.Core.exe"), "tampered");

        var result = MaintenanceCohortValidator.Validate(
            fixture.InstallDir,
            fixture.ManifestPath,
            fixture.PublicKeyDer,
            _ => AuthenticodePublisherTrust.Trusted(AuthenticodePublisherVerifier.ExpectedPublisher),
            _ => new(true, MaintenanceTrustSource.SignedReleaseChecksums, "trusted"));

        Assert.False(result.IsValid);
        Assert.Equal("binary_hash_mismatch:SuavoAgent.Core.exe", result.Code);
    }

    [Theory]
    [InlineData("Gui/Services/InstallOrchestrator.cs")]
    public void Every_install_path_stages_host_then_manifest_and_state_before_services(string relativePath)
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "SuavoAgent.Setup", relativePath));
        if (!File.Exists(sourcePath)) return;

        var source = File.ReadAllText(sourcePath);
        var stage = source.IndexOf("MaintenanceHostInstaller.StageCurrentProcess", StringComparison.Ordinal);
        var seal = source.IndexOf("NativeInstallCoordinator.SealPreparedCohort", StringComparison.Ordinal);
        var services = source.IndexOf("nativeCoordinator.Execute", StringComparison.Ordinal);

        Assert.True(stage >= 0 && seal > stage && services > seal,
            $"{relativePath} must stage -> seal complete cohort -> native transaction");

        var coordinatorPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "SuavoAgent.Setup", "Maintenance", "NativeInstallCoordinator.cs"));
        if (!File.Exists(coordinatorPath)) return;
        var coordinator = File.ReadAllText(coordinatorPath);
        var manifest = coordinator.IndexOf("BinaryDownloader.WriteBinariesManifest", StringComparison.Ordinal);
        var state = coordinator.IndexOf("MaintenanceHostInstaller.WriteInstallState", StringComparison.Ordinal);
        Assert.True(manifest >= 0 && state > manifest,
            "NativeInstallCoordinator must write manifest before install-state");
    }
}

internal sealed class MaintenanceFixture : IDisposable
{
    private readonly ECDsa _updateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public string Root { get; } = Path.Combine(
        Path.GetTempPath(),
        "suavo-maintenance-tests-" + Guid.NewGuid().ToString("N"));
    public string InstallDir => Path.Combine(Root, "install");
    public string DataDir => Path.Combine(Root, "data");
    public string ManifestPath => Path.Combine(DataDir, "binaries.manifest");
    public string PublicKeyDer => Convert.ToBase64String(_updateKey.ExportSubjectPublicKeyInfo());

    public MaintenanceFixture()
    {
        Directory.CreateDirectory(InstallDir);
        Directory.CreateDirectory(DataDir);
    }

    public static MaintenanceFixture CreateValid(bool writeState = false)
    {
        var fixture = new MaintenanceFixture();
        var source = Path.Combine(fixture.Root, "SuavoSetup.exe");
        File.WriteAllText(source, "signed-setup-bytes");
        MaintenanceHostInstaller.Stage(source, fixture.InstallDir);

        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binary in BinaryDownloader.RequiredBinaries)
        {
            var path = Path.Combine(fixture.InstallDir, binary);
            File.WriteAllText(path, "bytes-for-" + binary);
        }
        foreach (var binary in BinaryDownloader.InstalledCohort)
            entries[binary] = MaintenanceHostInstaller.ComputeSha256(Path.Combine(fixture.InstallDir, binary));
        File.WriteAllText(fixture.ManifestPath, JsonSerializer.Serialize(entries));
        fixture.WriteReleaseTrustReceipt();
        if (writeState)
        {
            MaintenanceHostInstaller.WriteInstallState(
                fixture.InstallDir,
                fixture.ManifestPath,
                "v3.92.1");
        }
        return fixture;
    }

    private void WriteReleaseTrustReceipt()
    {
        var checksums = Encoding.UTF8.GetBytes(
            $"{MaintenanceHostInstaller.ComputeSha256(Path.Combine(InstallDir, MaintenanceContract.ExecutableName))}  {MaintenanceContract.SignedSetupArtifactName}\n");
        File.WriteAllBytes(
            Path.Combine(InstallDir, MaintenanceContract.ReleaseChecksumsFileName),
            checksums);
        File.WriteAllBytes(
            Path.Combine(InstallDir, MaintenanceContract.ReleaseChecksumsSignatureFileName),
            _updateKey.SignData(
                checksums,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence));
    }

    public void Dispose()
    {
        _updateKey.Dispose();
        try { Directory.Delete(Root, recursive: true); } catch { }
    }
}
