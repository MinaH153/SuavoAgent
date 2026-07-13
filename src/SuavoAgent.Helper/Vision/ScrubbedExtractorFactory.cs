using Microsoft.Extensions.Options;
using Serilog;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Vision;

namespace SuavoAgent.Helper.Vision;

/// <summary>
/// Only way to obtain an <see cref="IScreenExtractor"/> outside this assembly.
/// Every extractor produced here is wrapped in <see cref="PhiScrubbingExtractor"/>
/// so PHI scrubbing is enforced by construction — callers CANNOT accidentally
/// use an un-scrubbed extractor (Codex suggestion).
///
/// Selection matrix (picks the richest available):
///
///   Tesseract reachable  +  Windows UIA  → CompositeScreenExtractor
///   Tesseract reachable  +  no UIA       → TesseractScreenExtractor
///   Tesseract disabled   +  Windows UIA  → CompositeScreenExtractor (Null+UIA)
///   Tesseract disabled   +  no UIA       → NullScreenExtractor
///
/// Configured OCR NEVER falls back to Null/UIA. Its exact Setup-provisioned
/// cohort and native runtime must verify and warm successfully, otherwise the
/// factory throws a static-code failure and the pipeline is visibly degraded.
/// Every successful case is wrapped in PhiScrubbingExtractor.
/// </summary>
public static class ScrubbedExtractorFactory
{
    /// <summary>
    /// The current default extractor — <see cref="NullScreenExtractor"/>
    /// wrapped in scrub. Kept for call sites that don't have config access.
    /// Prefer <see cref="Create(IOptions{AgentOptions}, ILogger)"/>.
    /// </summary>
    public static IScreenExtractor CreateDefault() =>
        new PhiScrubbingExtractor(new NullScreenExtractor());

    /// <summary>
    /// Picks the richest available extractor given the config and platform.
    /// Always wraps in PhiScrubbingExtractor.
    /// </summary>
    public static IScreenExtractor Create(
        IOptions<AgentOptions> options,
        ILogger logger,
        VisionRuntimeStatusTracker? runtimeStatus = null)
    {
        var tess = options.Value.Vision.Tesseract;

        // --- Text extraction tier (Tesseract or Null) ---------------------------
        IScreenExtractor textInner;
        if (tess.Enabled)
        {
            if (!TesseractNativeCohortPolicy.VerifyInstalled(tess) ||
                !TesseractIsReachable(tess, logger))
            {
                runtimeStatus?.RecordFailure(VisionRuntimeCodes.OcrCohortVerificationFailed);
                throw new VisionRuntimeUnavailableException(
                    VisionRuntimeCodes.OcrCohortVerificationFailed);
            }

            var tesseract = new TesseractScreenExtractor(options, logger, runtimeStatus);
            try
            {
                if (!tesseract.WarmUpAsync(CancellationToken.None)
                        .GetAwaiter().GetResult())
                {
                    throw new VisionRuntimeUnavailableException(
                        tesseract.LastFailureCode ??
                        VisionRuntimeCodes.OcrRuntimeInitializationFailed);
                }
            }
            catch (NativeOcrTimeoutException)
            {
                throw new VisionRuntimeUnavailableException(
                    VisionRuntimeCodes.OcrRuntimeTimeout);
            }
            textInner = tesseract;
            logger.Information("ScrubbedExtractorFactory: Tesseract selected ({Lang})", tess.Language);
        }
        else
        {
            textInner = new NullScreenExtractor();
        }

        // --- UIA element tier (only on Windows; Helper runs in user session) ----
        // FlaUI.UIA2 would throw at construction on non-Windows hosts — guard
        // at the factory so tests on macOS CI never attempt to instantiate it.
        IUiaElementExtractor uiaInner;
        IScreenExtractor combined;
        if (OperatingSystem.IsWindows())
        {
            uiaInner = new FlaUiElementExtractor(logger);
            combined = new CompositeScreenExtractor(
                textInner,
                uiaInner,
                requireTextSuccess: tess.Enabled);
        }
        else
        {
            uiaInner = new NullUiaElementExtractor();
            combined = textInner;
        }

        logger.Information(
            "ScrubbedExtractorFactory: final extractor = {Id}", combined.ExtractorId);

        return new PhiScrubbingExtractor(combined);
    }

    private static bool TesseractIsReachable(TesseractOptions tess, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(tess.TessdataPath) || !Directory.Exists(tess.TessdataPath))
        {
            logger.Warning(
                "Tesseract enabled but its data directory is unavailable");
            return false;
        }

        var trained = Path.Combine(tess.TessdataPath, $"{tess.Language}.traineddata");
        if (!File.Exists(trained))
        {
            logger.Warning(
                "Tesseract enabled but traineddata is unavailable");
            return false;
        }

        return true;
    }
}
