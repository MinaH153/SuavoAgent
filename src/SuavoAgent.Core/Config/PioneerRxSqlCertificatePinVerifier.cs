using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Config;

/// <summary>
/// Resolves an optional exact SQL certificate pin only after the fixed ProgramData
/// file, ACL boundary, dual-signed workstation approval, cloud authority counter,
/// installed device identity, certificate lifetime, and DER digest all agree.
/// </summary>
internal static class PioneerRxSqlCertificatePinVerifier
{
    private const int MaximumApprovalBytes = 64 * 1024;
    private const string ApprovalReceiptFileName = "pioneerrx-process-approval.json";
    private const string ApprovalAuthorityFileName = "pioneerrx-approval-authority.json";
    private static readonly JsonSerializerOptions StrictJson = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
    };

    internal static string? ResolveProduction(
        AgentOptions options,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.SqlTrustServerCertificate)
            throw new InvalidOperationException("sql_certificate_validation_bypass_forbidden");
        if (string.IsNullOrWhiteSpace(options.SqlServerCertificateSha256))
        {
            options.ValidatedSqlServerCertificatePath = null;
            return null;
        }
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("SQL certificate pin validation requires Windows.");

        var root = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent"));
        var certificatePath = Path.Combine(root, PioneerRxSqlCertificatePinContract.InstalledFileName);
        var authorityRoot = Path.Combine(root, PioneerRxApprovalMetadataAcl.AuthorityDirectoryName);
        var receiptPath = Path.Combine(authorityRoot, ApprovalReceiptFileName);
        var authorityPath = Path.Combine(authorityRoot, ApprovalAuthorityFileName);
        var vendorCatalogPath = Path.Combine(authorityRoot, PioneerRxVendorIdentityCatalogContract.InstalledFileName);
        var highWaterPath = Path.Combine(
            authorityRoot,
            PioneerRxApprovalMaintenanceContract.HighWaterFileName);
        ProductionAclBoundary.ValidatePath(
            certificatePath,
            PioneerRxSqlCertificatePinContract.InstalledFileName,
            fileMustExist: true);
        if (!PioneerRxApprovalMetadataAcl.ValidateDirectory(authorityRoot) ||
            !PioneerRxApprovalMetadataAcl.ValidateFile(receiptPath, interactiveRead: true) ||
            !PioneerRxApprovalMetadataAcl.ValidateFile(authorityPath, interactiveRead: true) ||
            !PioneerRxApprovalMetadataAcl.ValidateFile(vendorCatalogPath, interactiveRead: true) ||
            !PioneerRxApprovalMetadataAcl.ValidateFile(highWaterPath, interactiveRead: false))
            throw new UnauthorizedAccessException("pioneerrx_approval_metadata_acl_invalid");

        var receipt = ReadStrict<PioneerRxProcessApprovalReceipt>(receiptPath)
                      ?? throw new InvalidDataException("sql_certificate_approval_unreadable");
        var authority = ReadStrict<PioneerRxApprovalAuthorityState>(authorityPath)
                        ?? throw new InvalidDataException("sql_certificate_authority_unreadable");
        var vendorCatalog = ReadStrict<PioneerRxVendorIdentityCatalog>(vendorCatalogPath)
                            ?? throw new InvalidDataException("pioneerrx_vendor_catalog_unreadable");
        var highWater = ReadStrict<PioneerRxApprovalHighWaterState>(highWaterPath)
                        ?? throw new InvalidDataException("pioneerrx_high_water_unreadable");
        var instant = now ?? DateTimeOffset.UtcNow;
        var approvalCode = "sql_certificate_identity_unavailable";
        if (string.IsNullOrWhiteSpace(options.PharmacyId) ||
            string.IsNullOrWhiteSpace(options.MachineFingerprint) ||
            string.IsNullOrWhiteSpace(options.MaintenanceAttestationKeyId) ||
            !PioneerRxProcessApprovalContract.TryValidate(
                receipt,
                vendorCatalog,
                options.PharmacyId,
                options.MachineFingerprint,
                options.MaintenanceAttestationKeyId,
                instant,
                out approvalCode))
            throw new InvalidDataException(StableCode(approvalCode, "sql_certificate_approval_invalid"));
        if (!PioneerRxProcessApprovalContract.TryValidateAuthorityState(
                authority,
                receipt,
                options.PharmacyId,
                options.MachineFingerprint,
                instant,
                out var authorityCode))
            throw new InvalidDataException(StableCode(authorityCode, "sql_certificate_authority_invalid"));
        if (!PioneerRxApprovalMaintenanceContract.HighWaterMatches(
                highWater,
                receipt,
                authority,
                vendorCatalog))
            throw new InvalidDataException("pioneerrx_high_water_mismatch");
        if (!DigestsMatch(
                options.SqlServerCertificateSha256,
                receipt.SqlServerCertificateSha256))
            throw new InvalidDataException("sql_certificate_approval_digest_mismatch");
        if (!PioneerRxSqlCertificatePinContract.TryVerifyFile(
                certificatePath,
                receipt.SqlServerCertificateSha256,
                instant,
                out var pinCode))
            throw new InvalidDataException(StableCode(pinCode, "sql_certificate_pin_invalid"));

        options.ValidatedSqlServerCertificatePath = certificatePath;
        return certificatePath;
    }

    internal static bool DigestsMatch(string? left, string? right)
    {
        if (!IsLowerHex64(left) || !IsLowerHex64(right)) return false;
        byte[]? leftBytes = null;
        byte[]? rightBytes = null;
        try
        {
            leftBytes = Convert.FromHexString(left!);
            rightBytes = Convert.FromHexString(right!);
            return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        finally
        {
            if (leftBytes is not null) CryptographicOperations.ZeroMemory(leftBytes);
            if (rightBytes is not null) CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private static T? ReadStrict<T>(string path)
    {
        byte[]? bytes = null;
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                return default;
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaximumApprovalBytes) return default;
            bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (!HasUniqueRootProperties(bytes)) return default;
            return JsonSerializer.Deserialize<T>(bytes, StrictJson);
        }
        catch
        {
            return default;
        }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool HasUniqueRootProperties(ReadOnlySpan<byte> json)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reader = new Utf8JsonReader(json, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 8,
            AllowTrailingCommas = false,
        });
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1 &&
                !names.Add(reader.GetString() ?? string.Empty))
                return false;
        }
        return true;
    }

    private static bool IsLowerHex64(string? value) =>
        value is { Length: 64 } &&
        value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string StableCode(string? value, string fallback) =>
        value is { Length: > 0 and <= 80 } &&
        value.All(ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')
            ? value
            : fallback;
}
