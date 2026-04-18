using Microsoft.Extensions.Options;
using Serilog;
using SuavoAgent.Core.Config;

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
///   Tesseract missing    +  Windows UIA  → CompositeScreenExtractor (Null+UIA)
///   Tesseract missing    +  no UIA       → NullScreenExtractor
///
/// Every case is wrapped in PhiScrubbingExtractor.
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
    public static IScreenExtractor Create(IOptions<AgentOptions> options, ILogger logger)
    {
        var tess = options.Value.Vision.Tesseract;

        // --- Text extraction tier (Tesseract or Null) ---------------------------
        IScreenExtractor textInner;
        if (tess.Enabled && TesseractIsReachable(tess, logger))
        {
            textInner = new TesseractScreenExtractor(options, logger);
            logger.Information("ScrubbedExtractorFactory: Tesseract selected ({Lang})", tess.Language);
        }
        else
        {
            textInner = new NullScreenExtractor();
        }

        // --- UIA element tier (only on Windows; Helper runs in user session) ----
        IUiaElementExtractor uiaInner = OperatingSystem.IsWindows()
            ? new FlaUiElementExtractor(logger)
            : new NullUiaElementExtractor();

        IScreenExtractor combined = OperatingSystem.IsWindows()
            ? new CompositeScreenExtractor(textInner, uiaInner)
            : textInner;

        logger.Information(
            "ScrubbedExtractorFactory: final extractor = {Id}", combined.ExtractorId);

        return new PhiScrubbingExtractor(combined);
    }

    private static bool TesseractIsReachable(TesseractOptions tess, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(tess.TessdataPath) || !Directory.Exists(tess.TessdataPath))
        {
            logger.Warning(
                "Tesseract enabled but TessdataPath {Path} doesn't exist — falling back to Null",
                tess.TessdataPath);
            return false;
        }

        var trained = Path.Combine(tess.TessdataPath, $"{tess.Language}.traineddata");
        if (!File.Exists(trained))
        {
            logger.Warning(
                "Tesseract enabled but {Path} missing — falling back to Null",
                trained);
            return false;
        }

        return true;
    }
}
