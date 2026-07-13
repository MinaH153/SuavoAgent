using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;

namespace SuavoAgent.Setup.Security;

internal static class PioneerRxApprovalProposalBuilder
{
    internal const string LocalEvidencePrefix = "suavo.pioneerrx-local-approval-evidence.v1";

    internal static PioneerRxProcessApprovalReceipt Build(
        SetupConfig config,
        PioneerRxExecutableEvidence executable,
        PioneerRxVendorCatalogBootstrap bootstrap,
        string sqlServerCertificateSha256,
        string approvedBySid,
        string consentReceiptJson,
        IReadOnlyCollection<string> approvedBaaScopeTags,
        IMaintenanceAttestationKeyProvider maintenanceKeys)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(maintenanceKeys);
        if (!LowerHex64(sqlServerCertificateSha256) ||
            !IsSid(approvedBySid) ||
            string.IsNullOrWhiteSpace(consentReceiptJson) ||
            Encoding.UTF8.GetByteCount(consentReceiptJson) > 64 * 1024)
            throw new InvalidDataException("PioneerRx local approval evidence is invalid.");
        var scopes = approvedBaaScopeTags.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (scopes.Length is < 1 or > 16 ||
            scopes.Distinct(StringComparer.Ordinal).Count() != scopes.Length ||
            scopes.Any(scope => scope.Length is < 1 or > 64 ||
                                !char.IsAsciiLetter(scope[0]) ||
                                scope.Any(character =>
                                    !char.IsAsciiLetterOrDigit(character) &&
                                    character is not ('_' or '.' or ':' or '-'))))
            throw new InvalidDataException("PioneerRx approval scopes are invalid.");
        if (!PioneerRxVendorIdentityCatalogContract.TryMatchEvidence(
                bootstrap.VendorCatalog,
                executable.ProcessName,
                executable.ProductName,
                executable.AuthenticodeSignerSubject,
                executable.SignerCertificateSha256,
                executable.CanonicalExecutablePath,
                executable.FileVersion,
                out var code))
            throw new InvalidDataException(code);

        var registration = maintenanceKeys.OpenExisting(config.DeviceFingerprint!);
        if (!string.Equals(
                registration.Enrollment.KeyId,
                config.MaintenanceKeyId,
                StringComparison.Ordinal))
            throw new InvalidDataException("PioneerRx proposal maintenance key mismatch.");
        var evidenceDigest = ComputeLocalEvidenceDigest(
            config,
            executable,
            bootstrap,
            sqlServerCertificateSha256,
            approvedBySid,
            consentReceiptJson,
            scopes);
        var unsigned = new PioneerRxProcessApprovalReceipt(
            PioneerRxProcessApprovalContract.CurrentSchemaVersion,
            bootstrap.ApprovalChallenge.ReceiptId,
            config.PharmacyId,
            config.DeviceFingerprint!,
            registration.Enrollment.KeyId,
            registration.Enrollment.PublicKeySpki,
            executable.ProcessName,
            executable.CanonicalExecutablePath,
            executable.ExecutableSha256,
            executable.AuthenticodeSignerSubject,
            executable.SignerCertificateSha256,
            executable.ProductName,
            executable.FileVersion,
            bootstrap.VendorCatalog.CatalogId,
            sqlServerCertificateSha256,
            approvedBySid,
            evidenceDigest,
            bootstrap.ApprovalChallenge.ApprovalNonce,
            bootstrap.ApprovalChallenge.ApprovalCounter,
            scopes,
            bootstrap.ApprovalChallenge.ApprovedAtUtc,
            bootstrap.ApprovalChallenge.ExpiresAtUtc,
            null,
            RemoteCommandTrust.CommandV1KeyId,
            string.Empty,
            string.Empty);
        var canonicalBytes = Encoding.UTF8.GetBytes(
            PioneerRxProcessApprovalContract.Canonical(unsigned));
        try
        {
            var signed = maintenanceKeys.Sign(
                config.DeviceFingerprint!,
                registration.Enrollment.KeyId,
                canonicalBytes);
            if (!string.Equals(
                    signed.Enrollment.KeyId,
                    registration.Enrollment.KeyId,
                    StringComparison.Ordinal) ||
                signed.Signature.Length != 64)
                throw new CryptographicException("PioneerRx proposal signature is invalid.");
            return unsigned with
            {
                MaintenanceSignature = PioneerRxProcessApprovalContract.Base64UrlEncode(
                    signed.Signature.Span),
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonicalBytes);
        }
    }

    internal static string ComputeLocalEvidenceDigest(
        SetupConfig config,
        PioneerRxExecutableEvidence executable,
        PioneerRxVendorCatalogBootstrap bootstrap,
        string sqlCertificateDigest,
        string approvedBySid,
        string consentReceiptJson,
        IReadOnlyCollection<string> scopes)
    {
        var consentBytes = Encoding.UTF8.GetBytes(consentReceiptJson);
        try
        {
            var consentDigest = LowerSha256(consentBytes);
            var canonical = string.Join('|',
                LocalEvidencePrefix,
                config.AgentId,
                config.PharmacyId,
                config.DeviceFingerprint,
                approvedBySid,
                consentDigest,
                executable.ProcessName,
                executable.CanonicalExecutablePath,
                executable.ExecutableSha256,
                executable.AuthenticodeSignerSubject,
                executable.SignerCertificateSha256,
                executable.ProductName,
                executable.FileVersion,
                sqlCertificateDigest,
                bootstrap.VendorCatalog.CatalogId,
                bootstrap.ApprovalChallenge.ReceiptId,
                bootstrap.ApprovalChallenge.ApprovalNonce,
                bootstrap.ApprovalChallenge.ApprovalCounter,
                string.Join(',', scopes));
            var bytes = Encoding.UTF8.GetBytes(canonical);
            try { return LowerSha256(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(consentBytes);
        }
    }

    private static string LowerSha256(ReadOnlySpan<byte> bytes)
    {
        var digest = SHA256.HashData(bytes);
        try { return Convert.ToHexString(digest).ToLowerInvariant(); }
        finally { CryptographicOperations.ZeroMemory(digest); }
    }

    private static bool LowerHex64(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith("S-1-", StringComparison.Ordinal))
            return false;
        var segments = value.Split('-');
        return segments.Length >= 4 && segments[0] == "S" && segments[1] == "1" &&
               segments.Skip(2).All(segment =>
                   segment.Length > 0 && segment.All(char.IsAsciiDigit));
    }
}
