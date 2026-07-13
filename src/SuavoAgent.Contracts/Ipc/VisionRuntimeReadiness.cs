using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuavoAgent.Contracts.Ipc;

/// <summary>
/// PHI-free, authenticated Helper runtime truth for the machine-vision pipeline.
/// It travels only inside the existing command-pipe ping response; Core then
/// projects it into local health and the signed heartbeat sent to the cockpit.
/// No path, hash, OCR text, screenshot identifier, window title, or exception
/// message is permitted in this contract.
/// </summary>
public sealed record VisionRuntimeReadiness(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("visionEnabled")] bool VisionEnabled,
    [property: JsonPropertyName("ocrConfigured")] bool OcrConfigured,
    [property: JsonPropertyName("ready")] bool Ready,
    [property: JsonPropertyName("ocrReady")] bool OcrReady,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("configurationGeneration")] long ConfigurationGeneration,
    [property: JsonPropertyName("checkedAtUtc")] DateTimeOffset CheckedAtUtc)
{
    public const int CurrentContractVersion = 1;

    /// <summary>
    /// Default-disabled is healthy policy, not an incident. Every enabled but
    /// unready state requires operator/maintenance attention.
    /// </summary>
    [JsonIgnore]
    public bool RequiresAttention => VisionEnabled && !Ready;

    public bool IsValid()
    {
        if (ContractVersion != CurrentContractVersion ||
            ConfigurationGeneration < 0 ||
            !VisionRuntimeCodes.All.Contains(Code))
            return false;

        if (!VisionEnabled)
        {
            return !OcrConfigured && !Ready && !OcrReady &&
                   Code == VisionRuntimeCodes.VisionDisabled;
        }

        if (OcrReady && (!OcrConfigured || !Ready))
            return false;

        return Code switch
        {
            VisionRuntimeCodes.VisionStarting => !Ready && !OcrReady,
            VisionRuntimeCodes.VisionReadyUiaOnly =>
                Ready && !OcrConfigured && !OcrReady,
            VisionRuntimeCodes.VisionReadyOcr =>
                Ready && OcrConfigured && OcrReady,
            VisionRuntimeCodes.VisionPlatformUnsupported => !Ready && !OcrReady,
            VisionRuntimeCodes.OcrCohortVerificationFailed or
            VisionRuntimeCodes.OcrRuntimeInitializationFailed or
            VisionRuntimeCodes.OcrRuntimeTimeout or
            VisionRuntimeCodes.OcrMemoryPressure or
            VisionRuntimeCodes.OcrExtractionFailed =>
                OcrConfigured && !Ready && !OcrReady,
            VisionRuntimeCodes.VisionPipelineInitializationFailed =>
                !Ready && !OcrReady,
            _ => false,
        };
    }

    public static VisionRuntimeReadiness? TryParse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        try
        {
            var expected = new HashSet<string>(StringComparer.Ordinal)
            {
                "contractVersion",
                "visionEnabled",
                "ocrConfigured",
                "ready",
                "ocrReady",
                "code",
                "configurationGeneration",
                "checkedAtUtc",
            };
            var actual = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!actual.Add(property.Name))
                    return null;
            }
            if (!actual.SetEquals(expected))
                return null;

            var parsed = JsonSerializer.Deserialize<VisionRuntimeReadiness>(
                element.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = false });
            return parsed?.IsValid() == true ? parsed : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Closed vocabulary for vision runtime health. These codes and their fixed
/// operator messages are the only error material allowed onto IPC/heartbeat.
/// </summary>
public static class VisionRuntimeCodes
{
    public const string VisionDisabled = "vision_disabled";
    public const string VisionStarting = "vision_starting";
    public const string VisionReadyUiaOnly = "vision_ready_uia_only";
    public const string VisionReadyOcr = "vision_ready_ocr";
    public const string VisionPlatformUnsupported = "vision_platform_unsupported";
    public const string OcrCohortVerificationFailed = "ocr_cohort_verification_failed";
    public const string OcrRuntimeInitializationFailed = "ocr_runtime_initialization_failed";
    public const string OcrRuntimeTimeout = "ocr_runtime_timeout";
    public const string OcrMemoryPressure = "ocr_memory_pressure";
    public const string OcrExtractionFailed = "ocr_extraction_failed";
    public const string VisionPipelineInitializationFailed = "vision_pipeline_initialization_failed";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        VisionDisabled,
        VisionStarting,
        VisionReadyUiaOnly,
        VisionReadyOcr,
        VisionPlatformUnsupported,
        OcrCohortVerificationFailed,
        OcrRuntimeInitializationFailed,
        OcrRuntimeTimeout,
        OcrMemoryPressure,
        OcrExtractionFailed,
        VisionPipelineInitializationFailed,
    };

    public static string OperatorMessage(string code) => code switch
    {
        VisionDisabled => "Vision is disabled by policy.",
        VisionStarting => "Vision is starting and has not earned a ready verdict yet.",
        VisionReadyUiaOnly => "Vision is ready with UI Automation; OCR is not configured.",
        VisionReadyOcr => "Vision and the approved OCR runtime are ready.",
        VisionPlatformUnsupported => "Vision is enabled on an unsupported platform.",
        OcrCohortVerificationFailed => "The approved OCR installation failed integrity verification; run native repair.",
        OcrRuntimeInitializationFailed => "The approved OCR runtime could not initialize; run native repair.",
        OcrRuntimeTimeout => "The OCR runtime exceeded its startup or execution budget and the Helper was recycled.",
        OcrMemoryPressure => "OCR is paused because the workstation does not have the required memory headroom.",
        OcrExtractionFailed => "OCR failed during local extraction; no screen result was accepted.",
        VisionPipelineInitializationFailed => "The local vision pipeline could not initialize; run diagnostics or repair.",
        _ => "Vision readiness is unknown.",
    };
}
