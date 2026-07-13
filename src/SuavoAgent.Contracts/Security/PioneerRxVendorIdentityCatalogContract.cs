using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SuavoAgent.Contracts.Maintenance;

namespace SuavoAgent.Contracts.Security;

public sealed record PioneerRxVendorIdentityEntry(
    [property: JsonPropertyName("entryId")] string EntryId,
    [property: JsonPropertyName("processName")] string ProcessName,
    [property: JsonPropertyName("productName")] string ProductName,
    [property: JsonPropertyName("authenticodeSignerSubject")] string AuthenticodeSignerSubject,
    [property: JsonPropertyName("signerCertificateSha256")] string SignerCertificateSha256,
    [property: JsonPropertyName("allowedInstallRoots")] string[] AllowedInstallRoots,
    [property: JsonPropertyName("supportedFileVersions")] string[] SupportedFileVersions);

public sealed record PioneerRxVendorIdentityCatalog(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("catalogId")] string CatalogId,
    [property: JsonPropertyName("entries")] PioneerRxVendorIdentityEntry[] Entries,
    [property: JsonPropertyName("issuedAtUtc")] string IssuedAtUtc,
    [property: JsonPropertyName("expiresAtUtc")] string ExpiresAtUtc,
    [property: JsonPropertyName("cloudKeyId")] string CloudKeyId,
    [property: JsonPropertyName("cloudSignature")] string CloudSignature);

public static partial class PioneerRxVendorIdentityCatalogContract
{
    public const int SchemaVersion = 1;
    public const string CanonicalPrefix = "suavo.pioneerrx-vendor-catalog.v1";
    public const string InstalledFileName = "pioneerrx-vendor-catalog.json";

    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex LowerHex64();
    [GeneratedRegex("^[A-Za-z0-9_-]{86}$", RegexOptions.CultureInvariant)]
    private static partial Regex Signature86();
    [GeneratedRegex("^[A-Za-z]:\\\\[^|,\\r\\n]{1,500}\\\\$", RegexOptions.CultureInvariant)]
    private static partial Regex InstallRoot();

    public static string Canonical(PioneerRxVendorIdentityCatalog catalog) => string.Join('|',
        CanonicalPrefix,
        catalog.SchemaVersion,
        catalog.CatalogId,
        string.Join(';', (catalog.Entries ?? []).Select(Canonical)),
        catalog.IssuedAtUtc,
        catalog.ExpiresAtUtc,
        catalog.CloudKeyId);

    public static string Canonical(PioneerRxVendorIdentityEntry entry) => string.Join('~',
        entry.EntryId,
        entry.ProcessName,
        entry.ProductName,
        entry.AuthenticodeSignerSubject,
        entry.SignerCertificateSha256,
        string.Join(',', entry.AllowedInstallRoots ?? []),
        string.Join(',', entry.SupportedFileVersions ?? []));

    public static bool TryValidateAndMatch(
        PioneerRxVendorIdentityCatalog? catalog,
        PioneerRxProcessApprovalReceipt receipt,
        DateTimeOffset now,
        out string code) =>
        TryValidateAndMatch(
            catalog,
            receipt,
            now,
            RemoteCommandTrust.CreateProductionKeyRegistry(),
            out code);

    public static bool TryValidateAndMatch(
        PioneerRxVendorIdentityCatalog? catalog,
        PioneerRxProcessApprovalReceipt receipt,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string> trustedCloudKeys,
        out string code) => TryValidateAndMatchCore(
            catalog,
            receipt,
            now,
            trustedCloudKeys,
            requireCurrentlyActive: true,
            out code);

    public static bool TryValidateHistoricalAndMatch(
        PioneerRxVendorIdentityCatalog? catalog,
        PioneerRxProcessApprovalReceipt receipt,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string> trustedCloudKeys,
        out string code) => TryValidateAndMatchCore(
            catalog,
            receipt,
            now,
            trustedCloudKeys,
            requireCurrentlyActive: false,
            out code);

    public static bool TryValidate(
        PioneerRxVendorIdentityCatalog? catalog,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string> trustedCloudKeys,
        out string code) => TryValidateCatalogCore(
            catalog,
            now,
            trustedCloudKeys,
            requireCurrentlyActive: true,
            out code);

