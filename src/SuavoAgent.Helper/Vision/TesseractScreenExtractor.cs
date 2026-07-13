using System.Diagnostics;
using Microsoft.Extensions.Options;
using Serilog;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Vision;
using Tesseract;
using VisionRect = SuavoAgent.Contracts.Vision.Rect;

namespace SuavoAgent.Helper.Vision;

/// <summary>
/// Tesseract-backed OCR extractor. Produces PHI-scrubbed-safe input material:
/// the extractor itself emits raw OCR text, then <see cref="PhiScrubbingExtractor"/>
/// runs at the factory boundary so callers outside this assembly only ever
/// receive scrubbed <see cref="ScreenFrame"/>s.
///
/// Lifecycle:
///   - Engine lazy-loaded on first call (loads traineddata, ~50–100 MB RAM)
///   - Kept resident for <see cref="TesseractOptions.IdleUnloadSeconds"/>
///   - Unload refuses while an extraction is in flight
///
/// Vendor stealth:
///   - Native tesseract binaries come from <see cref="TesseractOptions.NativeLibraryPath"/>
///     after exact compiled-policy manifest verification. Default install
///     ships zero OCR native binaries.
///
/// Safety:
///   - Never throws for OCR failures — returns null so the controller
///     cleanly escalates or emits an empty frame.
///   - Confidence floor drops garbage regions before they reach the scrubber.
/// </summary>
internal sealed class TesseractScreenExtractor : IPricingScreenExtractor, IAsyncDisposable
{
    private readonly TesseractOptions _options;
    private readonly ILogger _logger;
    private readonly ITesseractNativeLoadBoundary _nativeLoadBoundary;
    private readonly Func<TesseractOptions, TesseractEngine> _engineFactory;
    private readonly VisionRuntimeStatusTracker? _runtimeStatus;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private TesseractEngine? _engine;
    private long _lastUseTicks = -1;
    private int _activeCalls;
    private int _runtimeReady;
    private string? _lastFailureCode;
    private CancellationTokenSource? _idleWatcherCts;
    public string ExtractorId => $"tesseract-{_options.Language}";
    public bool IsReady => Volatile.Read(ref _runtimeReady) == 1;
    internal string? LastFailureCode => Volatile.Read(ref _lastFailureCode);

    public TesseractScreenExtractor(IOptions<AgentOptions> options, ILogger logger)
        : this(options, logger, runtimeStatus: null)
    {
    }

    internal TesseractScreenExtractor(
        IOptions<AgentOptions> options,
        ILogger logger,
        VisionRuntimeStatusTracker? runtimeStatus)
        : this(
            options,
            logger,
            TesseractNativeLoadBoundary.Shared,
            configured => new TesseractEngine(
                configured.TessdataPath,
                configured.Language,
                EngineMode.Default),
            runtimeStatus)
    {
    }

    internal TesseractScreenExtractor(
        IOptions<AgentOptions> options,
        ILogger logger,
        ITesseractNativeLoadBoundary nativeLoadBoundary,
        Func<TesseractOptions, TesseractEngine> engineFactory,
        VisionRuntimeStatusTracker? runtimeStatus = null)
    {
        _options = options.Value.Vision.Tesseract;
        _logger = logger;
        _nativeLoadBoundary = nativeLoadBoundary;
        _engineFactory = engineFactory;
        _runtimeStatus = runtimeStatus;
    }

    internal async Task<bool> WarmUpAsync(
        CancellationToken ct,
        Action? failStop = null)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ready = await EnsureLoadedWithWatchdogLockedAsync(
                ct,
                failStop ?? NativeOcrWatchdog.TerminateCurrentHelper).ConfigureAwait(false);
            if (ready)
            {
                _lastUseTicks = Environment.TickCount64;
                RestartIdleWatcher();
            }
            return ready;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ScreenFrame?> ExtractAsync(ScreenBytes screen, CancellationToken ct)
        => await ExtractCoreAsync(
            screen,
            PageIteratorLevel.TextLine,
            ct).ConfigureAwait(false);

