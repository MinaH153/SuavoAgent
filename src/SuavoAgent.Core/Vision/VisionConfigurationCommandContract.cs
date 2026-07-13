using System.Text.Json;
using SuavoAgent.Contracts.Vision;

namespace SuavoAgent.Core.Vision;

internal sealed record VisionConfigurationCommand(
    string CommandId,
    bool Enabled,
    bool TesseractEnabled,
    string? BundleUrl,
    string? BundleSha256,
    string? CohortId,
    string? ManifestSha256,
    string NativeLibraryPath,
    string TessdataPath,
    string Language,
    int MinConfidence,
    VisionOptionsSnapshot EffectiveOptions);

internal sealed record VisionConfigurationCommandResult(
    bool IsValid,
    string Code,
    VisionConfigurationCommand? Command = null,
    string? CommandId = null);

/// <summary>
/// Strict signed-command contract for the local vision configuration. Paths
/// are derived locally and never accepted from the control plane.
/// </summary>
internal static class VisionConfigurationCommandContract
{
    internal static VisionConfigurationCommandResult Parse(
        JsonElement data,
        string dataDirectory,
        Func<string, string, TesseractNativeCohort?>? nativeCohortResolver = null)
    {
        if (data.ValueKind != JsonValueKind.Object)
            return Reject("vision_config_object_required");
        if (string.IsNullOrWhiteSpace(dataDirectory))
            return Reject("vision_data_directory_invalid");

        var commandId = ReadRequiredCanonicalUuid(data, "commandId");
        if (commandId is null)
            return Reject("vision_command_id_invalid");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in data.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                return Reject("vision_config_duplicate_field", commandId);
            if (property.Name is not (
                    "commandId" or "enabled" or "tesseractEnabled" or
                    "bundleUrl" or "bundleSha256" or "language" or
                    "minConfidence" or "retentionHours" or
                    "maxStoredScreens" or "minIntervalMs" or
                    "periodicCaptureEnabled" or "periodicCaptureIntervalSeconds" or
                    "requireForegroundMatch" or "shadowReasoningEnabled" or
                    "shadowSkillId" or "cloudFrameUploadEnabled" or
                    "cloudSamplingInterval" or "idleUnloadSeconds" or
                    "memoryHeadroomBytes" or "extractionTimeoutSeconds"))
                return Reject("vision_config_unknown_field", commandId);
        }

        if (!TryRequiredBoolean(data, "enabled", out var enabled) ||
            !TryRequiredBoolean(data, "tesseractEnabled", out var tesseractEnabled))
            return Reject("vision_config_boolean_invalid", commandId);
        if (!TryOptionalBoolean(data, "periodicCaptureEnabled", false, out var periodicEnabled) ||
            !TryOptionalBoolean(data, "requireForegroundMatch", true, out var requireForeground) ||
            !TryOptionalBoolean(data, "shadowReasoningEnabled", false, out var shadowEnabled) ||
            !TryOptionalBoolean(data, "cloudFrameUploadEnabled", false, out var cloudEnabled))
            return Reject("vision_config_optional_boolean_invalid", commandId);
        if (!enabled && (tesseractEnabled || periodicEnabled || shadowEnabled || cloudEnabled))
            return Reject("vision_config_disabled_subfeature_enabled", commandId);

