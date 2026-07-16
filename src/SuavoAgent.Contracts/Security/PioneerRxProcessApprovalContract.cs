using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SuavoAgent.Contracts.Maintenance;

namespace SuavoAgent.Contracts.Security;

/// <summary>
/// Immutable, maintenance-key-signed local approval for one exact PioneerRx executable
/// identity on one pharmacy workstation. The Core service cannot use this SYSTEM-only
/// TPM key; Setup constructs the evidence and a privileged human cloud transition co-signs it.
/// </summary>
public sealed record PioneerRxProcessApprovalReceipt(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("receiptId")] string ReceiptId,
    [property: JsonPropertyName("pharmacyId")] string PharmacyId,
    [property: JsonPropertyName("machineFingerprint")] string MachineFingerprint,
    [property: JsonPropertyName("maintenanceKeyId")] string MaintenanceKeyId,
    [property: JsonPropertyName("maintenancePublicKeySpki")] string MaintenancePublicKeySpki,
    [property: JsonPropertyName("processName")] string ProcessName,
    [property: JsonPropertyName("canonicalExecutablePath")] string CanonicalExecutablePath,
    [property: JsonPropertyName("executableSha256")] string ExecutableSha256,
    [property: JsonPropertyName("authenticodeSignerSubject")] string AuthenticodeSignerSubject,
    [property: JsonPropertyName("signerCertificateSha256")] string SignerCertificateSha256,
    [property: JsonPropertyName("productName")] string ProductName,
    [property: JsonPropertyName("fileVersion")] string FileVersion,
    [property: JsonPropertyName("vendorCatalogId")] string VendorCatalogId,
    [property: JsonPropertyName("sqlServerCertificateSha256")] string SqlServerCertificateSha256,
    [property: JsonPropertyName("approvedBySid")] string ApprovedBySid,
    [property: JsonPropertyName("localApprovalEvidenceDigest")] string LocalApprovalEvidenceDigest,
    [property: JsonPropertyName("approvalNonce")] string ApprovalNonce,
    [property: JsonPropertyName("approvalCounter")] long ApprovalCounter,
    [property: JsonPropertyName("approvedBaaScopeTags")] string[] ApprovedBaaScopeTags,
    [property: JsonPropertyName("approvedAtUtc")] string ApprovedAtUtc,
    [property: JsonPropertyName("expiresAtUtc")] string ExpiresAtUtc,
    [property: JsonPropertyName("revokedAtUtc")] string? RevokedAtUtc,
    [property: JsonPropertyName("cloudKeyId")] string CloudKeyId,
    [property: JsonPropertyName("cloudCoApprovalSignature")] string CloudCoApprovalSignature,
    [property: JsonPropertyName("maintenanceSignature")] string MaintenanceSignature);

/// <summary>
/// Short-lived cloud-signed authority state selecting exactly one active local approval counter and
/// receipt. Advancing the counter or listing a receipt id revokes stale local receipts without
/// trusting mutable ProgramData configuration.
/// </summary>
public sealed record PioneerRxApprovalAuthorityState(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("pharmacyId")] string PharmacyId,
    [property: JsonPropertyName("machineFingerprint")] string MachineFingerprint,
    [property: JsonPropertyName("activeReceiptId")] string ActiveReceiptId,
    [property: JsonPropertyName("currentApprovalCounter")] long CurrentApprovalCounter,
    [property: JsonPropertyName("revokedReceiptIds")] string[] RevokedReceiptIds,
    [property: JsonPropertyName("issuedAtUtc")] string IssuedAtUtc,
    [property: JsonPropertyName("expiresAtUtc")] string ExpiresAtUtc,
    [property: JsonPropertyName("cloudKeyId")] string CloudKeyId,
    [property: JsonPropertyName("cloudSignature")] string CloudSignature);

public static partial class PioneerRxProcessApprovalContract
{
    public const int CurrentSchemaVersion = 2;
    public const string CanonicalPrefix = "suavo.pioneerrx-process-approval.v2";
    public const string AuthorityCanonicalPrefix = "suavo.pioneerrx-approval-authority.v2";
    public const string UtcTimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
    public static readonly TimeSpan MaximumApprovalLifetime = TimeSpan.FromDays(30);

    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex LowerHex64();

