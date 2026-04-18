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
/// Raw extractor classes (NullScreenExtractor, TesseractScreenExtractor,
/// future PhiVisionExtractor) are internal to this assembly.
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
    /// Picks the best available extractor given the config:
    ///   - Tesseract when Tesseract.Enabled=true AND tessdata path is valid
    ///   - Null otherwise
    ///
    /// Either way, the returned instance is wrapped in PhiScrubbingExtractor.
    /// </summary>
    public static IScreenExtractor Create(IOptions<AgentOptions> options, ILogger logger)
    {
        var tess = options.Value.Vision.Tesseract;

        if (tess.Enabled && TesseractIsReachable(tess, logger))
        {
            logger.Information(
                "ScrubbedExtractorFactory: selecting Tesseract (language={Lang})",
                tess.Language);
            return new PhiScrubbingExtractor(new TesseractScreenExtractor(options, logger));
        }

        logger.Debug("ScrubbedExtractorFactory: selecting Null (Tesseract disabled or unreachable)");
        return new PhiScrubbingExtractor(new NullScreenExtractor());
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
