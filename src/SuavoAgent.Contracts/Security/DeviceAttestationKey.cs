using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace SuavoAgent.Contracts.Security;

public sealed record DeviceKeyEnrollment(
    string Algorithm,
    string KeyId,
    string PublicKeySpki);

public sealed record DeviceMaintenanceSignature(
    DeviceKeyEnrollment Enrollment,
    ReadOnlyMemory<byte> Signature);

public interface IDeviceAttestationKey : IDisposable
{
    DeviceKeyEnrollment Enrollment { get; }
    string LocalKeyName { get; }
    byte[] Sign(ReadOnlySpan<byte> canonicalBytes);
}

public interface IDeviceAttestationKeyProvider
{
    /// <summary>
    /// Creates (or reopens) a versioned pending key. This never replaces the
    /// current active key; Setup must call <see cref="CommitPending"/> only
    /// after cloud approval, durable credential promotion, and install health.
    /// </summary>
    IDeviceAttestationKey OpenOrCreate(string authoritativeFingerprint);
    IDeviceAttestationKey OpenExisting(string authoritativeFingerprint);
    /// <summary>
    /// Opens the active key for the signed native maintenance host. The Windows
    /// implementation grants SYSTEM only long enough to acquire a signing handle,
    /// then restores the Core-only DACL before returning that handle.
    /// </summary>
    IDeviceAttestationKey OpenExistingForMaintenance(string authoritativeFingerprint);
    DeviceMaintenanceSignature SignForMaintenance(
        string authoritativeFingerprint,
        string expectedActiveKeyId,
        ReadOnlyMemory<byte> canonicalBytes);
    IDeviceAttestationKey OpenVersion(
        string authoritativeFingerprint,
        string expectedKeyName,
        string expectedKeyId);
    bool IsActiveVersion(
        string authoritativeFingerprint,
        string expectedKeyName,
        string expectedKeyId);
    bool IsPendingVersion(
        string authoritativeFingerprint,
        string expectedKeyName,
        string expectedKeyId);
    void CommitPending(string authoritativeFingerprint, string expectedKeyId);
    void AbortPending(string authoritativeFingerprint, string expectedKeyId);
    /// <summary>
    /// Irreversibly removes the exact active device key and any abandoned pending
    /// version after a signed self-uninstall completion ticket is durable.
    /// </summary>
    void DestroyForUninstall(string authoritativeFingerprint, string expectedActiveKeyId);
}

public static class DeviceAttestationKeyProvider
{
#pragma warning disable CA1416 // Lazy value is reached only after the runtime Windows guard below.
    private static readonly Lazy<IDeviceAttestationKeyProvider> Production = new(
        () => new WindowsTpmDeviceAttestationKeyProvider(),
        LazyThreadSafetyMode.ExecutionAndPublication);
#pragma warning restore CA1416

    public static IDeviceAttestationKeyProvider CreateProduction()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Device authority requires the Windows TPM platform crypto provider.");
        return Production.Value;
    }

    public static string KeyName(string authoritativeFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authoritativeFingerprint);
        var digest = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(authoritativeFingerprint))).ToLowerInvariant();
        return $"SuavoAgent.DeviceAuthority.v1.{digest[..24]}";
    }
}

[SupportedOSPlatform("windows")]
internal sealed partial class WindowsTpmDeviceAttestationKeyProvider : IDeviceAttestationKeyProvider
{
    private const CngPropertyOptions DaclSecurityInformation = (CngPropertyOptions)0x4;
    private const int GenericAll = unchecked((int)0x10000000);
    private const int KeyManagementOnly = 0x00050000; // DELETE | WRITE_DAC
    private static readonly CngProvider PlatformProvider =
        CngProvider.MicrosoftPlatformCryptoProvider;
    private const string RegistryRoot = @"SOFTWARE\Suavo\Agent\DeviceAuthority";
    private const string ActiveValue = "ActiveSlot";
    private const string PendingValue = "PendingSlot";
    private readonly object _gate = new();