        var language = ReadOptionalString(data, "language", out var languageShape) ?? "eng";
        if (!languageShape || language != "eng")
            return Reject("vision_language_not_approved", commandId);
        var minConfidence = 50;
        if (data.TryGetProperty("minConfidence", out var confidence))
        {
            if (!confidence.TryGetInt32(out minConfidence) || minConfidence is < 0 or > 100)
                return Reject("vision_min_confidence_invalid", commandId);
        }
        if (!TryOptionalInt32(data, "retentionHours", 24, 0, 168, out var retentionHours) ||
            !TryOptionalInt32(data, "maxStoredScreens", 500, 1, 5_000, out var maxStored) ||
            !TryOptionalInt32(data, "minIntervalMs", 1_000, 250, 60_000, out var minInterval) ||
            !TryOptionalInt32(
                data,
                "periodicCaptureIntervalSeconds",
                30,
                5,
                3_600,
                out var periodicInterval) ||
            !TryOptionalInt32(data, "cloudSamplingInterval", 1, 1, 1_000, out var cloudSampling) ||
            !TryOptionalInt32(data, "idleUnloadSeconds", 45, 0, 3_600, out var idleUnload) ||
            !TryOptionalInt64(
                data,
                "memoryHeadroomBytes",
                350L * 1024 * 1024,
                0,
                4L * 1024 * 1024 * 1024,
                out var memoryHeadroom) ||
            memoryHeadroom is > 0 and < (64L * 1024 * 1024) ||
            !TryOptionalInt32(
                data,
                "extractionTimeoutSeconds",
                10,
                1,
                120,
                out var extractionTimeout))
            return Reject("vision_config_option_bounds_invalid", commandId);
        var shadowSkillId = ReadOptionalString(data, "shadowSkillId", out var skillShape)
                            ?? "vision-observe";
        if (!skillShape || shadowSkillId.Length > 128 || shadowSkillId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
            return Reject("vision_config_shadow_skill_invalid", commandId);

        var bundleUrl = ReadOptionalString(data, "bundleUrl", out var urlShape);
        var bundleSha = ReadOptionalString(data, "bundleSha256", out var hashShape);
        if (!urlShape || !hashShape)
            return Reject("vision_bundle_metadata_invalid", commandId);

        var root = Path.GetFullPath(dataDirectory);
        if (!tesseractEnabled)
        {
            if (bundleUrl is not null || bundleSha is not null)
                return Reject("vision_bundle_metadata_forbidden_when_ocr_disabled", commandId);
            var inactiveRoot = Path.Combine(root, "vision", "cohorts");
            var options = BuildOptions(
                enabled,
                false,
                null,
                null,
                null,
                inactiveRoot,
                Path.Combine(inactiveRoot, "tessdata"),
                language,
                minConfidence,
                retentionHours,
                maxStored,
                minInterval,
                periodicEnabled,
                periodicInterval,
                requireForeground,
                shadowEnabled,
                shadowSkillId,
                cloudEnabled,
                cloudSampling,
                idleUnload,
                memoryHeadroom,
                extractionTimeout);
            var validationCode = options.Validate(root);
            if (validationCode is not null) return Reject(validationCode, commandId);
            return Valid(new(
                commandId,
                enabled,
                false,
                null,
                null,
                null,
                null,
                inactiveRoot,
                Path.Combine(inactiveRoot, "tessdata"),
                language,
                minConfidence,
                options));
        }

        if (string.IsNullOrWhiteSpace(bundleUrl) || string.IsNullOrWhiteSpace(bundleSha))
            return Reject("vision_bundle_metadata_required", commandId);
        var normalizedHash = bundleSha.Trim().ToLowerInvariant();
        var resolver = nativeCohortResolver ?? TesseractNativeCohortPolicy.Resolve;
        var cohort = resolver(bundleUrl, normalizedHash);
        if (cohort is null || !TesseractNativeCohortPolicy.IsWellFormed(cohort) ||
            !string.Equals(cohort.BundleUrl, bundleUrl, StringComparison.Ordinal) ||
            !string.Equals(cohort.BundleSha256, normalizedHash, StringComparison.Ordinal))
            return Reject("tesseract_native_cohort_not_release_approved", commandId);

        var cohortRoot = Path.Combine(root, "vision", "cohorts", normalizedHash);
        var manifestSha = TesseractNativeCohortPolicy.ComputeManifestSha256(cohort);
        var enabledOptions = BuildOptions(
            enabled,
            true,
            cohort.CohortId,
            normalizedHash,
            manifestSha,
            cohortRoot,
            Path.Combine(cohortRoot, "tessdata"),
            language,
            minConfidence,
            retentionHours,
            maxStored,
            minInterval,
            periodicEnabled,
            periodicInterval,
            requireForeground,
            shadowEnabled,
            shadowSkillId,
            cloudEnabled,
            cloudSampling,
            idleUnload,
            memoryHeadroom,
            extractionTimeout);
        var enabledValidationCode = enabledOptions.Validate(root);
        if (enabledValidationCode is not null)
            return Reject(enabledValidationCode, commandId);
        return Valid(new(
            commandId,
            enabled,
            true,
            bundleUrl,
            normalizedHash,
            cohort.CohortId,
            manifestSha,
            cohortRoot,
            Path.Combine(cohortRoot, "tessdata"),
            language,
            minConfidence,
            enabledOptions));
    }

