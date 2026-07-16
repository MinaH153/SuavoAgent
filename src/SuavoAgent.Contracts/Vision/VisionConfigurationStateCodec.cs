using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SuavoAgent.Contracts.Vision;

/// <summary>Strict, deterministic codec for the one registry state value.</summary>
public static class VisionConfigurationStateCodec
{
    public const int SchemaVersion = 1;
    private const int MaximumDepth = 8;
    private const string UtcTimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    public static VisionConfigurationState Create(
        long generation,
        string commandId,
        DateTimeOffset appliedAt,
        VisionOptionsSnapshot options,
        string dataDirectory)
    {
        if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
        if (!IsCanonicalUuid(commandId))
            throw new ArgumentException("Command id must be a canonical UUID.", nameof(commandId));
        if (appliedAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("Applied time must be UTC.", nameof(appliedAt));
        var optionsCode = options.Validate(dataDirectory);
        if (optionsCode is not null)
            throw new ArgumentException(optionsCode, nameof(options));
        return new(
            SchemaVersion,
            generation,
            commandId,
            appliedAt,
            options,
            ComputeConfigDigest(options));
    }

    public static string Serialize(VisionConfigurationState state, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.SchemaVersion != SchemaVersion || state.Generation < 1 ||
            !IsCanonicalUuid(state.CommandId) || state.AppliedAt.Offset != TimeSpan.Zero ||
            state.VisionOptions.Validate(dataDirectory) is not null ||
            !FixedDigestEquals(
                state.ConfigDigest,
                ComputeConfigDigest(state.VisionOptions)))
        {
            throw new ArgumentException("Vision configuration state is invalid.", nameof(state));
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
               {
                   Indented = false,
                   SkipValidation = false,
               }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", state.SchemaVersion);
            writer.WriteNumber("generation", state.Generation);
            writer.WriteString("commandId", state.CommandId);
            writer.WriteString(
                "appliedAt",
                state.AppliedAt.UtcDateTime.ToString(UtcTimestampFormat, CultureInfo.InvariantCulture));
            writer.WritePropertyName("visionOptions");
            WriteOptions(writer, state.VisionOptions);
            writer.WriteString("configDigest", state.ConfigDigest);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static VisionConfigurationParseResult Parse(
        string? json,
        string dataDirectory)
    {
        if (string.IsNullOrEmpty(json))
            return Reject("vision_state_empty");
        if (json.Length > SuavoAgent.Contracts.Security.VisionRegistryAuthority.MaximumStateCharacters)
            return Reject("vision_state_too_large");

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaximumDepth,
            });
            var root = document.RootElement;
            var topCode = ValidateObject(
                root,
                "vision_state",
                "schemaVersion", "generation", "commandId", "appliedAt",
                "visionOptions", "configDigest");
            if (topCode is not null) return Reject(topCode);

            if (!TryRequiredInt32(root, "schemaVersion", out var schemaVersion) ||
                schemaVersion != SchemaVersion)
                return Reject("vision_state_schema_version_invalid");
            if (!TryRequiredInt64(root, "generation", out var generation) || generation < 1)
                return Reject("vision_state_generation_invalid");
            if (!TryRequiredString(root, "commandId", out var commandId) ||
                !IsCanonicalUuid(commandId))
                return Reject("vision_state_command_id_invalid");
            if (!TryRequiredString(root, "appliedAt", out var appliedAtText) ||
                !DateTimeOffset.TryParseExact(
                    appliedAtText,
                    UtcTimestampFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var appliedAt) ||
                appliedAt.Offset != TimeSpan.Zero ||
                !string.Equals(
                    appliedAt.UtcDateTime.ToString(UtcTimestampFormat, CultureInfo.InvariantCulture),
                    appliedAtText,
                    StringComparison.Ordinal))
                return Reject("vision_state_applied_at_invalid");
            if (!TryRequiredString(root, "configDigest", out var digest) ||
                !VisionOptionsSnapshot.IsLowerHexSha256(digest))
                return Reject("vision_state_digest_invalid");

            var optionsResult = ParseOptions(root.GetProperty("visionOptions"));
            if (optionsResult.Code is not null || optionsResult.Options is null)
                return Reject(optionsResult.Code ?? "vision_state_options_invalid");
            var validationCode = optionsResult.Options.Validate(dataDirectory);
            if (validationCode is not null) return Reject(validationCode);
            var computed = ComputeConfigDigest(optionsResult.Options);
            if (!FixedDigestEquals(digest, computed))
                return Reject("vision_state_digest_mismatch");

            return new(
                true,
                "valid",
                new(
                    schemaVersion,
                    generation,
                    commandId!,
                    appliedAt,
                    optionsResult.Options,
                    digest!));
        }
        catch (Exception exception) when (exception is
                   JsonException or ArgumentException or IOException or NotSupportedException)
        {
            return Reject($"vision_state_parse_failed_{exception.GetType().Name}");
        }
    }

    public static string ComputeConfigDigest(VisionOptionsSnapshot options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
               {
                   Indented = false,
                   SkipValidation = false,
               }))
        {
            WriteOptions(writer, options);
        }
        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    private static void WriteOptions(Utf8JsonWriter writer, VisionOptionsSnapshot options)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("enabled", options.Enabled);
        WriteNullableString(writer, "storageDirectory", options.StorageDirectory);
        writer.WriteNumber("retentionHours", options.RetentionHours);
        writer.WriteNumber("maxStoredScreens", options.MaxStoredScreens);
        writer.WriteNumber("minIntervalMs", options.MinIntervalMs);
        writer.WritePropertyName("tesseract");
        writer.WriteStartObject();
        writer.WriteBoolean("enabled", options.Tesseract.Enabled);
        WriteNullableString(writer, "cohortId", options.Tesseract.CohortId);
        WriteNullableString(writer, "bundleSha256", options.Tesseract.BundleSha256);
        WriteNullableString(writer, "manifestSha256", options.Tesseract.ManifestSha256);
        WriteNullableString(writer, "nativeLibraryPath", options.Tesseract.NativeLibraryPath);
        WriteNullableString(writer, "tessdataPath", options.Tesseract.TessdataPath);
        writer.WriteString("language", options.Tesseract.Language);
        writer.WriteNumber("minConfidence", options.Tesseract.MinConfidence);
        writer.WriteNumber("idleUnloadSeconds", options.Tesseract.IdleUnloadSeconds);
        writer.WriteNumber("memoryHeadroomBytes", options.Tesseract.MemoryHeadroomBytes);
        writer.WriteNumber(
            "extractionTimeoutSeconds",
            options.Tesseract.ExtractionTimeoutSeconds);
        writer.WriteEndObject();
        writer.WritePropertyName("periodicCapture");
        writer.WriteStartObject();
        writer.WriteBoolean("enabled", options.PeriodicCapture.Enabled);
        writer.WriteNumber("intervalSeconds", options.PeriodicCapture.IntervalSeconds);
        writer.WriteBoolean(
            "requireForegroundMatch",
            options.PeriodicCapture.RequireForegroundMatch);
        writer.WriteEndObject();
        writer.WritePropertyName("shadowReasoning");
        writer.WriteStartObject();
        writer.WriteBoolean("enabled", options.ShadowReasoning.Enabled);
        writer.WriteString("skillId", options.ShadowReasoning.SkillId);
        writer.WriteEndObject();
        writer.WritePropertyName("cloudFrameUpload");
        writer.WriteStartObject();
        writer.WriteBoolean("enabled", options.CloudFrameUpload.Enabled);
        writer.WriteNumber("samplingInterval", options.CloudFrameUpload.SamplingInterval);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static (VisionOptionsSnapshot? Options, string? Code) ParseOptions(JsonElement element)
    {
        var code = ValidateObject(
            element,
            "vision_state_options",
            "enabled", "storageDirectory", "retentionHours", "maxStoredScreens",
            "minIntervalMs", "tesseract", "periodicCapture", "shadowReasoning",
            "cloudFrameUpload");
        if (code is not null) return (null, code);
        if (!TryRequiredBoolean(element, "enabled", out var enabled) ||
            !TryNullableString(element, "storageDirectory", out var storageDirectory) ||
            !TryRequiredInt32(element, "retentionHours", out var retentionHours) ||
            !TryRequiredInt32(element, "maxStoredScreens", out var maxStoredScreens) ||
            !TryRequiredInt32(element, "minIntervalMs", out var minIntervalMs))
            return (null, "vision_state_options_shape_invalid");

        var tesseract = ParseTesseract(element.GetProperty("tesseract"));
        if (tesseract.Code is not null || tesseract.Value is null)
            return (null, tesseract.Code ?? "vision_state_tesseract_invalid");
        var periodic = ParsePeriodic(element.GetProperty("periodicCapture"));
        if (periodic.Code is not null || periodic.Value is null)
            return (null, periodic.Code ?? "vision_state_periodic_invalid");
        var shadow = ParseShadow(element.GetProperty("shadowReasoning"));
        if (shadow.Code is not null || shadow.Value is null)
            return (null, shadow.Code ?? "vision_state_shadow_invalid");
        var cloud = ParseCloud(element.GetProperty("cloudFrameUpload"));
        if (cloud.Code is not null || cloud.Value is null)
            return (null, cloud.Code ?? "vision_state_cloud_invalid");

        return (new(
            enabled,
            storageDirectory,
            retentionHours,
            maxStoredScreens,
            minIntervalMs,
            tesseract.Value,
            periodic.Value,
            shadow.Value,
            cloud.Value), null);
    }

    private static (VisionTesseractSnapshot? Value, string? Code) ParseTesseract(
        JsonElement element)
    {
        var code = ValidateObject(
            element,
            "vision_state_tesseract",
            "enabled", "cohortId", "bundleSha256", "manifestSha256",
            "nativeLibraryPath", "tessdataPath", "language", "minConfidence",
            "idleUnloadSeconds", "memoryHeadroomBytes", "extractionTimeoutSeconds");
        if (code is not null) return (null, code);
        if (!TryRequiredBoolean(element, "enabled", out var enabled) ||
            !TryNullableString(element, "cohortId", out var cohortId) ||
            !TryNullableString(element, "bundleSha256", out var bundleSha) ||
            !TryNullableString(element, "manifestSha256", out var manifestSha) ||
            !TryNullableString(element, "nativeLibraryPath", out var nativePath) ||
            !TryNullableString(element, "tessdataPath", out var tessdataPath) ||
            !TryRequiredString(element, "language", out var language) ||
            !TryRequiredInt32(element, "minConfidence", out var minConfidence) ||
            !TryRequiredInt32(element, "idleUnloadSeconds", out var idleUnload) ||
            !TryRequiredInt64(element, "memoryHeadroomBytes", out var memoryHeadroom) ||
            !TryRequiredInt32(
                element,
                "extractionTimeoutSeconds",
                out var extractionTimeout))
            return (null, "vision_state_tesseract_shape_invalid");
        return (new(
            enabled,
            cohortId,
            bundleSha,
            manifestSha,
            nativePath,
            tessdataPath,
            language!,
            minConfidence,
            idleUnload,
            memoryHeadroom,
            extractionTimeout), null);
    }

    private static (VisionPeriodicCaptureSnapshot? Value, string? Code) ParsePeriodic(
        JsonElement element)
    {
        var code = ValidateObject(
            element,
            "vision_state_periodic",
            "enabled", "intervalSeconds", "requireForegroundMatch");
        if (code is not null) return (null, code);
        return TryRequiredBoolean(element, "enabled", out var enabled) &&
               TryRequiredInt32(element, "intervalSeconds", out var interval) &&
               TryRequiredBoolean(element, "requireForegroundMatch", out var foreground)
            ? (new(enabled, interval, foreground), null)
            : (null, "vision_state_periodic_shape_invalid");
    }

    private static (VisionShadowReasoningSnapshot? Value, string? Code) ParseShadow(
        JsonElement element)
    {
        var code = ValidateObject(
            element,
            "vision_state_shadow",
            "enabled", "skillId");
        if (code is not null) return (null, code);
        return TryRequiredBoolean(element, "enabled", out var enabled) &&
               TryRequiredString(element, "skillId", out var skillId)
            ? (new(enabled, skillId!), null)
            : (null, "vision_state_shadow_shape_invalid");
    }

    private static (VisionCloudFrameUploadSnapshot? Value, string? Code) ParseCloud(
        JsonElement element)
    {
        var code = ValidateObject(
            element,
            "vision_state_cloud",
            "enabled", "samplingInterval");
        if (code is not null) return (null, code);
        return TryRequiredBoolean(element, "enabled", out var enabled) &&
               TryRequiredInt32(element, "samplingInterval", out var interval)
            ? (new(enabled, interval), null)
            : (null, "vision_state_cloud_shape_invalid");
    }

    private static string? ValidateObject(
        JsonElement element,
        string codePrefix,
        params string[] requiredNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return $"{codePrefix}_object_required";
        var expected = new HashSet<string>(requiredNames, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name)) return $"{codePrefix}_unknown_field";
            if (!seen.Add(property.Name)) return $"{codePrefix}_duplicate_field";
        }
        return seen.Count == expected.Count ? null : $"{codePrefix}_missing_field";
    }

    private static bool TryRequiredBoolean(JsonElement element, string name, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;
        value = property.GetBoolean();
        return true;
    }

    private static bool TryRequiredInt32(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.TryGetInt32(out value);
    }

    private static bool TryRequiredInt64(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.TryGetInt64(out value);
    }

    private static bool TryRequiredString(
        JsonElement element,
        string name,
        out string? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString();
        return value is { Length: >= 1 and <= 2_048 } && !value.Any(char.IsControl);
    }

    private static bool TryNullableString(
        JsonElement element,
        string name,
        out string? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property)) return false;
        if (property.ValueKind == JsonValueKind.Null) return true;
        if (property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString();
        return value is { Length: >= 1 and <= 2_048 } && !value.Any(char.IsControl);
    }

    private static bool IsCanonicalUuid(string? value) =>
        value is { Length: 36 } &&
        Guid.TryParseExact(value, "D", out var parsed) &&
        string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal);

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }

    private static bool FixedDigestEquals(string? left, string? right)
    {
        if (!VisionOptionsSnapshot.IsLowerHexSha256(left) ||
            !VisionOptionsSnapshot.IsLowerHexSha256(right))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left!),
            Encoding.ASCII.GetBytes(right!));
    }

    private static VisionConfigurationParseResult Reject(string code) => new(false, code);
}
