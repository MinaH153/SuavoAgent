using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SuavoAgent.Contracts.Maintenance;

namespace SuavoAgent.Diagnostics.Maintenance;

public enum MaintenanceTrustSource
{
    None = 0,
    SignedReleaseChecksums,
    SignedOtaManifest,
}

public sealed record MaintenanceHostTrustResult(
    bool IsTrusted,
    MaintenanceTrustSource Source,
    string Code,
    string? ExecutableSha256 = null)
{
    internal static MaintenanceHostTrustResult Trusted(
        MaintenanceTrustSource source,
        ReadOnlySpan<byte> executableSha256)
    {
        if (executableSha256.Length != 32)
            throw new ArgumentException(
                "Trusted maintenance digest must be SHA-256.",
                nameof(executableSha256));
        return new(
            true,
            source,
            "trusted",
            Convert.ToHexString(executableSha256).ToLowerInvariant());
    }

    internal static MaintenanceHostTrustResult Rejected(string code) =>
        new(false, MaintenanceTrustSource.None, code);
}

/// <summary>
/// Proves that the privileged native maintenance host is a byte-for-byte member of
/// a release signed by Suavo's production update key. Authenticode alone is not used
/// here: this verifier binds the exact on-disk hash to the same signed release/OTA
/// receipts that authorize the rest of the agent cohort.
/// </summary>
public static partial class MaintenanceHostTrustVerifier
{
    // ECDSA P-256 SubjectPublicKeyInfo DER, Base64. This is the production update
    // signing key also used by release checksums and package OTA manifests.
    public const string ProductionUpdatePublicKeyDer =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEBLRvZ572EpqNab9CxJ9/b/GfHpHOrhWkpaaCzIkXQ5d2dwiqdJHlxvrgN0/zCsgp/ccnDXed4DFCkh6wUWCvWA==";

    private const int MaxChecksumsBytes = 1024 * 1024;
    private const int MaxOtaManifestBytes = 64 * 1024;
    private const int MaxSignatureBytes = 1024;
    private const int OtaFieldCount = 13;
    private const int OtaMaintenanceUrlIndex = 11;
    private const int OtaMaintenanceHashIndex = 12;

