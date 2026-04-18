using System.Diagnostics;
using SuavoAgent.Contracts.Vision;

namespace SuavoAgent.Helper.Vision;

/// <summary>
/// Placeholder extractor used before Week 3b (Tesseract OCR) / 3c (VLM) ship.
/// Returns a ScreenFrame with empty TextRegions and Elements so the pipeline
/// end-to-end is exercisable without a real extractor. IsReady is always
/// true — the "extractor" is literally a no-op.
/// </summary>
public sealed class NullScreenExtractor : IScreenExtractor
{
    public string ExtractorId => "null";
    public bool IsReady => true;

    public Task<ScreenFrame?> ExtractAsync(ScreenBytes screen, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var frame = new ScreenFrame
        {
            Id = Guid.NewGuid().ToString("N"),
            CapturedAt = screen.CapturedAt,
            Width = screen.Width,
            Height = screen.Height,
            TextRegions = Array.Empty<TextRegion>(),
            Elements = Array.Empty<VisualElement>(),
            ExtractorId = ExtractorId,
            ExtractionLatencyMs = sw.ElapsedMilliseconds,
        };
        return Task.FromResult<ScreenFrame?>(frame);
    }
}
