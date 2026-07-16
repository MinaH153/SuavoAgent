using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Core.Config;

namespace SuavoAgent.Core.Cloud;

internal sealed record VerifiedRelease1InstallReceipt(
    SignedRelease1InstallReceipt Envelope,
    string InstallReceiptSha256,
    DateTimeOffset InstallCompletedAtUtc);

internal static class Release1InstallReceiptVerifier
{
    private const int MaximumReceiptBytes = 256 * 1024;
    private static readonly string[] ExactCohortKeys =
    [
        "SuavoAgent.Core.exe",
        "SuavoAgent.Broker.exe",
        "SuavoAgent.Helper.exe",
        "SuavoAgent.Watchdog.exe",
        MaintenanceContract.SignedSetupArtifactName,
    ];

    internal static VerifiedRelease1InstallReceipt ReadAndVerify(
        string path,
        Release1ConvergenceChallenge challenge,
        AgentOptions options,
        DateTimeOffset now)
    {
        var verified = ReadAndVerifyLocal(path, options, now);
        ValidateChallengeBinding(verified, challenge, now);
        return verified;
    }

    internal static VerifiedRelease1InstallReceipt ReadAndVerifyLocal(
        string path,
        AgentOptions options,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(options);
        var raw = ReadRegularBounded(path);
        try
        {
            SignedRelease1InstallReceipt envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<SignedRelease1InstallReceipt>(
                    raw,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    ?? throw new InvalidDataException(
                        "Release 1 install receipt is null.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    "Release 1 install receipt JSON is invalid.",
                    exception);
            }
            if (!CryptographicOperations.FixedTimeEquals(
                    raw,
                    Release1ConvergenceContract.CanonicalBytes(envelope)))
                throw new InvalidDataException(
                    "Release 1 install receipt envelope is not canonical.");
            if (envelope.InstallReceipt is not { } installReceipt)
                throw new InvalidDataException(
                    "Release 1 install receipt payload is missing.");

            ValidateLocalReceiptBinding(installReceipt, options, now);
            VerifyMaintenanceSignature(envelope, installReceipt, options);
            var installReceiptSha256 =
                Release1ConvergenceContract.CanonicalSha256(
                    installReceipt);
            var completedAt = ParseExactUtc(
                installReceipt.InstallCompletedAtUtc,
                "install completion time");
            return new(envelope, installReceiptSha256, completedAt);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    private static void ValidateLocalReceiptBinding(
        Release1InstallReceipt receipt,
        AgentOptions options,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(options.MachineFingerprint) ||
            string.IsNullOrWhiteSpace(options.MaintenanceAttestationKeyId) ||
            string.IsNullOrWhiteSpace(options.Version))
            throw new InvalidDataException(
                "Release 1 installed device identity is incomplete.");
        var completedAt = ParseExactUtc(
            receipt.InstallCompletedAtUtc,
            "install completion time");
        if (receipt.SchemaVersion != Release1ConvergenceContract.ReceiptSchemaVersion ||
            !string.Equals(
                receipt.Purpose,
                Release1ConvergenceContract.InstallReceiptPurpose,
                StringComparison.Ordinal) ||
            !FixedTextEquals(
                receipt.HostDigest,
                Release1ConvergenceContract.HostDigest(
                    options.MachineFingerprint)) ||
            !FixedTextEquals(
                receipt.MaintenanceKeyId,
                options.MaintenanceAttestationKeyId) ||
            !UpdateActivationContract.VersionsEquivalent(
                options.Version,
                receipt.InstalledReleaseTag) ||
            !string.Equals(
                receipt.InstallerType,
                Release1ConvergenceContract.MsiInstallerType,
                StringComparison.Ordinal) ||
            !string.Equals(
                receipt.InstallMode,
                Release1ConvergenceContract.FullReinstallMode,
                StringComparison.Ordinal) ||
            !IsLowerHex(receipt.MaintenanceKeyId, 64) ||
            !IsLowerHex(receipt.InstalledSourceSha, 40) ||
            !IsLowerHex(receipt.InstallerArtifactSha256, 64) ||
            !IsLowerHex(receipt.ReleaseReceiptSha256, 64) ||
            !IsLowerHex(receipt.ChecksumsSha256, 64) ||
            !IsLowerHex(receipt.ChecksumsSignatureSha256, 64) ||
            !IsLowerHex(receipt.InstallTransactionId, 64) ||
            !IsLowerHex(receipt.BootIdAtInstall, 64) ||
            completedAt > now.ToUniversalTime())
            throw new InvalidDataException(
                "Release 1 install receipt does not bind this workstation.");
        ValidateInstalledCohort(receipt.InstalledCohort);
    }

    private static void ValidateChallengeBinding(
        VerifiedRelease1InstallReceipt verified,
        Release1ConvergenceChallenge challenge,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        var receipt = verified.Envelope.InstallReceipt;
        var expiresAt = ParseExactUtc(
            challenge.ExpiresAtUtc,
            "challenge expiry time");
        if (!string.Equals(
                receipt.InstalledReleaseTag,
                challenge.BridgeReleaseTag,
                StringComparison.Ordinal) ||
            !FixedTextEquals(
                receipt.InstalledSourceSha,
                challenge.BridgeSourceSha) ||
            verified.InstallCompletedAtUtc > expiresAt ||
            expiresAt < now.ToUniversalTime())
            throw new InvalidDataException(
                "Release 1 install receipt does not bind this challenge.");
    }

    private static void ValidateInstalledCohort(
        IReadOnlyDictionary<string, string> installedCohort)
    {
        if (installedCohort is null ||
            installedCohort.Count != ExactCohortKeys.Length ||
            ExactCohortKeys.Any(key =>
                !installedCohort.TryGetValue(key, out var digest) ||
                !IsLowerHex(digest, 64)) ||
            installedCohort.Keys.Any(key =>
                !ExactCohortKeys.Contains(key, StringComparer.Ordinal)))
            throw new InvalidDataException(
                "Release 1 installed cohort is incomplete or malformed.");
    }

    private static void VerifyMaintenanceSignature(
        SignedRelease1InstallReceipt envelope,
        Release1InstallReceipt receipt,
        AgentOptions options)
    {
        byte[]? spki = null;
        byte[]? signature = null;
        try
        {
            if (string.IsNullOrWhiteSpace(
                    envelope.MaintenancePublicKeySpkiDerBase64) ||
                !string.Equals(
                    Convert.ToBase64String(Convert.FromBase64String(
                        envelope.MaintenancePublicKeySpkiDerBase64)),
                    envelope.MaintenancePublicKeySpkiDerBase64,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "Release 1 maintenance public key is not canonical Base64.");
            spki = Convert.FromBase64String(
                envelope.MaintenancePublicKeySpkiDerBase64);
            var keyId = Convert.ToHexString(SHA256.HashData(spki))
                .ToLowerInvariant();
            if (!FixedTextEquals(
                    keyId,
                    receipt.MaintenanceKeyId) ||
                !FixedTextEquals(
                    keyId,
                    options.MaintenanceAttestationKeyId!))
                throw new InvalidDataException(
                    "Release 1 maintenance public key identity is wrong.");
            signature = DecodeExactBase64UrlP1363(
                envelope.InstallReceiptSignatureBase64Url);
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(spki, out var consumed);
            var parameters = verifier.ExportParameters(includePrivateParameters: false);
            if (consumed != spki.Length ||
                verifier.KeySize != 256 ||
                !string.Equals(
                    parameters.Curve.Oid.Value,
                    "1.2.840.10045.3.1.7",
                    StringComparison.Ordinal) ||
                !verifier.VerifyData(
                    Release1ConvergenceContract.CanonicalBytes(
                        receipt),
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new CryptographicException(
                    "Release 1 install receipt signature is invalid.");
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                "Release 1 install receipt encoding is invalid.",
                exception);
        }
        finally
        {
            if (spki is not null) CryptographicOperations.ZeroMemory(spki);
            if (signature is not null)
                CryptographicOperations.ZeroMemory(signature);
        }
    }

    private static byte[] ReadRegularBounded(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException(
                "Release 1 install receipt is missing.",
                fullPath);
        var attributes = File.GetAttributes(fullPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException(
                "Release 1 install receipt must be a regular file.");
        using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > MaximumReceiptBytes)
            throw new InvalidDataException(
                "Release 1 install receipt exceeds its bound.");
        var raw = new byte[stream.Length];
        stream.ReadExactly(raw);
        if (stream.Position != stream.Length)
            throw new IOException("Release 1 install receipt read was incomplete.");
        return raw;
    }

    internal static DateTimeOffset ParseExactUtc(string value, string label)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed) ||
            !string.Equals(
                Release1ConvergenceContract.ExactUtc(parsed),
                value,
                StringComparison.Ordinal))
            throw new InvalidDataException($"Release 1 {label} is invalid.");
        return parsed;
    }

    internal static byte[] DecodeExactBase64UrlP1363(string value)
    {
        if (value is not { Length: 86 } ||
            value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            throw new InvalidDataException(
                "Release 1 signature is not exact Base64Url P1363.");
        var decoded = Convert.FromBase64String(
            value.Replace('-', '+').Replace('_', '/') + "==");
        if (decoded.Length != 64 ||
            !string.Equals(
                Release1ConvergenceContract.Base64Url(decoded),
                value,
                StringComparison.Ordinal))
        {
            CryptographicOperations.ZeroMemory(decoded);
            throw new InvalidDataException(
                "Release 1 signature is not exact Base64Url P1363.");
        }
        return decoded;
    }

    internal static bool IsLowerHex(string? value, int length) =>
        value is not null && value.Length == length && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool FixedTextEquals(string? left, string? right) =>
        left is not null && right is not null && left.Length == right.Length &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left),
            Encoding.UTF8.GetBytes(right));
}