    private static VisionOptionsSnapshot BuildOptions(
        bool enabled,
        bool tesseractEnabled,
        string? cohortId,
        string? bundleSha,
        string? manifestSha,
        string nativePath,
        string tessdataPath,
        string language,
        int minConfidence,
        int retentionHours,
        int maxStored,
        int minInterval,
        bool periodicEnabled,
        int periodicInterval,
        bool requireForeground,
        bool shadowEnabled,
        string shadowSkillId,
        bool cloudEnabled,
        int cloudSampling,
        int idleUnload,
        long memoryHeadroom,
        int extractionTimeout) => new(
        enabled,
        null,
        retentionHours,
        maxStored,
        minInterval,
        new(
            tesseractEnabled,
            cohortId,
            bundleSha,
            manifestSha,
            nativePath,
            tessdataPath,
            language,
            minConfidence,
            idleUnload,
            memoryHeadroom,
            extractionTimeout),
        new(periodicEnabled, periodicInterval, requireForeground),
        new(shadowEnabled, shadowSkillId),
        new(cloudEnabled, cloudSampling));

    private static bool TryRequiredBoolean(JsonElement data, string name, out bool value)
    {
        value = false;
        if (!data.TryGetProperty(name, out var property)) return false;
        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = property.GetBoolean();
        return true;
    }

    private static bool TryOptionalBoolean(
        JsonElement data,
        string name,
        bool defaultValue,
        out bool value)
    {
        value = defaultValue;
        if (!data.TryGetProperty(name, out var property)) return true;
        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = property.GetBoolean();
        return true;
    }

    private static bool TryOptionalInt32(
        JsonElement data,
        string name,
        int defaultValue,
        int minimum,
        int maximum,
        out int value)
    {
        value = defaultValue;
        if (!data.TryGetProperty(name, out var property)) return true;
        if (property.ValueKind != JsonValueKind.Number) return false;
        return property.TryGetInt32(out value) && value >= minimum && value <= maximum;
    }

    private static bool TryOptionalInt64(
        JsonElement data,
        string name,
        long defaultValue,
        long minimum,
        long maximum,
        out long value)
    {
        value = defaultValue;
        if (!data.TryGetProperty(name, out var property)) return true;
        if (property.ValueKind != JsonValueKind.Number) return false;
        return property.TryGetInt64(out value) && value >= minimum && value <= maximum;
    }

    private static string? ReadRequiredCanonicalUuid(JsonElement data, string name)
    {
        if (!data.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String)
            return null;
        var value = property.GetString();
        return value is { Length: 36 } && Guid.TryParseExact(value, "D", out var parsed) &&
               string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal)
            ? value
            : null;
    }

    private static string? ReadOptionalString(
        JsonElement data,
        string name,
        out bool shapeIsValid)
    {
        shapeIsValid = true;
        if (!data.TryGetProperty(name, out var property)) return null;
        if (property.ValueKind != JsonValueKind.String)
        {
            shapeIsValid = false;
            return null;
        }
        var value = property.GetString();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2_048 || value.Any(char.IsControl))
        {
            shapeIsValid = false;
            return null;
        }
        return value;
    }

    private static VisionConfigurationCommandResult Valid(VisionConfigurationCommand command) =>
        new(true, "valid", command, command.CommandId);

    private static VisionConfigurationCommandResult Reject(
        string code,
        string? commandId = null) => new(false, code, CommandId: commandId);
}