    [GeneratedRegex(
        "^(?<hash>[0-9A-Fa-f]{64})[ \\t]+\\*?(?<name>[^\\r\\n]+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ChecksumLineRegex();

    public static MaintenanceHostTrustResult Verify(string maintenanceExecutablePath) =>
        Verify(
            maintenanceExecutablePath,
            ProductionUpdatePublicKeyDer,
            AuthenticodePublisherVerifier.Verify);

    /// <summary>
    /// Key-injectable verification seam for rotation rehearsals and generated-key
    /// tests. Production callers use <see cref="Verify(string)"/>.
    /// </summary>
    public static MaintenanceHostTrustResult Verify(
        string maintenanceExecutablePath,
        string publicKeyDerBase64) =>
        Verify(
            maintenanceExecutablePath,
            publicKeyDerBase64,
            AuthenticodePublisherVerifier.Verify);

    internal static MaintenanceHostTrustResult Verify(
        string maintenanceExecutablePath,
        string publicKeyDerBase64,
        Func<string, AuthenticodePublisherTrust> verifyAuthenticode)
    {
        ArgumentNullException.ThrowIfNull(verifyAuthenticode);
        try
        {
            if (string.IsNullOrWhiteSpace(maintenanceExecutablePath) ||
                !Path.IsPathFullyQualified(maintenanceExecutablePath) ||
                !string.Equals(
                    Path.GetFileName(maintenanceExecutablePath),
                    MaintenanceContract.ExecutableName,
                    StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(maintenanceExecutablePath))
            {
                return MaintenanceHostTrustResult.Rejected("maintenance_host_invalid");
            }

            var installDir = Path.GetDirectoryName(Path.GetFullPath(maintenanceExecutablePath));
            if (string.IsNullOrWhiteSpace(installDir))
                return MaintenanceHostTrustResult.Rejected("maintenance_directory_invalid");

            var publisher = verifyAuthenticode(maintenanceExecutablePath);
            if (!publisher.IsTrusted)
                return MaintenanceHostTrustResult.Rejected(
                    "maintenance_" + publisher.Code);

            using var key = ImportP256PublicKey(publicKeyDerBase64);
            var hostHash = ComputeSha256(maintenanceExecutablePath);

            var release = VerifyReceiptSafely(
                () => VerifyReleaseReceipt(installDir, hostHash, key),
                "release");
            if (release.IsTrusted) return release;

            var ota = VerifyReceiptSafely(
                () => VerifyOtaReceipt(installDir, hostHash, key),
                "ota");
            if (ota.IsTrusted) return ota;

            if (release.Code == "release_receipt_missing" && ota.Code == "ota_receipt_missing")
                return MaintenanceHostTrustResult.Rejected("signed_receipt_missing");

            return MaintenanceHostTrustResult.Rejected(
                $"release={release.Code};ota={ota.Code}");
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            CryptographicException or
            FormatException or
            ArgumentException or
            DecoderFallbackException)
        {
            return MaintenanceHostTrustResult.Rejected($"trust_proof_unreadable:{ex.GetType().Name}");
        }
    }

    private static MaintenanceHostTrustResult VerifyReceiptSafely(
        Func<MaintenanceHostTrustResult> verify,
        string receiptKind)
    {
        try
        {
            return verify();
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            CryptographicException or
            FormatException or
            ArgumentException or
            DecoderFallbackException)
        {
            return MaintenanceHostTrustResult.Rejected(
                $"{receiptKind}_receipt_unreadable:{ex.GetType().Name}");
        }
    }

    private static MaintenanceHostTrustResult VerifyReleaseReceipt(
        string installDir,
        byte[] hostHash,
        ECDsa key)
    {
        var checksumsPath = Path.Combine(installDir, MaintenanceContract.ReleaseChecksumsFileName);
        var signaturePath = Path.Combine(installDir, MaintenanceContract.ReleaseChecksumsSignatureFileName);
        var checksumsPresent = File.Exists(checksumsPath);
        var signaturePresent = File.Exists(signaturePath);
        if (!checksumsPresent && !signaturePresent)
            return MaintenanceHostTrustResult.Rejected("release_receipt_missing");
        if (!checksumsPresent || !signaturePresent)
            return MaintenanceHostTrustResult.Rejected("release_receipt_incomplete");

        var checksums = ReadBounded(checksumsPath, MaxChecksumsBytes);
        var signature = ReadBounded(signaturePath, MaxSignatureBytes);
        if (signature.Length == 0 ||
            !key.VerifyData(
                checksums,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence))
        {
            return MaintenanceHostTrustResult.Rejected("release_signature_invalid");
        }

        // Windows PowerShell 5.1's UTF8 writer may prefix a BOM. It remains part
        // of the signed bytes; remove it only from the post-verification parser.
        var text = new UTF8Encoding(false, true).GetString(checksums).TrimStart('\uFEFF');
        byte[]? signedSetupHash = null;
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;
            var match = ChecksumLineRegex().Match(line);
            if (!match.Success)
                return MaintenanceHostTrustResult.Rejected("release_checksums_malformed");

            var name = match.Groups["name"].Value.Trim();
            if (name.Length == 0 || !seenNames.Add(name))
                return MaintenanceHostTrustResult.Rejected("release_checksums_duplicate_or_empty_name");
            if (!string.Equals(
                    name,
                    MaintenanceContract.SignedSetupArtifactName,
                    StringComparison.OrdinalIgnoreCase))
                continue;

            signedSetupHash = Convert.FromHexString(match.Groups["hash"].Value);
        }

        if (signedSetupHash is null)
            return MaintenanceHostTrustResult.Rejected("release_setup_hash_missing");
        if (!CryptographicOperations.FixedTimeEquals(hostHash, signedSetupHash))
            return MaintenanceHostTrustResult.Rejected("release_setup_hash_mismatch");

        return MaintenanceHostTrustResult.Trusted(
            MaintenanceTrustSource.SignedReleaseChecksums,
            hostHash);
    }

    private static MaintenanceHostTrustResult VerifyOtaReceipt(
        string installDir,
        byte[] hostHash,
        ECDsa key)
    {
        var manifestPath = Path.Combine(installDir, MaintenanceContract.CurrentOtaManifestFileName);
        var signaturePath = Path.Combine(installDir, MaintenanceContract.CurrentOtaManifestSignatureFileName);
        var manifestPresent = File.Exists(manifestPath);
        var signaturePresent = File.Exists(signaturePath);
        if (!manifestPresent && !signaturePresent)
            return MaintenanceHostTrustResult.Rejected("ota_receipt_missing");
        if (!manifestPresent || !signaturePresent)
            return MaintenanceHostTrustResult.Rejected("ota_receipt_incomplete");

        var manifestBytes = ReadBounded(manifestPath, MaxOtaManifestBytes);
        var canonical = new UTF8Encoding(false, true).GetString(manifestBytes);
        if (canonical.Length == 0 || !string.Equals(canonical, canonical.Trim(), StringComparison.Ordinal))
            return MaintenanceHostTrustResult.Rejected("ota_manifest_not_canonical");

        var fields = canonical.Split('|');
        if (fields.Length != OtaFieldCount)
            return MaintenanceHostTrustResult.Rejected("ota_manifest_wrong_field_count");
        if (fields.Any(string.IsNullOrWhiteSpace) || fields.Any(ContainsControlCharacter))
            return MaintenanceHostTrustResult.Rejected("ota_manifest_invalid_field");

        foreach (var index in new[] { 1, 3, 5, 10, OtaMaintenanceHashIndex })
            if (!IsSha256Hex(fields[index]))
                return MaintenanceHostTrustResult.Rejected("ota_manifest_invalid_hash");

        foreach (var index in new[] { 0, 2, 4, 9, OtaMaintenanceUrlIndex })
            if (!Uri.TryCreate(fields[index], UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                return MaintenanceHostTrustResult.Rejected("ota_manifest_invalid_url");

        if (!string.Equals(fields[7], "net8.0", StringComparison.Ordinal) ||
            !string.Equals(fields[8], "win-x64", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                Path.GetFileName(new Uri(fields[OtaMaintenanceUrlIndex]).AbsolutePath),
                MaintenanceContract.SignedSetupArtifactName,
                StringComparison.OrdinalIgnoreCase))
        {
            return MaintenanceHostTrustResult.Rejected("ota_manifest_wrong_maintenance_target");
        }

        var signatureText = new UTF8Encoding(false, true)
            .GetString(ReadBounded(signaturePath, MaxSignatureBytes))
            .Trim();
        if (signatureText.Length != 128 || !signatureText.All(Uri.IsHexDigit))
            return MaintenanceHostTrustResult.Rejected("ota_signature_malformed");
        var signature = Convert.FromHexString(signatureText);
        if (!key.VerifyData(
                manifestBytes,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            return MaintenanceHostTrustResult.Rejected("ota_signature_invalid");
        }

        var signedMaintenanceHash = Convert.FromHexString(fields[OtaMaintenanceHashIndex]);
        if (!CryptographicOperations.FixedTimeEquals(hostHash, signedMaintenanceHash))
            return MaintenanceHostTrustResult.Rejected("ota_maintenance_hash_mismatch");

        return MaintenanceHostTrustResult.Trusted(
            MaintenanceTrustSource.SignedOtaManifest,
            hostHash);
    }

    private static ECDsa ImportP256PublicKey(string publicKeyDerBase64)
    {
        var keyBytes = Convert.FromBase64String(publicKeyDerBase64);
        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(keyBytes, out var bytesRead);
            if (bytesRead != keyBytes.Length || key.KeySize != 256)
                throw new CryptographicException("Update signing key is not an exact P-256 SPKI value.");
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private static byte[] ReadBounded(string path, int maxBytes)
    {
        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > maxBytes)
            throw new InvalidDataException("Signed receipt has an invalid length.");
        return File.ReadAllBytes(path);
    }

    private static byte[] ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }

    private static bool IsSha256Hex(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool ContainsControlCharacter(string value) =>
        value.Any(char.IsControl);
}