    public async Task<ScreenFrame?> ExtractPricingAsync(
        ScreenBytes screen,
        CancellationToken ct) => await ExtractCoreAsync(
            screen,
            PageIteratorLevel.Word,
            ct).ConfigureAwait(false);

    private async Task<ScreenFrame?> ExtractCoreAsync(
        ScreenBytes screen,
        PageIteratorLevel level,
        CancellationToken ct)
    {
        if (screen.Png == null || screen.Png.Length == 0)
        {
            _logger.Debug("TesseractScreenExtractor: empty PNG, returning empty frame");
            return EmptyFrame(screen);
        }

        TesseractEngine? engine;
        await _lock.WaitAsync(ct);
        try
        {
            if (!await EnsureLoadedWithWatchdogLockedAsync(
                    ct,
                    NativeOcrWatchdog.TerminateCurrentHelper).ConfigureAwait(false))
            {
                return null;
            }
            if (!LoadedNativeModulesMatchApprovedCohort())
            {
                RecordFailure(VisionRuntimeCodes.OcrRuntimeInitializationFailed);
                return null;
            }
            engine = _engine;
            if (engine == null) return null;
            Interlocked.Increment(ref _activeCalls);
            _lastUseTicks = Environment.TickCount64;
        }
        finally
        {
            _lock.Release();
        }

        var sw = Stopwatch.StartNew();
        IReadOnlyList<TextRegion> regions;
        var timeoutSec = Math.Clamp(_options.ExtractionTimeoutSeconds, 1, 120);
        try
        {
            regions = await NativeOcrWatchdog.RunAsync(
                () => ExtractRegions(engine, screen.Png, level),
                TimeSpan.FromSeconds(timeoutSec),
                ct,
                NativeOcrWatchdog.TerminateCurrentHelper).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (NativeOcrTimeoutException)
        {
            // Production's watchdog already terminated the Helper. If a host
            // unexpectedly suppresses termination, invoke the no-dump fail-stop
            // again; never return while native code may still own the engine.
            NativeOcrWatchdog.TerminateCurrentHelper();
            throw new UnreachableException("Native OCR fail-stop unexpectedly returned.");
        }
        catch (Exception ex)
        {
            // Codex M-5: include type name so COM / RCW failures are spotable.
            _logger.Warning(
                "TesseractScreenExtractor: OCR failed ({Type})",
                ex.GetType().Name);
            RecordFailure(VisionRuntimeCodes.OcrExtractionFailed);
            return null;
        }
        finally
        {
            sw.Stop();
            _lastUseTicks = Environment.TickCount64;
            Interlocked.Decrement(ref _activeCalls);
            RestartIdleWatcher();
        }

        _logger.Debug(
            "TesseractScreenExtractor: extracted {Count} regions in {Ms}ms",
            regions.Count, sw.ElapsedMilliseconds);
        RecordReady();

        return new ScreenFrame
        {
            Id = Guid.NewGuid().ToString("N"),
            CapturedAt = screen.CapturedAt,
            Width = screen.Width,
            Height = screen.Height,
            TextRegions = regions,
            Elements = Array.Empty<VisualElement>(), // Tesseract doesn't detect UI elements
            ExtractorId = ExtractorId,
            ExtractionLatencyMs = sw.ElapsedMilliseconds,
        };
    }

    private async Task<bool> EnsureLoadedWithWatchdogLockedAsync(
        CancellationToken ct,
        Action failStop)
    {
        var timeoutSec = Math.Clamp(_options.ExtractionTimeoutSeconds, 1, 120);
        try
        {
            return await NativeOcrWatchdog.RunAsync(
                EnsureLoadedLocked,
                TimeSpan.FromSeconds(timeoutSec),
                ct,
                failStop).ConfigureAwait(false);
        }
        catch (NativeOcrTimeoutException)
        {
            RecordFailure(VisionRuntimeCodes.OcrRuntimeTimeout);
            throw;
        }
    }

    private IReadOnlyList<TextRegion> ExtractRegions(
        TesseractEngine engine,
        byte[] png,
        PageIteratorLevel level)
    {
        var regions = new List<TextRegion>();
        using var pix = Pix.LoadFromMemory(png);
        using var page = engine.Process(pix);
        using var iter = page.GetIterator();
        iter.Begin();
        do
        {
            if (!iter.TryGetBoundingBox(level, out var bounds))
                continue;
            var text = iter.GetText(level)?.Trim() ?? string.Empty;
            if (text.Length == 0) continue;
            var confidence = iter.GetConfidence(level);
            if (confidence < _options.MinConfidence) continue;
            regions.Add(new TextRegion
            {
                Text = text,
                Bounds = new VisionRect(bounds.X1, bounds.Y1, bounds.Width, bounds.Height),
                Confidence = confidence / 100.0,
            });
        } while (iter.Next(level));
        return regions;
    }

    /// <summary>
    /// Loads TesseractEngine under the lock. Returns false on any setup
    /// error — missing paths, missing traineddata, native-lib load failure.
    /// </summary>
    private bool EnsureLoadedLocked()
    {
        // Re-hash the complete compiled-policy inventory immediately before
        // every native OCR call. A valid config or an earlier startup check
        // cannot bless files, fallback roots, or process modules that changed.
        if (!_nativeLoadBoundary.TryPrepare(_options, _logger))
        {
            _logger.Warning(
                "TesseractScreenExtractor: exact native cohort verification failed");
            RecordFailure(VisionRuntimeCodes.OcrCohortVerificationFailed);
            return false;
        }

        if (_engine != null) return true;

        // Trip A 2026-04-25 Vision-On safety: refuse to load the engine if
        // Helper is already in resource pressure. Tesseract adds ~50-100 MB;
        // loading on top of an already-stressed Helper is exactly how the
        // first install at Nadim's hung the OS. Pairs with ResourceBudgetGuard
        // (500 MB soft warn / 800 MB hard kill) so OCR can't push Helper
        // into the danger zone.
        if (_options.MemoryHeadroomBytes > 0)
        {
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            var rss = proc.WorkingSet64;
            if (rss >= _options.MemoryHeadroomBytes)
            {
                _logger.Warning(
                    "TesseractScreenExtractor: refusing engine load — Helper RSS={RssMb}MB " +
                    "is at/above MemoryHeadroomBytes={LimitMb}MB. Set MemoryHeadroomBytes=0 to disable headroom check.",
                    rss / (1024 * 1024),
                    _options.MemoryHeadroomBytes / (1024 * 1024));
                RecordFailure(VisionRuntimeCodes.OcrMemoryPressure);
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(_options.TessdataPath))
        {
            _logger.Warning("TesseractScreenExtractor: TessdataPath not configured");
            RecordFailure(VisionRuntimeCodes.OcrCohortVerificationFailed);
            return false;
        }
        if (!Directory.Exists(_options.TessdataPath))
        {
            _logger.Warning("TesseractScreenExtractor: tessdata directory missing");
            RecordFailure(VisionRuntimeCodes.OcrCohortVerificationFailed);
            return false;
        }

        var trainedData = Path.Combine(
            _options.TessdataPath, $"{_options.Language}.traineddata");
        if (!File.Exists(trainedData))
        {
            _logger.Warning(
                "TesseractScreenExtractor: traineddata missing for language '{Lang}'",
                _options.Language);
            RecordFailure(VisionRuntimeCodes.OcrCohortVerificationFailed);
            return false;
        }

        try
        {
            // The exclusive native-load boundary has already re-hashed the
            // cohort, rejected every upstream wrapper fallback, safely
            // preloaded both native modules, and proved their exact paths.
            if (!_nativeLoadBoundary.TryRunEngineConstructor(
                    _options,
                    _logger,
                    () => _engine = _engineFactory(_options)) ||
                _engine is null ||
                !LoadedNativeModulesMatchApprovedCohort())
            {
                _engine?.Dispose();
                _engine = null;
                _logger.Error(
                    "TesseractScreenExtractor: loaded native module identity is not release-approved");
                RecordFailure(VisionRuntimeCodes.OcrRuntimeInitializationFailed);
                return false;
            }
            _logger.Information(
                "TesseractScreenExtractor: engine loaded ({Lang}, idleUnloadSec={Idle}, headroomMb={HeadroomMb})",
                _options.Language,
                _options.IdleUnloadSeconds,
                _options.MemoryHeadroomBytes / (1024 * 1024));
            RecordReady();
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(
                "TesseractScreenExtractor: engine init failed ({Type})",
                ex.GetType().Name);
            _engine = null;
            RecordFailure(VisionRuntimeCodes.OcrRuntimeInitializationFailed);
            return false;
        }
    }

    private void RecordReady()
    {
        Volatile.Write(ref _lastFailureCode, null);
        Volatile.Write(ref _runtimeReady, 1);
        _runtimeStatus?.RecordReady(ocrReady: true);
    }

    private void RecordFailure(string code)
    {
        Volatile.Write(ref _lastFailureCode, code);
        Volatile.Write(ref _runtimeReady, 0);
        _runtimeStatus?.RecordFailure(code);
    }

    private bool LoadedNativeModulesMatchApprovedCohort()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var modules = process.Modules
                .Cast<ProcessModule>()
                .Where(module =>
                    string.Equals(
                        module.ModuleName,
                        "tesseract50.dll",
                        StringComparison.OrdinalIgnoreCase) ||
                    module.ModuleName.StartsWith(
                        "leptonica-",
                        StringComparison.OrdinalIgnoreCase))
                .ToDictionary(
                    module => module.ModuleName,
                    module => module.FileName,
                    StringComparer.OrdinalIgnoreCase);
            return TesseractNativeCohortPolicy.VerifyLoadedNativeModulePaths(
                _options,
                modules);
        }
        catch (Exception exception) when (exception is SystemException)
        {
            _logger.Warning(
                "TesseractScreenExtractor: native module enumeration failed ({Type})",
                exception.GetType().Name);
            return false;
        }
    }

