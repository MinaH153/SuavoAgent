using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace SuavoAgent.Contracts.Security;

public sealed record MaintenanceKeyRegistration(
    DeviceKeyEnrollment Enrollment,
    string PossessionProof);

/// <summary>
/// A TPM identity reserved for terminal maintenance receipts. Unlike ordinary
/// device authority, its private key is never accessible to the Core service.
/// </summary>
public interface IMaintenanceAttestationKeyProvider
{
    MaintenanceKeyRegistration OpenOrCreate(string authoritativeFingerprint);
    MaintenanceKeyRegistration OpenExisting(string authoritativeFingerprint);
    DeviceMaintenanceSignature Sign(
        string authoritativeFingerprint,
        string expectedKeyId,
        ReadOnlyMemory<byte> canonicalBytes);
    void DestroyForUninstall(string authoritativeFingerprint, string expectedKeyId);
}

public static class MaintenanceAttestationKeyProvider
{
    private const string Domain = "suavo.maintenance-enrollment.v1";

#pragma warning disable CA1416
    private static readonly Lazy<IMaintenanceAttestationKeyProvider> Production = new(
        () => new WindowsTpmMaintenanceAttestationKeyProvider(),
        LazyThreadSafetyMode.ExecutionAndPublication);
#pragma warning restore CA1416

