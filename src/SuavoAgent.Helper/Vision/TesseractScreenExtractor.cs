using System.Diagnostics;
using Microsoft.Extensions.Options;
using Serilog;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Core.Config;
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
///     (operator-provided). Default install ships zero OCR binaries.
///
/// Safety:
///   - Never throws for OCR failures — returns null so the controller
///     cleanly escalates or emits an empty frame.
///   - Confidence floor drops garbage regions before they reach the scrubber.
/// </summary>
internal sealed class TesseractScreenExtractor : IScreenExtractor, IAsyncDisposable
{
    private readonly TesseractOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private TesseractEngine? _engine;
    private long _lastUseTicks = -1;
    private int _activeCalls;
    private CancellationTokenSource? _idleWatcherCts;
    private static int s_nativeConfigured; // 0 = not yet, 1 = done

    public string ExtractorId => $"tesseract-{_options.Language}";
    public bool IsReady => true; // "configured" — lazy-loads on first call

    public TesseractScreenExtractor(IOptions<AgentOptions> options, ILogger logger)
    {
        _options = options.Value.Vision.Tesseract;
        _logger = logger;

        // Codex M-1: configure native library path at construction time, BEFORE
        // any TesseractEngine constructor can execute. This guarantees the
        // Windows DLL search order knows about the operator-provided native
        // dir when llama.dll / leptonica / tesseract P/Invokes resolve.
        ConfigureNativeLibraryOnce();
    }

    private void ConfigureNativeLibraryOnce()
    {
        if (Interlocked.CompareExchange(ref s_nativeConfigured, 1, 0) != 0) return;

        if (string.IsNullOrEmpty(_options.NativeLibraryPath)) return;
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            AddDllDirectory(_options.NativeLibraryPath);
            _logger.Information(
                "TesseractScreenExtractor: added native dir to DLL search: {Path}",
                _options.NativeLibraryPath);
        }
        catch (Exception ex)
        {
            _logger.Warning(
                "TesseractScreenExtractor: failed to register native path ({Type}: {Msg})",
                ex.GetType().FullName, ex.Message);
            Interlocked.Exchange(ref s_nativeConfigured, 0);
        }
    }

    public async Task<ScreenFrame?> ExtractAsync(ScreenBytes screen, CancellationToken ct)
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
            if (!EnsureLoadedLocked())
            {
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
        var regions = new List<TextRegion>();
        // Codex M-4: use try/finally so _activeCalls ALWAYS decrements,
        // regardless of which exception path fires.
        try
        {
            await Task.Run(() =>
            {
                using var pix = Pix.LoadFromMemory(screen.Png);
                using var page = engine.Process(pix);
                using var iter = page.GetIterator();
                iter.Begin();
                do
                {
                    if (ct.IsCancellationRequested) break;
                    if (!iter.TryGetBoundingBox(PageIteratorLevel.TextLine, out var bounds))
                        continue;

                    var text = iter.GetText(PageIteratorLevel.TextLine)?.Trim() ?? "";
                    if (string.IsNullOrEmpty(text)) continue;

                    var confidence = iter.GetConfidence(PageIteratorLevel.TextLine);
                    if (confidence < _options.MinConfidence) continue;

                    regions.Add(new TextRegion
                    {
                        Text = text,
                        Bounds = new VisionRect(bounds.X1, bounds.Y1, bounds.Width, bounds.Height),
                        Confidence = confidence / 100.0, // Tesseract reports 0–100
                    });
                } while (iter.Next(PageIteratorLevel.TextLine));
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancellation — propagate. finally still runs.
            throw;
        }
        catch (Exception ex)
        {
            // Codex M-5: include type name so COM / RCW failures are spotable.
            _logger.Warning(
                "TesseractScreenExtractor: OCR failed ({Type}: {Msg})",
                ex.GetType().FullName, ex.Message);
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

    /// <summary>
    /// Loads TesseractEngine under the lock. Returns false on any setup
    /// error — missing paths, missing traineddata, native-lib load failure.
    /// </summary>
    private bool EnsureLoadedLocked()
    {
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
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(_options.TessdataPath))
        {
            _logger.Warning("TesseractScreenExtractor: TessdataPath not configured");
            return false;
        }
        if (!Directory.Exists(_options.TessdataPath))
        {
            _logger.Warning(
                "TesseractScreenExtractor: tessdata directory missing at {Path}",
                _options.TessdataPath);
            return false;
        }

        var trainedData = Path.Combine(
            _options.TessdataPath, $"{_options.Language}.traineddata");
        if (!File.Exists(trainedData))
        {
            _logger.Warning(
                "TesseractScreenExtractor: missing traineddata for '{Lang}' at {Path}",
                _options.Language, trainedData);
            return false;
        }

        // Native library path was configured in the constructor (Codex M-1).
        try
        {
            _engine = new TesseractEngine(_options.TessdataPath, _options.Language, EngineMode.Default);
            _logger.Information(
                "TesseractScreenExtractor: engine loaded ({Lang}, tessdata={Path}, idleUnloadSec={Idle}, headroomMb={HeadroomMb})",
                _options.Language, _options.TessdataPath,
                _options.IdleUnloadSeconds,
                _options.MemoryHeadroomBytes / (1024 * 1024));
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(
                "TesseractScreenExtractor: engine init failed ({Type}: {Msg})",
                ex.GetType().FullName, ex.Message);
            _engine = null;
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

    // --- Win32 interop --------------------------------------------------------

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static extern IntPtr AddDllDirectory(string newDirectory);
}
