using Microsoft.Extensions.Options;
using Serilog;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Vision;
using SuavoAgent.Helper.Vision;
using Tesseract;
using Xunit;

namespace SuavoAgent.Helper.Tests.Vision;

/// <summary>
/// Pins the managed fail-closed boundary around native OCR. These tests never
/// load native code; every failure is proved before a Tesseract engine can
/// become ready or screen material can be returned as successfully extracted.
/// </summary>
public sealed class TesseractScreenExtractorSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-ocr-extractor-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EmptyPng_ReturnsExplicitEmptyFrameWithoutNativeBoundary()
    {
        var boundary = new FakeBoundary();
        await using var extractor = CreateExtractor(
            OptionsFor(tessdataPath: null),
            boundary,
            _ => throw new Xunit.Sdk.XunitException("Engine must not be constructed."));
        var capturedAt = DateTimeOffset.UtcNow;

        var frame = await extractor.ExtractAsync(
            new ScreenBytes(Array.Empty<byte>(), 640, 480, capturedAt),
            CancellationToken.None);

        Assert.NotNull(frame);
        Assert.Equal(640, frame!.Width);
        Assert.Equal(480, frame.Height);
        Assert.Equal(capturedAt, frame.CapturedAt);
        Assert.Equal("tesseract-eng", frame.ExtractorId);
        Assert.Empty(frame.TextRegions);
        Assert.Equal(0, boundary.PrepareCalls);
        Assert.Equal(0, boundary.ConstructorCalls);
        Assert.False(extractor.IsReady);
        Assert.Null(extractor.LastFailureCode);
    }

    [Fact]
    public async Task WarmUp_CohortRejectionIsVisibleAndNeverConstructsEngine()
    {
        var boundary = new FakeBoundary { CohortVerificationResult = false };
        var tracker = RuntimeTracker();
        var engineCalls = 0;
        await using var extractor = CreateExtractor(
            OptionsFor(tessdataPath: null),
            boundary,
            _ =>
            {
                engineCalls++;
                return null!;
            },
            tracker);

        var ready = await extractor.WarmUpAsync(CancellationToken.None, () => { });

        Assert.False(ready);
        Assert.Equal(1, boundary.CohortVerificationCalls);
        Assert.Equal(0, boundary.PrepareCalls);
        Assert.Equal(0, boundary.ConstructorCalls);
        Assert.Equal(0, engineCalls);
        Assert.False(extractor.IsReady);
        Assert.Equal(
            VisionRuntimeCodes.OcrCohortVerificationFailed,
            extractor.LastFailureCode);
        Assert.Equal(
            VisionRuntimeCodes.OcrCohortVerificationFailed,
            tracker.Snapshot().Code);
        Assert.True(tracker.Snapshot().RequiresAttention);
    }

    [Fact]
    public async Task WarmUp_MemoryPressureRejectsBeforeFilesystemAndEngine()
    {
        var boundary = new FakeBoundary();
        var engineCalls = 0;
        await using var extractor = CreateExtractor(
            OptionsFor(tessdataPath: null, memoryHeadroomBytes: 1),
            boundary,
            _ =>
            {
                engineCalls++;
                return null!;
            });

        var ready = await extractor.WarmUpAsync(CancellationToken.None, () => { });

        Assert.False(ready);
        Assert.Equal(1, boundary.CohortVerificationCalls);
        Assert.Equal(0, boundary.PrepareCalls);
        Assert.Equal(0, boundary.ConstructorCalls);
        Assert.Equal(0, engineCalls);
        Assert.Equal(VisionRuntimeCodes.OcrMemoryPressure, extractor.LastFailureCode);
    }

    [Fact]
    public async Task WarmUp_MissingTessdataPathRejectsAfterCohortProof()
    {
        var boundary = new FakeBoundary();
        await using var extractor = CreateExtractor(
            OptionsFor(tessdataPath: "  "),
            boundary,
            _ => null!);

        var ready = await extractor.WarmUpAsync(CancellationToken.None, () => { });

        Assert.False(ready);
        Assert.Equal(1, boundary.CohortVerificationCalls);
        Assert.Equal(0, boundary.PrepareCalls);
        Assert.Equal(0, boundary.ConstructorCalls);
        Assert.Equal(
            VisionRuntimeCodes.OcrCohortVerificationFailed,
            extractor.LastFailureCode);
    }

    [Fact]
    public async Task WarmUp_MissingTessdataDirectoryRejectsBeforeEngine()
    {
        var boundary = new FakeBoundary();
        var missing = Path.Combine(_root, "missing-tessdata");
        await using var extractor = CreateExtractor(
            OptionsFor(missing),
            boundary,
            _ => null!);

        var failStops = 0;
        var ready = await extractor.WarmUpAsync(
            CancellationToken.None,
            () => failStops++);

        Assert.False(ready);
        Assert.Equal(1, boundary.CohortVerificationCalls);
        Assert.Equal(0, boundary.PrepareCalls);
        Assert.Equal(0, boundary.ConstructorCalls);
        Assert.Equal(0, failStops);
        Assert.Equal(
            VisionRuntimeCodes.OcrCohortVerificationFailed,
            extractor.LastFailureCode);
    }

    [Fact]
    public async Task WarmUp_MissingLanguageDataRejectsBeforeEngine()
    {
        var boundary = new FakeBoundary();
        var tessdata = Path.Combine(_root, "tessdata");
        Directory.CreateDirectory(tessdata);
        await using var extractor = CreateExtractor(
            OptionsFor(tessdata),
            boundary,
            _ => null!);

        var ready = await extractor.WarmUpAsync(CancellationToken.None, () => { });

        Assert.False(ready);
        Assert.Equal(1, boundary.CohortVerificationCalls);
        Assert.Equal(0, boundary.PrepareCalls);
        Assert.Equal(0, boundary.ConstructorCalls);
        Assert.Equal(
            VisionRuntimeCodes.OcrCohortVerificationFailed,
            extractor.LastFailureCode);
    }

    [Fact]
    public async Task WarmUp_UnapprovedLanguageRejectsBeforeNativeBoundary()
    {
        var boundary = new FakeBoundary();
        var tessdata = Path.Combine(_root, "tessdata-fra");
        Directory.CreateDirectory(tessdata);
        File.WriteAllBytes(Path.Combine(tessdata, "fra.traineddata"), [1]);
        await using var extractor = CreateExtractor(
            OptionsFor(tessdata, language: "fra"),
            boundary,
            _ => null!);

        var ready = await extractor.WarmUpAsync(CancellationToken.None, () => { });

        Assert.False(ready);
        Assert.Equal(1, boundary.CohortVerificationCalls);
        Assert.Equal(0, boundary.PrepareCalls);
        Assert.Equal(0, boundary.ConstructorCalls);
        Assert.Equal(
            VisionRuntimeCodes.OcrCohortVerificationFailed,
            extractor.LastFailureCode);
    }

    [Fact]
    public async Task WarmUp_NativePreparationRejectionPreservesCohortFailureCode()
    {
        var boundary = new FakeBoundary { PrepareResult = false };
        var tessdata = CreateTessdata();
        await using var extractor = CreateExtractor(
            OptionsFor(tessdata),
            boundary,
            _ => null!);

        var ready = await extractor.WarmUpAsync(CancellationToken.None, () => { });

        Assert.False(ready);
        Assert.Equal(1, boundary.CohortVerificationCalls);
        Assert.Equal(1, boundary.PrepareCalls);
        Assert.Equal(0, boundary.ConstructorCalls);
        Assert.Equal(
            VisionRuntimeCodes.OcrCohortVerificationFailed,
            extractor.LastFailureCode);
    }

    [Fact]
    public async Task WarmUp_EngineFactoryFailureBecomesClosedRuntimeCode()
    {
        var boundary = new FakeBoundary { InvokeConstructor = true };
        var tessdata = CreateTessdata();
        await using var extractor = CreateExtractor(
            OptionsFor(tessdata),
            boundary,
            _ => throw new InvalidOperationException("test-native-init-failure"));

        var ready = await extractor.WarmUpAsync(CancellationToken.None, () => { });

        Assert.False(ready);
        Assert.Equal(1, boundary.ConstructorCalls);
        Assert.False(extractor.IsReady);
        Assert.Equal(
            VisionRuntimeCodes.OcrRuntimeInitializationFailed,
            extractor.LastFailureCode);
    }

    [Fact]
    public async Task WarmUp_BoundaryConstructorRejectionCannotFalseReportReady()
    {
        var boundary = new FakeBoundary
        {
            InvokeConstructor = false,
            ConstructorResult = false,
        };
        var tessdata = CreateTessdata();
        await using var extractor = CreateExtractor(
            OptionsFor(tessdata),
            boundary,
            _ => null!);

        var ready = await extractor.WarmUpAsync(CancellationToken.None, () => { });

        Assert.False(ready);
        Assert.Equal(1, boundary.ConstructorCalls);
        Assert.False(extractor.IsReady);
        Assert.Equal(
            VisionRuntimeCodes.OcrRuntimeInitializationFailed,
            extractor.LastFailureCode);
    }

    [Fact]
    public async Task WarmUp_AlreadyCancelledTokenDoesNotEnterNativeBoundary()
    {
        var boundary = new FakeBoundary();
        await using var extractor = CreateExtractor(
            OptionsFor(tessdataPath: null),
            boundary,
            _ => null!);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            extractor.WarmUpAsync(cancellation.Token, () => { }));

        Assert.Equal(0, boundary.PrepareCalls);
        Assert.Equal(0, boundary.ConstructorCalls);
        Assert.Equal(0, boundary.CohortVerificationCalls);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Cleanup is best-effort; no assertion depends on it.
        }
    }

    private string CreateTessdata()
    {
        var tessdata = Path.Combine(_root, "tessdata");
        Directory.CreateDirectory(tessdata);
        File.WriteAllBytes(Path.Combine(tessdata, "eng.traineddata"), [1]);
        return tessdata;
    }

    private static AgentOptions OptionsFor(
        string? tessdataPath,
        long memoryHeadroomBytes = 0,
        string language = "eng") => new()
        {
            Vision = new VisionOptions
            {
                Enabled = true,
                Tesseract = new TesseractOptions
                {
                    Enabled = true,
                    Language = language,
                    TessdataPath = tessdataPath,
                    MemoryHeadroomBytes = memoryHeadroomBytes,
                    IdleUnloadSeconds = 0,
                    ExtractionTimeoutSeconds = 1,
                },
            },
        };

    private static TesseractScreenExtractor CreateExtractor(
        AgentOptions options,
        ITesseractNativeLoadBoundary boundary,
        Func<TesseractOptions, TesseractEngine> engineFactory,
        VisionRuntimeStatusTracker? tracker = null) => new(
            Options.Create(options),
            new LoggerConfiguration().CreateLogger(),
            boundary,
            engineFactory,
            tracker);

    private static VisionRuntimeStatusTracker RuntimeTracker()
    {
        var defaults = VisionOptionsSnapshot.DisabledDefault();
        var enabled = defaults with
        {
            Enabled = true,
            Tesseract = defaults.Tesseract with { Enabled = true },
        };
        return new VisionRuntimeStatusTracker(new VisionConfigurationLoadResult(
            IsValid: true,
            IsMissing: false,
            Code: "active",
            EffectiveOptions: enabled));
    }

    private sealed class FakeBoundary : ITesseractNativeLoadBoundary
    {
        public bool CohortVerificationResult { get; init; } = true;
        public bool PrepareResult { get; init; } = true;
        public bool ConstructorResult { get; init; } = true;
        public bool InvokeConstructor { get; init; }
        public int CohortVerificationCalls { get; private set; }
        public int PrepareCalls { get; private set; }
        public int ConstructorCalls { get; private set; }

        public bool TryVerifyCohort(TesseractOptions options, ILogger logger)
        {
            CohortVerificationCalls++;
            return CohortVerificationResult;
        }

        public bool TryPrepare(TesseractOptions options, ILogger logger)
        {
            PrepareCalls++;
            return PrepareResult;
        }

        public bool TryRunEngineConstructor(
            TesseractOptions options,
            ILogger logger,
            Action constructEngine)
        {
            ConstructorCalls++;
            if (InvokeConstructor)
                constructEngine();
            return ConstructorResult;
        }
    }
}
