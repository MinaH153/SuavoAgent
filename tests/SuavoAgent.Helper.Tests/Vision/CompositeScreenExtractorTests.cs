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
        // UIA-only is valid only when OCR was deliberately not required.
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
    public async Task Extract_RequiredOcrNull_FailsClosedInsteadOfFalseUiaSuccess()
    {
        var text = new FakeText { Output = null };
        var uia = new FakeUia
        {
            Output = new[] { UiaElement("Button", "Save", 0, 0, 10, 10) },
        };

        var result = await new CompositeScreenExtractor(
            text,
            uia,
            requireTextSuccess: true).ExtractAsync(Screen, default);

        Assert.Null(result);
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
        // Deterministic concurrency proof — no wall-clock (the old Stopwatch
        // "<180ms" assertion flaked ~50% under CI-runner CPU load).
        //
        // Each fake signals "I have started", then BLOCKS until it observes the
        // other fake's start signal before it returns. This rendezvous can only
        // be satisfied if the SUT launches both tasks before awaiting either:
        //   * concurrent  -> both start, both observe the sibling, both return.
        //   * sequential  -> task A starts and waits forever for B (which the SUT
        //                    hasn't launched yet) -> the test hangs and the
        //                    timeout guard below fails it FAST as a regression.
        var textStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uiaStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var text = new FakeText
        {
            Output = Frame(Array.Empty<TextRegion>(), Array.Empty<VisualElement>()),
            OnStarted = textStarted,
            AwaitBeforeReturn = uiaStarted.Task,
        };
        var uia = new FakeUia
        {
            Output = Array.Empty<VisualElement>(),
            OnStarted = uiaStarted,
            AwaitBeforeReturn = textStarted.Task,
        };

        var extract = new CompositeScreenExtractor(text, uia).ExtractAsync(Screen, default);

        // Generous guard: a regression to sequential execution fails fast here
        // instead of hanging the whole test run forever.
        var completed = await Task.WhenAny(extract, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(ReferenceEquals(completed, extract),
            "Composite did not complete within 5s — extractors were not run concurrently (each blocks on the other's start signal).");

        // Surface any exception from the extract task and assert the merge ran.
        var result = await extract;
        Assert.NotNull(result);
        Assert.Empty(result!.TextRegions);
        Assert.Empty(result.Elements);
        Assert.StartsWith("composite-", result.ExtractorId);

        // Both extractors actually started (rendezvous was satisfied, not skipped).
        Assert.True(textStarted.Task.IsCompletedSuccessfully);
        Assert.True(uiaStarted.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Extract_UsesMaxUiaElementsParameter()
    {
        var text = new FakeText { Output = Frame(Array.Empty<TextRegion>(), Array.Empty<VisualElement>()) };
        var uia = new FakeUia();

        await new CompositeScreenExtractor(text, uia, maxUiaElements: 42).ExtractAsync(Screen, default);

        Assert.Equal(42, uia.RequestedMax);
    }

    [Fact]
    public async Task Extract_PropagatesHwnd_ToUia()
    {
        // Codex C-2: UIA must bind to the same hwnd the capture recorded.
        var bound = new ScreenBytes(new byte[] { 1 }, 100, 100, DateTimeOffset.UtcNow, Hwnd: 0xDEADBEEF);
        var text = new FakeText { Output = Frame(Array.Empty<TextRegion>(), Array.Empty<VisualElement>()) };
        var uia = new FakeUia();

        await new CompositeScreenExtractor(text, uia).ExtractAsync(bound, default);

        Assert.Equal(0xDEADBEEF, uia.ReceivedHwnd);
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

        // Concurrency-rendezvous hooks (used by Extract_RunsBothInParallel).
        public TaskCompletionSource? OnStarted { get; set; }
        public Task? AwaitBeforeReturn { get; set; }

        public string ExtractorId => "fake-text";
        public bool IsReady => true;

        public async Task<ScreenFrame?> ExtractAsync(ScreenBytes screen, CancellationToken ct)
        {
            OnStarted?.TrySetResult();
            if (AwaitBeforeReturn != null) await AwaitBeforeReturn.WaitAsync(ct);
            if (DelayMs > 0) await Task.Delay(DelayMs, ct);
            return Output;
        }
    }

    private sealed class FakeUia : IUiaElementExtractor
    {
        public IReadOnlyList<VisualElement> Output { get; set; } = Array.Empty<VisualElement>();
        public int DelayMs { get; set; }
        public int RequestedMax { get; private set; }
        public long ReceivedHwnd { get; private set; }

        // Concurrency-rendezvous hooks (used by Extract_RunsBothInParallel).
        public TaskCompletionSource? OnStarted { get; set; }
        public Task? AwaitBeforeReturn { get; set; }

        public async Task<IReadOnlyList<VisualElement>> ExtractAsync(
            ScreenBytes screen, int maxElements, CancellationToken ct)
        {
            RequestedMax = maxElements;
            ReceivedHwnd = screen.Hwnd;
            OnStarted?.TrySetResult();
            if (AwaitBeforeReturn != null) await AwaitBeforeReturn.WaitAsync(ct);
            if (DelayMs > 0) await Task.Delay(DelayMs, ct);
            return Output;
        }
    }
}
