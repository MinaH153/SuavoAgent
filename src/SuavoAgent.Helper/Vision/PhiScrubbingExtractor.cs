using SuavoAgent.Contracts.Vision;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Core.Learning;

namespace SuavoAgent.Helper.Vision;

/// <summary>
/// Decorator that PHI-scrubs every ScreenFrame emitted by an inner extractor.
/// This is the HIPAA boundary of the vision pipeline: every text that leaves
/// the extractor must have passed through PhiScrubber.ScrubText.
///
/// Internal — callers never construct this directly. The public entry point
/// is <see cref="ScrubbedExtractorFactory"/>, which guarantees every returned
/// extractor is wrapped in this decorator (Codex suggestion).
/// </summary>
internal sealed class PhiScrubbingExtractor : IPricingScreenExtractor
{
    private readonly IScreenExtractor _inner;

    internal PhiScrubbingExtractor(IScreenExtractor inner)
    {
        _inner = inner;
    }

    public string ExtractorId => _inner.ExtractorId;
    public bool IsReady => _inner.IsReady;

    public async Task<ScreenFrame?> ExtractAsync(ScreenBytes screen, CancellationToken ct)
    {
        var frame = await _inner.ExtractAsync(screen, ct);
        return Scrub(frame);
    }

    public async Task<ScreenFrame?> ExtractPricingAsync(
        ScreenBytes screen,
        CancellationToken ct)
    {
        var frame = _inner is IPricingScreenExtractor pricing
            ? await pricing.ExtractPricingAsync(screen, ct)
            : await _inner.ExtractAsync(screen, ct);
        return Scrub(frame);
    }

    private static ScreenFrame? Scrub(ScreenFrame? frame)
    {
        if (frame == null) return null;

        var scrubbedRegions = new List<TextRegion>(frame.TextRegions.Count);
        foreach (var region in frame.TextRegions)
        {
            var scrubbed = PhiScrubber.ScrubText(region.Text) ?? "";
            scrubbedRegions.Add(region with { Text = scrubbed });
        }

        var scrubbedElements = new List<VisualElement>(frame.Elements.Count);
        foreach (var el in frame.Elements)
        {
            scrubbedElements.Add(el with
            {
                Name = PhiScrubber.ScrubText(el.Name),
                AutomationId = StructuralIdentifierSanitizer.AllowOrNull(el.AutomationId),
                ClassName = StructuralIdentifierSanitizer.AllowOrNull(el.ClassName),
            });
        }

        return frame with
        {
            TextRegions = scrubbedRegions,
            Elements = scrubbedElements,
        };
    }
}
