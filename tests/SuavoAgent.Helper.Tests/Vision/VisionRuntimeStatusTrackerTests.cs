using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Core.Vision;
using SuavoAgent.Helper.Vision;
using Xunit;

namespace SuavoAgent.Helper.Tests.Vision;

public sealed class VisionRuntimeStatusTrackerTests
{
    private static readonly DateTimeOffset Now = new(
        2026, 7, 12, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EnabledOcr_StartsUnready_ThenRecordsOnlyClosedCodeFailure()
    {
        var tracker = new VisionRuntimeStatusTracker(
            Loaded(enabled: true, ocr: true, generation: 9),
            () => Now);

        Assert.Equal(VisionRuntimeCodes.VisionStarting, tracker.Snapshot().Code);
        Assert.False(tracker.Snapshot().Ready);

        tracker.RecordFailure(VisionRuntimeCodes.OcrRuntimeInitializationFailed);
        var failed = tracker.Snapshot();

        Assert.True(failed.RequiresAttention);
        Assert.Equal(VisionRuntimeCodes.OcrRuntimeInitializationFailed, failed.Code);
        Assert.Equal(9, failed.ConfigurationGeneration);
        Assert.Equal(Now, failed.CheckedAtUtc);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tracker.RecordFailure("C:\\patient\\screen.png"));
    }

    [Fact]
    public void OcrRecovery_ReplacesFailureWithEarnedReadyVerdict()
    {
        var tracker = new VisionRuntimeStatusTracker(
            Loaded(enabled: true, ocr: true, generation: 3),
            () => Now);
        tracker.RecordFailure(VisionRuntimeCodes.OcrExtractionFailed);

        tracker.RecordReady(ocrReady: true);
        var ready = tracker.Snapshot();

        Assert.True(ready.Ready);
        Assert.True(ready.OcrReady);
        Assert.False(ready.RequiresAttention);
        Assert.Equal(VisionRuntimeCodes.VisionReadyOcr, ready.Code);
        Assert.True(ready.IsValid());
    }

    private static VisionConfigurationLoadResult Loaded(
        bool enabled,
        bool ocr,
        long generation)
    {
        var defaults = VisionOptionsSnapshot.DisabledDefault();
        var options = defaults with
        {
            Enabled = enabled,
            Tesseract = defaults.Tesseract with { Enabled = ocr },
        };
        var state = new VisionConfigurationState(
            1,
            generation,
            "11111111-1111-4111-8111-111111111111",
            Now,
            options,
            new string('a', 64));
        return new(true, false, "active", options, state);
    }
}
