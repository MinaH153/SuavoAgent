using SuavoAgent.Contracts.Vision;

namespace SuavoAgent.Helper.Vision;

/// <summary>
/// Transforms raw screen pixels into structured ScreenFrame data that can
/// safely cross the HIPAA trust boundary into Tier-2 reasoning.
///
/// Every implementation MUST pass extracted text through PhiScrubber BEFORE
/// populating the returned ScreenFrame. That makes the extractor the last
/// PHI scrub boundary — anything downstream (prompt builder, cloud) trusts
/// that ScreenFrame.TextRegions is already safe.
///
/// Returns null on any failure (not configured, model error, timeout). Never
/// throws so callers escalate cleanly.
/// </summary>
public interface IScreenExtractor
{
    /// <summary>
    /// Identifier for audit — e.g. "null", "tesseract-5", "phi-3.5-vision".
    /// </summary>
    string ExtractorId { get; }

    /// <summary>True if the extractor is ready to serve.</summary>
    bool IsReady { get; }

    /// <summary>
    /// Runs extraction + scrubbing. Input screen stays in-memory briefly and
    /// must be dropped by the caller after this call returns.
    /// </summary>
    Task<ScreenFrame?> ExtractAsync(ScreenBytes screen, CancellationToken ct);
}