    public static IMaintenanceAttestationKeyProvider CreateProduction()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Maintenance authority requires the Windows TPM platform crypto provider.");
        return Production.Value;
    }

    public static string BuildEnrollmentCanonical(
        DeviceKeyEnrollment enrollment,
        string authoritativeFingerprint)
    {
        ArgumentNullException.ThrowIfNull(enrollment);
        ArgumentException.ThrowIfNullOrWhiteSpace(authoritativeFingerprint);
        return string.Join('|',
            Domain,
            enrollment.Algorithm,
            enrollment.KeyId,
            authoritativeFingerprint);
    }

    public static bool VerifyPossessionProof(
        MaintenanceKeyRegistration registration,
        string authoritativeFingerprint)
    {
        try
        {
            var enrollment = registration.Enrollment;
            if (enrollment.Algorithm != "ES256" ||
                !IsLowerHex64(enrollment.KeyId) ||
                registration.PossessionProof.Length != 86 ||
                registration.PossessionProof.Any(character =>
                    character is not (>= 'A' and <= 'Z') and
                    not (>= 'a' and <= 'z') and
                    not (>= '0' and <= '9') and not '-' and not '_'))
                return false;
            var spki = Convert.FromBase64String(enrollment.PublicKeySpki);
            if (!string.Equals(
                    Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant(),
                    enrollment.KeyId,
                    StringComparison.Ordinal))
                return false;
            var signature = Base64UrlDecode(registration.PossessionProof);
            if (signature.Length != 64) return false;
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(spki, out var consumed);
            return consumed == spki.Length && verifier.KeySize == 256 &&
                   verifier.VerifyData(
                       Encoding.UTF8.GetBytes(BuildEnrollmentCanonical(
                           enrollment,
                           authoritativeFingerprint)),
                       signature,
                       HashAlgorithmName.SHA256,
                       DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception exception) when (exception is
            FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    internal static string KeyPrefix(string authoritativeFingerprint)
    {
        var digest = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(authoritativeFingerprint))).ToLowerInvariant();
        return $"SuavoAgent.MaintenanceAuthority.v1.{digest[..24]}";
    }

    internal static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var standard = value.Replace('-', '+').Replace('_', '/');
        standard += (standard.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(standard);
    }

    private static bool IsLowerHex64(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

[SupportedOSPlatform("windows")]
internal sealed partial class WindowsTpmMaintenanceAttestationKeyProvider
    : IMaintenanceAttestationKeyProvider
{
    private const CngPropertyOptions DaclSecurityInformation = (CngPropertyOptions)0x4;
    private const int GenericAll = unchecked((int)0x10000000);
    private const int KeyManagementOnly = 0x00050000;
    private const string RegistryRoot = @"SOFTWARE\Suavo\Agent\MaintenanceAuthority";
    private const string EnrollmentValue = "Enrollment";
    private static readonly CngProvider PlatformProvider =
        CngProvider.MicrosoftPlatformCryptoProvider;
    private readonly object _gate = new();

    private sealed record PersistedRegistration(
        string KeyName,
        string KeyId,
        string PublicKeySpki,
        string PossessionProof);

    public MaintenanceKeyRegistration OpenOrCreate(string authoritativeFingerprint)
    {
        using var crossProcess = DeviceAuthorityCrossProcessLock.Acquire(
            "maintenance:" + authoritativeFingerprint);
        lock (_gate)
        {
            var statePath = StatePath(authoritativeFingerprint);
            var existing = ReadRegistration(statePath);
            if (existing is not null)
                return ValidateRegistration(
                    existing,
                    authoritativeFingerprint,
                    requirePrivateKey: true);
            CleanupOrphanedKeys(authoritativeFingerprint);

            var keyName = MaintenanceAttestationKeyProvider.KeyPrefix(
                              authoritativeFingerprint) + ".slot." + Guid.NewGuid().ToString("N");
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
            using var key = CngKey.Create(CngAlgorithm.ECDsaP256, keyName, creation);
            using var signer = new ECDsaCng(key);
            var spki = signer.ExportSubjectPublicKeyInfo();
            var enrollment = new DeviceKeyEnrollment(
                "ES256",
                Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant(),
                Convert.ToBase64String(spki));
            var proof = MaintenanceAttestationKeyProvider.Base64UrlEncode(signer.SignData(
                Encoding.UTF8.GetBytes(
                    MaintenanceAttestationKeyProvider.BuildEnrollmentCanonical(
                        enrollment,
                        authoritativeFingerprint)),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

            // The creation handle is the only interactive handle that ever sees
            // signing rights. Harden the durable key before publishing enrollment.
            ApplyMaintenanceAcl(key);
            var persisted = new PersistedRegistration(
                keyName,
                enrollment.KeyId,
                enrollment.PublicKeySpki,
                proof);
            WriteRegistration(statePath, persisted);
            return ValidateRegistration(
                persisted,
                authoritativeFingerprint,
                requirePrivateKey: true);
        }
    }

    public MaintenanceKeyRegistration OpenExisting(string authoritativeFingerprint)
    {
        using var crossProcess = DeviceAuthorityCrossProcessLock.Acquire(
            "maintenance:" + authoritativeFingerprint);
        lock (_gate)
        {
            var persisted = ReadRegistration(StatePath(authoritativeFingerprint))
                ?? throw new InvalidOperationException(
                    "The TPM maintenance key is missing. Re-pair this workstation.");
            return ValidateRegistration(
                persisted,
                authoritativeFingerprint,
                requirePrivateKey: true);
        }
    }

    public DeviceMaintenanceSignature Sign(
        string authoritativeFingerprint,
        string expectedKeyId,
        ReadOnlyMemory<byte> canonicalBytes)
    {
        if (canonicalBytes.IsEmpty)
            throw new ArgumentException("Maintenance canonical bytes are required.", nameof(canonicalBytes));
        using var crossProcess = DeviceAuthorityCrossProcessLock.Acquire(
            "maintenance:" + authoritativeFingerprint);
        lock (_gate)
        {
            var persisted = ReadRegistration(StatePath(authoritativeFingerprint))
                ?? throw new InvalidOperationException("The TPM maintenance key is missing.");
            var registration = ValidateRegistration(
                persisted,
                authoritativeFingerprint,
                requirePrivateKey: true);
            if (!string.Equals(
                    registration.Enrollment.KeyId,
                    expectedKeyId,
                    StringComparison.Ordinal))
                throw new InvalidOperationException("Maintenance TPM key identity mismatch.");
            using var key = CngKey.Open(
                persisted.KeyName,
                PlatformProvider,
                CngKeyOpenOptions.MachineKey);
            AssertMaintenanceAcl(key);
            using var signer = new ECDsaCng(key);
            var signature = signer.SignData(
                canonicalBytes.Span,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            if (signature.Length != 64)
                throw new CryptographicException(
                    "The TPM returned an invalid maintenance signature length.");
            return new(registration.Enrollment, signature);
        }
    }

    public void DestroyForUninstall(string authoritativeFingerprint, string expectedKeyId)
    {
        using var crossProcess = DeviceAuthorityCrossProcessLock.Acquire(
            "maintenance:" + authoritativeFingerprint);
        lock (_gate)
        {
            var statePath = StatePath(authoritativeFingerprint);
            var persisted = ReadRegistration(statePath);
            if (persisted is null) return;
            var registration = ValidateRegistration(
                persisted,
                authoritativeFingerprint,
                requirePrivateKey: false);
            if (!string.Equals(
                    registration.Enrollment.KeyId,
                    expectedKeyId,
                    StringComparison.Ordinal))
                throw new InvalidOperationException("Maintenance TPM key identity mismatch.");
            if (CngKey.Exists(
                    persisted.KeyName,
                    PlatformProvider,
                    CngKeyOpenOptions.MachineKey))
            {
                using var key = CngKey.Open(
                    persisted.KeyName,
                    PlatformProvider,
                    CngKeyOpenOptions.MachineKey);
                AssertMaintenanceAcl(key);
                key.Delete();
            }
            using var root = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64);
            root.DeleteSubKeyTree(statePath, throwOnMissingSubKey: false);
            root.Flush();
        }
    }

    internal static string RegistryStatePath(string authoritativeFingerprint) =>
        StatePath(authoritativeFingerprint);

    internal static void AssertMaintenanceAcl(CngKey key)
    {
        var bytes = key.GetProperty("Security Descr", DaclSecurityInformation).GetValue()
            ?? throw new InvalidOperationException("The TPM maintenance key ACL is missing.");
        var descriptor = new RawSecurityDescriptor(bytes, 0);
        var actual = descriptor.DiscretionaryAcl?
            .OfType<CommonAce>()
            .Where(ace => ace.AceType == AceType.AccessAllowed)
            .Select(ace => (Sid: ace.SecurityIdentifier.Value, ace.AccessMask))
            .ToArray() ?? [];
        var expected = new HashSet<(string Sid, int AccessMask)>
        {
            ("S-1-5-18", GenericAll),
            ("S-1-5-32-544", KeyManagementOnly),
        };
        if (actual.Length != expected.Count || actual.Any(ace => !expected.Contains(ace)) ||
            actual.Any(ace => ace.Sid == CoreServiceIdentity.ServiceSid))
            throw new InvalidOperationException(
                "The TPM maintenance key ACL is not SYSTEM-signing-only.");
    }

    private static void ApplyMaintenanceAcl(CngKey key)
    {
        var descriptor = new RawSecurityDescriptor(
            "D:P(A;;GA;;;SY)(A;;0x00050000;;;BA)");
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        key.SetProperty(new CngProperty(
            "Security Descr",
            bytes,
            CngPropertyOptions.Persist | DaclSecurityInformation));
        AssertMaintenanceAcl(key);
    }

    private static MaintenanceKeyRegistration ValidateRegistration(
        PersistedRegistration persisted,
        string authoritativeFingerprint,
        bool requirePrivateKey)
    {
        if (string.IsNullOrWhiteSpace(persisted.KeyName) ||
            requirePrivateKey && !CngKey.Exists(
                persisted.KeyName,
                PlatformProvider,
                CngKeyOpenOptions.MachineKey))
            throw new InvalidOperationException("The TPM maintenance key is missing.");
        var registration = new MaintenanceKeyRegistration(
            new DeviceKeyEnrollment("ES256", persisted.KeyId, persisted.PublicKeySpki),
            persisted.PossessionProof);
        if (!MaintenanceAttestationKeyProvider.VerifyPossessionProof(
                registration,
                authoritativeFingerprint))
            throw new InvalidOperationException(
                "The TPM maintenance enrollment proof is invalid.");
        return registration;
    }

    private static string StatePath(string authoritativeFingerprint)
    {
        var digest = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(authoritativeFingerprint))).ToLowerInvariant();
        return $@"{RegistryRoot}\{digest}";
    }

    private static PersistedRegistration? ReadRegistration(string statePath)
    {
        using var root = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Registry64);
        using var key = root.OpenSubKey(statePath, writable: false);
        if (key is not null) AssertRegistryAcl(key);
        var json = key?.GetValue(
            EnrollmentValue,
            null,
            RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<PersistedRegistration>(json)
                ?? throw new InvalidOperationException(
                    "Maintenance enrollment metadata is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "Maintenance enrollment metadata is invalid.",
                exception);
        }
    }

    private static void WriteRegistration(string statePath, PersistedRegistration registration)
    {
        using var root = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Registry64);
        using var key = root.CreateSubKey(statePath, writable: true)
            ?? throw new InvalidOperationException(
                "Maintenance authority state could not be opened.");
        var security = new RegistrySecurity();
        security.SetSecurityDescriptorSddlForm("D:P(A;;KA;;;SY)(A;;KA;;;BA)");
        key.SetAccessControl(security);
        AssertRegistryAcl(key);
        key.SetValue(
            EnrollmentValue,
            JsonSerializer.Serialize(registration),
            RegistryValueKind.String);
        key.Flush();
    }

    private static void AssertRegistryAcl(RegistryKey key)
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
        };
        if (actual.Length != expected.Count || actual.Any(ace => !expected.Contains(ace)) ||
            actual.Any(ace => ace.Sid == CoreServiceIdentity.ServiceSid))
            throw new InvalidOperationException(
                "Maintenance authority registry ACL permits Core access.");
    }
}

