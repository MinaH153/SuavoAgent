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
internal sealed class CompositeScreenExtractor : IPricingScreenExtractor
{
    private readonly IScreenExtractor _textInner;
    private readonly IUiaElementExtractor _uiaInner;
    private readonly int _maxUiaElements;
    private readonly bool _requireTextSuccess;

    public CompositeScreenExtractor(
        IScreenExtractor textInner,
        IUiaElementExtractor uiaInner,
        int maxUiaElements = 128,
        bool requireTextSuccess = false)
    {
        _textInner = textInner;
        _uiaInner = uiaInner;
        _maxUiaElements = maxUiaElements;
        _requireTextSuccess = requireTextSuccess;
    }

    public string ExtractorId => $"composite-{_textInner.ExtractorId}+uia";

    public bool IsReady => _textInner.IsReady;

    public async Task<ScreenFrame?> ExtractAsync(ScreenBytes screen, CancellationToken ct)
        => await ExtractCoreAsync(screen, pricing: false, ct).ConfigureAwait(false);

    public async Task<ScreenFrame?> ExtractPricingAsync(
        ScreenBytes screen,
        CancellationToken ct)
        => await ExtractCoreAsync(screen, pricing: true, ct).ConfigureAwait(false);

    private async Task<ScreenFrame?> ExtractCoreAsync(
        ScreenBytes screen,
        bool pricing,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Linked CTS so one inner failure cancels the sibling (Codex M-3).
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = linked.Token;

        var textTask = pricing && _textInner is IPricingScreenExtractor pricingExtractor
            ? pricingExtractor.ExtractPricingAsync(screen, token)
            : _textInner.ExtractAsync(screen, token);
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

        // When OCR is explicitly configured, its failure is load-bearing: an
        // apparently successful UIA-only frame would lie about runtime vision
        // readiness. Fail the whole capture so Core/dashboard sees the static
        // OCR error recorded by TesseractScreenExtractor. UIA-only operation
        // remains valid when OCR was deliberately left disabled.
        if (textFrame == null)
        {
            if (_requireTextSuccess)
                return null;
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
