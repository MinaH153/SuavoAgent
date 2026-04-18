using Serilog;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Helper.Vision;
using Xunit;

namespace SuavoAgent.Helper.Tests.Vision;

public class ScreenCaptureControllerTests
{
    private static readonly ILogger Log = new LoggerConfiguration().CreateLogger();

    [Fact]
    public async Task Capture_UnavailableCapture_ReturnsNull()
    {
        var controller = new ScreenCaptureController(
            new FakeCapture { Available = false },
            new FakeStore(),
            new FakeExtractor(),
            Log);

        var result = await controller.CaptureAndExtractAsync(CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task Capture_NullScreen_ReturnsNull()
    {
        var controller = new ScreenCaptureController(
            new FakeCapture { Available = true, Result = null },
            new FakeStore(),
            new FakeExtractor(),
            Log);

        var result = await controller.CaptureAndExtractAsync(CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task Capture_FullPipeline_ReturnsStorageIdAndFrame()
    {
        var store = new FakeStore { StoreResult = "stored-123" };
        var controller = new ScreenCaptureController(
            new FakeCapture
            {
                Available = true,
                Result = new ScreenBytes(new byte[] { 1, 2, 3 }, 100, 100, DateTimeOffset.UtcNow),
            },
            store,
            new FakeExtractor { Ready = true },
            Log);

        var result = await controller.CaptureAndExtractAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("stored-123", result.StorageId);
        Assert.NotNull(result.Frame);
    }

    [Fact]
    public async Task Capture_StoreFails_StillReturnsFrameWithNullId()
    {
        // Losing storage shouldn't block reasoning — the frame itself is
        // still safe to hand to Tier-2. Storage is retention, not auth.
        var controller = new ScreenCaptureController(
            new FakeCapture
            {
                Available = true,
                Result = new ScreenBytes(new byte[] { 1 }, 100, 100, DateTimeOffset.UtcNow),
            },
            new FakeStore { StoreResult = null },
            new FakeExtractor { Ready = true },
            Log);

        var result = await controller.CaptureAndExtractAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Null(result.StorageId);
        Assert.NotNull(result.Frame);
    }

    [Fact]
    public async Task Capture_ExtractorReturnsNull_ReturnsNull()
    {
        var controller = new ScreenCaptureController(
            new FakeCapture
            {
                Available = true,
                Result = new ScreenBytes(new byte[] { 1 }, 100, 100, DateTimeOffset.UtcNow),
            },
            new FakeStore { StoreResult = "x" },
            new FakeExtractor { Result = null },
            Log);

        var result = await controller.CaptureAndExtractAsync(CancellationToken.None);
        Assert.Null(result);
    }

    // --- fakes ---------------------------------------------------------------

    private sealed class FakeCapture : IScreenCapture
    {
        public bool Available { get; set; }
        public ScreenBytes? Result { get; set; }
        public bool IsAvailable => Available;
        public Task<ScreenBytes?> CapturePrimaryAsync(CancellationToken ct) =>
            Task.FromResult(Result);
    }

    private sealed class FakeStore : IScreenStore
    {
        public string? StoreResult { get; set; } = "fake-id";
        public Task<string?> StoreAsync(ScreenBytes screen, CancellationToken ct) =>
            Task.FromResult(StoreResult);
        public Task<ScreenBytes?> LoadAsync(string id, CancellationToken ct) =>
            Task.FromResult<ScreenBytes?>(null);
        public Task<int> PurgeExpiredAsync(CancellationToken ct) =>
            Task.FromResult(0);
    }

    private sealed class FakeExtractor : IScreenExtractor
    {
        public bool Ready { get; set; } = true;
        public ScreenFrame? Result { get; set; } = new()
        {
            Id = "f1",
            CapturedAt = DateTimeOffset.UtcNow,
            Width = 100,
            Height = 100,
            ExtractorId = "fake",
        };
        public string ExtractorId => "fake";
        public bool IsReady => Ready;
        public Task<ScreenFrame?> ExtractAsync(ScreenBytes screen, CancellationToken ct) =>
            Task.FromResult(Result);
    }
}