    private sealed record KeySlot(
        string KeyName,
        string KeyId,
        string PublicKeySpki,
        string CreatedAtUtc);

    public IDeviceAttestationKey OpenOrCreate(string authoritativeFingerprint)
    {
        using var crossProcess = DeviceAuthorityCrossProcessLock.Acquire(
            authoritativeFingerprint);
        var statePath = StatePath(authoritativeFingerprint);
        lock (_gate)
        {
            var pending = ReadSlot(statePath, PendingValue);
            if (pending is not null)
            {
                ValidateEnrollment(pending);
                // After a Setup restart the pending private key is intentionally
                // not reopenable by the interactive administrator. Its public
                // enrollment is durable, so registration/poll retries can reuse
                // the exact same key without gaining signing access.
                if (!CngKey.Exists(
                        pending.KeyName,
                        PlatformProvider,
                        CngKeyOpenOptions.MachineKey))
                    throw new InvalidOperationException(
                        "The pending TPM device key is missing. Cancel pairing and retry.");
                return new EnrollmentOnlyDeviceAttestationKey(new(
                    "ES256", pending.KeyId, pending.PublicKeySpki), pending.KeyName);
            }

            var name = $"{DeviceAttestationKeyProvider.KeyName(authoritativeFingerprint)}" +
                       $".slot.{Guid.NewGuid():N}";
            var creation = new CngKeyCreationParameters
            {
                Provider = PlatformProvider,
                KeyCreationOptions = CngKeyCreationOptions.MachineKey,
                ExportPolicy = CngExportPolicies.None,
                KeyUsage = CngKeyUsages.Signing,
            };
            creation.Parameters.Add(new CngProperty(
                "Length",
                BitConverter.GetBytes(256),
                CngPropertyOptions.None));
            using var key = CngKey.Create(CngAlgorithm.ECDsaP256, name, creation);
            var enrollment = BuildEnrollment(key);
            ApplyCoreServiceAcl(key);
            WriteSlot(statePath, PendingValue, new(
                name,
                enrollment.KeyId,
                enrollment.PublicKeySpki,
                DateTimeOffset.UtcNow.ToString("O")));

            // Setup must not retain the creation handle it obtained before the
            // Core-only DACL was applied. It keeps public enrollment only.
            return new EnrollmentOnlyDeviceAttestationKey(enrollment, name);
        }
    }

