using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuavoAgent.Contracts.Maintenance;

/// <summary>
/// Exact signed cloud-command envelope carried across the LocalService-to-SYSTEM
/// repair boundary. The raw command data is retained so SYSTEM can independently
/// verify both its SHA-256 binding and the control-plane ECDSA signature.
/// </summary>
public sealed record RemoteRepairRequest(
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
    string Reason,
    string RequestedAtUtc);

public sealed record RemoteRepairValidationResult(bool IsValid, string Code, string? ReplayId = null)
{
    public static RemoteRepairValidationResult Valid(string replayId) => new(true, "valid", replayId);
    public static RemoteRepairValidationResult Reject(string code) => new(false, code);
}

public static class RemoteRepairContract
{
    private static readonly string[] TimestampFormats =
    [
        "O",
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
    ];

    private static readonly HashSet<string> Commands = new(StringComparer.Ordinal)
    {
        "repair",
        "repair_agent",
    };

    public static readonly IReadOnlySet<string> AllowedReasons = new HashSet<string>(
        new[]
        {
            "remote_command",
            "watchdog_critical",
            "cloud_stale",
            "install_repair",
            "runtime_health_missing",
            "operator_requested",
        },
        StringComparer.Ordinal);

    public const int SchemaVersion = 1;
    public const string RequestFileName = "watchdog-repair-request.json";
    public const string ReplayLedgerFileName = "repair-replay.json";
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

    public static string Serialize(RemoteRepairRequest request) =>
        JsonSerializer.Serialize(request, JsonOptions);

    public static bool TryDeserialize(
        string json,
        out RemoteRepairRequest? request,
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
            request = JsonSerializer.Deserialize<RemoteRepairRequest>(json, JsonOptions);
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

    public static RemoteRepairValidationResult Validate(
        RemoteRepairRequest request,
        string expectedAgentId,
        string expectedMachineFingerprint,
        IReadOnlyDictionary<string, string> trustedPublicKeys,
        DateTimeOffset now)
    {
        if (request is null || trustedPublicKeys is null)
            return RemoteRepairValidationResult.Reject("request_invalid");
        if (request.SchemaVersion != SchemaVersion)
            return RemoteRepairValidationResult.Reject("schema_mismatch");
        if (!Commands.Contains(request.Command))
            return RemoteRepairValidationResult.Reject("command_mismatch");
        if (!string.Equals(request.AgentId, expectedAgentId, StringComparison.Ordinal))
            return RemoteRepairValidationResult.Reject("agent_mismatch");
        if (!string.Equals(
                request.MachineFingerprint,
                expectedMachineFingerprint,
                StringComparison.Ordinal))
            return RemoteRepairValidationResult.Reject("fingerprint_mismatch");
        if (!IsSafeToken(request.AgentId, 160) ||
            !IsSafeToken(request.MachineFingerprint, 256) ||
            !IsSafeToken(request.CommandId, 128) ||
            !IsSafeToken(request.Nonce, 160) ||
            !IsSafeToken(request.KeyId, 80) ||
            !AllowedReasons.Contains(request.Reason))
            return RemoteRepairValidationResult.Reject("request_identity_invalid");

        if (!TryValidateTimestamp(request.Timestamp, now, out var commandAt))
            return RemoteRepairValidationResult.Reject("command_timestamp_invalid_or_stale");
        if (!TryValidateTimestamp(request.RequestedAtUtc, now, out var requestedAt) ||
            requestedAt < commandAt - MaximumFutureSkew)
            return RemoteRepairValidationResult.Reject("request_timestamp_invalid_or_stale");

        if (Encoding.UTF8.GetByteCount(request.DataJson ?? string.Empty) > MaxDataJsonBytes)
            return RemoteRepairValidationResult.Reject("command_data_too_large");
        var computedHash = RemoteCommandTrust.ComputeSha256Hex(request.DataJson);
        if (!FixedTimeHexEquals(computedHash, request.DataHash))
            return RemoteRepairValidationResult.Reject("command_data_hash_mismatch");
        if (!TryReadMinimumNecessaryData(
                request.DataJson,
                out var commandId,
                out var reason,
                out var expiresAt) ||
            !string.Equals(commandId, request.CommandId, StringComparison.Ordinal) ||
            !string.Equals(reason, request.Reason, StringComparison.Ordinal))
            return RemoteRepairValidationResult.Reject("command_data_invalid");
        if (!TryValidateAuthorityExpiry(expiresAt, commandAt, now))
            return RemoteRepairValidationResult.Reject("command_expiry_invalid_or_stale");
        if (!VerifySignature(
                trustedPublicKeys,
                request.KeyId,
                RemoteCommandTrust.BuildCommandCanonical(
                    request.Command,
                    request.AgentId,
                    request.MachineFingerprint,
                    request.Timestamp,
                    request.Nonce,
                    request.DataHash),
                request.Signature))
            return RemoteRepairValidationResult.Reject("command_signature_invalid");

        return RemoteRepairValidationResult.Valid(ComputeReplayId(request));
    }

    public static string ComputeReplayId(RemoteRepairRequest request) =>
        RemoteCommandTrust.ComputeSha256Hex(
            $"{request.SchemaVersion}|{request.Command}|{request.AgentId}|" +
            $"{request.MachineFingerprint}|{request.Timestamp}|{request.Nonce}|" +
            $"{request.KeyId}|{request.DataHash}");

    public static bool TryReadMinimumNecessaryData(
        string? dataJson,
        out string commandId,
        out string reason) =>
        TryReadMinimumNecessaryData(dataJson, out commandId, out reason, out _);

    public static bool TryReadMinimumNecessaryData(
        string? dataJson,
        out string commandId,
        out string reason,
        out string expiresAt)
    {
        commandId = string.Empty;
        reason = "remote_command";
        expiresAt = string.Empty;
        if (string.IsNullOrWhiteSpace(dataJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(dataJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!seen.Add(property.Name) ||
                    property.Name is not ("commandId" or "reason" or "expiresAt") ||
                    property.Value.ValueKind != JsonValueKind.String)
                    return false;
            }
            if (!seen.Contains("commandId") || !seen.Contains("expiresAt")) return false;
            commandId = document.RootElement.GetProperty("commandId").GetString() ?? string.Empty;
            expiresAt = document.RootElement.GetProperty("expiresAt").GetString() ?? string.Empty;
            if (document.RootElement.TryGetProperty("reason", out var reasonElement))
                reason = reasonElement.GetString() ?? string.Empty;
            return IsSafeToken(commandId, 128) &&
                   AllowedReasons.Contains(reason) &&
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
                TimestampFormats,
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
        out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (string.IsNullOrWhiteSpace(value) ||
            !DateTimeOffset.TryParseExact(
                value,
                TimestampFormats,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out timestamp))
            return false;
        return timestamp <= now + MaximumFutureSkew &&
               now - timestamp <= MaximumRequestAge;
    }

    private static bool VerifySignature(
        IReadOnlyDictionary<string, string> trustedPublicKeys,
        string keyId,
        string canonical,
        string signatureBase64)
    {
        if (!trustedPublicKeys.TryGetValue(keyId, out var publicKeyDer) ||
            string.IsNullOrWhiteSpace(signatureBase64))
            return false;
        try
        {
            var keyBytes = Convert.FromBase64String(publicKeyDer);
            var signature = Convert.FromBase64String(signatureBase64);
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(keyBytes, out var read);
            return read == keyBytes.Length &&
                   key.KeySize == 256 &&
                   key.VerifyData(
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
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left),
            Convert.FromHexString(right));
    }

    private static bool IsSafeToken(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        value.All(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' or '.');
}
