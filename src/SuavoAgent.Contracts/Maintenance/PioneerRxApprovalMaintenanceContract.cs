using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SuavoAgent.Contracts.Security;

namespace SuavoAgent.Contracts.Maintenance;

public sealed record PioneerRxApprovalInstallRequest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("protocolEpoch")] int ProtocolEpoch,
    [property: JsonPropertyName("commandId")] string CommandId,
    [property: JsonPropertyName("payloadDigest")] string PayloadDigest,
    [property: JsonPropertyName("receipt")] PioneerRxProcessApprovalReceipt Receipt,
    [property: JsonPropertyName("authority")] PioneerRxApprovalAuthorityState Authority,
    [property: JsonPropertyName("vendorCatalog")] PioneerRxVendorIdentityCatalog VendorCatalog,
    [property: JsonPropertyName("requestedAtUtc")] string RequestedAtUtc);

public sealed record PioneerRxApprovalHighWaterState(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("protocolEpoch")] int ProtocolEpoch,
    [property: JsonPropertyName("approvalCounter")] long ApprovalCounter,
    [property: JsonPropertyName("receiptId")] string ReceiptId,
    [property: JsonPropertyName("commandId")] string CommandId,
    [property: JsonPropertyName("payloadDigest")] string PayloadDigest,
    [property: JsonPropertyName("vendorCatalogId")] string VendorCatalogId,
    [property: JsonPropertyName("authorityIssuedAtUtc")] string AuthorityIssuedAtUtc,
    [property: JsonPropertyName("authorityDigest")] string AuthorityDigest,
    [property: JsonPropertyName("revoked")] bool Revoked,
    [property: JsonPropertyName("committedAtUtc")] string CommittedAtUtc);

public sealed record PioneerRxApprovalHighWaterProjection(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("state")] PioneerRxApprovalHighWaterState State,
    [property: JsonPropertyName("maintenanceKeyId")] string MaintenanceKeyId,
    [property: JsonPropertyName("maintenanceSignature")] string MaintenanceSignature);

public sealed record PioneerRxApprovalInstallCompletion(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("protocolEpoch")] int ProtocolEpoch,
    [property: JsonPropertyName("commandId")] string CommandId,
    [property: JsonPropertyName("payloadDigest")] string PayloadDigest,
    [property: JsonPropertyName("approvalCounter")] long ApprovalCounter,
    [property: JsonPropertyName("receiptId")] string ReceiptId,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("completedAtUtc")] string CompletedAtUtc);

