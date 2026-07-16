using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuavoAgent.Contracts.Maintenance;

public sealed record UpdateActivationRequest(
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
    string ManifestCanonical,
    string ManifestSignature,
    string StagingId,
    string RequestedAtUtc);

public sealed record UpdatePackageFile(string FileName, string Sha256);

public sealed record UpdatePackageManifest(
    string Canonical,
    string Version,
    string Runtime,
    string Architecture,
    IReadOnlyList<UpdatePackageFile> Files,
    bool IncludesMaintenance);

public sealed record UpdateActivationValidationResult(
    bool IsValid,
    string Code,
    UpdatePackageManifest? Manifest = null)
{
    public static UpdateActivationValidationResult Valid(UpdatePackageManifest manifest) =>
        new(true, "valid", manifest);

    public static UpdateActivationValidationResult Reject(string code) =>
        new(false, code);
}

public sealed record UpdateActivationClaimPointer(
    int SchemaVersion,
    string ReplayId,
    string StagingId,
    string TargetVersion,
    string RequestPath,
    string PayloadDirectory,
    string ClaimedAtUtc,
    string LastHeartbeatAtUtc);

public sealed record UpdateActivationCompletion(
    int SchemaVersion,
    string ReplayId,
    string StagingId,
    string TargetVersion,
    string Outcome,
    string StartedAtUtc,
    string CompletedAtUtc);

