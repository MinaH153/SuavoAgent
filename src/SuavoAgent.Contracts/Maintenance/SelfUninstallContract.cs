using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuavoAgent.Contracts.Maintenance;

/// <summary>Production trust root for ECDSA-signed control-plane commands.</summary>
public static class RemoteCommandTrust
{
    public const string CommandV1KeyId = "suavo-cmd-v1";

    // ECDSA P-256 SubjectPublicKeyInfo DER, Base64. The private key is held only
    // by the Suavo command-signing control plane.
    public const string CommandV1PublicKeyDer =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE1mIlEiYIEqjp/YBymnFH9FEUxYFXd+Y25cPiF5wdcEo9CP+760IMxHgajrUt9A3zJ47dwV893LWwlZ1/nDP3YA==";

    public static IReadOnlyDictionary<string, string> CreateProductionKeyRegistry() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CommandV1KeyId] = CommandV1PublicKeyDer,
        };

    public static string ComputeSha256Hex(string? value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public static string BuildCommandCanonical(
        string command,
        string agentId,
        string machineFingerprint,
        string timestamp,
        string nonce,
        string dataHash) =>
        $"{command}|{agentId}|{machineFingerprint}|{timestamp}|{nonce}|{dataHash}";

    public static string BuildArchiveReceiptCanonical(
        SelfUninstallArchiveReceipt receipt,
        string agentId,
        string machineFingerprint,
        string commandId,
        string nonce) =>
        $"self_uninstall_archive_receipt|{SelfUninstallContract.SchemaVersion}|" +
        $"{receipt.ArchiveId}|{receipt.ArchiveDigest}|{receipt.Timestamp}|" +
        $"{agentId}|{machineFingerprint}|{commandId}|{nonce}";
}

/// <summary>
/// Signed cloud acknowledgement that an exact audit archive digest was stored.
/// The signature is separate from the original command signature because the
/// archive is produced only after the terminal uninstall audit event is appended.
/// </summary>
public sealed record SelfUninstallArchiveReceipt(
    string ArchiveId,
    string ArchiveDigest,
    string Timestamp,
    string CommandNonce,
    string KeyId,
    string Signature);

/// <summary>
/// Durable Core-to-Broker handoff for remote self-uninstall. It carries the exact
/// original signed-command fields and exact raw JSON data string, plus a separately
/// signed cloud receipt binding the retained audit archive to that command.
/// </summary>
public sealed record SelfUninstallRequest(
    int SchemaVersion,
    string Command,
    string AgentId,
    string MachineFingerprint,
    string Timestamp,
    string Nonce,
    string KeyId,
    string Signature,
    string DataJson,
    string DataHash,
    string CommandId,
    string RequestedAtUtc,
    string ArchiveDigest,
    SelfUninstallArchiveReceipt ArchiveReceipt);

public sealed record SelfUninstallValidationResult(bool IsValid, string Code)
{
    public static SelfUninstallValidationResult Valid() => new(true, "valid");
    public static SelfUninstallValidationResult Reject(string code) => new(false, code);
}

/// <summary>Serialization, canonicalization, and fail-closed verification rules.</summary>
public static class SelfUninstallContract
{
    private static readonly string[] AcceptedTimestampFormats =
    [
        "O",
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
    ];