public sealed class InMemoryMaintenanceAttestationKeyProvider
    : IMaintenanceAttestationKeyProvider, IDisposable
{
    private sealed record State(ECDsa Signer, MaintenanceKeyRegistration Registration);
    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public MaintenanceKeyRegistration OpenOrCreate(string authoritativeFingerprint)
    {
        lock (_gate)
        {
            if (_states.TryGetValue(authoritativeFingerprint, out var existing))
                return existing.Registration;
            var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var spki = signer.ExportSubjectPublicKeyInfo();
            var enrollment = new DeviceKeyEnrollment(
                "ES256",
                Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant(),
                Convert.ToBase64String(spki));
            var proof = MaintenanceAttestationKeyProvider.Base64UrlEncode(signer.SignData(
                Encoding.UTF8.GetBytes(
                    MaintenanceAttestationKeyProvider.BuildEnrollmentCanonical(
                        enrollment,
                        authoritativeFingerprint)),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            var registration = new MaintenanceKeyRegistration(enrollment, proof);
            _states[authoritativeFingerprint] = new State(signer, registration);
            return registration;
        }
    }

    public MaintenanceKeyRegistration OpenExisting(string authoritativeFingerprint)
    {
        lock (_gate)
            return _states.TryGetValue(authoritativeFingerprint, out var state)
                ? state.Registration
                : throw new InvalidOperationException("The test maintenance key is missing.");
    }

    public DeviceMaintenanceSignature Sign(
        string authoritativeFingerprint,
        string expectedKeyId,
        ReadOnlyMemory<byte> canonicalBytes)
    {
        lock (_gate)
        {
            var state = _states.TryGetValue(authoritativeFingerprint, out var found)
                ? found
                : throw new InvalidOperationException("The test maintenance key is missing.");
            if (!string.Equals(
                    state.Registration.Enrollment.KeyId,
                    expectedKeyId,
                    StringComparison.Ordinal))
                throw new InvalidOperationException("Test maintenance key identity mismatch.");
            return new(
                state.Registration.Enrollment,
                state.Signer.SignData(
                    canonicalBytes.Span,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }
    }

    public void DestroyForUninstall(string authoritativeFingerprint, string expectedKeyId)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(authoritativeFingerprint, out var state)) return;
            if (!string.Equals(
                    state.Registration.Enrollment.KeyId,
                    expectedKeyId,
                    StringComparison.Ordinal))
                throw new InvalidOperationException("Test maintenance key identity mismatch.");
            _states.Remove(authoritativeFingerprint);
            state.Signer.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var state in _states.Values) state.Signer.Dispose();
            _states.Clear();
        }
    }
}
