using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using SuavoAgent.Contracts.Reasoning;

namespace SuavoAgent.Core.Workers;

internal sealed record ReasoningConfigCommandResult(
    bool IsValid,
    string Code,
    string? CommandId = null,
    JsonObject? Reasoning = null,
    BrainCohortPublisherManifest? PublisherManifest = null);

/// <summary>
/// Parses the control-plane selection separately from offline publisher
/// authorization. A signed cloud command may disable Tier-2 or select one
/// publisher-signed cohort; it cannot invent URLs, hashes, paths, or tuning.
/// </summary>
internal static class ReasoningConfigCommandContract
{
    private static readonly FrozenSet<string> EnabledFields = new[]
    {
        "commandId",
        "enabled",
        "schemaVersion",
        "cohortId",
        "modelId",
        "modelUrl",
        "modelSha256",
        "modelSizeBytes",
        "nativeLibsUrl",
        "nativeLibsSha256",
        "nativeLibsSizeBytes",
        "nativePackageKind",
        "contextSize",
        "maxOutputTokens",
        "issuedAtUtc",
        "expiresAtUtc",
        "keyId",
        "signature",
        "modelKeyId",
        "modelSignature",
        "nativeKeyId",
        "nativeSignature",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> DisabledFields = new[]
    {
        "commandId",
        "enabled",
    }.ToFrozenSet(StringComparer.Ordinal);

    internal static ReasoningConfigCommandResult Parse(
        JsonElement data,
        string dataDirectory,
        DateTimeOffset now) =>
        Parse(
            data,
            dataDirectory,
            BrainCohortContract.ProductionTrustedPublisherKeys,
            now);

    internal static ReasoningConfigCommandResult Parse(
        JsonElement data,
        string dataDirectory,
        IReadOnlyDictionary<string, string> trustedPublisherKeys,
        DateTimeOffset now)
    {
        if (data.ValueKind != JsonValueKind.Object ||
            !TryString(data, "commandId", out var commandId) ||
            commandId.Length > 200 ||
            !data.TryGetProperty("enabled", out var enabledElement) ||
            enabledElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return new(false, "reasoning_command_schema_invalid");

        var enabled = enabledElement.GetBoolean();
        if (!HasExactFields(data, enabled ? EnabledFields : DisabledFields))
            return new(false, "reasoning_command_schema_invalid", commandId);
        if (!enabled)
            return new(
                true,
                "valid",
                commandId,
                new JsonObject { ["Enabled"] = false });

        if (!TryInt32(data, "schemaVersion", out var schemaVersion) ||
            !TryString(data, "cohortId", out var cohortId) ||
            !TryString(data, "modelId", out var modelId) ||
            !TryString(data, "modelUrl", out var modelUrl) ||
            !TryString(data, "modelSha256", out var modelSha) ||
            !TryInt64(data, "modelSizeBytes", out var modelSize) ||
            !TryString(data, "nativeLibsUrl", out var nativeUrl) ||
            !TryString(data, "nativeLibsSha256", out var nativeSha) ||
            !TryInt64(data, "nativeLibsSizeBytes", out var nativeSize) ||
            !TryString(data, "nativePackageKind", out var nativePackageKind) ||
            !TryInt32(data, "contextSize", out var contextSize) ||
            !TryInt32(data, "maxOutputTokens", out var maxOutputTokens) ||
            !TryString(data, "issuedAtUtc", out var issuedAt) ||
            !TryString(data, "expiresAtUtc", out var expiresAt) ||
            !TryStringAllowEmpty(data, "keyId", out var keyId) ||
            !TryStringAllowEmpty(data, "signature", out var signature) ||
            !TryString(data, "modelKeyId", out var modelKeyId) ||
            !TryStringAllowEmpty(data, "modelSignature", out var modelSignature) ||
            !TryString(data, "nativeKeyId", out var nativeKeyId) ||
            !TryStringAllowEmpty(data, "nativeSignature", out var nativeSignature))
            return new(false, "reasoning_command_schema_invalid", commandId);

        var manifest = new BrainCohortPublisherManifest(
            schemaVersion,
            cohortId,
            modelId,
            modelUrl,
            modelSha,
            modelSize,
            nativeUrl,
            nativeSha,
            nativeSize,
            contextSize,
            maxOutputTokens,
            issuedAt,
            expiresAt,
            keyId,
            signature,
            modelKeyId,
            modelSignature,
            nativeKeyId,
            nativeSignature,
            nativePackageKind);
        var publisher = BrainCohortContract.Validate(manifest, trustedPublisherKeys, now);
        if (!publisher.IsValid)
            return new(false, publisher.Code, commandId, PublisherManifest: manifest);

        var reasoning = new JsonObject
        {
            ["Enabled"] = true,
            ["SchemaVersion"] = manifest.SchemaVersion,
            ["CohortId"] = manifest.CohortId,
            ["ModelId"] = manifest.ModelId,
            ["ModelUrl"] = manifest.ModelUrl,
            ["ModelSha256"] = manifest.ModelSha256,
            ["ModelSizeBytes"] = manifest.ModelSizeBytes,
            ["ModelPath"] = BrainCohortContract.GetModelPath(dataDirectory, manifest),
            ["NativeLibsUrl"] = manifest.NativeLibsUrl,
            ["NativeLibsSha256"] = manifest.NativeLibsSha256,
            ["NativeLibsSizeBytes"] = manifest.NativeLibsSizeBytes,
            ["NativePackageKind"] = manifest.NativePackageKind,
            ["NativeLibraryPath"] = BrainCohortContract.GetNativeDirectory(dataDirectory, manifest),
            ["ContextSize"] = manifest.ContextSize,
            ["MaxOutputTokens"] = manifest.MaxOutputTokens,
            ["IssuedAtUtc"] = manifest.IssuedAtUtc,
            ["ExpiresAtUtc"] = manifest.ExpiresAtUtc,
            ["KeyId"] = manifest.KeyId,
            ["Signature"] = manifest.Signature,
            ["ModelKeyId"] = manifest.ModelKeyId,
            ["ModelSignature"] = manifest.ModelSignature,
            ["NativeKeyId"] = manifest.NativeKeyId,
            ["NativeSignature"] = manifest.NativeSignature,
        };
        return new(true, "valid", commandId, reasoning, manifest);
    }

    private static bool HasExactFields(JsonElement data, IReadOnlySet<string> expected)
    {
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in data.EnumerateObject())
            if (!actual.Add(property.Name)) return false;
        return actual.SetEquals(expected);
    }

    private static bool TryString(JsonElement data, string name, out string value)
    {
        value = string.Empty;
        if (!data.TryGetProperty(name, out var element) ||
            element.ValueKind != JsonValueKind.String)
            return false;
        value = element.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool TryStringAllowEmpty(
        JsonElement data,
        string name,
        out string value)
    {
        value = string.Empty;
        if (!data.TryGetProperty(name, out var element) ||
            element.ValueKind != JsonValueKind.String)
            return false;
        value = element.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryInt32(JsonElement data, string name, out int value)
    {
        value = 0;
        return data.TryGetProperty(name, out var element) &&
               element.ValueKind == JsonValueKind.Number &&
               element.TryGetInt32(out value);
    }

    private static bool TryInt64(JsonElement data, string name, out long value)
    {
        value = 0;
        return data.TryGetProperty(name, out var element) &&
               element.ValueKind == JsonValueKind.Number &&
               element.TryGetInt64(out value);
    }
}
