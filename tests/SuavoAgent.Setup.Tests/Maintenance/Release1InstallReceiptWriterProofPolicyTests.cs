using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Diagnostics.Maintenance;
using SuavoAgent.Setup.InstallerSupport;
using SuavoAgent.Setup.Maintenance;
using Xunit;

namespace SuavoAgent.Setup.Tests.Maintenance;

public sealed partial class Release1InstallReceiptWriterTests
{
    [Fact]
    public void Marker_settlement_refuses_pending_and_cleans_only_committed_tombstone()
    {
        var (_, _, proof) = CreateInstalledDirectories();
        var invocationId = new string('a', 64);
        using var proofLock = Release1MsiInstallMarkerTransaction.AcquireProofLock(
            proof);
        Release1MsiInstallMarkerTransaction.Prepare(proof, invocationId);

        Assert.Throws<InvalidDataException>(() =>
            Release1MsiInstallMarkerTransaction
                .RequireSettledForArmOrFinalization(proof));
        Assert.True(Release1MsiInstallMarkerTransaction.HasPendingJournal(proof));

        Release1MsiInstallMarkerTransaction.MarkCommitted(proof, invocationId);
        Release1MsiInstallMarkerTransaction
            .RequireSettledForArmOrFinalization(proof);

        Assert.False(Release1MsiInstallMarkerTransaction.HasPendingJournal(proof));
    }

    [Fact]
    public void Marker_settlement_requires_shared_proof_lock()
    {
        var (_, _, proof) = CreateInstalledDirectories();

        Assert.Throws<InvalidOperationException>(() =>
            Release1MsiInstallMarkerTransaction
                .RequireSettledForArmOrFinalization(proof));
    }

    [Fact]
    public void Legacy_marker_without_measured_installer_identity_fails_closed()
    {
        var (_, _, proof) = CreateInstalledDirectories();
        var path = Path.Combine(
            proof,
            Release1ConvergenceContract.MsiInstallCommitMarkerFileName);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            purpose = Release1ConvergenceContract.MsiInstallCommitMarkerPurpose,
            installedReleaseTag = "v4.0.0",
            maintenanceHostSha256 = new string('a', 64),
            installTransactionId = new string('b', 64),
            installCompletedAtUtc = "2026-07-15T20:00:00Z",
            bootTokenAtInstall = "windows-boot-id:71",
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Release1MsiInstallMarkerStore.ProtectProofDirectory(proof);

        Assert.Throws<InvalidDataException>(() =>
            Release1MsiInstallMarkerStore.Read(proof));
    }

    [Fact]
    public void Installer_proof_acl_excludes_core_and_every_interactive_principal()
    {
        var directory = Release1MsiInstallMarkerStore.ProofDirectoryPolicy(inherit: true);
        var file = Release1MsiInstallMarkerStore.ProofFilePolicy();

        Assert.Equal(HandleBoundAcl.SystemSid, directory.OwnerSid);
        Assert.Equal(
            [HandleBoundAcl.SystemSid, HandleBoundAcl.AdministratorsSid],
            directory.Aces.Select(ace => ace.Sid));
        Assert.All(directory.Aces, ace =>
        {
            Assert.Equal(FileSystemRights.FullControl, ace.Rights);
            Assert.Equal(
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                ace.InheritanceFlags);
        });
        Assert.Equal(
            [HandleBoundAcl.SystemSid, HandleBoundAcl.AdministratorsSid],
            file.Aces.Select(ace => ace.Sid));
        Assert.All(file.Aces, ace =>
        {
            Assert.Equal(FileSystemRights.FullControl, ace.Rights);
            Assert.Equal(InheritanceFlags.None, ace.InheritanceFlags);
        });
        Assert.DoesNotContain(directory.Aces, ace =>
            ace.Sid == CoreServiceIdentity.ServiceSid ||
            ace.Sid == HandleBoundAcl.UsersSid);
    }

    private (string Install, string Data, string Proof) CreateInstalledDirectories()
    {
        var install = Path.Combine(_root, "Program Files", "Suavo", "Agent");
        var data = Path.Combine(_root, "ProgramData", "SuavoAgent");
        var proof = Path.Combine(
            _root,
            "ProgramData",
            Release1ConvergenceContract.InstallProofRootDirectoryName);
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(proof);
        File.WriteAllBytes(
            Path.Combine(install, MaintenanceContract.ExecutableName),
            [1, 2, 3, 4]);
        File.WriteAllText(
            Path.Combine(install, MaintenanceContract.InstallStateFileName),
            "{\"version\":\"4.0.0\"}");
        return (install, data, proof);
    }

    private string CreateInstaller(
        string fileName = "SuavoAgent-v4.0.0-win-x64.msi")
    {
        var directory = Path.Combine(_root, "Installer Source");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, [9, 8, 7, 6, 5, 4]);
        return path;
    }

    private static string ParseInvocationId(string data)
    {
        if (!MsiInstallerInvocation.TryParse(data, out var invocation))
            throw new InvalidOperationException("Test MSI invocation data is invalid.");
        return invocation.InvocationId;
    }

    private static SignedReleaseCohortEvidence Evidence(
        string maintenanceKeyId,
        string maintenanceHostHash,
        string? msiArtifactSha256 = null) => new(
        ReleaseTag: "v4.0.0",
        SourceCommit: new string('d', 40),
        OtaSigningKeyId: OtaUpdateTrust.LegacyV1KeyId,
        MsiArtifactSha256: msiArtifactSha256 ?? new string('e', 64),
        ReleaseReceiptSha256: new string('f', 64),
        ChecksumsSha256: new string('c', 64),
        ChecksumsSignatureSha256: new string('8', 64),
        MaintenanceHostSha256: maintenanceHostHash,
        InstalledCohort: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SuavoAgent.Core.exe"] = new string('1', 64),
            ["SuavoAgent.Broker.exe"] = new string('2', 64),
            ["SuavoAgent.Helper.exe"] = new string('3', 64),
            ["SuavoAgent.Watchdog.exe"] = new string('4', 64),
            [MaintenanceContract.SignedSetupArtifactName] = maintenanceHostHash,
        });

    private static string MaintenanceHostHash(string installDirectory) =>
        Convert.ToHexString(SHA256.HashData(
            File.ReadAllBytes(Path.Combine(
                installDirectory,
                MaintenanceContract.ExecutableName))))
            .ToLowerInvariant();

    private static string FileSha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
            .ToLowerInvariant();

    private static AuthenticodePublisherTrust TrustInstaller(string _) =>
        AuthenticodePublisherTrust.Trusted(
            AuthenticodePublisherVerifier.ExpectedPublisher);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
