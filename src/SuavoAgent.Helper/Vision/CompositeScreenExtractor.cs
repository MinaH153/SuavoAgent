using System.Diagnostics;
using SuavoAgent.Contracts.Vision;

namespace SuavoAgent.Helper.Vision;

/// <summary>
/// Runs a text extractor (Tesseract or Null) and a UIA element extractor in
/// parallel, merges their outputs into one ScreenFrame. The resulting frame
/// carries both pixel-rendered text regions AND deterministic UIA element
/// metadata, giving downstream reasoning the richest possible view.
///
/// Lifecycle: each inner extractor manages its own state. This composite is
/// a pure orchestrator.
/// </summary>
internal sealed class CompositeScreenExtractor : IScreenExtractor
{
    private readonly IScreenExtractor _textInner;
    private readonly IUiaElementExtractor _uiaInner;
    private readonly int _maxUiaElements;

    public CompositeScreenExtractor(
        IScreenExtractor textInner,
        IUiaElementExtractor uiaInner,
        int maxUiaElements = 128)
    {
        _textInner = textInner;
        _uiaInner = uiaInner;
        _maxUiaElements = maxUiaElements;
    }

    public string ExtractorId => $"composite-{_textInner.ExtractorId}+uia";

    public bool IsReady => _textInner.IsReady;

    public async Task<ScreenFrame?> ExtractAsync(ScreenBytes screen, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Parallel: text extraction is CPU-bound on Tesseract, UIA query is
        // blocking on the UI thread. Running them concurrently reduces total
        // latency to roughly max(text, uia) instead of their sum.
        var textTask = _textInner.ExtractAsync(screen, ct);
        var uiaTask = _uiaInner.ExtractAsync(_maxUiaElements, ct);

        ScreenFrame? textFrame;
        IReadOnlyList<VisualElement> elements;

        try
        {
            await Task.WhenAll(textTask, uiaTask);
            textFrame = await textTask;
            elements = await uiaTask;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }

        sw.Stop();

        // If text extractor failed, still emit a frame with UIA elements only —
        // UIA is deterministic, we don't need probabilistic text to have useful
        // structure.
        if (textFrame == null)
        {
            return new ScreenFrame
            {
                Id = Guid.NewGuid().ToString("N"),
                CapturedAt = screen.CapturedAt,
                Width = screen.Width,
                Height = screen.Height,
                TextRegions = Array.Empty<TextRegion>(),
                Elements = elements,
                ExtractorId = ExtractorId,
                ExtractionLatencyMs = sw.ElapsedMilliseconds,
            };
        }

        return textFrame with
        {
            Elements = elements,
            ExtractorId = ExtractorId,
            ExtractionLatencyMs = sw.ElapsedMilliseconds,
        };
    }
}