    public IDeviceAttestationKey OpenExisting(string authoritativeFingerprint)
    {
        var statePath = StatePath(authoritativeFingerprint);
        try
        {
            var active = ReadSlot(statePath, ActiveValue);
            if (active is not null)
            {
                ValidateEnrollment(active);
                return OpenValidated(
                    active.KeyName,
                    active.KeyId,
                    () => IsCurrentActive(statePath, active.KeyName, active.KeyId));
            }

            // Compatibility for the original fixed-name key. A re-pair creates
            // a side-by-side versioned slot; canceling never deletes this key.
            var name = DeviceAttestationKeyProvider.KeyName(authoritativeFingerprint);
            if (!CngKey.Exists(name, PlatformProvider, CngKeyOpenOptions.MachineKey))
                throw new InvalidOperationException(
                    "The TPM device key is missing. Re-pair this workstation from Suavo Settings.");
            return OpenValidated(
                name,
                expectedKeyId: null,
                () => IsCurrentActive(statePath, name, expectedKeyId: null));
        }
        catch (Exception ex) when (ex is CryptographicException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "The TPM device key cannot be opened. Re-pair this workstation from Suavo Settings.",
                ex);
        }
    }

    public IDeviceAttestationKey OpenVersion(
        string authoritativeFingerprint,
        string expectedKeyName,
        string expectedKeyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedKeyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedKeyId);
        var statePath = StatePath(authoritativeFingerprint);
        var active = ReadSlot(statePath, ActiveValue);
        var pending = ReadSlot(statePath, PendingValue);
        var slot = new[] { active, pending }
            .FirstOrDefault(candidate => candidate is not null && string.Equals(
                candidate.KeyName, expectedKeyName, StringComparison.Ordinal) &&
                string.Equals(candidate.KeyId, expectedKeyId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "The configured TPM device key version is unavailable. Re-pair this workstation.");
        ValidateEnrollment(slot);
        return OpenValidated(
            slot.KeyName,
            slot.KeyId,
            () => IsCurrentOrPendingVersion(statePath, slot.KeyName, slot.KeyId));
    }

    public void CommitPending(string authoritativeFingerprint, string expectedKeyId)
    {
        using var crossProcess = DeviceAuthorityCrossProcessLock.Acquire(
            authoritativeFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedKeyId);
        var statePath = StatePath(authoritativeFingerprint);
        lock (_gate)
        {
            var previous = ReadSlot(statePath, ActiveValue);
            var pending = ReadSlot(statePath, PendingValue);
            if (pending is null)
            {
                if (previous is not null && string.Equals(
                        previous.KeyId, expectedKeyId, StringComparison.Ordinal))
                    return;
                throw new InvalidOperationException("No pending TPM device key exists.");
            }
            ValidateEnrollment(pending);
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(pending.KeyId),
                    Encoding.ASCII.GetBytes(expectedKeyId)))
                throw new InvalidOperationException("Pending TPM device key identity mismatch.");
            if (!CngKey.Exists(
                    pending.KeyName,
                    PlatformProvider,
                    CngKeyOpenOptions.MachineKey))
                throw new InvalidOperationException("The pending TPM device key is missing.");

            // A registry value write is the single local authority switch. Its
            // signer guard fails closed immediately; a restarted Core opens the
            // new key. The caller reaches this method only after health proves
            // the pre-cutover Core has stopped.
            WriteSlot(statePath, ActiveValue, pending);
            DeleteSlot(statePath, PendingValue);

            // CommitPending is invoked only after native activation proved the
            // old Core stopped and the new Core healthy. Before this point the
            // old key is preserved for rollback; after it, remove obsolete
            // private material. A legacy fixed-name key is the implicit prior
            // slot when no registry pointer exists yet.
            var previousName = previous?.KeyName ??
                               DeviceAttestationKeyProvider.KeyName(authoritativeFingerprint);
            if (!string.Equals(previousName, pending.KeyName, StringComparison.Ordinal))
                DeleteKey(previousName);
        }
    }

    public void AbortPending(string authoritativeFingerprint, string expectedKeyId)
    {
        using var crossProcess = DeviceAuthorityCrossProcessLock.Acquire(
            authoritativeFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedKeyId);
        var statePath = StatePath(authoritativeFingerprint);
        lock (_gate)
        {
            var pending = ReadSlot(statePath, PendingValue);
            if (pending is null) return;
            if (!string.Equals(pending.KeyId, expectedKeyId, StringComparison.Ordinal))
                throw new InvalidOperationException("Pending TPM device key identity mismatch.");
            var active = ReadSlot(statePath, ActiveValue);
            if (active is null || !string.Equals(
                    active.KeyName, pending.KeyName, StringComparison.Ordinal))
                DeleteKey(pending.KeyName);
            DeleteSlot(statePath, PendingValue);
        }
    }

    private static IDeviceAttestationKey OpenValidated(
        string name,
        string? expectedKeyId,
        Func<bool> canSign)
    {
        var key = CngKey.Open(name, PlatformProvider, CngKeyOpenOptions.MachineKey);
        var opened = CreateValidated(key, ownsKey: true, canSign);
        if (expectedKeyId is not null && !string.Equals(
                opened.Enrollment.KeyId, expectedKeyId, StringComparison.Ordinal))
        {
            opened.Dispose();
            throw new InvalidOperationException("The active TPM key does not match its authority pointer.");
        }
        return opened;
    }

    private static IDeviceAttestationKey CreateValidated(
        CngKey key,
        bool ownsKey,
        Func<bool> canSign)
    {
        if (key.Algorithm != CngAlgorithm.ECDsaP256 ||
            key.AlgorithmGroup != CngAlgorithmGroup.ECDsa ||
            key.Provider != PlatformProvider ||
            key.KeyUsage != CngKeyUsages.Signing ||
            key.ExportPolicy != CngExportPolicies.None)
        {
            key.Dispose();
            throw new InvalidOperationException(
                "The TPM device key has an invalid security policy. Re-pair this workstation.");
        }
        AssertCoreServiceAcl(key);
        return new WindowsTpmDeviceAttestationKey(key, ownsKey, canSign);
    }

    private static void DeleteKey(string name)
    {
        if (!CngKey.Exists(name, PlatformProvider, CngKeyOpenOptions.MachineKey)) return;
        using var key = CngKey.Open(name, PlatformProvider, CngKeyOpenOptions.MachineKey);
        key.Delete();
    }

    private static DeviceKeyEnrollment BuildEnrollment(CngKey key)
    {
        using var signer = new ECDsaCng(key);
        var spki = signer.ExportSubjectPublicKeyInfo();
        return new(
            "ES256",
            Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant(),
            Convert.ToBase64String(spki));
    }

    private static bool IsCurrentActive(
        string statePath,
        string keyName,
        string? expectedKeyId)
    {
        var current = ReadSlot(statePath, ActiveValue);
        if (current is null) return expectedKeyId is null;
        return string.Equals(current.KeyName, keyName, StringComparison.Ordinal) &&
               (expectedKeyId is null || string.Equals(
                   current.KeyId, expectedKeyId, StringComparison.Ordinal));
    }

    private static bool IsCurrentOrPendingVersion(
        string statePath,
        string keyName,
        string keyId)
    {
        var active = ReadSlot(statePath, ActiveValue);
        var pending = ReadSlot(statePath, PendingValue);
        return new[] { active, pending }.Any(slot =>
            slot is not null &&
            string.Equals(slot.KeyName, keyName, StringComparison.Ordinal) &&
            string.Equals(slot.KeyId, keyId, StringComparison.Ordinal));
    }

    private static string StatePath(string authoritativeFingerprint)
    {
        var digest = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(authoritativeFingerprint))).ToLowerInvariant();
        return $@"{RegistryRoot}\{digest}";
    }

    private static KeySlot? ReadSlot(string statePath, string valueName)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(statePath, writable: false);
        if (key is not null) AssertStateAcl(key);
        var json = key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
            as string;
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<KeySlot>(json)
                ?? throw new InvalidOperationException("Device authority slot is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Device authority slot metadata is invalid.", ex);
        }
    }

    private static void WriteSlot(string statePath, string valueName, KeySlot slot)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.CreateSubKey(statePath, writable: true)
            ?? throw new InvalidOperationException("Device authority state could not be opened.");
        key.SetAccessControl(StateRegistrySecurity());
        AssertStateAcl(key);
        key.SetValue(valueName, JsonSerializer.Serialize(slot), RegistryValueKind.String);
        key.Flush();
    }

    private static void DeleteSlot(string statePath, string valueName)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(statePath, writable: true);
        if (key is not null) AssertStateAcl(key);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
        key?.Flush();
    }

    private static RegistrySecurity StateRegistrySecurity()
    {
        var security = new RegistrySecurity();
        security.SetSecurityDescriptorSddlForm(
            $"D:P(A;;KA;;;SY)(A;;KA;;;BA)(A;;KR;;;{CoreServiceIdentity.ServiceSid})");
        return security;
    }

    private static void AssertStateAcl(RegistryKey key)
    {
        var binary = key.GetAccessControl(AccessControlSections.Access)
            .GetSecurityDescriptorBinaryForm();
        var descriptor = new RawSecurityDescriptor(binary, 0);
        var actual = descriptor.DiscretionaryAcl?
            .OfType<CommonAce>()
            .Where(ace => ace.AceType == AceType.AccessAllowed)
            .Select(ace => (Sid: ace.SecurityIdentifier.Value, ace.AccessMask))
            .ToArray() ?? [];
        var expected = new HashSet<(string Sid, int AccessMask)>
        {
            ("S-1-5-18", (int)RegistryRights.FullControl),
            ("S-1-5-32-544", (int)RegistryRights.FullControl),
            (CoreServiceIdentity.ServiceSid, (int)RegistryRights.ReadKey),
        };
        if (actual.Length != expected.Count || actual.Any(ace => !expected.Contains(ace)))
            throw new InvalidOperationException(
                "Device authority pointer ACL permits an unauthorized writer.");
    }

    private static void ValidateEnrollment(KeySlot slot)
    {
        if (string.IsNullOrWhiteSpace(slot.KeyName) ||
            slot.KeyId.Length != 64 ||
            slot.KeyId.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidOperationException("Device authority slot identity is invalid.");
        byte[] spki;
        try
        {
            spki = Convert.FromBase64String(slot.PublicKeySpki);
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(spki, out var consumed);
            if (consumed != spki.Length || verifier.KeySize != 256)
                throw new CryptographicException("The enrollment is not P-256.");
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw new InvalidOperationException("Device authority public enrollment is invalid.", ex);
        }
        var calculated = Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(calculated),
                Encoding.ASCII.GetBytes(slot.KeyId)))
            throw new InvalidOperationException("Device authority public enrollment digest mismatch.");
    }

    private static void ApplyCoreServiceAcl(
        CngKey key,
        bool assertAfterWrite = true)
    {
        // Only the exact Core service SID may use/sign with the key. SYSTEM and
        // Administrators retain DELETE+WRITE_DAC solely for approved repair;
        // neither receives GENERIC_READ/GENERIC_ALL and therefore neither is a
        // signing principal.
        var descriptor = new RawSecurityDescriptor(
            $"D:P(A;;GA;;;{CoreServiceIdentity.ServiceSid})" +
            "(A;;0x00050000;;;SY)(A;;0x00050000;;;BA)");
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        key.SetProperty(new CngProperty(
            "Security Descr",
            bytes,
            CngPropertyOptions.Persist | DaclSecurityInformation));
        if (assertAfterWrite) AssertCoreServiceAcl(key);
    }

    private static void AssertCoreServiceAcl(CngKey key)
    {
        var bytes = key.GetProperty(
            "Security Descr",
            DaclSecurityInformation).GetValue()
            ?? throw new InvalidOperationException("The TPM device key ACL is missing.");
        var descriptor = new RawSecurityDescriptor(bytes, 0);
        var actual = descriptor.DiscretionaryAcl?
            .OfType<CommonAce>()
            .Where(ace => ace.AceType == AceType.AccessAllowed)
            .Select(ace => (Sid: ace.SecurityIdentifier.Value, ace.AccessMask))
            .ToArray() ?? [];
        var expected = new HashSet<(string Sid, int AccessMask)>
        {
            (CoreServiceIdentity.ServiceSid, GenericAll),
            ("S-1-5-18", KeyManagementOnly),
            ("S-1-5-32-544", KeyManagementOnly),
        };
        if (actual.Length != expected.Count || actual.Any(ace => !expected.Contains(ace)))
            throw new InvalidOperationException(
                "The TPM device key ACL is not restricted to Core signing plus repair-only principals.");
    }
}