/// <summary>
/// Fixed LocalService-to-SYSTEM handoff for PioneerRx approval installation. The incoming
/// request is deliberately untrusted; the SYSTEM maintenance host independently validates
/// all three signed artifacts and the enrolled SQL certificate before touching live state.
/// </summary>
public static class PioneerRxApprovalMaintenanceContract
{
    public const int SchemaVersion = 1;
    // Epoch 2 is the first authority lifecycle that requires an acknowledged,
    // signed local revocation before the cloud receipt becomes terminal. Keeping
    // this independent from JSON schema version makes future wire-compatible
    // security cutovers explicit and causes every epoch-1 projection to fail closed.
    public const int CurrentProtocolEpoch = 2;
    public const int MaximumJsonBytes = 256 * 1024;
    public const string InstallSwitch = "--install-pioneerrx-approval";
    public const string RequestPathSwitch = "--approval-request";
    public const string RequestFileName = "pioneerrx-approval-install.request.json";
    public const string ReceiptFileName = "pioneerrx-process-approval.json";
    public const string AuthorityFileName = "pioneerrx-approval-authority.json";
    public const string HighWaterFileName = "pioneerrx-approval-high-water.json";
    public const string HighWaterProjectionFileName = "pioneerrx-approval-high-water.projection.json";
    public const string CompletionFileName = "pioneerrx-approval-install-completion.json";
    public const string PayloadCanonicalPrefix = "suavo.pioneerrx-approval-install.v2";
    public const string ProjectionCanonicalPrefix = "suavo.pioneerrx-high-water-projection.v2";
    public const string InstalledOutcome = "installed";
    public const string RevokedOutcome = "revoked";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 12,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        WriteIndented = false,
    };

    public static string DefaultRequestPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SuavoAgent",
        RequestFileName);

    public static string DefaultAuthorityDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SuavoAgent",
        PioneerRxApprovalMetadataAcl.AuthorityDirectoryName);

    public static string DefaultCompletionPath() =>
        Path.Combine(DefaultAuthorityDirectory(), CompletionFileName);

    public static string ComputePayloadDigest(
        string commandId,
        PioneerRxProcessApprovalReceipt receipt,
        PioneerRxApprovalAuthorityState authority,
        PioneerRxVendorIdentityCatalog vendorCatalog,
        int protocolEpoch = CurrentProtocolEpoch)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(vendorCatalog);
        var canonical = string.Join('|',
            PayloadCanonicalPrefix,
            protocolEpoch,
            commandId,
            PioneerRxProcessApprovalContract.Canonical(receipt),
            receipt.MaintenancePublicKeySpki,
            receipt.MaintenanceSignature,
            receipt.CloudCoApprovalSignature,
            PioneerRxProcessApprovalContract.Canonical(authority),
            authority.CloudSignature,
            PioneerRxVendorIdentityCatalogContract.Canonical(vendorCatalog),
            vendorCatalog.CloudSignature);
        var bytes = Encoding.UTF8.GetBytes(canonical);
        try
        {
            var digest = SHA256.HashData(bytes);
            try { return Convert.ToHexString(digest).ToLowerInvariant(); }
            finally { CryptographicOperations.ZeroMemory(digest); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static string ComputeAuthorityDigest(PioneerRxApprovalAuthorityState authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        return Sha256Hex(string.Join('|',
            PioneerRxProcessApprovalContract.Canonical(authority),
            authority.CloudSignature));
    }

    public static string Canonical(PioneerRxApprovalHighWaterState state) => string.Join('|',
        ProjectionCanonicalPrefix,
        state.SchemaVersion,
        state.ProtocolEpoch,
        state.ApprovalCounter,
        state.ReceiptId,
        state.CommandId,
        state.PayloadDigest,
        state.VendorCatalogId,
        state.AuthorityIssuedAtUtc,
        state.AuthorityDigest,
        state.Revoked ? "revoked" : "approved",
        state.CommittedAtUtc);

    public static bool TryValidateProjection(
        PioneerRxApprovalHighWaterProjection? projection,
        PioneerRxProcessApprovalReceipt receipt,
        PioneerRxApprovalAuthorityState authority,
        PioneerRxVendorIdentityCatalog vendorCatalog,
        DateTimeOffset now,
        out string code)
    {
        code = "approval_high_water_projection_invalid";
        if (projection is null || projection.SchemaVersion != SchemaVersion ||
            projection.State is null || projection.State.SchemaVersion != SchemaVersion ||
            projection.State.ProtocolEpoch != CurrentProtocolEpoch ||
            !LowerHex64(projection.MaintenanceKeyId) ||
            !Signature86(projection.MaintenanceSignature) ||
            !string.Equals(
                projection.MaintenanceKeyId,
                receipt.MaintenanceKeyId,
                StringComparison.Ordinal) ||
            !HighWaterMatches(
                projection.State,
                receipt,
                authority,
                vendorCatalog) ||
            !ExactUtc(projection.State.CommittedAtUtc, out var committedAt) ||
            committedAt > now.AddMinutes(5))
            return false;

        byte[]? publicKey = null;
        byte[]? signature = null;
        try
        {
            publicKey = Convert.FromBase64String(receipt.MaintenancePublicKeySpki);
            signature = Base64UrlDecode(projection.MaintenanceSignature);
            var keyDigest = SHA256.HashData(publicKey);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(
                        keyDigest,
                        Convert.FromHexString(projection.MaintenanceKeyId)))
                    return false;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(keyDigest);
            }
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(publicKey, out var consumed);
            if (consumed != publicKey.Length || verifier.KeySize != 256 || signature.Length != 64 ||
                !verifier.VerifyData(
                    Encoding.UTF8.GetBytes(Canonical(projection.State)),
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return false;
            code = projection.State.Revoked ? "approval_revoked" : "approved";
            return true;
        }
        catch (Exception exception) when (exception is
            FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
        finally
        {
            if (publicKey is not null) CryptographicOperations.ZeroMemory(publicKey);
            if (signature is not null) CryptographicOperations.ZeroMemory(signature);
        }
    }

    public static bool HighWaterMatches(
        PioneerRxApprovalHighWaterState state,
        PioneerRxProcessApprovalReceipt receipt,
        PioneerRxApprovalAuthorityState authority,
        PioneerRxVendorIdentityCatalog vendorCatalog) =>
        state.SchemaVersion == SchemaVersion &&
        state.ProtocolEpoch == CurrentProtocolEpoch &&
        state.ApprovalCounter == receipt.ApprovalCounter &&
        state.ApprovalCounter == authority.CurrentApprovalCounter &&
        string.Equals(state.ReceiptId, receipt.ReceiptId, StringComparison.Ordinal) &&
        string.Equals(state.ReceiptId, authority.ActiveReceiptId, StringComparison.Ordinal) &&
        CanonicalUuid(state.CommandId) &&
        FixedHexEquals(
            state.PayloadDigest,
            ComputePayloadDigest(
                state.CommandId,
                receipt,
                authority,
                vendorCatalog,
                state.ProtocolEpoch)) &&
        string.Equals(state.VendorCatalogId, vendorCatalog.CatalogId, StringComparison.Ordinal) &&
        string.Equals(state.AuthorityIssuedAtUtc, authority.IssuedAtUtc, StringComparison.Ordinal) &&
        FixedHexEquals(state.AuthorityDigest, ComputeAuthorityDigest(authority)) &&
        state.Revoked == PioneerRxProcessApprovalContract.IsReceiptRevoked(
            authority,
            receipt.ReceiptId) &&
        ExactUtc(state.CommittedAtUtc);

    public static bool IsExactRequestPath(string? candidate, string? expected = null) =>
        IsExactPath(candidate, expected ?? DefaultRequestPath(), RequestFileName);

    public static bool IsExactAuthorityPath(string? candidate, string fileName)
    {
        if (!IsFixedFileName(fileName)) return false;
        return IsExactPath(
            candidate,
            Path.Combine(DefaultAuthorityDirectory(), fileName),
            fileName);
    }

    public static bool TryDeserializeRequest(
        ReadOnlySpan<byte> utf8,
        out PioneerRxApprovalInstallRequest? request,
        out string code)
    {
        request = null;
        code = "approval_request_invalid_json";
        if (utf8.Length is <= 0 or > MaximumJsonBytes || !HasUniquePropertiesRecursive(utf8))
            return false;
        try
        {
            request = JsonSerializer.Deserialize<PioneerRxApprovalInstallRequest>(utf8, JsonOptions);
            if (request is null) return false;
            if (request.SchemaVersion != SchemaVersion ||
                request.ProtocolEpoch != CurrentProtocolEpoch ||
                !CanonicalUuid(request.CommandId) ||
                !LowerHex64(request.PayloadDigest) ||
                request.Receipt is null || request.Authority is null || request.VendorCatalog is null ||
                !ExactUtc(request.RequestedAtUtc) ||
                !FixedHexEquals(
                    request.PayloadDigest,
                    ComputePayloadDigest(
                        request.CommandId,
                        request.Receipt,
                        request.Authority,
                        request.VendorCatalog,
                        request.ProtocolEpoch)))
            {
                code = "approval_request_fields_invalid";
                request = null;
                return false;
            }
            code = "valid";
            return true;
        }
        catch (JsonException)
        {
            request = null;
            return false;
        }
    }

    public static bool TryDeserializeCompletion(
        ReadOnlySpan<byte> utf8,
        out PioneerRxApprovalInstallCompletion? completion)
    {
        completion = null;
        if (utf8.Length is <= 0 or > MaximumJsonBytes || !HasUniquePropertiesRecursive(utf8))
            return false;
        try
        {
            completion = JsonSerializer.Deserialize<PioneerRxApprovalInstallCompletion>(utf8, JsonOptions);
            return completion is not null &&
                   completion.SchemaVersion == SchemaVersion &&
                   completion.ProtocolEpoch == CurrentProtocolEpoch &&
                   CanonicalUuid(completion.CommandId) &&
                   CanonicalUuid(completion.ReceiptId) &&
                   LowerHex64(completion.PayloadDigest) &&
                   completion.ApprovalCounter > 0 &&
                   completion.Outcome is InstalledOutcome or RevokedOutcome &&
                   ExactUtc(completion.CompletedAtUtc);
        }
        catch (JsonException)
        {
            completion = null;
            return false;
        }
    }

    public static bool TryDeserializeHighWater(
        ReadOnlySpan<byte> utf8,
        out PioneerRxApprovalHighWaterState? state)
    {
        state = null;
        if (utf8.Length is <= 0 or > MaximumJsonBytes || !HasUniquePropertiesRecursive(utf8))
            return false;
        try
        {
            state = JsonSerializer.Deserialize<PioneerRxApprovalHighWaterState>(utf8, JsonOptions);
            return HighWaterFieldsValid(state);
        }
        catch (JsonException)
        {
            state = null;
            return false;
        }
    }

    public static bool TryDeserializeProjection(
        ReadOnlySpan<byte> utf8,
        out PioneerRxApprovalHighWaterProjection? projection)
    {
        projection = null;
        if (utf8.Length is <= 0 or > MaximumJsonBytes || !HasUniquePropertiesRecursive(utf8))
            return false;
        try
        {
            projection = JsonSerializer.Deserialize<PioneerRxApprovalHighWaterProjection>(utf8, JsonOptions);
            return projection is not null && projection.SchemaVersion == SchemaVersion &&
                   HighWaterFieldsValid(projection.State) &&
                   LowerHex64(projection.MaintenanceKeyId) &&
                   Signature86(projection.MaintenanceSignature);
        }
        catch (JsonException)
        {
            projection = null;
            return false;
        }
    }

    public static bool CompletionMatches(
        PioneerRxApprovalInstallCompletion? completion,
        string commandId,
        string payloadDigest,
        PioneerRxProcessApprovalReceipt receipt,
        PioneerRxApprovalAuthorityState authority) =>
        completion is not null &&
        completion.ProtocolEpoch == CurrentProtocolEpoch &&
        string.Equals(completion.CommandId, commandId, StringComparison.Ordinal) &&
        FixedHexEquals(completion.PayloadDigest, payloadDigest) &&
        completion.ApprovalCounter == receipt.ApprovalCounter &&
        string.Equals(completion.ReceiptId, receipt.ReceiptId, StringComparison.Ordinal) &&
        string.Equals(
            completion.Outcome,
            PioneerRxProcessApprovalContract.IsReceiptRevoked(authority, receipt.ReceiptId)
                ? RevokedOutcome
                : InstalledOutcome,
            StringComparison.Ordinal);

    private static bool IsExactPath(string? candidate, string expected, string fileName)
    {
        if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate) ||
            !string.Equals(Path.GetFileName(candidate), fileName, StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(candidate),
                Path.GetFullPath(expected),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool HighWaterFieldsValid(PioneerRxApprovalHighWaterState? state) =>
        state is not null &&
        state.SchemaVersion == SchemaVersion &&
        state.ProtocolEpoch == CurrentProtocolEpoch &&
        state.ApprovalCounter > 0 &&
        CanonicalUuid(state.ReceiptId) &&
        CanonicalUuid(state.CommandId) &&
        LowerHex64(state.PayloadDigest) &&
        CanonicalUuid(state.VendorCatalogId) &&
        ExactUtc(state.AuthorityIssuedAtUtc) &&
        LowerHex64(state.AuthorityDigest) &&
        ExactUtc(state.CommittedAtUtc);

    private static bool HasUniquePropertiesRecursive(ReadOnlySpan<byte> utf8)
    {
        var copy = utf8.ToArray();
        try
        {
            using var document = JsonDocument.Parse(copy, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 12,
            });
            return Unique(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    private static bool Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || !Unique(property.Value)) return false;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                if (!Unique(child)) return false;
        }
        return true;
    }

    private static bool CanonicalUuid(string? value) =>
        value is { Length: 36 } && Guid.TryParseExact(value, "D", out var parsed) &&
        string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal);

    private static bool LowerHex64(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ExactUtc(string? value) => ExactUtc(value, out _);

    private static bool ExactUtc(string? value, out DateTimeOffset parsed) =>
        DateTimeOffset.TryParseExact(
            value,
            PioneerRxProcessApprovalContract.UtcTimestampFormat,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal |
            System.Globalization.DateTimeStyles.AdjustToUniversal,
            out parsed);

    private static bool IsFixedFileName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal) &&
        value is ReceiptFileName or AuthorityFileName or HighWaterFileName or
            HighWaterProjectionFileName or
            CompletionFileName or PioneerRxVendorIdentityCatalogContract.InstalledFileName;

    private static string Sha256Hex(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            var digest = SHA256.HashData(bytes);
            try { return Convert.ToHexString(digest).ToLowerInvariant(); }
            finally { CryptographicOperations.ZeroMemory(digest); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool Signature86(string? value) =>
        value is { Length: 86 } && value.All(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');

    private static byte[] Base64UrlDecode(string value)
    {
        if (!Signature86(value)) throw new FormatException("Invalid P-256 signature encoding.");
        return Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + "==");
    }

    private static bool FixedHexEquals(string? left, string? right)
    {
        if (!LowerHex64(left) || !LowerHex64(right)) return false;
        var leftBytes = Convert.FromHexString(left!);
        var rightBytes = Convert.FromHexString(right!);
        try { return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes); }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }
}
