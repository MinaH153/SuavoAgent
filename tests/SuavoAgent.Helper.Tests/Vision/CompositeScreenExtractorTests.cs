using SuavoAgent.Contracts.Vision;
using SuavoAgent.Helper.Vision;
using Xunit;

namespace SuavoAgent.Helper.Tests.Vision;

public class CompositeScreenExtractorTests
{
    private static readonly ScreenBytes Screen =
        new(new byte[] { 1, 2, 3 }, 640, 480, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Extract_MergesTextAndElements()
    {
        var text = new FakeText
        {
            Output = Frame(
                textRegions: new[] { TextRegion("Save", 10, 20, 40, 15, 0.9) },
                elements: Array.Empty<VisualElement>()),
        };
        var uia = new FakeUia
        {
            Output = new[] { UiaElement("Button", "Save", 10, 20, 40, 15) },
        };

        var result = await new CompositeScreenExtractor(text, uia).ExtractAsync(Screen, default);

        Assert.NotNull(result);
        Assert.Single(result.TextRegions);
        Assert.Equal("Save", result.TextRegions[0].Text);
        Assert.Single(result.Elements);
        Assert.Equal("Button", result.Elements[0].Role);
        Assert.StartsWith("composite-", result.ExtractorId);
    }

    [Fact]
    public async Task Extract_TextNull_StillReturnsUiaFrame()
    {
        // Tesseract failure shouldn't lose deterministic UIA data.
        var text = new FakeText { Output = null };
        var uia = new FakeUia
        {
            Output = new[] { UiaElement("Button", "Save", 0, 0, 10, 10) },
        };

        var result = await new CompositeScreenExtractor(text, uia).ExtractAsync(Screen, default);

        Assert.NotNull(result);
        Assert.Empty(result.TextRegions);
        Assert.Single(result.Elements);
    }

    [Fact]
    public async Task Extract_UiaEmpty_ReturnsTextOnlyFrame()
    {
        var text = new FakeText
        {
            Output = Frame(textRegions: new[] { TextRegion("Hello", 0, 0, 50, 15, 0.95) },
                elements: Array.Empty<VisualElement>()),
        };
        var uia = new FakeUia { Output = Array.Empty<VisualElement>() };

        var result = await new CompositeScreenExtractor(text, uia).ExtractAsync(Screen, default);

        Assert.NotNull(result);
        Assert.Single(result.TextRegions);
        Assert.Empty(result.Elements);
    }

    [Fact]
    public async Task Extract_RunsBothInParallel()
    {
        // Both extractors simulate 100 ms latency — composite should finish in
        // ~100 ms, not 200 ms.
        var text = new FakeText
        {
            Output = Frame(Array.Empty<TextRegion>(), Array.Empty<VisualElement>()),
            DelayMs = 100,
        };
        var uia = new FakeUia
        {
            Output = Array.Empty<VisualElement>(),
            DelayMs = 100,
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await new CompositeScreenExtractor(text, uia).ExtractAsync(Screen, default);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 180,
            $"Composite took {sw.ElapsedMilliseconds}ms; expected <180ms under parallel execution");
    }

    [Fact]
    public async Task Extract_UsesMaxUiaElementsParameter()
    {
        var text = new FakeText { Output = Frame(Array.Empty<TextRegion>(), Array.Empty<VisualElement>()) };
        var uia = new FakeUia();

        await new CompositeScreenExtractor(text, uia, maxUiaElements: 42).ExtractAsync(Screen, default);

        Assert.Equal(42, uia.RequestedMax);
    }

    // --- helpers -------------------------------------------------------------

    private static TextRegion TextRegion(string text, int x, int y, int w, int h, double conf) =>
        new()
        {
            Text = text,
            Bounds = new Rect(x, y, w, h),
            Confidence = conf,
        };

    private static VisualElement UiaElement(string role, string name, int x, int y, int w, int h) =>
        new()
        {
            Role = role,
            Name = name,
            Bounds = new Rect(x, y, w, h),
            Confidence = 1.0,
        };

    private static ScreenFrame Frame(
        IEnumerable<TextRegion> textRegions,
        IEnumerable<VisualElement> elements,
        string extractorId = "test") =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            CapturedAt = DateTimeOffset.UtcNow,
            Width = 640,
            Height = 480,
            TextRegions = textRegions.ToList(),
            Elements = elements.ToList(),
            ExtractorId = extractorId,
            ExtractionLatencyMs = 5,
        };

    private sealed class FakeText : IScreenExtractor
    {
        public ScreenFrame? Output { get; set; }
        public int DelayMs { get; set; }
        public string ExtractorId => "fake-text";
        public bool IsReady => true;

        public async Task<ScreenFrame?> ExtractAsync(ScreenBytes screen, CancellationToken ct)
        {
            if (DelayMs > 0) await Task.Delay(DelayMs, ct);
            return Output;
        }
    }

    private sealed class FakeUia : IUiaElementExtractor
    {
        public IReadOnlyList<VisualElement> Output { get; set; } = Array.Empty<VisualElement>();
        public int DelayMs { get; set; }
        public int RequestedMax { get; private set; }

        public async Task<IReadOnlyList<VisualElement>> ExtractAsync(int maxElements, CancellationToken ct)
        {
            RequestedMax = maxElements;
            if (DelayMs > 0) await Task.Delay(DelayMs, ct);
            return Output;
        }
    }
}
