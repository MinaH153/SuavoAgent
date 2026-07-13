using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace SuavoAgent.Contracts.Security;

[SupportedOSPlatform("windows")]
internal sealed partial class WindowsTpmDeviceAttestationKeyProvider
{
    public DeviceMaintenanceSignature SignForMaintenance(
        string authoritativeFingerprint,
        string expectedActiveKeyId,
        ReadOnlyMemory<byte> canonicalBytes)
    {
        using var crossProcess = DeviceAuthorityCrossProcessLock.Acquire(
            authoritativeFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedActiveKeyId);
        if (canonicalBytes.IsEmpty)
            throw new ArgumentException("Maintenance canonical bytes are required.", nameof(canonicalBytes));
        var statePath = StatePath(authoritativeFingerprint);
        lock (_gate)
        {
            var active = ReadSlot(statePath, ActiveValue);
            if (active is not null) ValidateEnrollment(active);
            var keyName = active?.KeyName ??
                          DeviceAttestationKeyProvider.KeyName(authoritativeFingerprint);
            if (active is not null && !string.Equals(
                    active.KeyId,
                    expectedActiveKeyId,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Active TPM device key identity mismatch during maintenance signing.");
            if (!CngKey.Exists(keyName, PlatformProvider, CngKeyOpenOptions.MachineKey))
                throw new InvalidOperationException("The active TPM device key is missing.");

            using var managementHandle = CngKey.Open(
                keyName,
                PlatformProvider,
                CngKeyOpenOptions.MachineKey);
            CngKey? signingKey = null;
            try
            {
                ApplyMaintenanceHandleAcl(managementHandle);
                signingKey = CngKey.Open(
                    keyName,
                    PlatformProvider,
                    CngKeyOpenOptions.MachineKey);
                if (signingKey.Algorithm != CngAlgorithm.ECDsaP256 ||
                    signingKey.AlgorithmGroup != CngAlgorithmGroup.ECDsa ||
                    signingKey.Provider != PlatformProvider ||
                    signingKey.KeyUsage != CngKeyUsages.Signing ||
                    signingKey.ExportPolicy != CngExportPolicies.None)
                    throw new InvalidOperationException(
                        "The TPM device key has an invalid security policy.");
                using var signer = new ECDsaCng(signingKey);
                var spki = signer.ExportSubjectPublicKeyInfo();
                var enrollment = new DeviceKeyEnrollment(
                    "ES256",
                    Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant(),
                    Convert.ToBase64String(spki));
                if (!string.Equals(
                        enrollment.KeyId,
                        expectedActiveKeyId,
                        StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Active TPM device key identity mismatch during maintenance signing.");
                var signature = signer.SignData(
                    canonicalBytes.Span,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                if (signature.Length != 64)
                    throw new CryptographicException(
                        "The TPM returned an invalid maintenance signature length.");
                return new(enrollment, signature);
            }
            finally
            {
                // The signature is completed before the durable DACL is restored,
                // so this does not rely on provider-specific handle-right caching.
                try
                {
                    ApplyCoreServiceAcl(managementHandle, assertAfterWrite: false);
                    if (signingKey is not null) AssertCoreServiceAcl(signingKey);
                }
                finally
                {
                    signingKey?.Dispose();
                }
            }
        }
    }

    public IDeviceAttestationKey OpenExistingForMaintenance(string authoritativeFingerprint)
    {
        using var crossProcess = DeviceAuthorityCrossProcessLock.Acquire(
            authoritativeFingerprint);
        var statePath = StatePath(authoritativeFingerprint);
        lock (_gate)
        {
            var active = ReadSlot(statePath, ActiveValue);
            if (active is not null) ValidateEnrollment(active);
            var keyName = active?.KeyName ??
                          DeviceAttestationKeyProvider.KeyName(authoritativeFingerprint);
            var expectedKeyId = active?.KeyId;
            if (!CngKey.Exists(keyName, PlatformProvider, CngKeyOpenOptions.MachineKey))
                throw new InvalidOperationException(
                    "The active TPM device key is missing. Re-pair this workstation.");

            CngKey? signingHandle = null;
            using var managementHandle = CngKey.Open(
                keyName,
                PlatformProvider,
                CngKeyOpenOptions.MachineKey);
            try
            {
                ApplyMaintenanceHandleAcl(managementHandle);
                signingHandle = CngKey.Open(
                    keyName,
                    PlatformProvider,
                    CngKeyOpenOptions.MachineKey);
            }
            finally
            {
                try
                {
                    // Access checks occur when the second handle is opened. Restore the
                    // durable DACL before any caller receives or uses that handle.
                    ApplyCoreServiceAcl(managementHandle, assertAfterWrite: false);
                }
                catch
                {
                    signingHandle?.Dispose();
                    throw;
                }
            }

            if (signingHandle is null)
                throw new InvalidOperationException(
                    "The active TPM device key could not be opened for maintenance.");
            var opened = CreateValidated(
                signingHandle,
                ownsKey: true,
                () => IsCurrentActive(statePath, keyName, expectedKeyId));
            if (expectedKeyId is not null && !string.Equals(
                    opened.Enrollment.KeyId,
                    expectedKeyId,
                    StringComparison.Ordinal))
            {
                opened.Dispose();
                throw new InvalidOperationException(
                    "The active TPM key does not match its authority pointer.");
            }
            return opened;
        }
    }

    public void DestroyForUninstall(
        string authoritativeFingerprint,
        string expectedActiveKeyId)
    {
        using var crossProcess = DeviceAuthorityCrossProcessLock.Acquire(
            authoritativeFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedActiveKeyId);
        var statePath = StatePath(authoritativeFingerprint);
        lock (_gate)
        {
            var active = ReadSlot(statePath, ActiveValue);
            var activeName = active?.KeyName ??
                             DeviceAttestationKeyProvider.KeyName(authoritativeFingerprint);
            if (active is not null)
            {
                ValidateEnrollment(active);
                if (!string.Equals(active.KeyId, expectedActiveKeyId, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Active TPM device key identity mismatch during uninstall.");
            }
            else if (CngKey.Exists(
                         activeName,
                         PlatformProvider,
                         CngKeyOpenOptions.MachineKey))
            {
                using var legacy = OpenExistingForMaintenance(authoritativeFingerprint);
                if (!string.Equals(
                        legacy.Enrollment.KeyId,
                        expectedActiveKeyId,
                        StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Active TPM device key identity mismatch during uninstall.");
            }

            // Delete private material before clearing its authority pointer. A crash
            // between these operations leaves an unusable pointer, never an orphan signer.
            DeleteKey(activeName);
            DeleteSlot(statePath, ActiveValue);

            var pending = ReadSlot(statePath, PendingValue);
            if (pending is not null)
            {
                ValidateEnrollment(pending);
                if (!string.Equals(pending.KeyName, activeName, StringComparison.Ordinal))
                    DeleteKey(pending.KeyName);
                DeleteSlot(statePath, PendingValue);
            }
            using var root = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64);
            root.DeleteSubKeyTree(statePath, throwOnMissingSubKey: false);
            root.Flush();
        }
    }

    private static void ApplyMaintenanceHandleAcl(CngKey key)
    {
        // SYSTEM already has WRITE_DAC on the durable key. Grant it signing rights
        // only for the milliseconds required to open a maintenance handle; the
        // caller restores the Core-only DACL before returning or signing.
        var descriptor = new RawSecurityDescriptor(
            $"D:P(A;;GA;;;{CoreServiceIdentity.ServiceSid})" +
            "(A;;GA;;;SY)(A;;0x00050000;;;BA)");
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        key.SetProperty(new CngProperty(
            "Security Descr",
            bytes,
            CngPropertyOptions.Persist | DaclSecurityInformation));
    }
}

public sealed partial class InMemoryDeviceAttestationKeyProvider
{
    public DeviceMaintenanceSignature SignForMaintenance(
        string authoritativeFingerprint,
        string expectedActiveKeyId,
        ReadOnlyMemory<byte> canonicalBytes)
    {
        using var key = OpenExisting(authoritativeFingerprint);
        if (!string.Equals(
                key.Enrollment.KeyId,
                expectedActiveKeyId,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Active test device key identity mismatch during maintenance signing.");
        return new(key.Enrollment, key.Sign(canonicalBytes.Span));
    }

    public IDeviceAttestationKey OpenExistingForMaintenance(string authoritativeFingerprint) =>
        OpenExisting(authoritativeFingerprint);

    public void DestroyForUninstall(
        string authoritativeFingerprint,
        string expectedActiveKeyId)
    {
        var name = DeviceAttestationKeyProvider.KeyName(authoritativeFingerprint);
        if (!_keys.TryGetValue(name, out var state)) return;
        if (state.Active is null)
        {
            state.Pending?.Signer.Dispose();
            state.Pending = null;
            return;
        }
        if (!string.Equals(
                state.Active.Enrollment.KeyId,
                expectedActiveKeyId,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Active test device key identity mismatch during uninstall.");
        state.Active.Signer.Dispose();
        state.Active = null;
        state.Pending?.Signer.Dispose();
        state.Pending = null;
    }
}
