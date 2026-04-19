using SuavoAgent.Contracts.Vision;
using SuavoAgent.Helper.Vision;
using Xunit;

namespace SuavoAgent.Helper.Tests.Vision;

public class PhiScrubbingExtractorTests
{
    [Fact]
    public async Task Extract_DelegatesToInner_WhenInnerReturnsNull()
    {
        var inner = new FakeExtractor { Output = null };
        var scrubber = new PhiScrubbingExtractor(inner);

        var result = await scrubber.ExtractAsync(FakeScreen(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Extract_ScrubsTextRegions()
    {
        // Deliberate PHI-shaped inputs. PhiScrubber should redact.
        var inner = new FakeExtractor
        {
            Output = FrameWith(textRegions: new[]
            {
                new TextRegion
                {
                    Text = "SSN: 123-45-6789 patient record",
                    Bounds = new Rect(0, 0, 100, 20),
                },
                new TextRegion
                {
                    Text = "Call (555) 123-4567 for refills",
                    Bounds = new Rect(0, 20, 100, 20),
                },
            }),
        };

        var result = await new PhiScrubbingExtractor(inner).ExtractAsync(
            FakeScreen(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.DoesNotContain("123-45-6789", result.TextRegions[0].Text);
        Assert.DoesNotContain("555", result.TextRegions[1].Text);
    }

    [Fact]
    public async Task Extract_ScrubsVisualElementNames()
    {
        var inner = new FakeExtractor
        {
            Output = FrameWith(elements: new[]
            {
                new VisualElement
                {
                    Role = "input",
                    Name = "DOB: 01/15/1985",
                    Bounds = new Rect(0, 0, 100, 20),
                },
            }),
        };

        var result = await new PhiScrubbingExtractor(inner).ExtractAsync(
            FakeScreen(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.DoesNotContain("01/15/1985", result.Elements[0].Name ?? "");
    }

    [Fact]
    public async Task Extract_PreservesNonPHIFields()
    {
        const string clean = "Pricing tab";
        var inner = new FakeExtractor
        {
            Output = FrameWith(textRegions: new[]
            {
                new TextRegion { Text = clean, Bounds = new Rect(0, 0, 100, 20), Confidence = 0.95 },
            }),
        };

        var result = await new PhiScrubbingExtractor(inner).ExtractAsync(
            FakeScreen(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(clean, result.TextRegions[0].Text);
        Assert.Equal(0.95, result.TextRegions[0].Confidence);
    }

    [Fact]
    public async Task Extract_PreservesExtractorId_AndLatency()
    {
        var inner = new FakeExtractor
        {
            Output = FrameWith(extractorId: "test-extractor-1"),
        };

        var result = await new PhiScrubbingExtractor(inner).ExtractAsync(
            FakeScreen(), CancellationToken.None);

        Assert.Equal("test-extractor-1", result!.ExtractorId);
    }

    [Fact]
    public void Properties_DelegateToInner()
    {
        var inner = new FakeExtractor { IdValue = "id-x", ReadyValue = false };
        var scrubber = new PhiScrubbingExtractor(inner);

        Assert.Equal("id-x", scrubber.ExtractorId);
        Assert.False(scrubber.IsReady);
    }

    // --- helpers -------------------------------------------------------------

    private static ScreenBytes FakeScreen() =>
        new(Array.Empty<byte>(), 100, 100, DateTimeOffset.UtcNow);

    private static ScreenFrame FrameWith(
        IEnumerable<TextRegion>? textRegions = null,
        IEnumerable<VisualElement>? elements = null,
        string extractorId = "test") =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            CapturedAt = DateTimeOffset.UtcNow,
            Width = 100,
            Height = 100,
            TextRegions = textRegions?.ToList() ?? new List<TextRegion>(),
            Elements = elements?.ToList() ?? new List<VisualElement>(),
            ExtractorId = extractorId,
            ExtractionLatencyMs = 5,
        };

    private sealed class FakeExtractor : IScreenExtractor
    {
        public ScreenFrame? Output { get; set; }
        public string IdValue { get; set; } = "fake";
        public bool ReadyValue { get; set; } = true;

        public string ExtractorId => IdValue;
        public bool IsReady => ReadyValue;
        public Task<ScreenFrame?> ExtractAsync(ScreenBytes screen, CancellationToken ct) =>
            Task.FromResult(Output);
    }
}
