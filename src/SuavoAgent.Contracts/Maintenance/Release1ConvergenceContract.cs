using System.Security.Cryptography;
using System.Security;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Win32;

namespace SuavoAgent.Contracts.Maintenance;

public sealed class Release1BootIdentityUnavailableException : InvalidOperationException
{
    public Release1BootIdentityUnavailableException(string message)
        : base(message) { }

    public Release1BootIdentityUnavailableException(string message, Exception innerException)
        : base(message, innerException) { }
}

public sealed record Release1InstallReceipt(
    int SchemaVersion,
    string Purpose,
    string HostDigest,
    string MaintenanceKeyId,
    string InstalledReleaseTag,
    string InstalledSourceSha,
    string InstallerType,
    string InstallerArtifactSha256,
    string ReleaseReceiptSha256,
    string ChecksumsSha256,
    string ChecksumsSignatureSha256,
    IReadOnlyDictionary<string, string> InstalledCohort,
    string InstallTransactionId,
    string InstallCompletedAtUtc,
    string BootIdAtInstall,
    string InstallMode);

public sealed record SignedRelease1InstallReceipt(
    Release1InstallReceipt InstallReceipt,
    string InstallReceiptSignatureBase64Url,
    string MaintenancePublicKeySpkiDerBase64);

public sealed record Release1MsiInstallCommitMarker(
    int SchemaVersion,
    string Purpose,
    string InstalledReleaseTag,
    string MaintenanceHostSha256,
    string InstallerArtifactSha256,
    string ProductCode,
    string InstallTransactionId,
    string InstallCompletedAtUtc,
    string BootTokenAtInstall);

public sealed record Release1RestartReceipt(
    int SchemaVersion,
    string Purpose,
    string HostDigest,
    string InstallReceiptSha256,
    string BootIdBeforeRestart,
    string BootIdAfterRestart,
    string RunningReleaseTag,
    string RunningSourceSha,
    string Outcome,
    string RestartObservedAtUtc);

public sealed record Release1V1NoopRehearsalReceipt(
    int SchemaVersion,
    string Purpose,
    string HostDigest,
    string InventorySha256,
    string InstallReceiptSha256,
    string RestartReceiptSha256,
    string InstalledReleaseTag,
    string InstalledSourceSha,
    string OtaSigningKeyId,
    string UpdateManifestName,
    string UpdateManifestCanonical,
    string UpdateManifestSignatureP1363Hex,
    string ChecksumsSha256,
    string ChecksumsSignatureSha256,
    string Outcome,
    string ObservedAtUtc);

public sealed record Release1PreliminaryConvergenceProof(
    int SchemaVersion,
    string Purpose,
    string AttestationAuthority,
    string AttestationKeyId,
    string HostDigest,
    string InventorySha256,
    Release1InstallReceipt InstallReceipt,
    string InstallReceiptSha256,
    string InstallReceiptSignatureBase64Url,
    Release1RestartReceipt RestartReceipt,
    string RestartReceiptSha256,
    string VerifiedAtUtc,
    string PhiClassification);

public sealed record SignedRelease1PreliminaryConvergenceProof(
    Release1PreliminaryConvergenceProof Proof,
    string ProofSignatureBase64Url);

public sealed record Release1DeviceConvergenceAttestation(
    int SchemaVersion,
    string Purpose,
    string AttestationAuthority,
    string AttestationKeyId,
    string HostDigest,
    string InventorySha256,
    Release1InstallReceipt InstallReceipt,
    string InstallReceiptSha256,
    Release1RestartReceipt RestartReceipt,
    string RestartReceiptSha256,
    Release1V1NoopRehearsalReceipt V1NoopRehearsalReceipt,
    string V1NoopRehearsalReceiptSha256,
    string VerifiedAtUtc,
    string PhiClassification);

public sealed record SignedRelease1DeviceConvergenceEvidence(
    Release1DeviceConvergenceAttestation Attestation,
    string AttestationSignatureBase64Url,
    string InstallReceiptSignatureBase64Url);