[SupportedOSPlatform("windows")]
internal static class DeviceAuthorityCrossProcessLock
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    internal static IDisposable Acquire(
        string authoritativeFingerprint,
        TimeSpan? timeout = null)
    {
        var digest = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(authoritativeFingerprint))).ToLowerInvariant();
        var mutex = new Mutex(
            initiallyOwned: false,
            name: $@"Global\SuavoAgent.DeviceAuthority.{digest}");
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(timeout ?? DefaultTimeout);
            }
            catch (AbandonedMutexException)
            {
                // The predecessor died while holding the lock. Windows grants
                // ownership to this waiter; registry/CNG state is revalidated
                // before every mutation below.
                acquired = true;
            }
            if (!acquired)
                throw new TimeoutException(
                    "Another Suavo Setup process is pairing this workstation.");
            return new Releaser(mutex);
        }
        catch
        {
            if (!acquired) mutex.Dispose();
            throw;
        }
    }

    private sealed class Releaser(Mutex mutex) : IDisposable
    {
        private Mutex? _mutex = mutex;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _mutex, null);
            if (current is null) return;
            current.ReleaseMutex();
            current.Dispose();
        }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsTpmDeviceAttestationKey : IDeviceAttestationKey
{
    private readonly CngKey _key;
    private readonly ECDsaCng _signer;
    private readonly bool _ownsKey;
    private readonly Func<bool> _canSign;

    internal WindowsTpmDeviceAttestationKey(
        CngKey key,
        bool ownsKey,
        Func<bool> canSign)
    {
        _key = key;
        _signer = new ECDsaCng(key);
        _ownsKey = ownsKey;
        _canSign = canSign;
        LocalKeyName = key.KeyName
            ?? throw new InvalidOperationException("The TPM key is not persistently named.");
        var spki = _signer.ExportSubjectPublicKeyInfo();
        Enrollment = new DeviceKeyEnrollment(
            "ES256",
            Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant(),
            Convert.ToBase64String(spki));
    }

    public DeviceKeyEnrollment Enrollment { get; }
    public string LocalKeyName { get; }

    public byte[] Sign(ReadOnlySpan<byte> canonicalBytes)
    {
        if (!_canSign())
            throw new InvalidOperationException(
                "This device key is not the active local authority key.");
        return _signer.SignData(
            canonicalBytes,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public void Dispose()
    {
        if (!_ownsKey) return;
        _signer.Dispose();
        _key.Dispose();
    }
}

internal sealed class EnrollmentOnlyDeviceAttestationKey(
    DeviceKeyEnrollment enrollment,
    string localKeyName) : IDeviceAttestationKey
{
    public DeviceKeyEnrollment Enrollment { get; } = enrollment;
    public string LocalKeyName { get; } = localKeyName;

    public byte[] Sign(ReadOnlySpan<byte> canonicalBytes) =>
        throw new InvalidOperationException(
            "Setup can enroll a pending device key but cannot use it to sign.");

    public void Dispose() { }
}

/// <summary>Explicit non-production fake for cross-platform contract tests.</summary>
public sealed partial class InMemoryDeviceAttestationKeyProvider : IDeviceAttestationKeyProvider, IDisposable
{
    private sealed class MemoryKey
    {
        internal MemoryKey(string name)
        {
            Name = name;
            Signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Enrollment = BuildEnrollment(Signer);
        }

        internal string Name { get; }
        internal ECDsa Signer { get; }
        internal DeviceKeyEnrollment Enrollment { get; }
    }

    private sealed class KeyState
    {
        internal MemoryKey? Active { get; set; }
        internal MemoryKey? Pending { get; set; }
    }

    private readonly Dictionary<string, KeyState> _keys = new(StringComparer.Ordinal);

    public IDeviceAttestationKey OpenOrCreate(string authoritativeFingerprint)
    {
        var name = DeviceAttestationKeyProvider.KeyName(authoritativeFingerprint);
        if (!_keys.TryGetValue(name, out var state))
            _keys.Add(name, state = new KeyState());
        state.Pending ??= new MemoryKey($"{name}.slot.{Guid.NewGuid():N}");
        var key = state.Pending;
        return new InMemoryDeviceAttestationKey(
            key.Signer,
            key.Enrollment,
            LocalName(key),
            static () => false);
    }

    public IDeviceAttestationKey OpenExisting(string authoritativeFingerprint)
    {
        var name = DeviceAttestationKeyProvider.KeyName(authoritativeFingerprint);
        return _keys.TryGetValue(name, out var state) && state.Active is { } key
            ? new InMemoryDeviceAttestationKey(
                key.Signer,
                key.Enrollment,
                LocalName(key),
                () => ReferenceEquals(state.Active, key))
            : throw new InvalidOperationException("Test device key does not exist.");
    }

    public IDeviceAttestationKey OpenVersion(
        string authoritativeFingerprint,
        string expectedKeyName,
        string expectedKeyId)
    {
        var name = DeviceAttestationKeyProvider.KeyName(authoritativeFingerprint);
        if (!_keys.TryGetValue(name, out var state))
            throw new InvalidOperationException("Test device key does not exist.");
        var candidates = new[] { state.Active, state.Pending };
        var key = candidates.FirstOrDefault(candidate => candidate is not null && string.Equals(
            LocalName(candidate), expectedKeyName, StringComparison.Ordinal) && string.Equals(
            candidate.Enrollment.KeyId, expectedKeyId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Test device key version does not exist.");
        return new InMemoryDeviceAttestationKey(
            key.Signer,
            key.Enrollment,
            LocalName(key),
            () => ReferenceEquals(state.Active, key) || ReferenceEquals(state.Pending, key));
    }

    public void CommitPending(string authoritativeFingerprint, string expectedKeyId)
    {
        var name = DeviceAttestationKeyProvider.KeyName(authoritativeFingerprint);
        if (!_keys.TryGetValue(name, out var state))
            throw new InvalidOperationException("No pending test device key exists.");
        if (state.Pending is null)
        {
            if (state.Active is not null && string.Equals(
                    state.Active.Enrollment.KeyId,
                    expectedKeyId,
                    StringComparison.Ordinal))
                return;
            throw new InvalidOperationException("No pending test device key exists.");
        }
        var pendingId = state.Pending.Enrollment.KeyId;
        if (!string.Equals(pendingId, expectedKeyId, StringComparison.Ordinal))
            throw new InvalidOperationException("Pending test device key identity mismatch.");
        state.Active?.Signer.Dispose();
        state.Active = state.Pending;
        state.Pending = null;
    }

    public void AbortPending(string authoritativeFingerprint, string expectedKeyId)
    {
        var name = DeviceAttestationKeyProvider.KeyName(authoritativeFingerprint);
        if (!_keys.TryGetValue(name, out var state) || state.Pending is null) return;
        var pendingId = state.Pending.Enrollment.KeyId;
        if (!string.Equals(pendingId, expectedKeyId, StringComparison.Ordinal))
            throw new InvalidOperationException("Pending test device key identity mismatch.");
        state.Pending.Signer.Dispose();
        state.Pending = null;
    }

    public void Dispose()
    {
        foreach (var state in _keys.Values)
        {
            state.Active?.Signer.Dispose();
            state.Pending?.Signer.Dispose();
        }
        _keys.Clear();
    }

    private sealed class InMemoryDeviceAttestationKey(
        ECDsa key,
        DeviceKeyEnrollment enrollment,
        string localKeyName,
        Func<bool> canSign) : IDeviceAttestationKey
    {
        public DeviceKeyEnrollment Enrollment { get; } = enrollment;
        public string LocalKeyName { get; } = localKeyName;

        public byte[] Sign(ReadOnlySpan<byte> canonicalBytes)
        {
            if (!canSign())
                throw new InvalidOperationException(
                    "This test device key is not the active local authority key.");
            return key.SignData(
                canonicalBytes,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }

        public void Dispose() { }

    }

    private static DeviceKeyEnrollment BuildEnrollment(ECDsa signer)
    {
        var spki = signer.ExportSubjectPublicKeyInfo();
        return new(
            "ES256",
            Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant(),
            Convert.ToBase64String(spki));
    }

    private static string LocalName(MemoryKey key) => key.Name;
}
