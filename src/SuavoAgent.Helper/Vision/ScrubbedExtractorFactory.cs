namespace SuavoAgent.Helper.Vision;

/// <summary>
/// Only way to obtain an <see cref="IScreenExtractor"/> outside this assembly.
/// Every extractor produced here is wrapped in <see cref="PhiScrubbingExtractor"/>
/// so PHI scrubbing is enforced by construction — callers CANNOT accidentally
/// use an un-scrubbed extractor (Codex suggestion).
///
/// Raw extractor classes (NullScreenExtractor, future TesseractExtractor,
/// future PhiVisionExtractor) are internal to this assembly.
/// </summary>
public static class ScrubbedExtractorFactory
{
    /// <summary>
    /// The current default extractor. Today: Null wrapped in scrub. Week 3b
    /// swaps the inner to Tesseract; Week 3c swaps to Phi-3.5-vision. Callers
    /// get a consistent scrubbed interface regardless.
    /// </summary>
    public static IScreenExtractor CreateDefault() =>
        new PhiScrubbingExtractor(new NullScreenExtractor());
}