/// <summary>
/// Closed, PHI-negative wire contract used by the one-time Release 1 to v2
/// signing-root convergence ceremony. Canonical JSON is recursively sorted,
/// compact UTF-8 with exactly one trailing LF, matching the offline verifier.
/// </summary>
public static class Release1ConvergenceContract
{
    public const int InventorySchemaVersion = 3;
    public const int EvidenceBundleSchemaVersion = 4;
    public const int DeviceAttestationSchemaVersion = 2;
    public const int ReceiptSchemaVersion = 1;
    public const int MsiInstallCommitMarkerSchemaVersion = 2;
    public const int PreliminaryProofSchemaVersion = 1;
    public const string InstallReceiptFileName =
        "release1-convergence-install-receipt.json";
    public const string MsiInstallCommitMarkerFileName =
        "release1-msi-install-commit.json";
    public const string InstallProofRootDirectoryName =
        "SuavoAgent-InstallerProof";
    public const string PendingEvidenceFileName =
        "release1-convergence-evidence.json";
    public const string AttestationPurpose =
        "suavoagent-release1-device-convergence-attestation";
    public const string AttestationAuthority =
        "enrolled-device-attestation-key-v1";
    public const string InstallReceiptPurpose =
        "suavoagent-release1-full-installer-receipt";
    public const string MsiInstallCommitMarkerPurpose =
        "suavoagent-msi-install-commit-marker";
    public const string RestartReceiptPurpose =
        "suavoagent-release1-post-install-restart-receipt";
    public const string PreliminaryProofPurpose =
        "suavoagent-release1-preliminary-convergence-proof";
    public const string V1NoopReceiptPurpose =
        "suavoagent-release1-v1-noop-rehearsal-receipt";
    public const string RestartOutcome = "release1-active-after-restart";
    public const string V1NoopOutcome = "already-current-noop";
    public const string FullReinstallMode = "full-reinstall";
    public const string PhiClassification = "phi-negative";
    public const string BurnInstallerType = "burn";
    public const string MsiInstallerType = "msi";

    public static string ReleaseMsiArtifactName(string releaseTag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseTag);
        var normalized = releaseTag.StartsWith('v') || releaseTag.StartsWith('V')
            ? "v" + releaseTag[1..]
            : "v" + releaseTag;
        return $"SuavoAgent-{normalized}-win-x64.msi";
    }

    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static string HostDigest(string machineFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineFingerprint);
        return Sha256(Encoding.UTF8.GetBytes(machineFingerprint));
    }

    public static string CurrentBootId(string machineFingerprint)
        => BootIdFromToken(machineFingerprint, CurrentBootToken());

    public static string CurrentBootToken()
    {
        if (!OperatingSystem.IsWindows())
            throw new Release1BootIdentityUnavailableException(
                "Release 1 install proof requires the Windows boot identifier.");

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters",
                writable: false);
            var value = key?.GetValue(
                "BootId",
                null,
                RegistryValueOptions.DoNotExpandEnvironmentNames);
            return WindowsBootToken(value);
        }
        catch (Release1BootIdentityUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            UnauthorizedAccessException or IOException or SecurityException)
        {
            throw new Release1BootIdentityUnavailableException(
                "Windows did not expose a stable boot identifier; Release 1 install proof was refused.",
                exception);
        }
    }

    internal static string WindowsBootToken(object? bootId) => bootId switch
    {
        int signed => $"windows-boot-id:{unchecked((uint)signed)}",
        long wide when wide >= 0 => $"windows-boot-id:{wide}",
        _ => throw new Release1BootIdentityUnavailableException(
            "Windows did not expose a stable boot identifier; Release 1 install proof was refused."),
    };

    public static string BootIdFromToken(
        string machineFingerprint,
        string bootToken)
    {
        var hostDigest = HostDigest(machineFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(bootToken);
        if (bootToken.Length > 96 ||
            bootToken.Any(character => character is < ' ' or > '~' or '|'))
            throw new ArgumentException("Boot token is invalid.", nameof(bootToken));
        return Sha256(Encoding.ASCII.GetBytes(
            $"{hostDigest}|{bootToken}"));
    }

    public static byte[] CanonicalBytes<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var element = JsonSerializer.SerializeToElement(value, JsonOptions);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false,
        }))
        {
            WriteSorted(writer, element);
        }
        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    public static string CanonicalSha256<T>(T value) => Sha256(CanonicalBytes(value));

    public static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string ExactUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    private static string Sha256(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static void WriteSorted(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject()
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteSorted(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    WriteSorted(writer, item);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}
