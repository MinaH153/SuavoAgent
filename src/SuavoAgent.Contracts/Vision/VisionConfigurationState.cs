namespace SuavoAgent.Contracts.Vision;

public sealed record VisionTesseractSnapshot(
    bool Enabled,
    string? CohortId,
    string? BundleSha256,
    string? ManifestSha256,
    string? NativeLibraryPath,
    string? TessdataPath,
    string Language,
    int MinConfidence,
    int IdleUnloadSeconds,
    long MemoryHeadroomBytes,
    int ExtractionTimeoutSeconds);

public sealed record VisionPeriodicCaptureSnapshot(
    bool Enabled,
    int IntervalSeconds,
    bool RequireForegroundMatch);

public sealed record VisionShadowReasoningSnapshot(bool Enabled, string SkillId);

public sealed record VisionCloudFrameUploadSnapshot(bool Enabled, int SamplingInterval);

/// <summary>
/// Immutable representation of every effective machine-vision option stored
/// in the registry document.
/// </summary>
public sealed record VisionOptionsSnapshot(
    bool Enabled,
    string? StorageDirectory,
    int RetentionHours,
    int MaxStoredScreens,
    int MinIntervalMs,
    VisionTesseractSnapshot Tesseract,
    VisionPeriodicCaptureSnapshot PeriodicCapture,
    VisionShadowReasoningSnapshot ShadowReasoning,
    VisionCloudFrameUploadSnapshot CloudFrameUpload)
{
    public static VisionOptionsSnapshot DisabledDefault() => new(
        false,
        null,
        24,
        500,
        1_000,
        new(false, null, null, null, null, null, "eng", 50, 45,
            350L * 1024 * 1024, 10),
        new(false, 30, true),
        new(false, "vision-observe"),
        new(false, 1));

    public string? Validate(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            return "vision_state_data_directory_invalid";
        if (RetentionHours is < 0 or > 168)
            return "vision_state_retention_hours_invalid";
        if (MaxStoredScreens is < 1 or > 5_000)
            return "vision_state_max_stored_screens_invalid";
        if (MinIntervalMs is < 250 or > 60_000)
            return "vision_state_min_interval_invalid";
        if (PeriodicCapture.IntervalSeconds is < 5 or > 3_600)
            return "vision_state_periodic_interval_invalid";
        if (CloudFrameUpload.SamplingInterval is < 1 or > 1_000)
            return "vision_state_cloud_sampling_invalid";
        if (!IsSafeSkillId(ShadowReasoning.SkillId))
            return "vision_state_shadow_skill_invalid";
        if (Tesseract.Language != "eng")
            return "vision_state_language_invalid";
        if (Tesseract.MinConfidence is < 0 or > 100)
            return "vision_state_min_confidence_invalid";
        if (Tesseract.IdleUnloadSeconds is < 0 or > 3_600)
            return "vision_state_idle_unload_invalid";
        if (Tesseract.MemoryHeadroomBytes != 0 &&
            Tesseract.MemoryHeadroomBytes is < (64L * 1024 * 1024) or > (4L * 1024 * 1024 * 1024))
            return "vision_state_memory_headroom_invalid";
        if (Tesseract.ExtractionTimeoutSeconds is < 1 or > 120)
            return "vision_state_extraction_timeout_invalid";
        if (!Enabled && (Tesseract.Enabled || PeriodicCapture.Enabled ||
                         ShadowReasoning.Enabled || CloudFrameUpload.Enabled))
            return "vision_state_disabled_subfeature_enabled";

        var root = Path.GetFullPath(dataDirectory);
        var screens = Path.Combine(root, "screens");
        if (StorageDirectory is not null && !PathEquals(StorageDirectory, screens))
            return "vision_state_storage_path_invalid";

        var cohorts = Path.Combine(root, "vision", "cohorts");
        if (!Tesseract.Enabled)
        {
            if (Tesseract.CohortId is not null || Tesseract.BundleSha256 is not null ||
                Tesseract.ManifestSha256 is not null)
                return "vision_state_disabled_tesseract_identity_present";
            var bothNull = Tesseract.NativeLibraryPath is null && Tesseract.TessdataPath is null;
            var inactivePaths = Tesseract.NativeLibraryPath is not null &&
                                Tesseract.TessdataPath is not null &&
                                PathEquals(Tesseract.NativeLibraryPath, cohorts) &&
                                PathEquals(
                                    Tesseract.TessdataPath,
                                    Path.Combine(cohorts, "tessdata"));
            return bothNull || inactivePaths
                ? null
                : "vision_state_disabled_tesseract_path_invalid";
        }

        if (!IsCanonicalIdentifier(Tesseract.CohortId) ||
            !IsLowerHexSha256(Tesseract.BundleSha256) ||
            !IsLowerHexSha256(Tesseract.ManifestSha256) ||
            Tesseract.NativeLibraryPath is null || Tesseract.TessdataPath is null)
            return "vision_state_tesseract_identity_invalid";
        var expectedCohort = Path.Combine(cohorts, Tesseract.BundleSha256!);
        return PathEquals(Tesseract.NativeLibraryPath, expectedCohort) &&
               PathEquals(Tesseract.TessdataPath, Path.Combine(expectedCohort, "tessdata"))
            ? null
            : "vision_state_tesseract_path_invalid";
    }

    private static bool IsSafeSkillId(string? value) =>
        value is { Length: >= 1 and <= 128 } && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool IsCanonicalIdentifier(string? value) =>
        value is { Length: >= 1 and <= 128 } && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    public static bool IsLowerHexSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool PathEquals(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is
                   ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }
}

public sealed record VisionConfigurationState(
    int SchemaVersion,
    long Generation,
    string CommandId,
    DateTimeOffset AppliedAt,
    VisionOptionsSnapshot VisionOptions,
    string ConfigDigest);

public sealed record VisionConfigurationParseResult(
    bool IsValid,
    string Code,
    VisionConfigurationState? State = null);