    [GeneratedRegex("^S-1-[0-9]+(?:-[0-9]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex SidPattern();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.:-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ScopePattern();

    [GeneratedRegex("""^[A-Za-z]:\\(?!.*(?:^|\\)\.\.(?:\\|$))[^|\r\n]{1,1018}\.exe$""", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalWindowsExePath();

    [GeneratedRegex("^[A-Za-z0-9_-]{86}$", RegexOptions.CultureInvariant)]
    private static partial Regex P256SignatureEncoding();

    public static string Canonical(PioneerRxProcessApprovalReceipt receipt) => string.Join('|',
        CanonicalPrefix,
        receipt.SchemaVersion,
        receipt.ReceiptId,
        receipt.PharmacyId,
        receipt.MachineFingerprint,
        receipt.MaintenanceKeyId,
        receipt.ProcessName,
        receipt.CanonicalExecutablePath,
        receipt.ExecutableSha256,
        receipt.AuthenticodeSignerSubject,
        receipt.SignerCertificateSha256,
        receipt.ProductName,
        receipt.FileVersion,
        receipt.VendorCatalogId,
        receipt.SqlServerCertificateSha256,
        receipt.ApprovedBySid,
        receipt.LocalApprovalEvidenceDigest,
        receipt.ApprovalNonce,
        receipt.ApprovalCounter,
        CanonicalScopes(receipt.ApprovedBaaScopeTags),
        receipt.ApprovedAtUtc,
        receipt.ExpiresAtUtc,
        receipt.RevokedAtUtc ?? string.Empty,
        receipt.CloudKeyId);

    public static string Canonical(PioneerRxApprovalAuthorityState state) => string.Join('|',
        AuthorityCanonicalPrefix,
        state.SchemaVersion,
        state.PharmacyId,
        state.MachineFingerprint,
        state.ActiveReceiptId,
        state.CurrentApprovalCounter,
        string.Join(',', state.RevokedReceiptIds ?? Array.Empty<string>()),
        state.IssuedAtUtc,
        state.ExpiresAtUtc,
        state.CloudKeyId);

    public static bool TryValidate(
        PioneerRxProcessApprovalReceipt? receipt,
        PioneerRxVendorIdentityCatalog? vendorCatalog,
        string expectedPharmacyId,
        string expectedMachineFingerprint,
        string expectedMaintenanceKeyId,
        DateTimeOffset now,
        out string code) => TryValidate(
            receipt,
            vendorCatalog,
            expectedPharmacyId,
            expectedMachineFingerprint,
            expectedMaintenanceKeyId,
            now,
            RemoteCommandTrust.CreateProductionKeyRegistry(),
            out code);

    public static bool TryValidate(
        PioneerRxProcessApprovalReceipt? receipt,
        PioneerRxVendorIdentityCatalog? vendorCatalog,
        string expectedPharmacyId,
        string expectedMachineFingerprint,
        string expectedMaintenanceKeyId,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string> trustedCloudKeys,
        out string code) => TryValidateCore(
            receipt,
            vendorCatalog,
            expectedPharmacyId,
            expectedMachineFingerprint,
            expectedMaintenanceKeyId,
            now,
            trustedCloudKeys,
            requireCurrentlyActive: true,
            out code);

    public static bool TryValidateHistoricalForRevocation(
        PioneerRxProcessApprovalReceipt? receipt,
        PioneerRxVendorIdentityCatalog? vendorCatalog,
        string expectedPharmacyId,
        string expectedMachineFingerprint,
        string expectedMaintenanceKeyId,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string> trustedCloudKeys,
        out string code) => TryValidateCore(
            receipt,
            vendorCatalog,
            expectedPharmacyId,
            expectedMachineFingerprint,
            expectedMaintenanceKeyId,
            now,
            trustedCloudKeys,
            requireCurrentlyActive: false,
            out code);

    private static bool TryValidateCore(
        PioneerRxProcessApprovalReceipt? receipt,
        PioneerRxVendorIdentityCatalog? vendorCatalog,
        string expectedPharmacyId,
        string expectedMachineFingerprint,
        string expectedMaintenanceKeyId,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string> trustedCloudKeys,
        bool requireCurrentlyActive,
        out string code)
    {
        code = "approval_invalid";
        if (receipt is null) { code = "approval_missing"; return false; }
        if (receipt.SchemaVersion != CurrentSchemaVersion)
        { code = "approval_schema_invalid"; return false; }
        if (!Safe(receipt.ReceiptId, 160) ||
            !Safe(receipt.PharmacyId, 160) ||
            !Safe(receipt.MachineFingerprint, 256) ||
            !Safe(receipt.ProcessName, 128) ||
            !CanonicalWindowsExePath().IsMatch(receipt.CanonicalExecutablePath ?? string.Empty) ||
            !Safe(receipt.AuthenticodeSignerSubject, 512) ||
            !Safe(receipt.ProductName, 256) ||
            !Safe(receipt.FileVersion, 128) ||
            !CanonicalUuid(receipt.VendorCatalogId) ||
            !SidPattern().IsMatch(receipt.ApprovedBySid ?? string.Empty) ||
            !LowerHex64().IsMatch(receipt.MaintenanceKeyId ?? string.Empty) ||
            !LowerHex64().IsMatch(receipt.ExecutableSha256 ?? string.Empty) ||
            !LowerHex64().IsMatch(receipt.SignerCertificateSha256 ?? string.Empty) ||
            !LowerHex64().IsMatch(receipt.SqlServerCertificateSha256 ?? string.Empty) ||
            !LowerHex64().IsMatch(receipt.LocalApprovalEvidenceDigest ?? string.Empty) ||
            !LowerHex64().IsMatch(receipt.ApprovalNonce ?? string.Empty) ||
            receipt.ApprovalCounter <= 0 ||
            !string.Equals(receipt.CloudKeyId, RemoteCommandTrust.CommandV1KeyId, StringComparison.Ordinal) ||
            !P256SignatureEncoding().IsMatch(receipt.MaintenanceSignature ?? string.Empty) ||
            !P256SignatureEncoding().IsMatch(receipt.CloudCoApprovalSignature ?? string.Empty) ||
            !CanonicalUuid(receipt.ReceiptId) ||
            !CanonicalUuid(receipt.PharmacyId) ||
            !CanonicalUuid(receipt.MachineFingerprint) ||
            !CanonicalScopesValid(receipt.ApprovedBaaScopeTags))
        { code = "approval_fields_invalid"; return false; }
        if (!string.Equals(receipt.PharmacyId, expectedPharmacyId, StringComparison.Ordinal) ||
            !string.Equals(receipt.MachineFingerprint, expectedMachineFingerprint, StringComparison.Ordinal) ||
            !string.Equals(receipt.MaintenanceKeyId, expectedMaintenanceKeyId, StringComparison.Ordinal))
        { code = "approval_workstation_mismatch"; return false; }
        if (!ExactUtc(receipt.ApprovedAtUtc, out var approvedAt) ||
            !ExactUtc(receipt.ExpiresAtUtc, out var expiresAt) ||
            approvedAt > now.AddMinutes(5) ||
            expiresAt <= approvedAt ||
            expiresAt - approvedAt > MaximumApprovalLifetime ||
            requireCurrentlyActive && now >= expiresAt)
        { code = "approval_expired_or_time_invalid"; return false; }
        if (!string.IsNullOrEmpty(receipt.RevokedAtUtc) &&
            (requireCurrentlyActive ||
             !ExactUtc(receipt.RevokedAtUtc, out var revokedAt) ||
             revokedAt < approvedAt || revokedAt > now.AddMinutes(5)))
        { code = "approval_revoked"; return false; }
        var catalogValid = requireCurrentlyActive
            ? PioneerRxVendorIdentityCatalogContract.TryValidateAndMatch(
                vendorCatalog,
                receipt,
                now,
                trustedCloudKeys,
                out code)
            : PioneerRxVendorIdentityCatalogContract.TryValidateHistoricalAndMatch(
                vendorCatalog,
                receipt,
                now,
                trustedCloudKeys,
                out code);
        if (!catalogValid)
            return false;

        byte[] publicKey;
        byte[] maintenanceSignature;
        byte[] cloudSignature;
        try
        {
            publicKey = Convert.FromBase64String(receipt.MaintenancePublicKeySpki);
            maintenanceSignature = Base64UrlDecode(receipt.MaintenanceSignature!);
            cloudSignature = Base64UrlDecode(receipt.CloudCoApprovalSignature!);
        }
        catch
        { code = "approval_signature_encoding_invalid"; return false; }
        if (publicKey.Length is < 80 or > 160 ||
            maintenanceSignature.Length != 64 ||
            cloudSignature.Length != 64 ||
            !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(publicKey),
                Convert.FromHexString(receipt.MaintenanceKeyId!)))
        { code = "approval_maintenance_key_invalid"; return false; }
        try
        {
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(publicKey, out var consumed);
            if (consumed != publicKey.Length ||
                verifier.KeySize != 256 ||
                !verifier.VerifyData(
                    Encoding.UTF8.GetBytes(Canonical(receipt)),
                    maintenanceSignature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            { code = "approval_signature_invalid"; return false; }
        }
        catch
        { code = "approval_signature_invalid"; return false; }

        if (!VerifyCloudSignature(
                Canonical(receipt),
                cloudSignature,
                receipt.CloudKeyId,
                trustedCloudKeys))
        { code = "approval_cloud_coapproval_invalid"; return false; }

        code = "approved";
        return true;
    }

    public static bool TryValidateAuthorityState(
        PioneerRxApprovalAuthorityState? state,
        PioneerRxProcessApprovalReceipt receipt,
        string expectedPharmacyId,
        string expectedMachineFingerprint,
        DateTimeOffset now,
        out string code) => TryValidateAuthorityState(
            state,
            receipt,
            expectedPharmacyId,
            expectedMachineFingerprint,
            now,
            RemoteCommandTrust.CreateProductionKeyRegistry(),
            out code);

    public static bool TryValidateAuthorityState(
        PioneerRxApprovalAuthorityState? state,
        PioneerRxProcessApprovalReceipt receipt,
        string expectedPharmacyId,
        string expectedMachineFingerprint,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string> trustedCloudKeys,
        out string code)
    {
        if (state is not null &&
            (!string.Equals(state.ActiveReceiptId, receipt.ReceiptId, StringComparison.Ordinal) ||
             state.CurrentApprovalCounter != receipt.ApprovalCounter))
        { code = "approval_authority_counter_mismatch"; return false; }
        if (!TryValidateAuthorityDocument(
                state,
                expectedPharmacyId,
                expectedMachineFingerprint,
                now,
                trustedCloudKeys,
                out code))
            return false;
        if (IsReceiptRevoked(state!, receipt.ReceiptId))
        { code = "approval_revoked"; return false; }
        code = "approved";
        return true;
    }

    /// <summary>
    /// Validates the signed authority document without deciding whether it currently permits the
    /// receipt. SYSTEM installation uses this path so a newer signed revocation can be persisted;
    /// runtime consumers use <see cref="TryValidateAuthorityState"/> and still fail closed on deny.
    /// </summary>
    public static bool TryValidateAuthorityDocument(
        PioneerRxApprovalAuthorityState? state,
        string expectedPharmacyId,
        string expectedMachineFingerprint,
        DateTimeOffset now,
        out string code) => TryValidateAuthorityDocument(
            state,
            expectedPharmacyId,
            expectedMachineFingerprint,
            now,
            RemoteCommandTrust.CreateProductionKeyRegistry(),
            out code);

    public static bool TryValidateAuthorityDocument(
        PioneerRxApprovalAuthorityState? state,
        string expectedPharmacyId,
        string expectedMachineFingerprint,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string> trustedCloudKeys,
        out string code)
    {
        code = "approval_authority_invalid";
        if (state is null) { code = "approval_authority_missing"; return false; }
        if (state.SchemaVersion != CurrentSchemaVersion ||
            !CanonicalUuid(state.PharmacyId) ||
            !CanonicalUuid(state.MachineFingerprint) ||
            !CanonicalUuid(state.ActiveReceiptId) ||
            state.CurrentApprovalCounter <= 0 ||
            !string.Equals(state.CloudKeyId, RemoteCommandTrust.CommandV1KeyId, StringComparison.Ordinal) ||
            !P256SignatureEncoding().IsMatch(state.CloudSignature ?? string.Empty) ||
            !CanonicalRevocationsValid(state.RevokedReceiptIds))
        { code = "approval_authority_fields_invalid"; return false; }
        if (!string.Equals(state.PharmacyId, expectedPharmacyId, StringComparison.Ordinal) ||
            !string.Equals(state.MachineFingerprint, expectedMachineFingerprint, StringComparison.Ordinal))
        { code = "approval_authority_workstation_mismatch"; return false; }
        if (!ExactUtc(state.IssuedAtUtc, out var issuedAt) ||
            !ExactUtc(state.ExpiresAtUtc, out var expiresAt) ||
            issuedAt > now.AddMinutes(5) ||
            expiresAt <= issuedAt ||
            expiresAt - issuedAt > TimeSpan.FromDays(7) ||
            now >= expiresAt)
        { code = "approval_authority_expired_or_time_invalid"; return false; }
        byte[] signature;
        try { signature = Base64UrlDecode(state.CloudSignature!); }
        catch { code = "approval_authority_signature_encoding_invalid"; return false; }
        try
        {
            if (!VerifyCloudSignature(
                    Canonical(state),
                    signature,
                    state.CloudKeyId,
                    trustedCloudKeys))
            { code = "approval_authority_signature_invalid"; return false; }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
        code = "authority_document_valid";
        return true;
    }

    public static bool IsReceiptRevoked(
        PioneerRxApprovalAuthorityState state,
        string receiptId) =>
        Array.BinarySearch(
            state.RevokedReceiptIds ?? Array.Empty<string>(),
            receiptId,
            StringComparer.Ordinal) >= 0;

    public static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        if (!P256SignatureEncoding().IsMatch(value ?? string.Empty))
            throw new FormatException("Unpadded base64url is required.");
        var normalized = value!.Replace('-', '+').Replace('_', '/');
        normalized += (normalized.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            0 => "",
            _ => throw new FormatException(),
        };
        return Convert.FromBase64String(normalized);
    }

    private static bool ExactUtc(string value, out DateTimeOffset parsed)
    {
        parsed = default;
        return !string.IsNullOrWhiteSpace(value) &&
               DateTimeOffset.TryParseExact(
                   value,
                   UtcTimestampFormat,
                   System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.AssumeUniversal |
                   System.Globalization.DateTimeStyles.AdjustToUniversal,
                   out parsed);
    }

    private static bool Safe(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= max &&
        !value.Any(ch => char.IsControl(ch) || ch == '|');

    private static bool CanonicalUuid(string? value) =>
        Guid.TryParseExact(value, "D", out var parsed) &&
        string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal);

    private static bool CanonicalScopesValid(string[]? scopes)
    {
        if (scopes is null || scopes.Length is < 1 or > 16) return false;
        for (var i = 0; i < scopes.Length; i++)
        {
            if (!ScopePattern().IsMatch(scopes[i] ?? string.Empty)) return false;
            if (i > 0 && StringComparer.Ordinal.Compare(scopes[i - 1], scopes[i]) >= 0) return false;
        }
        return true;
    }

    private static string CanonicalScopes(string[]? scopes) =>
        scopes is null ? string.Empty : string.Join(',', scopes);

    private static bool CanonicalRevocationsValid(string[]? receiptIds)
    {
        if (receiptIds is null || receiptIds.Length > 256) return false;
        for (var i = 0; i < receiptIds.Length; i++)
        {
            if (!CanonicalUuid(receiptIds[i])) return false;
            if (i > 0 && StringComparer.Ordinal.Compare(receiptIds[i - 1], receiptIds[i]) >= 0)
                return false;
        }
        return true;
    }

    private static bool VerifyCloudSignature(
        string canonical,
        byte[] signature,
        string cloudKeyId,
        IReadOnlyDictionary<string, string> trustedCloudKeys)
    {
        if (signature.Length != 64 ||
            !trustedCloudKeys.TryGetValue(cloudKeyId, out var publicKeyDer)) return false;
        try
        {
            using var verifier = ECDsa.Create();
            var publicKey = Convert.FromBase64String(publicKeyDer);
            verifier.ImportSubjectPublicKeyInfo(publicKey, out var consumed);
            return consumed == publicKey.Length && verifier.KeySize == 256 &&
                   verifier.VerifyData(
                       Encoding.UTF8.GetBytes(canonical),
                       signature,
                       HashAlgorithmName.SHA256,
                       DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch { return false; }
    }
}
