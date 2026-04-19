using System.Diagnostics;
using SuavoAgent.Contracts.Vision;

namespace SuavoAgent.Helper.Vision;

/// <summary>
/// Runs a text extractor (Tesseract or Null) and a UIA element extractor in
/// parallel, merges their outputs into one ScreenFrame. The resulting frame
/// carries both pixel-rendered text regions AND deterministic UIA element
/// metadata, giving downstream reasoning the richest possible view.
///
/// Cancellation semantics (Codex M-3): if one inner extractor throws, the
/// surviving task is immediately cancelled via a linked CTS so we don't leak
/// CPU on an already-failed compose call.
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

        // Linked CTS so one inner failure cancels the sibling (Codex M-3).
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = linked.Token;

        var textTask = _textInner.ExtractAsync(screen, token);
        var uiaTask = _uiaInner.ExtractAsync(screen, _maxUiaElements, token);

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
            linked.Cancel();
            throw;
        }
        catch
        {
            // Cancel the surviving task so it doesn't waste CPU on a
            // composite we're about to discard.
            linked.Cancel();
            // Recover whatever completed — the composite contract is fail-soft:
            // one branch's failure shouldn't block the other branch's output.
            textFrame = textTask.IsCompletedSuccessfully ? await textTask : null;
            elements = uiaTask.IsCompletedSuccessfully
                ? await uiaTask
                : Array.Empty<VisualElement>();
        }

        sw.Stop();

        // If text extractor failed, still emit a frame with UIA elements only.
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