    private void RestartIdleWatcher()
    {
        var idleAfter = _options.IdleUnloadSeconds;
        if (idleAfter <= 0) return; // 0 = keep loaded forever

        var previous = Interlocked.Exchange(ref _idleWatcherCts, new CancellationTokenSource());
        previous?.Cancel();
        previous?.Dispose();

        var token = _idleWatcherCts!.Token;
        var delay = TimeSpan.FromSeconds(Math.Max(10, idleAfter));

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, token);
                await UnloadIfIdleAsync(delay);
            }
            catch (OperationCanceledException) { /* another call came in */ }
        }, token);
    }

    private async Task UnloadIfIdleAsync(TimeSpan idleAfter)
    {
        await _lock.WaitAsync();
        try
        {
            if (Volatile.Read(ref _activeCalls) > 0) return;
            var elapsedMs = Environment.TickCount64 - _lastUseTicks;
            if (elapsedMs < idleAfter.TotalMilliseconds) return;
            if (_engine == null) return;

            _logger.Information(
                "TesseractScreenExtractor: unloading engine after {Sec}s idle",
                idleAfter.TotalSeconds);
            _engine.Dispose();
            _engine = null;
        }
        finally
        {
            _lock.Release();
        }
    }

    private ScreenFrame EmptyFrame(ScreenBytes screen) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        CapturedAt = screen.CapturedAt,
        Width = screen.Width,
        Height = screen.Height,
        ExtractorId = ExtractorId,
        ExtractionLatencyMs = 0,
    };

    public async ValueTask DisposeAsync()
    {
        var cts = Interlocked.Exchange(ref _idleWatcherCts, null);
        cts?.Cancel();
        cts?.Dispose();

        await _lock.WaitAsync();
        try
        {
            _engine?.Dispose();
            _engine = null;
        }
        finally
        {
            _lock.Release();
            _lock.Dispose();
        }
    }

}
