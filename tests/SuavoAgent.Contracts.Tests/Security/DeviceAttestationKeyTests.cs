using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using SuavoAgent.Contracts.Security;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Security;

public sealed class DeviceAttestationKeyTests
{
    [Fact]
    public void InMemoryFake_ProducesEs256P1363Signature()
    {
        using var provider = new InMemoryDeviceAttestationKeyProvider();
        using var pending = provider.OpenOrCreate("test-fingerprint");
        provider.CommitPending("test-fingerprint", pending.Enrollment.KeyId);
        using var key = provider.OpenExisting("test-fingerprint");
        var bytes = Encoding.UTF8.GetBytes("suavo.device-test.v1");
        var signature = key.Sign(bytes);
        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(
            Convert.FromBase64String(key.Enrollment.PublicKeySpki), out _);
        Assert.Equal(64, signature.Length);
        Assert.True(verifier.VerifyData(
            bytes,
            signature,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public void Maintenance_signature_uses_distinct_enrollment_and_rejects_device_key_alias()
    {
        const string fingerprint = "maintenance-signature";
        using var deviceProvider = new InMemoryDeviceAttestationKeyProvider();
        using var pending = deviceProvider.OpenOrCreate(fingerprint);
        deviceProvider.CommitPending(fingerprint, pending.Enrollment.KeyId);
        using var provider = new InMemoryMaintenanceAttestationKeyProvider();
        var maintenance = provider.OpenOrCreate(fingerprint);
        var canonical = "suavo.self-uninstall-completion.test"u8.ToArray();

        var signed = provider.Sign(
            fingerprint,
            maintenance.Enrollment.KeyId,
            canonical);

        Assert.NotEqual(pending.Enrollment.KeyId, maintenance.Enrollment.KeyId);
        Assert.Equal(maintenance.Enrollment, signed.Enrollment);
        Assert.True(MaintenanceAttestationKeyProvider.VerifyPossessionProof(
            maintenance,
            fingerprint));
        Assert.Equal(64, signed.Signature.Length);
        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(
            Convert.FromBase64String(signed.Enrollment.PublicKeySpki), out _);
        Assert.True(verifier.VerifyData(
            canonical,
            signed.Signature.Span,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Assert.Throws<InvalidOperationException>(() =>
            provider.Sign(fingerprint, pending.Enrollment.KeyId, canonical));
    }

    [Fact]
    public void Maintenance_enrollment_proof_is_bound_to_fingerprint_and_exact_public_key()
    {
        using var provider = new InMemoryMaintenanceAttestationKeyProvider();
        var registration = provider.OpenOrCreate("machine-a");

        Assert.True(MaintenanceAttestationKeyProvider.VerifyPossessionProof(
            registration,
            "machine-a"));
        Assert.False(MaintenanceAttestationKeyProvider.VerifyPossessionProof(
            registration,
            "machine-b"));
        Assert.False(MaintenanceAttestationKeyProvider.VerifyPossessionProof(
            registration with { PossessionProof = new string('A', 86) },
            "machine-a"));
    }

    [Fact]
    public void CanceledRepair_DeletesPendingButLeavesActiveKeyUntouched()
    {
        using var provider = new InMemoryDeviceAttestationKeyProvider();
        using var firstPending = provider.OpenOrCreate("repair-cancel");
        provider.CommitPending("repair-cancel", firstPending.Enrollment.KeyId);
        using var runningCore = provider.OpenExisting("repair-cancel");

        using var repairPending = provider.OpenOrCreate("repair-cancel");
        Assert.NotEqual(runningCore.Enrollment.KeyId, repairPending.Enrollment.KeyId);
        provider.AbortPending("repair-cancel", repairPending.Enrollment.KeyId);

        using var afterCancel = provider.OpenExisting("repair-cancel");
        Assert.Equal(runningCore.Enrollment.KeyId, afterCancel.Enrollment.KeyId);
        Assert.NotEmpty(runningCore.Sign("still-active"u8));
    }

    [Fact]
    public void RegistrationResponseLoss_ReusesExactPendingKey()
    {
        using var provider = new InMemoryDeviceAttestationKeyProvider();
        using var firstAttempt = provider.OpenOrCreate("response-loss");
        using var retryAttempt = provider.OpenOrCreate("response-loss");

        Assert.Equal(firstAttempt.Enrollment, retryAttempt.Enrollment);
        Assert.Throws<InvalidOperationException>(() => firstAttempt.Sign("setup-cannot-sign"u8));
    }

    [Fact]
    public void VersionStateCanBeProvedWithoutOpeningPrivateSigningMaterial()
    {
        const string fingerprint = "version-state";
        using var provider = new InMemoryDeviceAttestationKeyProvider();
        using var pending = provider.OpenOrCreate(fingerprint);

        Assert.True(provider.IsPendingVersion(
            fingerprint,
            pending.LocalKeyName,
            pending.Enrollment.KeyId));
        Assert.False(provider.IsActiveVersion(
            fingerprint,
            pending.LocalKeyName,
            pending.Enrollment.KeyId));

        provider.CommitPending(fingerprint, pending.Enrollment.KeyId);

        Assert.False(provider.IsPendingVersion(
            fingerprint,
            pending.LocalKeyName,
            pending.Enrollment.KeyId));
        Assert.True(provider.IsActiveVersion(
            fingerprint,
            pending.LocalKeyName,
            pending.Enrollment.KeyId));
    }

    [Fact]
    public void Cutover_IsVersionedAndRunningCoreFailsClosedAfterPointerSwitch()
    {
        using var provider = new InMemoryDeviceAttestationKeyProvider();
        using var firstPending = provider.OpenOrCreate("versioned-cutover");
        provider.CommitPending("versioned-cutover", firstPending.Enrollment.KeyId);
        using var runningCore = provider.OpenExisting("versioned-cutover");

        using var nextPending = provider.OpenOrCreate("versioned-cutover");
        using var restartedBeforeCutover = provider.OpenExisting("versioned-cutover");
        Assert.Equal(runningCore.Enrollment.KeyId, restartedBeforeCutover.Enrollment.KeyId);

        provider.CommitPending("versioned-cutover", nextPending.Enrollment.KeyId);

        Assert.Throws<InvalidOperationException>(() => runningCore.Sign("retired"u8));
        using var restartedAfterCutover = provider.OpenExisting("versioned-cutover");
        Assert.Equal(nextPending.Enrollment.KeyId, restartedAfterCutover.Enrollment.KeyId);
        Assert.NotEqual(runningCore.Enrollment.KeyId, restartedAfterCutover.Enrollment.KeyId);
        Assert.NotEmpty(restartedAfterCutover.Sign("active"u8));
    }

    [Fact]
    public void ProductionKey_InteractiveCallerCannotReopenAsSigner()
    {
        if (!OperatingSystem.IsWindows()) return;
        var provider = DeviceAttestationKeyProvider.CreateProduction();
        using (var created = provider.OpenOrCreate("suavo-device-acl-contract-test"))
        {
            Assert.Equal("ES256", created.Enrollment.Algorithm);
            Assert.Equal(64, created.Enrollment.KeyId.Length);
            Assert.Throws<InvalidOperationException>(() => created.Sign("setup"u8));
            Assert.True(provider.IsPendingVersion(
                "suavo-device-acl-contract-test",
                created.LocalKeyName,
                created.Enrollment.KeyId));
            Assert.NotNull(Record.Exception(() => provider.OpenVersion(
                "suavo-device-acl-contract-test",
                created.LocalKeyName,
                created.Enrollment.KeyId)));
            AssertPointerAclRejectsUnprivilegedWriters("suavo-device-acl-contract-test");
            provider.CommitPending(
                "suavo-device-acl-contract-test",
                created.Enrollment.KeyId);
            Assert.True(provider.IsActiveVersion(
                "suavo-device-acl-contract-test",
                created.LocalKeyName,
                created.Enrollment.KeyId));
        }

        // The persisted DACL gives GENERIC_ALL only to the exact Core service
        // SID. SYSTEM/Administrators have DELETE+WRITE_DAC for approved repair,
        // not key-use rights, so an interactive test process cannot sign.
        Assert.Throws<InvalidOperationException>(() =>
            provider.OpenExisting("suavo-device-acl-contract-test"));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ProductionMaintenanceKey_DurableAclHasNoCoreOpenOrSignAuthority()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fingerprint = "suavo-maintenance-acl-" + Guid.NewGuid().ToString("N");
        var provider = new WindowsTpmMaintenanceAttestationKeyProvider();
        var registration = provider.OpenOrCreate(fingerprint);
        try
        {
            using var root = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64);
            using var state = root.OpenSubKey(
                WindowsTpmMaintenanceAttestationKeyProvider.RegistryStatePath(fingerprint),
                writable: false);
            Assert.NotNull(state);
            using var json = JsonDocument.Parse((string)state!.GetValue("Enrollment")!);
            var keyName = json.RootElement.GetProperty("KeyName").GetString()!;
            using var key = CngKey.Open(
                keyName,
                CngProvider.MicrosoftPlatformCryptoProvider,
                CngKeyOpenOptions.MachineKey);
            var descriptor = new RawSecurityDescriptor(
                key.GetProperty("Security Descr", (CngPropertyOptions)0x4).GetValue()!,
                0);
            var entries = descriptor.DiscretionaryAcl!
                .OfType<CommonAce>()
                .Where(ace => ace.AceType == AceType.AccessAllowed)
                .Select(ace => (ace.SecurityIdentifier.Value, ace.AccessMask))
                .ToArray();

            Assert.Equal(2, entries.Length);
            Assert.Contains(entries, entry =>
                entry.Value == "S-1-5-18" &&
                entry.AccessMask == unchecked((int)0x10000000));
            Assert.Contains(entries, entry =>
                entry.Value == "S-1-5-32-544" && entry.AccessMask == 0x00050000);
            Assert.DoesNotContain(entries, entry =>
                entry.Value == CoreServiceIdentity.ServiceSid);
            if (!System.Security.Principal.WindowsIdentity.GetCurrent().IsSystem)
                Assert.ThrowsAny<Exception>(() => provider.Sign(
                    fingerprint,
                    registration.Enrollment.KeyId,
                    "core-must-not-sign"u8.ToArray()));
        }
        finally
        {
            provider.DestroyForUninstall(fingerprint, registration.Enrollment.KeyId);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ProductionMaintenanceKey_DestroyRecoversKeyDeletedStatePresentCrash()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fingerprint = "suavo-maintenance-destroy-" + Guid.NewGuid().ToString("N");
        var provider = new WindowsTpmMaintenanceAttestationKeyProvider();
        var registration = provider.OpenOrCreate(fingerprint);
        using (var root = RegistryKey.OpenBaseKey(
                   RegistryHive.LocalMachine,
                   RegistryView.Registry64))
        using (var state = root.OpenSubKey(
                   WindowsTpmMaintenanceAttestationKeyProvider.RegistryStatePath(fingerprint),
                   writable: false))
        using (var json = JsonDocument.Parse((string)state!.GetValue("Enrollment")!))
        using (var key = CngKey.Open(
                   json.RootElement.GetProperty("KeyName").GetString()!,
                   CngProvider.MicrosoftPlatformCryptoProvider,
                   CngKeyOpenOptions.MachineKey))
        {
            key.Delete();
        }

        var error = Record.Exception(() => provider.DestroyForUninstall(
            fingerprint,
            registration.Enrollment.KeyId));

        Assert.Null(error);
        Assert.Throws<InvalidOperationException>(() => provider.OpenExisting(fingerprint));
        using var verifyRoot = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Registry64);
        Assert.Null(verifyRoot.OpenSubKey(
            WindowsTpmMaintenanceAttestationKeyProvider.RegistryStatePath(fingerprint),
            writable: false));
    }

    [Fact]
    public void ProductionMaintenanceKey_NextPairingDeletesBoundedOrphanBeforeReplacement()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fingerprint = "suavo-maintenance-orphan-" + Guid.NewGuid().ToString("N");
        var provider = new WindowsTpmMaintenanceAttestationKeyProvider();
        var first = provider.OpenOrCreate(fingerprint);
        string orphanName;
        using (var root = RegistryKey.OpenBaseKey(
                   RegistryHive.LocalMachine,
                   RegistryView.Registry64))
        using (var state = root.OpenSubKey(
                   WindowsTpmMaintenanceAttestationKeyProvider.RegistryStatePath(fingerprint),
                   writable: false))
        using (var json = JsonDocument.Parse((string)state!.GetValue("Enrollment")!))
            orphanName = json.RootElement.GetProperty("KeyName").GetString()!;
        using (var root = RegistryKey.OpenBaseKey(
                   RegistryHive.LocalMachine,
                   RegistryView.Registry64))
        {
            root.DeleteSubKeyTree(
                WindowsTpmMaintenanceAttestationKeyProvider.RegistryStatePath(fingerprint),
                throwOnMissingSubKey: false);
            root.Flush();
        }

        var replacement = provider.OpenOrCreate(fingerprint);
        try
        {
            Assert.NotEqual(first.Enrollment.KeyId, replacement.Enrollment.KeyId);
            Assert.False(CngKey.Exists(
                orphanName,
                CngProvider.MicrosoftPlatformCryptoProvider,
                CngKeyOpenOptions.MachineKey));
        }
        finally
        {
            provider.DestroyForUninstall(fingerprint, replacement.Enrollment.KeyId);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task SeparateProviders_CannotCreateCompetingPendingSlots()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fingerprint = "suavo-concurrent-pair-" + Guid.NewGuid().ToString("N");
        var firstProvider = new WindowsTpmDeviceAttestationKeyProvider();
        var secondProvider = new WindowsTpmDeviceAttestationKeyProvider();

        var attempts = await Task.WhenAll(
            Task.Run(() =>
            {
                using var key = firstProvider.OpenOrCreate(fingerprint);
                return (key.LocalKeyName, key.Enrollment.KeyId);
            }),
            Task.Run(() =>
            {
                using var key = secondProvider.OpenOrCreate(fingerprint);
                return (key.LocalKeyName, key.Enrollment.KeyId);
            }));

        Assert.Equal(attempts[0], attempts[1]);
        firstProvider.AbortPending(fingerprint, attempts[0].KeyId);
    }

    [SupportedOSPlatform("windows")]
    private static void AssertPointerAclRejectsUnprivilegedWriters(string fingerprint)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)))
            .ToLowerInvariant();
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(
            $@"SOFTWARE\Suavo\Agent\DeviceAuthority\{digest}",
            writable: false);
        Assert.NotNull(key);
        var binary = key!.GetAccessControl(AccessControlSections.Access)
            .GetSecurityDescriptorBinaryForm();
        var descriptor = new RawSecurityDescriptor(binary, 0);
        var entries = descriptor.DiscretionaryAcl!
            .OfType<CommonAce>()
            .Where(ace => ace.AceType == AceType.AccessAllowed)
            .Select(ace => (ace.SecurityIdentifier.Value, ace.AccessMask))
            .ToArray();
        Assert.Equal(3, entries.Length);
        Assert.Contains(entries, entry =>
            entry.Value == CoreServiceIdentity.ServiceSid &&
            entry.AccessMask == (int)RegistryRights.ReadKey);
        Assert.DoesNotContain(entries, entry =>
            entry.Value is not (
                CoreServiceIdentity.ServiceSid or "S-1-5-18" or "S-1-5-32-544"));
    }
}