    public const int SchemaVersion = 1;
    public const string CommandName = "self_uninstall";
    public const string RequestFileName = "uninstall.request";
    public const string PreserveDataSwitch = "--preserve-data";
    public const string PurgeRetainedDataSwitch = "--purge-retained-data";
    public const string AuthenticatedRequestSwitch = "--authenticated-self-uninstall-request";
    public const int MaxRequestBytes = 64 * 1024;
    public const int MaxDataJsonBytes = 16 * 1024;
    public static readonly TimeSpan MaximumRequestAge = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromSeconds(30);

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        WriteIndented = false,
    };

    public static string Serialize(SelfUninstallRequest request) =>
        JsonSerializer.Serialize(request, JsonOptions);

    public static bool TryDeserialize(
        string json,
        out SelfUninstallRequest? request,
        out string rejectionCode)
    {
        request = null;
        rejectionCode = "request_invalid_json";
        if (string.IsNullOrWhiteSpace(json))
        {
            rejectionCode = "request_empty";
            return false;
        }

        if (Encoding.UTF8.GetByteCount(json) > MaxRequestBytes)
        {
            rejectionCode = "request_too_large";
            return false;
        }

        try
        {
            request = JsonSerializer.Deserialize<SelfUninstallRequest>(json, JsonOptions);
            if (request is null)
            {
                rejectionCode = "request_null";
                return false;
            }
            rejectionCode = "valid";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static SelfUninstallValidationResult Validate(
        SelfUninstallRequest request,
        string expectedAgentId,
        string expectedMachineFingerprint,
        IReadOnlyDictionary<string, string> trustedPublicKeys,
        DateTimeOffset now,
        TimeSpan? maximumAge = null)
    {
        if (request.SchemaVersion != SchemaVersion)
            return SelfUninstallValidationResult.Reject("schema_mismatch");
        var age = maximumAge ?? MaximumRequestAge;
        var commandValidation = ValidateSignedCommand(
            request.Command,
            request.AgentId,
            request.MachineFingerprint,
            request.Timestamp,
            request.Nonce,
            request.KeyId,
            request.Signature,
            request.DataJson,
            request.DataHash,
            request.CommandId,
            expectedAgentId,
            expectedMachineFingerprint,
            trustedPublicKeys,
            now,
            age);
        if (!commandValidation.IsValid) return commandValidation;

        _ = TryValidateTimestamp(request.Timestamp, now, age, out var commandTimestamp);
        if (!TryValidateTimestamp(request.RequestedAtUtc, now, age, out var requestedAt) ||
            requestedAt < commandTimestamp - MaximumFutureSkew)
            return SelfUninstallValidationResult.Reject("request_timestamp_invalid_or_stale");

        var receipt = request.ArchiveReceipt;
        if (receipt is null ||
            !IsCanonicalUuid(receipt.ArchiveId) ||
            !IsSafeToken(receipt.KeyId, 80) ||
            !string.Equals(receipt.CommandNonce, request.Nonce, StringComparison.Ordinal))
            return SelfUninstallValidationResult.Reject("archive_receipt_identity_invalid");
        if (!IsLowerHex64(request.ArchiveDigest) ||
            !IsLowerHex64(receipt.ArchiveDigest) ||
            !FixedTimeHexEquals(request.ArchiveDigest, receipt.ArchiveDigest))
            return SelfUninstallValidationResult.Reject("archive_digest_mismatch");
        if (!TryValidateCloudReceiptTimestamp(
                receipt.Timestamp,
                now,
                age,
                out var receiptTimestamp) ||
            receiptTimestamp < commandTimestamp - MaximumFutureSkew)
            return SelfUninstallValidationResult.Reject("archive_receipt_stale");
        if (!VerifySignature(
                trustedPublicKeys,
                receipt.KeyId,
                RemoteCommandTrust.BuildArchiveReceiptCanonical(
                    receipt,
                    request.AgentId,
                    request.MachineFingerprint,
                    request.CommandId,
                    request.Nonce),
                receipt.Signature))
            return SelfUninstallValidationResult.Reject("archive_receipt_signature_invalid");

        return SelfUninstallValidationResult.Valid();
    }

    public static SelfUninstallValidationResult ValidateSignedCommand(
        string command,
        string agentId,
        string machineFingerprint,
        string timestamp,
        string nonce,
        string keyId,
        string signature,
        string dataJson,
        string dataHash,
        string commandId,
        string expectedAgentId,
        string expectedMachineFingerprint,
        IReadOnlyDictionary<string, string> trustedPublicKeys,
        DateTimeOffset now,
        TimeSpan? maximumAge = null)
    {
        if (!string.Equals(command, CommandName, StringComparison.Ordinal))
            return SelfUninstallValidationResult.Reject("command_mismatch");
        if (!string.Equals(agentId, expectedAgentId, StringComparison.Ordinal))
            return SelfUninstallValidationResult.Reject("agent_mismatch");
        if (!string.Equals(machineFingerprint, expectedMachineFingerprint, StringComparison.Ordinal))
            return SelfUninstallValidationResult.Reject("fingerprint_mismatch");
        if (!IsCanonicalUuid(agentId) ||
            !IsSafeToken(machineFingerprint, 160) ||
            !IsCanonicalUuid(commandId) ||
            !IsCanonicalUuid(nonce) ||
            !IsSafeToken(keyId, 80))
            return SelfUninstallValidationResult.Reject("command_identity_invalid");
        if (!TryValidateTimestamp(
                timestamp,
                now,
                maximumAge ?? MaximumRequestAge,
                out var commandTimestamp))
            return SelfUninstallValidationResult.Reject("command_timestamp_invalid_or_stale");
        if (Encoding.UTF8.GetByteCount(dataJson ?? string.Empty) > MaxDataJsonBytes)
            return SelfUninstallValidationResult.Reject("command_data_too_large");
        var computedDataHash = RemoteCommandTrust.ComputeSha256Hex(dataJson);
        if (!FixedTimeHexEquals(computedDataHash, dataHash))
            return SelfUninstallValidationResult.Reject("command_data_hash_mismatch");
        if (!TryReadCommandAuthorityData(
                dataJson,
                out var payloadCommandId,
                out var expiresAt) ||
            !string.Equals(payloadCommandId, commandId, StringComparison.Ordinal))
            return SelfUninstallValidationResult.Reject("payload_command_id_mismatch");
        if (!TryValidateAuthorityExpiry(expiresAt, commandTimestamp, now))
            return SelfUninstallValidationResult.Reject("command_expiry_invalid_or_stale");
        if (!VerifySignature(
                trustedPublicKeys,
                keyId,
                RemoteCommandTrust.BuildCommandCanonical(
                    command,
                    agentId,
                    machineFingerprint,
                    timestamp,
                    nonce,
                    dataHash),
                signature))
            return SelfUninstallValidationResult.Reject("command_signature_invalid");
        return SelfUninstallValidationResult.Valid();
    }

    public static bool TryReadCommandId(string? dataJson, out string commandId) =>
        TryReadCommandAuthorityData(dataJson, out commandId, out _);

    public static bool TryReadCommandAuthorityData(
        string? dataJson,
        out string commandId,
        out string expiresAt)
    {
        commandId = string.Empty;
        expiresAt = string.Empty;
        if (string.IsNullOrWhiteSpace(dataJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(dataJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            // Remote self-uninstall has exactly two minimum-necessary fields: the command
            // identity and its short-lived execution authority. Reject every unknown/nested
            // property so a signed but over-broad payload cannot persist PHI in ProgramData.
            var properties = document.RootElement.EnumerateObject().ToArray();
            if (properties.Length != 2 ||
                properties.Any(property =>
                    property.Name is not ("commandId" or "expiresAt") ||
                    property.Value.ValueKind != JsonValueKind.String) ||
                !document.RootElement.TryGetProperty("commandId", out var element) ||
                !document.RootElement.TryGetProperty("expiresAt", out var expiryElement))
                return false;
            commandId = element.GetString() ?? string.Empty;
            expiresAt = expiryElement.GetString() ?? string.Empty;
            return IsSafeToken(commandId, 128) &&
                   expiresAt.Length is > 0 and <= 64;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryValidateAuthorityExpiry(
        string? value,
        DateTimeOffset commandTimestamp,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !DateTimeOffset.TryParseExact(
                value,
                AcceptedTimestampFormats,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var expiresAt))
            return false;
        return expiresAt > now &&
               expiresAt > commandTimestamp &&
               expiresAt - commandTimestamp <= MaximumRequestAge;
    }

    private static bool TryValidateTimestamp(
        string? value,
        DateTimeOffset now,
        TimeSpan maximumAge,
        out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (string.IsNullOrWhiteSpace(value) ||
            !DateTimeOffset.TryParseExact(
                value,
                AcceptedTimestampFormats,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out timestamp))
            return false;
        if (timestamp > now + MaximumFutureSkew) return false;
        return now - timestamp <= maximumAge;
    }

    private static bool TryValidateCloudReceiptTimestamp(
        string? value,
        DateTimeOffset now,
        TimeSpan maximumAge,
        out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (value is not { Length: 24 } ||
            !DateTimeOffset.TryParseExact(
                value,
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal,
                out timestamp))
            return false;
        if (timestamp > now + MaximumFutureSkew) return false;
        return now - timestamp <= maximumAge;
    }

    private static bool VerifySignature(
        IReadOnlyDictionary<string, string> trustedPublicKeys,
        string? keyId,
        string canonical,
        string? signatureBase64)
    {
        if (string.IsNullOrWhiteSpace(keyId) ||
            !trustedPublicKeys.TryGetValue(keyId, out var publicKeyDer) ||
            string.IsNullOrWhiteSpace(signatureBase64))
            return false;
        try
        {
            var keyBytes = Convert.FromBase64String(publicKeyDer);
            var signature = Convert.FromBase64String(signatureBase64);
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(keyBytes, out var bytesRead);
            if (bytesRead != keyBytes.Length || key.KeySize != 256) return false;
            return key.VerifyData(
                Encoding.UTF8.GetBytes(canonical),
                signature,
                HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is
            FormatException or
            CryptographicException or
            ArgumentException)
        {
            return false;
        }
    }

    private static bool FixedTimeHexEquals(string? left, string? right)
    {
        if (left is null || right is null ||
            left.Length != 64 || right.Length != 64 ||
            !left.All(Uri.IsHexDigit) || !right.All(Uri.IsHexDigit))
            return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(left),
                Convert.FromHexString(right));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsSafeToken(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');

    public static bool IsCanonicalUuid(string? value) =>
        value is { Length: 36 } &&
        Guid.TryParseExact(value, "D", out var parsed) &&
        string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal) &&
        value[14] is >= '1' and <= '5' &&
        value[19] is '8' or '9' or 'a' or 'b';

    private static bool IsLowerHex64(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