/// <summary>
/// Signed LocalService-to-SYSTEM OTA handoff. LocalService may populate the incoming directory, but
/// every byte remains untrusted until Watchdog and Maintenance independently validate this contract.
/// The cloud command signature binds identity, freshness, nonce, and the exact raw data JSON; the
/// update signature independently binds the canonical 11/13-field binary manifest.
/// </summary>
public static partial class UpdateActivationContract
{
    private static readonly string[] TimestampFormats =
    [
        "O",
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
    ];

    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "suavollc.com",
        "raw.githubusercontent.com",
        "objects.githubusercontent.com",
        "github-releases.githubusercontent.com",
    };

    public const int SchemaVersion = 1;
    public const string CommandName = "update";
    public const string UpdatesDirectoryName = "updates";
    public const string IncomingDirectoryName = "incoming";
    public const string CoordinatorDirectoryName = "coordinator";
    public const string TrustedStagingDirectoryName = "trusted-staging";
    public const string RunnerDirectoryName = "runner";
    public const string ActivationRequestFileName = "activation.request.json";
    public const string ReplayLedgerFileName = "activation-replay.json";
    public const string TransactionJournalFileName = "activation-journal.json";
    public const string CompletionFileName = "activation-completion.json";
    public const string ActiveClaimFileName = "active-update-claim.json";
    public const string ActivateSwitch = "--activate-update";
    public const string RunnerSwitch = "--activate-update-runner";
    public const string ResumeSwitch = "--resume-update";
    public const string RequestPathSwitch = "--activation-request";
    public const string ClaimPathSwitch = "--claim";
    public const int MaxRequestBytes = 128 * 1024;
    public const int MaxClaimPointerBytes = 64 * 1024;
    public const int MaxCompletionBytes = 64 * 1024;
    public const int MaxDataJsonBytes = 32 * 1024;
    public static readonly TimeSpan MaximumRequestAge = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromSeconds(30);

    public const string ProductionUpdateKeyId = OtaUpdateTrust.LegacyV1KeyId;

    /// <summary>Compatibility alias for tests and pre-rotation callers that inject one root.</summary>
    public static string ProductionUpdatePublicKeyDer =>
        OtaUpdateTrust.ProductionTrustedPublicKeys[OtaUpdateTrust.LegacyV1KeyId];

    /// <summary>Production bridge registry: v1 plus v2 only after the reviewed v2 SPKI is committed.</summary>
    public static IReadOnlyDictionary<string, string> ProductionUpdatePublicKeys =>
        OtaUpdateTrust.ProductionTrustedPublicKeys;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        WriteIndented = false,
    };

    public static string DefaultUpdateRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SuavoAgent",
        UpdatesDirectoryName);

    public static string DefaultActivationRequestPath() =>
        Path.Combine(DefaultUpdateRoot(), ActivationRequestFileName);

    public static string DefaultMaintenanceRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SuavoAgent-Maintenance");

    public static string DefaultActiveClaimPath() =>
        Path.Combine(DefaultMaintenanceRoot(), ActiveClaimFileName);

    public static string DefaultCompletionPath() =>
        Path.Combine(DefaultMaintenanceRoot(), CompletionFileName);

    public static string GetCoordinatorClaimDirectory(string maintenanceRoot, string stagingId)
    {
        if (!IsSha256Hex(stagingId))
            throw new ArgumentException("stagingId must be exact SHA-256 hex", nameof(stagingId));
        return Path.Combine(maintenanceRoot, CoordinatorDirectoryName, stagingId);
    }

    public static string GetCoordinatorRequestPath(string maintenanceRoot, string stagingId) =>
        Path.Combine(
            GetCoordinatorClaimDirectory(maintenanceRoot, stagingId),
            ActivationRequestFileName);

    public static string GetCoordinatorPayloadDirectory(string maintenanceRoot, string stagingId) =>
        Path.Combine(
            GetCoordinatorClaimDirectory(maintenanceRoot, stagingId),
            "payload");

    public static string GetMaintenanceRunnerPath(string maintenanceRoot, string stagingId)
    {
        if (!IsSha256Hex(stagingId))
            throw new ArgumentException("stagingId must be exact SHA-256 hex", nameof(stagingId));
        return Path.Combine(
            maintenanceRoot,
            RunnerDirectoryName,
            stagingId.ToLowerInvariant(),
            MaintenanceContract.ExecutableName);
    }

    public static string ComputeStagingId(string nonce, string dataHash) =>
        RemoteCommandTrust.ComputeSha256Hex($"{nonce}|{dataHash}");

    public static string ComputeReplayId(UpdateActivationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RemoteCommandTrust.ComputeSha256Hex(
            $"{request.KeyId}|{request.Nonce}|{request.DataHash}|{request.ManifestSignature}");
    }

    public static string GetIncomingStagingDirectory(string updateRoot, string stagingId)
    {
        if (!IsSha256Hex(stagingId))
            throw new ArgumentException("stagingId must be exact SHA-256 hex", nameof(stagingId));
        return Path.Combine(updateRoot, IncomingDirectoryName, stagingId);
    }

    public static string Serialize(UpdateActivationRequest request) =>
        JsonSerializer.Serialize(request, JsonOptions);

    public static string Serialize(UpdateActivationClaimPointer pointer) =>
        JsonSerializer.Serialize(pointer, JsonOptions);

    public static string Serialize(UpdateActivationCompletion completion) =>
        JsonSerializer.Serialize(completion, JsonOptions);

    public static bool TryDeserialize(
        string json,
        out UpdateActivationRequest? request,
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
            request = JsonSerializer.Deserialize<UpdateActivationRequest>(json, JsonOptions);
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

    public static bool TryDeserializeClaimPointer(
        string json,
        out UpdateActivationClaimPointer? pointer,
        out string rejectionCode) =>
        TryDeserializeBounded(
            json,
            MaxClaimPointerBytes,
            "claim_pointer",
            out pointer,
            out rejectionCode);

    public static bool TryDeserializeCompletion(
        string json,
        out UpdateActivationCompletion? completion,
        out string rejectionCode) =>
        TryDeserializeBounded(
            json,
            MaxCompletionBytes,
            "completion",
            out completion,
            out rejectionCode);

    public static bool ValidateClaimPointer(
        UpdateActivationClaimPointer pointer,
        string maintenanceRoot,
        DateTimeOffset now,
        out string rejectionCode)
    {
        rejectionCode = "claim_pointer_invalid";
        if (pointer is null) return false;
        if (pointer.SchemaVersion != SchemaVersion)
        {
            rejectionCode = "claim_pointer_schema_mismatch";
            return false;
        }
        if (!IsSha256Hex(pointer.ReplayId) ||
            !IsSha256Hex(pointer.StagingId) ||
            !IsSafeToken(pointer.TargetVersion, 80) ||
            !TryValidateTimestampWithoutMaximumAge(pointer.ClaimedAtUtc, now, out var claimedAt) ||
            !TryValidateTimestampWithoutMaximumAge(pointer.LastHeartbeatAtUtc, now, out var heartbeatAt) ||
            heartbeatAt < claimedAt - MaximumFutureSkew)
        {
            return false;
        }

        try
        {
            if (!PathEquals(
                    pointer.RequestPath,
                    GetCoordinatorRequestPath(maintenanceRoot, pointer.StagingId)) ||
                !PathEquals(
                    pointer.PayloadDirectory,
                    GetCoordinatorPayloadDirectory(maintenanceRoot, pointer.StagingId)))
            {
                rejectionCode = "claim_pointer_path_mismatch";
                return false;
            }
        }
        catch (ArgumentException)
        {
            rejectionCode = "claim_pointer_path_invalid";
            return false;
        }

        rejectionCode = "valid";
        return true;
    }

    public static bool ValidateCompletion(
        UpdateActivationCompletion completion,
        UpdateActivationClaimPointer pointer,
        DateTimeOffset now,
        out string rejectionCode)
    {
        if (!ValidateCompletionStandalone(completion, now, out rejectionCode) || pointer is null)
            return false;
        if (!string.Equals(completion.ReplayId, pointer.ReplayId, StringComparison.Ordinal) ||
            !string.Equals(completion.StagingId, pointer.StagingId, StringComparison.Ordinal) ||
            !string.Equals(completion.TargetVersion, pointer.TargetVersion, StringComparison.Ordinal))
        {
            rejectionCode = "completion_claim_mismatch";
            return false;
        }
        rejectionCode = "valid";
        return true;
    }

    public static bool ValidateCompletionStandalone(
        UpdateActivationCompletion completion,
        DateTimeOffset now,
        out string rejectionCode)
    {
        rejectionCode = "completion_invalid";
        if (completion is null) return false;
        if (completion.SchemaVersion != SchemaVersion)
        {
            rejectionCode = "completion_schema_mismatch";
            return false;
        }
        if (!IsSha256Hex(completion.ReplayId) ||
            !IsSha256Hex(completion.StagingId) ||
            !IsSafeToken(completion.TargetVersion, 80) ||
            !TryNormalizeVersion(completion.TargetVersion, out _) ||
            completion.Outcome is not (
                "committed" or "rolled_back" or "rejected" or "failed") ||
            !TryValidateTimestampWithoutMaximumAge(completion.StartedAtUtc, now, out var startedAt) ||
            !TryValidateTimestampWithoutMaximumAge(completion.CompletedAtUtc, now, out var completedAt) ||
            completedAt < startedAt - MaximumFutureSkew)
            return false;
        rejectionCode = "valid";
        return true;
    }

    public static UpdateActivationValidationResult Validate(
        UpdateActivationRequest request,
        IReadOnlyDictionary<string, string> trustedCommandKeys,
        string updatePublicKeyDerBase64,
        DateTimeOffset now,
        string? expectedAgentId = null,
        string? expectedMachineFingerprint = null,
        TimeSpan? maximumAge = null)
        => Validate(
            request,
            trustedCommandKeys,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [OtaUpdateTrust.LegacyV1KeyId] = updatePublicKeyDerBase64,
            },
            now,
            expectedAgentId,
            expectedMachineFingerprint,
            maximumAge);

    public static UpdateActivationValidationResult Validate(
        UpdateActivationRequest request,
        IReadOnlyDictionary<string, string> trustedCommandKeys,
        IReadOnlyDictionary<string, string> updatePublicKeys,
        DateTimeOffset now,
        string? expectedAgentId = null,
        string? expectedMachineFingerprint = null,
        TimeSpan? maximumAge = null)
    {
        if (request is null || trustedCommandKeys is null || updatePublicKeys is null)
            return UpdateActivationValidationResult.Reject("request_invalid");
        if (request.SchemaVersion != SchemaVersion)
            return UpdateActivationValidationResult.Reject("schema_mismatch");
        if (!string.Equals(request.Command, CommandName, StringComparison.Ordinal))
            return UpdateActivationValidationResult.Reject("command_mismatch");
        if (!string.IsNullOrEmpty(expectedAgentId) &&
            !string.Equals(request.AgentId, expectedAgentId, StringComparison.Ordinal))
            return UpdateActivationValidationResult.Reject("agent_mismatch");
        if (!string.IsNullOrEmpty(expectedMachineFingerprint) &&
            !string.Equals(request.MachineFingerprint, expectedMachineFingerprint, StringComparison.Ordinal))
            return UpdateActivationValidationResult.Reject("fingerprint_mismatch");
        if (!IsSafeToken(request.AgentId, 160) ||
            !IsSafeToken(request.MachineFingerprint, 256) ||
            !IsSafeToken(request.Nonce, 160) ||
            !IsSafeToken(request.KeyId, 80) ||
            !IsSha256Hex(request.StagingId))
            return UpdateActivationValidationResult.Reject("request_identity_invalid");

        var age = maximumAge ?? MaximumRequestAge;
        if (!TryValidateTimestamp(request.Timestamp, now, age, out var commandTimestamp))
            return UpdateActivationValidationResult.Reject("command_timestamp_invalid_or_stale");
        if (!TryValidateTimestamp(request.RequestedAtUtc, now, age, out var requestedAt) ||
            requestedAt < commandTimestamp - MaximumFutureSkew)
            return UpdateActivationValidationResult.Reject("request_timestamp_invalid_or_stale");

        if (Encoding.UTF8.GetByteCount(request.DataJson ?? string.Empty) > MaxDataJsonBytes)
            return UpdateActivationValidationResult.Reject("command_data_too_large");
        var computedDataHash = RemoteCommandTrust.ComputeSha256Hex(request.DataJson ?? string.Empty);
        if (!FixedTimeHexEquals(computedDataHash, request.DataHash))
            return UpdateActivationValidationResult.Reject("command_data_hash_mismatch");
        if (!string.Equals(
                ComputeStagingId(request.Nonce, request.DataHash),
                request.StagingId,
                StringComparison.Ordinal))
            return UpdateActivationValidationResult.Reject("staging_id_mismatch");

        if (!VerifyCommandSignature(trustedCommandKeys, request))
            return UpdateActivationValidationResult.Reject("command_signature_invalid");
        if (!TryReadUpdateData(
                request.DataJson,
                out var dataManifest,
                out var dataManifestSignature))
            return UpdateActivationValidationResult.Reject("command_data_invalid");
        if (!string.Equals(dataManifest, request.ManifestCanonical, StringComparison.Ordinal) ||
            !string.Equals(dataManifestSignature, request.ManifestSignature, StringComparison.Ordinal))
            return UpdateActivationValidationResult.Reject("command_data_manifest_mismatch");

        var manifestResult = ValidateManifest(
            request.ManifestCanonical,
            request.ManifestSignature,
            updatePublicKeys);
        return manifestResult;
    }

    public static UpdateActivationValidationResult ValidateManifest(
        string canonical,
        string signatureHex,
        string updatePublicKeyDerBase64)
        => ValidateManifest(
            canonical,
            signatureHex,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [OtaUpdateTrust.LegacyV1KeyId] = updatePublicKeyDerBase64,
            });

    public static UpdateActivationValidationResult ValidateManifest(
        string canonical,
        string signatureHex,
        IReadOnlyDictionary<string, string> updatePublicKeys)
    {
        if (string.IsNullOrEmpty(canonical) ||
            !string.Equals(canonical, canonical.Trim(), StringComparison.Ordinal) ||
            canonical.Any(char.IsControl))
            return UpdateActivationValidationResult.Reject("manifest_not_canonical");

        var fields = canonical.Split('|');
        if (fields.Length is not (11 or 13) || fields.Any(string.IsNullOrWhiteSpace))
            return UpdateActivationValidationResult.Reject("manifest_field_count_invalid");
        if (!string.Equals(fields[7], "net8.0", StringComparison.Ordinal) ||
            !string.Equals(fields[8], "win-x64", StringComparison.OrdinalIgnoreCase) ||
            !IsSafeToken(fields[6], 80))
            return UpdateActivationValidationResult.Reject("manifest_runtime_or_version_invalid");

        var fileNames = fields.Length == 13
            ? new[]
            {
                "SuavoAgent.Core.exe", "SuavoAgent.Broker.exe", "SuavoAgent.Helper.exe",
                "SuavoAgent.Watchdog.exe", MaintenanceContract.SignedSetupArtifactName,
            }
            : new[]
            {
                "SuavoAgent.Core.exe", "SuavoAgent.Broker.exe", "SuavoAgent.Helper.exe",
                "SuavoAgent.Watchdog.exe",
            };
        var urlIndexes = fields.Length == 13
            ? new[] { 0, 2, 4, 9, 11 }
            : new[] { 0, 2, 4, 9 };
        var hashIndexes = fields.Length == 13
            ? new[] { 1, 3, 5, 10, 12 }
            : new[] { 1, 3, 5, 10 };
        var files = new List<UpdatePackageFile>(fileNames.Length);
        for (var index = 0; index < fileNames.Length; index++)
        {
            if (!TryValidateArtifactUrl(fields[urlIndexes[index]], fileNames[index]) ||
                !IsSha256Hex(fields[hashIndexes[index]]))
                return UpdateActivationValidationResult.Reject("manifest_artifact_invalid");
            files.Add(new UpdatePackageFile(
                fileNames[index] == MaintenanceContract.SignedSetupArtifactName
                    ? MaintenanceContract.ExecutableName
                    : fileNames[index],
                fields[hashIndexes[index]].ToLowerInvariant()));
        }

        if (!VerifyUpdateSignature(canonical, signatureHex, updatePublicKeys))
            return UpdateActivationValidationResult.Reject("manifest_signature_invalid");

        return UpdateActivationValidationResult.Valid(new UpdatePackageManifest(
            canonical,
            fields[6],
            fields[7],
            fields[8],
            files,
            IncludesMaintenance: fields.Length == 13));
    }

    private static bool TryReadUpdateData(
        string? dataJson,
        out string manifest,
        out string manifestSignature)
    {
        manifest = string.Empty;
        manifestSignature = string.Empty;
        if (string.IsNullOrWhiteSpace(dataJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(dataJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!seen.Add(property.Name) ||
                    property.Name is not ("manifest" or "manifestSignature" or "channel"))
                    return false;
                if (property.Value.ValueKind != JsonValueKind.String) return false;
            }
            if (!seen.Contains("manifest") || !seen.Contains("manifestSignature")) return false;

            manifest = document.RootElement.GetProperty("manifest").GetString() ?? string.Empty;
            manifestSignature = document.RootElement.GetProperty("manifestSignature").GetString() ?? string.Empty;
            if (document.RootElement.TryGetProperty("channel", out var channel) &&
                !IsSafeToken(channel.GetString(), 40))
                return false;
            return manifest.Length > 0 && manifestSignature.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool VerifyCommandSignature(
        IReadOnlyDictionary<string, string> trustedCommandKeys,
        UpdateActivationRequest request)
    {
        if (!trustedCommandKeys.TryGetValue(request.KeyId, out var publicKeyDerBase64))
            return false;
        return VerifySignature(
            publicKeyDerBase64,
            RemoteCommandTrust.BuildCommandCanonical(
                request.Command,
                request.AgentId,
                request.MachineFingerprint,
                request.Timestamp,
                request.Nonce,
                request.DataHash),
            request.Signature,
            signatureIsHex: false);
    }

    private static bool VerifyUpdateSignature(
        string canonical,
        string signatureHex,
        IReadOnlyDictionary<string, string> trustedRoots) =>
        OtaUpdateTrust.VerifyP1363Hex(trustedRoots, canonical, signatureHex);

    private static bool VerifySignature(
        string publicKeyDerBase64,
        string canonical,
        string signature,
        bool signatureIsHex)
    {
        try
        {
            var keyBytes = Convert.FromBase64String(publicKeyDerBase64);
            var signatureBytes = signatureIsHex
                ? Convert.FromHexString(signature)
                : Convert.FromBase64String(signature);
            if (signatureBytes.Length != 64) return false;

            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(keyBytes, out var bytesRead);
            if (bytesRead != keyBytes.Length || key.KeySize != 256) return false;
            return key.VerifyData(
                Encoding.UTF8.GetBytes(canonical),
                signatureBytes,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is
            FormatException or
            CryptographicException or
            ArgumentException)
        {
            return false;
        }
    }

    private static bool TryValidateArtifactUrl(string value, string expectedFileName)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !AllowedHosts.Contains(uri.Host))
            return false;
        return string.Equals(
            Path.GetFileName(uri.AbsolutePath),
            expectedFileName,
            StringComparison.OrdinalIgnoreCase);
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
                TimestampFormats,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out timestamp))
            return false;
        if (timestamp > now + MaximumFutureSkew) return false;
        return now - timestamp <= maximumAge;
    }

    private static bool TryValidateTimestampWithoutMaximumAge(
        string? value,
        DateTimeOffset now,
        out DateTimeOffset timestamp)
    {
        timestamp = default;
        return !string.IsNullOrWhiteSpace(value) &&
               DateTimeOffset.TryParseExact(
                   value,
                   TimestampFormats,
                   System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.RoundtripKind,
                   out timestamp) &&
               timestamp <= now + MaximumFutureSkew;
    }

    private static bool TryDeserializeBounded<T>(
        string json,
        int maximumBytes,
        string codePrefix,
        out T? value,
        out string rejectionCode)
        where T : class
    {
        value = null;
        rejectionCode = codePrefix + "_invalid_json";
        if (string.IsNullOrWhiteSpace(json))
        {
            rejectionCode = codePrefix + "_empty";
            return false;
        }
        if (Encoding.UTF8.GetByteCount(json) > maximumBytes)
        {
            rejectionCode = codePrefix + "_too_large";
            return false;
        }

        try
        {
            value = JsonSerializer.Deserialize<T>(json, JsonOptions);
            if (value is null)
            {
                rejectionCode = codePrefix + "_null";
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

    private static bool PathEquals(string? candidate, string expected)
    {
        if (string.IsNullOrWhiteSpace(candidate) ||
            !Path.IsPathFullyQualified(candidate) ||
            !Path.IsPathFullyQualified(expected))
            return false;
        return string.Equals(
            Path.GetFullPath(candidate),
            Path.GetFullPath(expected),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool FixedTimeHexEquals(string? left, string? right)
    {
        if (!IsSha256Hex(left) || !IsSha256Hex(right)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left!),
            Convert.FromHexString(right!));
    }

    private static bool IsSha256Hex(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool IsSafeToken(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');
}
