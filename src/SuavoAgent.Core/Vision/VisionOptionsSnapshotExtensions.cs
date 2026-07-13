using System.Globalization;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Core.Config;

namespace SuavoAgent.Core.Vision;

public static class VisionOptionsSnapshotExtensions
{
    public static VisionOptionsSnapshot ToSnapshot(this VisionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new(
            options.Enabled,
            options.StorageDirectory,
            options.RetentionHours,
            options.MaxStoredScreens,
            options.MinIntervalMs,
            new(
                options.Tesseract.Enabled,
                options.Tesseract.CohortId,
                options.Tesseract.BundleSha256,
                options.Tesseract.ManifestSha256,
                options.Tesseract.NativeLibraryPath,
                options.Tesseract.TessdataPath,
                options.Tesseract.Language,
                options.Tesseract.MinConfidence,
                options.Tesseract.IdleUnloadSeconds,
                options.Tesseract.MemoryHeadroomBytes,
                options.Tesseract.ExtractionTimeoutSeconds),
            new(
                options.PeriodicCapture.Enabled,
                options.PeriodicCapture.IntervalSeconds,
                options.PeriodicCapture.RequireForegroundMatch),
            new(options.ShadowReasoning.Enabled, options.ShadowReasoning.SkillId),
            new(
                options.CloudFrameUpload.Enabled,
                options.CloudFrameUpload.SamplingInterval));
    }

    public static VisionOptions ToOptions(this VisionOptionsSnapshot snapshot) => new()
    {
        Enabled = snapshot.Enabled,
        StorageDirectory = snapshot.StorageDirectory,
        RetentionHours = snapshot.RetentionHours,
        MaxStoredScreens = snapshot.MaxStoredScreens,
        MinIntervalMs = snapshot.MinIntervalMs,
        Tesseract = new TesseractOptions
        {
            Enabled = snapshot.Tesseract.Enabled,
            CohortId = snapshot.Tesseract.CohortId,
            BundleSha256 = snapshot.Tesseract.BundleSha256,
            ManifestSha256 = snapshot.Tesseract.ManifestSha256,
            NativeLibraryPath = snapshot.Tesseract.NativeLibraryPath,
            TessdataPath = snapshot.Tesseract.TessdataPath,
            Language = snapshot.Tesseract.Language,
            MinConfidence = snapshot.Tesseract.MinConfidence,
            IdleUnloadSeconds = snapshot.Tesseract.IdleUnloadSeconds,
            MemoryHeadroomBytes = snapshot.Tesseract.MemoryHeadroomBytes,
            ExtractionTimeoutSeconds = snapshot.Tesseract.ExtractionTimeoutSeconds,
        },
        PeriodicCapture = new VisionPeriodicCaptureOptions
        {
            Enabled = snapshot.PeriodicCapture.Enabled,
            IntervalSeconds = snapshot.PeriodicCapture.IntervalSeconds,
            RequireForegroundMatch = snapshot.PeriodicCapture.RequireForegroundMatch,
        },
        ShadowReasoning = new VisionShadowReasoningOptions
        {
            Enabled = snapshot.ShadowReasoning.Enabled,
            SkillId = snapshot.ShadowReasoning.SkillId,
        },
        CloudFrameUpload = new VisionCloudFrameUploadOptions
        {
            Enabled = snapshot.CloudFrameUpload.Enabled,
            SamplingInterval = snapshot.CloudFrameUpload.SamplingInterval,
        },
    };

    public static IReadOnlyDictionary<string, string?> ToConfigurationValues(
        this VisionOptionsSnapshot snapshot,
        string prefix = "Agent:Vision") => new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        [$"{prefix}:Enabled"] = snapshot.Enabled.ToString(),
        [$"{prefix}:StorageDirectory"] = snapshot.StorageDirectory,
        [$"{prefix}:RetentionHours"] = Text(snapshot.RetentionHours),
        [$"{prefix}:MaxStoredScreens"] = Text(snapshot.MaxStoredScreens),
        [$"{prefix}:MinIntervalMs"] = Text(snapshot.MinIntervalMs),
        [$"{prefix}:Tesseract:Enabled"] = snapshot.Tesseract.Enabled.ToString(),
        [$"{prefix}:Tesseract:CohortId"] = snapshot.Tesseract.CohortId,
        [$"{prefix}:Tesseract:BundleSha256"] = snapshot.Tesseract.BundleSha256,
        [$"{prefix}:Tesseract:ManifestSha256"] = snapshot.Tesseract.ManifestSha256,
        [$"{prefix}:Tesseract:NativeLibraryPath"] = snapshot.Tesseract.NativeLibraryPath,
        [$"{prefix}:Tesseract:TessdataPath"] = snapshot.Tesseract.TessdataPath,
        [$"{prefix}:Tesseract:Language"] = snapshot.Tesseract.Language,
        [$"{prefix}:Tesseract:MinConfidence"] = Text(snapshot.Tesseract.MinConfidence),
        [$"{prefix}:Tesseract:IdleUnloadSeconds"] = Text(snapshot.Tesseract.IdleUnloadSeconds),
        [$"{prefix}:Tesseract:MemoryHeadroomBytes"] = Text(snapshot.Tesseract.MemoryHeadroomBytes),
        [$"{prefix}:Tesseract:ExtractionTimeoutSeconds"] =
            Text(snapshot.Tesseract.ExtractionTimeoutSeconds),
        [$"{prefix}:PeriodicCapture:Enabled"] = snapshot.PeriodicCapture.Enabled.ToString(),
        [$"{prefix}:PeriodicCapture:IntervalSeconds"] =
            Text(snapshot.PeriodicCapture.IntervalSeconds),
        [$"{prefix}:PeriodicCapture:RequireForegroundMatch"] =
            snapshot.PeriodicCapture.RequireForegroundMatch.ToString(),
        [$"{prefix}:ShadowReasoning:Enabled"] = snapshot.ShadowReasoning.Enabled.ToString(),
        [$"{prefix}:ShadowReasoning:SkillId"] = snapshot.ShadowReasoning.SkillId,
        [$"{prefix}:CloudFrameUpload:Enabled"] = snapshot.CloudFrameUpload.Enabled.ToString(),
        [$"{prefix}:CloudFrameUpload:SamplingInterval"] =
            Text(snapshot.CloudFrameUpload.SamplingInterval),
    };

    private static string Text<T>(T value) where T : IFormattable =>
        value.ToString(null, CultureInfo.InvariantCulture);
}