    public static bool TryMatchEvidence(
        PioneerRxVendorIdentityCatalog catalog,
        string processName,
        string productName,
        string authenticodeSignerSubject,
        string signerCertificateSha256,
        string canonicalExecutablePath,
        string fileVersion,
        out string code)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var matched = catalog.Entries.Any(entry =>
            string.Equals(entry.ProcessName, processName, StringComparison.Ordinal) &&
            string.Equals(entry.ProductName, productName, StringComparison.Ordinal) &&
            string.Equals(
                entry.AuthenticodeSignerSubject,
                authenticodeSignerSubject,
                StringComparison.Ordinal) &&
            FixedHexEquals(entry.SignerCertificateSha256, signerCertificateSha256) &&
            Array.BinarySearch(entry.SupportedFileVersions, fileVersion, StringComparer.Ordinal) >= 0 &&
            entry.AllowedInstallRoots.Any(root =>
                canonicalExecutablePath.StartsWith(root, StringComparison.OrdinalIgnoreCase)));
        code = matched ? "approved" : "approval_vendor_identity_unknown";
        return matched;
    }

    private static bool TryValidateAndMatchCore(
        PioneerRxVendorIdentityCatalog? catalog,
        PioneerRxProcessApprovalReceipt receipt,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string> trustedCloudKeys,
        bool requireCurrentlyActive,
        out string code)
    {
        if (!TryValidateCatalogCore(
                catalog,
                now,
                trustedCloudKeys,
                requireCurrentlyActive,
                out code))
            return false;
        if (!string.Equals(catalog!.CatalogId, receipt.VendorCatalogId, StringComparison.Ordinal))
        {
            code = "approval_vendor_catalog_mismatch";
            return false;
        }
        return TryMatchEvidence(
            catalog,
            receipt.ProcessName,
            receipt.ProductName,
            receipt.AuthenticodeSignerSubject,
            receipt.SignerCertificateSha256,
            receipt.CanonicalExecutablePath,
            receipt.FileVersion,
            out code);
    }

    private static bool TryValidateCatalogCore(
        PioneerRxVendorIdentityCatalog? catalog,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string> trustedCloudKeys,
        bool requireCurrentlyActive,
        out string code)
    {
        code = "approval_vendor_catalog_invalid";
        if (catalog is null)
        {
            code = "approval_vendor_catalog_missing";
            return false;
        }
        if (catalog.SchemaVersion != SchemaVersion ||
            !CanonicalUuid(catalog.CatalogId) ||
            catalog.Entries is not { Length: > 0 and <= 128 } ||
            !string.Equals(catalog.CloudKeyId, RemoteCommandTrust.CommandV1KeyId, StringComparison.Ordinal) ||
            !Signature86().IsMatch(catalog.CloudSignature ?? string.Empty) ||
            !ExactUtc(catalog.IssuedAtUtc, out var issued) ||
            !ExactUtc(catalog.ExpiresAtUtc, out var expires) ||
            issued > now.AddMinutes(5) || expires <= issued ||
            expires - issued > TimeSpan.FromDays(90) ||
            requireCurrentlyActive && now >= expires ||
            !EntriesValid(catalog.Entries))
            return false;
        if (!trustedCloudKeys.TryGetValue(catalog.CloudKeyId, out var publicKey) ||
            !VerifySignature(Canonical(catalog), catalog.CloudSignature!, publicKey))
        {
            code = "approval_vendor_catalog_signature_invalid";
            return false;
        }
        code = "approval_vendor_catalog_valid";
        return true;
    }

    private static bool EntriesValid(PioneerRxVendorIdentityEntry[] entries)
    {
        var prior = string.Empty;
        foreach (var entry in entries)
        {
            if (!CanonicalUuid(entry.EntryId) ||
                string.CompareOrdinal(prior, entry.EntryId) >= 0 ||
                !Safe(entry.ProcessName, 128) || !Safe(entry.ProductName, 256) ||
                !Safe(entry.AuthenticodeSignerSubject, 512) ||
                !LowerHex64().IsMatch(entry.SignerCertificateSha256 ?? string.Empty) ||
                !SortedUnique(entry.AllowedInstallRoots, value => InstallRoot().IsMatch(value)) ||
                !SortedUnique(entry.SupportedFileVersions, value => Safe(value, 128)))
                return false;
            prior = entry.EntryId;
        }
        return true;
    }

    private static bool SortedUnique(string[]? values, Func<string, bool> validate)
    {
        if (values is not { Length: > 0 and <= 64 }) return false;
        for (var index = 0; index < values.Length; index++)
            if (!validate(values[index]) ||
                index > 0 && string.CompareOrdinal(values[index - 1], values[index]) >= 0)
                return false;
        return true;
    }

    private static bool VerifySignature(string canonical, string signature, string publicKey)
    {
        byte[]? signatureBytes = null;
        try
        {
            var normalized = signature.Replace('-', '+').Replace('_', '/') + "==";
            signatureBytes = Convert.FromBase64String(normalized);
            using var verifier = ECDsa.Create();
            var spki = Convert.FromBase64String(publicKey);
            verifier.ImportSubjectPublicKeyInfo(spki, out var consumed);
            return consumed == spki.Length && verifier.KeySize == 256 && signatureBytes.Length == 64 &&
                   verifier.VerifyData(
                       Encoding.UTF8.GetBytes(canonical),
                       signatureBytes,
                       HashAlgorithmName.SHA256,
                       DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (signatureBytes is not null) CryptographicOperations.ZeroMemory(signatureBytes);
        }
    }

    private static bool FixedHexEquals(string left, string right)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(left), Convert.FromHexString(right));
        }
        catch { return false; }
    }

    private static bool ExactUtc(string value, out DateTimeOffset parsed)
    {
        parsed = default;
        return DateTimeOffset.TryParseExact(
            value,
            PioneerRxProcessApprovalContract.UtcTimestampFormat,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal |
            System.Globalization.DateTimeStyles.AdjustToUniversal,
            out parsed);
    }

    private static bool Safe(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= max &&
        !value.Any(ch => char.IsControl(ch) || ch is '|' or '~' or ',' or ';');

    private static bool CanonicalUuid(string? value) =>
        value is { Length: 36 } && Guid.TryParseExact(value, "D", out var parsed) &&
        string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal);
}
