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

public sealed partial class Release1InstallReceiptWriterTests : IDisposable
{
    private const string ProductCode =
        "{A1111111-B222-C333-D444-E55555555555}";
    private static readonly string InvocationData =
        MsiInstallerInvocation.BuildForTests(
            ProductCode,
            "restart-manager-session-a",
            @"C:\rehearsal\SuavoAgent-v4.0.0-win-x64.msi");
    private static readonly string InvocationId = ParseInvocationId(InvocationData);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-release1-install-proof-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Msi_runner_accepts_only_fixed_commit_switch_on_windows_as_local_system()
    {
        var calls = 0;
        var originalDatabase = @"C:\rehearsal\SuavoAgent-v4.0.0-win-x64.msi";
        string? observedDatabase = null;
        string? observedProductCode = null;
        var accepted = MsiRelease1InstallMarkerRunner.Run(
            [
                MsiRelease1InstallMarkerRunner.Switch,
                InvocationData,
            ],
            isWindows: true,
            isLocalSystem: () => true,
            writeMarker: (database, productCode, installDirectory, invocationId) =>
            {
                calls++;
                observedDatabase = database;
                observedProductCode = productCode;
                Assert.Equal(InvocationId, invocationId);
                Assert.Equal(@"C:\Program Files\Suavo\Agent\", installDirectory);
            });
        var rejected = MsiRelease1InstallMarkerRunner.Run(
            [MsiRelease1InstallMarkerRunner.Switch, "unexpected"],
            isWindows: true,
            isLocalSystem: () => true,
            writeMarker: (_, _, _, _) => calls++);
        var extraSeparatorRejected = MsiRelease1InstallMarkerRunner.Run(
            [
                MsiRelease1InstallMarkerRunner.Switch,
                InvocationData + "|unexpected",
            ],
            isWindows: true,
            isLocalSystem: () => true,
            writeMarker: (_, _, _, _) => calls++);
        var unavailable = MsiRelease1InstallMarkerRunner.Run(
            [
                MsiRelease1InstallMarkerRunner.Switch,
                InvocationData,
            ],
            isWindows: true,
            isLocalSystem: () => true,
            writeMarker: (_, _, _, _) => throw new Release1BootIdentityUnavailableException(
                "stable boot identity unavailable"));

        Assert.Equal((int)MsiRelease1InstallMarkerExitCode.Success, accepted);
        Assert.Equal((int)MsiRelease1InstallMarkerExitCode.InvalidArguments, rejected);
        Assert.Equal(
            (int)MsiRelease1InstallMarkerExitCode.InvalidArguments,
            extraSeparatorRejected);
        Assert.Equal(
            (int)MsiRelease1InstallMarkerExitCode.BootIdentityUnavailable,
            unavailable);
        Assert.Equal(1, calls);
        Assert.Equal(originalDatabase, observedDatabase);
        Assert.Equal(ProductCode, observedProductCode);
        Assert.True(MsiRelease1InstallMarkerRunner.IsRequested(
            ["--MSI-WRITE-RELEASE-INSTALL-MARKER"]));
        Assert.False(MsiRelease1InstallMarkerRunner.IsRequested(
            ["--connect-installed"]));
    }

    [Fact]
    public void Msi_runner_accepts_only_fixed_system_transaction_modes()
    {
        var rollbackCalls = 0;
        var commitCalls = 0;
        var writeCalls = 0;

        var rollback = MsiRelease1InstallMarkerRunner.Run(
            [MsiRelease1InstallMarkerRunner.RollbackSwitch, InvocationData],
            isWindows: true,
            isLocalSystem: () => true,
            rollback: (installDirectory, id) =>
            {
                Assert.Equal(@"C:\Program Files\Suavo\Agent\", installDirectory);
                Assert.Equal(InvocationId, id);
                rollbackCalls++;
            },
            writeMarker: (_, _, _, _) => writeCalls++,
            commit: (installDirectory, id) =>
            {
                Assert.Equal(@"C:\Program Files\Suavo\Agent\", installDirectory);
                Assert.Equal(InvocationId, id);
                commitCalls++;
            });
        var commit = MsiRelease1InstallMarkerRunner.Run(
            [MsiRelease1InstallMarkerRunner.CommitSwitch, InvocationData],
            isWindows: true,
            isLocalSystem: () => true,
            rollback: (_, _) => rollbackCalls++,
            writeMarker: (_, _, _, _) => writeCalls++,
            commit: (_, _) => commitCalls++);
        var injected = MsiRelease1InstallMarkerRunner.Run(
            [MsiRelease1InstallMarkerRunner.RollbackSwitch, "unexpected"],
            isWindows: true,
            isLocalSystem: () => true,
            rollback: (_, _) => rollbackCalls++,
            writeMarker: (_, _, _, _) => writeCalls++,
            commit: (_, _) => commitCalls++);

        Assert.Equal((int)MsiRelease1InstallMarkerExitCode.Success, rollback);
        Assert.Equal((int)MsiRelease1InstallMarkerExitCode.Success, commit);
        Assert.Equal((int)MsiRelease1InstallMarkerExitCode.InvalidArguments, injected);
        Assert.Equal(1, rollbackCalls);
        Assert.Equal(1, commitCalls);
        Assert.Equal(0, writeCalls);
    }

    [Fact]
    public void Late_install_failure_removes_marker_created_by_this_transaction()
    {
        var (install, _, proof) = CreateInstalledDirectories();
        var installer = CreateInstaller();
        var invocationId = new string('a', 64);
        Release1MsiInstallMarkerTransaction.Prepare(proof, invocationId);
        var marker = Release1MsiInstallMarkerStore.Write(
            install,
            proof,
            DateTimeOffset.Parse("2026-07-15T20:00:00Z"),
            "windows-boot-id:71",
            invocationId,
            installer,
            ProductCode,
            TrustInstaller);
        Assert.True(File.Exists(marker));

        Release1MsiInstallMarkerTransaction.Rollback(proof, invocationId);

        Assert.False(File.Exists(marker));
        Assert.False(Release1MsiInstallMarkerTransaction.HasPendingJournal(proof));
    }

    [Fact]
    public void Locked_marker_path_forbids_recursive_acl_walk_but_exact_file_path_succeeds()
    {
        var (install, _, proof) = CreateInstalledDirectories();
        var installer = CreateInstaller();
        var invocationId = new string('f', 64);

        using (Release1MsiInstallMarkerTransaction.AcquireProofLock(proof))
        {
            Assert.True(
                Release1MsiInstallMarkerTransaction.IsProofLockHeldByCurrentContext);
            Assert.Throws<InvalidOperationException>(() =>
                Release1MsiInstallMarkerStore.ProtectProofDirectory(proof));
            Release1MsiInstallMarkerTransaction.Prepare(proof, invocationId);
            var marker = Release1MsiInstallMarkerStore.Write(
                install,
                proof,
                DateTimeOffset.Parse("2026-07-15T20:00:00Z"),
                "windows-boot-id:71",
                invocationId,
                installer,
                ProductCode,
                TrustInstaller,
                proofLockHeld: true);
            Assert.True(File.Exists(marker));
            Release1MsiInstallMarkerTransaction.Rollback(proof, invocationId);
        }

        Assert.False(
            Release1MsiInstallMarkerTransaction.IsProofLockHeldByCurrentContext);
    }

    [Fact]
    public void Late_install_failure_restores_exact_prior_valid_marker_bytes()
    {
        var (install, _, proof) = CreateInstalledDirectories();
        var installer = CreateInstaller();
        var marker = Release1MsiInstallMarkerStore.Write(
            install,
            proof,
            DateTimeOffset.Parse("2026-07-15T19:00:00Z"),
            "windows-boot-id:70",
            new string('b', 64),
            installer,
            ProductCode,
            TrustInstaller);
        var prior = File.ReadAllBytes(marker);
        var invocationId = new string('c', 64);
        Release1MsiInstallMarkerTransaction.Prepare(proof, invocationId);
        _ = Release1MsiInstallMarkerStore.Write(
            install,
            proof,
            DateTimeOffset.Parse("2026-07-15T20:00:00Z"),
            "windows-boot-id:71",
            invocationId,
            installer,
            ProductCode,
            TrustInstaller);

        Release1MsiInstallMarkerTransaction.Rollback(proof, invocationId);

        Assert.Equal(prior, File.ReadAllBytes(marker));
        Assert.Equal(
            new string('b', 64),
            Release1MsiInstallMarkerStore.Read(proof).InstallTransactionId);
        Assert.False(Release1MsiInstallMarkerTransaction.HasPendingJournal(proof));
    }

    [Fact]
    public void Successful_install_cleanup_keeps_new_marker_and_removes_rollback_journal()
    {
        var (install, _, proof) = CreateInstalledDirectories();
        var installer = CreateInstaller();
        var invocationId = new string('d', 64);
        Release1MsiInstallMarkerTransaction.Prepare(proof, invocationId);
        var marker = Release1MsiInstallMarkerStore.Write(
            install,
            proof,
            DateTimeOffset.Parse("2026-07-15T20:00:00Z"),
            "windows-boot-id:71",
            invocationId,
            installer,
            ProductCode,
            TrustInstaller);

        Release1MsiInstallMarkerTransaction.Commit(proof, invocationId);

        Assert.True(File.Exists(marker));
        Assert.Equal(
            new string('d', 64),
            Release1MsiInstallMarkerStore.Read(proof).InstallTransactionId);
        Assert.False(Release1MsiInstallMarkerTransaction.HasPendingJournal(proof));
    }

    [Fact]
    public void Later_invocation_cannot_replay_prior_pending_marker_snapshot()
    {
        var (install, _, proof) = CreateInstalledDirectories();
        var installer = CreateInstaller();
        var priorInvocation = new string('8', 64);
        var currentInvocation = new string('9', 64);
        Release1MsiInstallMarkerTransaction.Prepare(proof, priorInvocation);
        var marker = Release1MsiInstallMarkerStore.Write(
            install,
            proof,
            DateTimeOffset.Parse("2026-07-15T20:00:00Z"),
            "windows-boot-id:71",
            priorInvocation,
            installer,
            ProductCode,
            TrustInstaller);
        var bytes = File.ReadAllBytes(marker);

        Assert.Throws<InvalidDataException>(() =>
            Release1MsiInstallMarkerTransaction.Rollback(
                proof,
                currentInvocation));

        Assert.Equal(bytes, File.ReadAllBytes(marker));
        Assert.True(Release1MsiInstallMarkerTransaction.HasPendingJournal(proof));
    }

    [Fact]
    public void Committed_marker_tombstone_is_never_restored_and_next_forward_cleans_it()
    {
        var (install, _, proof) = CreateInstalledDirectories();
        var installer = CreateInstaller();
        var priorInvocation = new string('6', 64);
        var currentInvocation = new string('7', 64);
        Release1MsiInstallMarkerTransaction.Prepare(proof, priorInvocation);
        var marker = Release1MsiInstallMarkerStore.Write(
            install,
            proof,
            DateTimeOffset.Parse("2026-07-15T20:00:00Z"),
            "windows-boot-id:71",
            priorInvocation,
            installer,
            ProductCode,
            TrustInstaller);
        Release1MsiInstallMarkerTransaction.MarkCommitted(proof, priorInvocation);
        var committedBytes = File.ReadAllBytes(marker);

        Assert.Throws<InvalidDataException>(() =>
            Release1MsiInstallMarkerTransaction.Rollback(
                proof,
                priorInvocation));
        Assert.Equal(committedBytes, File.ReadAllBytes(marker));

        Release1MsiInstallMarkerTransaction.Prepare(proof, currentInvocation);
        Assert.Equal(committedBytes, File.ReadAllBytes(marker));
        Assert.True(Release1MsiInstallMarkerTransaction.HasPendingJournal(proof));
    }

    [Theory]
    [InlineData("marker")]
    [InlineData("service")]
    [InlineData("active")]
    public void Pending_msi_transaction_journal_cannot_mint_release_receipt(
        string pendingJournal)
    {
        var (install, data, proof) = CreateInstalledDirectories();
        const string fingerprint = "machine-guid-release1-test";
        const string bootToken = "windows-boot-id:71";
        var now = DateTimeOffset.Parse("2026-07-15T20:00:00Z");
        var installer = CreateInstaller();
        var marker = Release1MsiInstallMarkerStore.Write(
            install,
            proof,
            now,
            bootToken,
            new string('e', 64),
            installer,
            ProductCode,
            TrustInstaller);
        if (pendingJournal == "marker")
            Release1MsiInstallMarkerTransaction.Prepare(proof, new string('e', 64));
        else if (pendingJournal == "active")
            File.WriteAllBytes(
                Path.Combine(
                    install,
                    FileMsiInstallerTransactionActivation.FileName),
                [1]);
        else
            File.WriteAllBytes(
                Path.Combine(
                    install,
                    FileInstallerServiceHardeningJournal.FileName),
                [1]);

        using var keys = new InMemoryMaintenanceAttestationKeyProvider();
        var registration = keys.OpenOrCreate(fingerprint);
        var writer = new Release1InstallReceiptWriter(
            keys,
            (_, _) => SignedReleaseCohortValidation.Valid(Evidence(
                registration.Enrollment.KeyId,
                MaintenanceHostHash(install),
                FileSha256(installer))),
            () => now.AddMinutes(1),
            () => bootToken,
            () => proof);

        var exception = Assert.Throws<InvalidDataException>(() => writer.Write(
            install,
            data,
            "v4.0.0",
            fingerprint,
            registration.Enrollment.KeyId));

        Assert.Contains("durable cleanup", exception.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(marker));
        Assert.False(File.Exists(Path.Combine(
            data,
            Release1ConvergenceContract.InstallReceiptFileName)));
    }

    [Fact]
    public void Direct_admin_invocation_cannot_write_an_msi_commit_marker()
    {
        var calls = 0;
        var result = MsiRelease1InstallMarkerRunner.Run(
            [
                MsiRelease1InstallMarkerRunner.Switch,
                InvocationData,
            ],
            isWindows: true,
            isLocalSystem: () => false,
            writeMarker: (_, _, _, _) => calls++);

        Assert.Equal(
            (int)MsiRelease1InstallMarkerExitCode.UntrustedProcessIdentity,
            result);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Msi_runner_fails_closed_when_process_identity_cannot_be_resolved()
    {
        var calls = 0;
        var result = MsiRelease1InstallMarkerRunner.Run(
            [
                MsiRelease1InstallMarkerRunner.Switch,
                InvocationData,
            ],
            isWindows: true,
            isLocalSystem: () => throw new InvalidOperationException(
                "sensitive identity failure"),
            writeMarker: (_, _, _, _) => calls++);

        Assert.Equal(
            (int)MsiRelease1InstallMarkerExitCode.UntrustedProcessIdentity,
            result);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Direct_msi_commit_becomes_truthful_maintenance_signed_msi_receipt_once()
    {
        var (install, data, proof) = CreateInstalledDirectories();
        const string fingerprint = "machine-guid-release1-test";
        const string bootToken = "windows-boot-id:71";
        var now = DateTimeOffset.Parse("2026-07-15T20:00:00Z");
        var transactionId = new string('7', 64);
        var installer = CreateInstaller();
        var installerSha256 = FileSha256(installer);
        var markerPath = Release1MsiInstallMarkerStore.Write(
            install,
            proof,
            now,
            bootToken,
            transactionId,
            installer,
            ProductCode,
            TrustInstaller);
        var marker = Release1MsiInstallMarkerStore.Read(proof);
        Assert.Equal(
            Release1ConvergenceContract.MsiInstallCommitMarkerSchemaVersion,
            marker.SchemaVersion);
        Assert.Equal(installerSha256, marker.InstallerArtifactSha256);
        Assert.Equal(MaintenanceHostHash(install), marker.MaintenanceHostSha256);
        Assert.Equal(ProductCode, marker.ProductCode);
        using (var markerJson = JsonDocument.Parse(File.ReadAllBytes(markerPath)))
        {
            Assert.True(markerJson.RootElement.TryGetProperty(
                "installerArtifactSha256",
                out _));
            Assert.True(markerJson.RootElement.TryGetProperty("productCode", out _));
        }
        using var keys = new InMemoryMaintenanceAttestationKeyProvider();
        var registration = keys.OpenOrCreate(fingerprint);
        var evidence = Evidence(
            registration.Enrollment.KeyId,
            MaintenanceHostHash(install),
            installerSha256);
        var writer = new Release1InstallReceiptWriter(
            keys,
            (_, _) => SignedReleaseCohortValidation.Valid(evidence),
            () => now.AddMinutes(1),
            () => bootToken,
            () => proof);

        var result = writer.Write(
            install,
            data,
            "v4.0.0",
            fingerprint,
            registration.Enrollment.KeyId);

        Assert.True(result.Succeeded);
        Assert.True(result.Required);
        Assert.Equal("written", result.Code);
        Assert.False(File.Exists(markerPath));
        var path = Path.Combine(
            data,
            Release1ConvergenceContract.InstallReceiptFileName);
        Assert.True(File.Exists(path));
        var persisted = JsonSerializer.Deserialize<SignedRelease1InstallReceipt>(
            File.ReadAllBytes(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(persisted);
        Assert.Equal(transactionId, persisted.InstallReceipt.InstallTransactionId);
        Assert.Equal(
            Release1ConvergenceContract.MsiInstallerType,
            persisted.InstallReceipt.InstallerType);
        Assert.Equal(
            evidence.MsiArtifactSha256,
            persisted.InstallReceipt.InstallerArtifactSha256);
        Assert.Equal(
            Release1ConvergenceContract.BootIdFromToken(fingerprint, bootToken),
            persisted.InstallReceipt.BootIdAtInstall);
        Assert.Equal(86, persisted.InstallReceiptSignatureBase64Url.Length);
        Assert.Equal(
            registration.Enrollment.PublicKeySpki,
            persisted.MaintenancePublicKeySpkiDerBase64);

        var replay = writer.Write(
            install,
            data,
            "v4.0.0",
            fingerprint,
            registration.Enrollment.KeyId);
        Assert.True(replay.Succeeded);
        Assert.Equal("already_written", replay.Code);
    }

    [Fact]
    public async Task Arm_waits_until_receipt_finishes_signing_and_consumes_marker()
    {
        var (install, data, proof) = CreateInstalledDirectories();
        const string fingerprint = "machine-guid-release1-race-test";
        const string bootToken = "windows-boot-id:71";
        var now = DateTimeOffset.Parse("2026-07-15T20:00:00Z");
        var installer = CreateInstaller();
        var installerSha256 = FileSha256(installer);
        var markerPath = Release1MsiInstallMarkerStore.Write(
            install,
            proof,
            now,
            bootToken,
            new string('5', 64),
            installer,
            ProductCode,
            TrustInstaller);
        using var keys = new InMemoryMaintenanceAttestationKeyProvider();
        var registration = keys.OpenOrCreate(fingerprint);
        var evidence = Evidence(
            registration.Enrollment.KeyId,
            MaintenanceHostHash(install),
            installerSha256);
        using var checkPassed = new ManualResetEventSlim();
        using var armAttempted = new ManualResetEventSlim();
        using var armAcquired = new ManualResetEventSlim();
        var writer = new Release1InstallReceiptWriter(
            keys,
            (_, _) => SignedReleaseCohortValidation.Valid(evidence),
            () => now.AddMinutes(1),
            () => bootToken,
            () => proof,
            () =>
            {
                checkPassed.Set();
                Assert.True(armAttempted.Wait(TimeSpan.FromSeconds(5)));
                Thread.Sleep(100);
                Assert.False(armAcquired.IsSet);
                Assert.True(File.Exists(markerPath));
            });

        var receiptTask = Task.Run(() => writer.Write(
            install,
            data,
            "v4.0.0",
            fingerprint,
            registration.Enrollment.KeyId));
        Assert.True(checkPassed.Wait(TimeSpan.FromSeconds(5)));
        var activationPath = Path.Combine(
            install,
            FileMsiInstallerTransactionActivation.FileName);
        var armTask = Task.Run(() =>
        {
            armAttempted.Set();
            using var gate = Release1MsiInstallMarkerTransaction.AcquireProofLock(proof);
            armAcquired.Set();
            new FileMsiInstallerTransactionActivation(
                activationPath,
                static _ => { }).Arm(InvocationId);
        });

        var receipt = await receiptTask;
        await armTask;

        Assert.True(receipt.Succeeded);
        Assert.False(File.Exists(markerPath));
        Assert.True(armAcquired.IsSet);
        Assert.True(File.Exists(activationPath));
    }

    [Fact]
    public void Shortcut_launch_without_fresh_msi_marker_cannot_mint_receipt()
    {
        var (install, data, proof) = CreateInstalledDirectories();
        using var keys = new InMemoryMaintenanceAttestationKeyProvider();
        var registration = keys.OpenOrCreate("machine-guid-release1-test");
        var writer = new Release1InstallReceiptWriter(
            keys,
            (_, _) => SignedReleaseCohortValidation.Valid(Evidence(
                registration.Enrollment.KeyId,
                MaintenanceHostHash(install))),
            () => DateTimeOffset.Parse("2026-07-15T20:00:00Z"),
            () => "windows-boot-id:71",
            () => proof);

        Assert.Throws<InvalidDataException>(() => writer.Write(
            install,
            data,
            "v4.0.0",
            "machine-guid-release1-test",
            registration.Enrollment.KeyId));
        Assert.False(File.Exists(Path.Combine(
            data,
            Release1ConvergenceContract.InstallReceiptFileName)));
    }

    [Fact]
    public void Reboot_before_receipt_seal_is_rejected_and_marker_is_retained()
    {
        var (install, data, proof) = CreateInstalledDirectories();
        var now = DateTimeOffset.Parse("2026-07-15T20:00:00Z");
        var installer = CreateInstaller();
        var marker = Release1MsiInstallMarkerStore.Write(
            install,
            proof,
            now,
            "windows-boot-id:71",
            new string('7', 64),
            installer,
            ProductCode,
            TrustInstaller);
        using var keys = new InMemoryMaintenanceAttestationKeyProvider();
        var registration = keys.OpenOrCreate("machine-guid-release1-test");
        var writer = new Release1InstallReceiptWriter(
            keys,
            (_, _) => SignedReleaseCohortValidation.Valid(Evidence(
                registration.Enrollment.KeyId,
                MaintenanceHostHash(install),
                FileSha256(installer))),
            () => now.AddMinutes(1),
            () => "windows-boot-id:72",
            () => proof);

        var exception = Assert.Throws<InvalidDataException>(() => writer.Write(
            install,
            data,
            "v4.0.0",
            "machine-guid-release1-test",
            registration.Enrollment.KeyId));

        Assert.Contains("rebooted", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(marker));
        Assert.False(File.Exists(Path.Combine(
            data,
            Release1ConvergenceContract.InstallReceiptFileName)));
    }

    [Fact]
    public void Core_writable_runtime_marker_cannot_substitute_for_protected_msi_proof()
    {
        var (install, data, proof) = CreateInstalledDirectories();
        const string fingerprint = "machine-guid-release1-test";
        const string bootToken = "windows-boot-id:71";
        var now = DateTimeOffset.Parse("2026-07-15T20:00:00Z");
        var installer = CreateInstaller();
        var forgedRuntimeMarker = Release1MsiInstallMarkerStore.Write(
            install,
            data,
            now,
            bootToken,
            new string('6', 64),
            installer,
            ProductCode,
            TrustInstaller);
        using var keys = new InMemoryMaintenanceAttestationKeyProvider();
        var registration = keys.OpenOrCreate(fingerprint);
        var writer = new Release1InstallReceiptWriter(
            keys,
            (_, _) => SignedReleaseCohortValidation.Valid(Evidence(
                registration.Enrollment.KeyId,
                MaintenanceHostHash(install),
                FileSha256(installer))),
            () => now.AddMinutes(1),
            () => bootToken,
            () => proof);

        Assert.Throws<InvalidDataException>(() => writer.Write(
            install,
            data,
            "v4.0.0",
            fingerprint,
            registration.Enrollment.KeyId));
        Assert.True(File.Exists(forgedRuntimeMarker));
        Assert.False(File.Exists(Path.Combine(
            data,
            Release1ConvergenceContract.InstallReceiptFileName)));
    }

    [Fact]
    public void Measured_msi_hash_must_match_signed_canonical_msi_before_receipt_signing()
    {
        var (install, data, proof) = CreateInstalledDirectories();
        const string fingerprint = "machine-guid-release1-test";
        const string bootToken = "windows-boot-id:71";
        var now = DateTimeOffset.Parse("2026-07-15T20:00:00Z");
        var installer = CreateInstaller();
        var marker = Release1MsiInstallMarkerStore.Write(
            install,
            proof,
            now,
            bootToken,
            new string('5', 64),
            installer,
            ProductCode,
            TrustInstaller);
        using var keys = new InMemoryMaintenanceAttestationKeyProvider();
        var registration = keys.OpenOrCreate(fingerprint);
        var writer = new Release1InstallReceiptWriter(
            keys,
            (_, _) => SignedReleaseCohortValidation.Valid(Evidence(
                registration.Enrollment.KeyId,
                MaintenanceHostHash(install),
                new string('e', 64))),
            () => now.AddMinutes(1),
            () => bootToken,
            () => proof);

        var exception = Assert.Throws<InvalidDataException>(() => writer.Write(
            install,
            data,
            "v4.0.0",
            fingerprint,
            registration.Enrollment.KeyId));

        Assert.Contains("does not bind", exception.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(marker));
        Assert.False(File.Exists(Path.Combine(
            data,
            Release1ConvergenceContract.InstallReceiptFileName)));
    }

    [Fact]
    public void Commit_marker_requires_a_regular_local_msi_from_the_approved_publisher()
    {
        var (install, _, proof) = CreateInstalledDirectories();
        var now = DateTimeOffset.Parse("2026-07-15T20:00:00Z");
        var nonMsi = CreateInstaller("SuavoAgent-v4.0.0-win-x64.exe");

        Assert.Throws<InvalidDataException>(() =>
            Release1MsiInstallMarkerStore.Write(
                install,
                proof,
                now,
                "windows-boot-id:71",
                new string('4', 64),
                nonMsi,
                ProductCode,
                TrustInstaller));

        var directoryMsi = Path.Combine(_root, "directory.msi");
        Directory.CreateDirectory(directoryMsi);
        Assert.Throws<InvalidDataException>(() =>
            Release1MsiInstallMarkerStore.Write(
                install,
                proof,
                now,
                "windows-boot-id:71",
                new string('4', 64),
                directoryMsi,
                ProductCode,
                TrustInstaller));

        var installer = CreateInstaller();
        Assert.Throws<InvalidDataException>(() =>
            Release1MsiInstallMarkerStore.Write(
                install,
                proof,
                now,
                "windows-boot-id:71",
                new string('4', 64),
                Path.GetFileName(installer),
                ProductCode,
                TrustInstaller));
        Assert.Throws<InvalidDataException>(() =>
            Release1MsiInstallMarkerStore.Write(
                install,
                proof,
                now,
                "windows-boot-id:71",
                new string('4', 64),
                installer,
                "not-a-product-code",
                TrustInstaller));
        Assert.Throws<InvalidDataException>(() =>
            Release1MsiInstallMarkerStore.Write(
                install,
                proof,
                now,
                "windows-boot-id:71",
                new string('4', 64),
                installer,
                ProductCode,
                _ => AuthenticodePublisherTrust.Trusted("UNAPPROVED PUBLISHER")));
        Assert.False(Release1MsiInstallMarkerStore.Exists(proof));
    }

}
