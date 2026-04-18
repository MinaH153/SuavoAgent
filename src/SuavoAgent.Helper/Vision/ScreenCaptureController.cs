using Serilog;
using SuavoAgent.Contracts.Vision;

namespace SuavoAgent.Helper.Vision;

/// <summary>
/// Orchestrates the end-to-end vision pipeline: capture → encrypt/store →
/// extract → scrub. Callers get back only the PHI-scrubbed ScreenFrame and
/// the storage id; raw bytes and unscrubbed text never escape.
///
/// Stateless, thread-safe after construction. Never throws for pipeline
/// failures — returns a null-or-empty result so callers cleanly skip vision.
/// </summary>
public sealed class ScreenCaptureController
{
    private readonly IScreenCapture _capture;
    private readonly IScreenStore _store;
    private readonly IScreenExtractor _extractor;
    private readonly ILogger _logger;

    public ScreenCaptureController(
        IScreenCapture capture,
        IScreenStore store,
        IScreenExtractor extractor,
        ILogger logger)
    {
        _capture = capture;
        _store = store;
        _extractor = extractor;
        _logger = logger;
    }

    /// <summary>
    /// Captures the current screen, stores the encrypted bytes, extracts a
    /// scrubbed ScreenFrame. Returns the storage id + frame, or null if any
    /// stage failed / vision is disabled.
    /// </summary>
    public async Task<CaptureResult?> CaptureAndExtractAsync(CancellationToken ct)
    {
        if (!_capture.IsAvailable)
        {
            _logger.Debug("ScreenCaptureController: capture unavailable — skipping");
            return null;
        }

        var screen = await _capture.CapturePrimaryAsync(ct);
        if (screen == null) return null;

        var storedId = await _store.StoreAsync(screen.Value, ct);
        if (storedId == null)
        {
            _logger.Warning("ScreenCaptureController: store failed, continuing without retention");
        }

        var frame = await _extractor.ExtractAsync(screen.Value, ct);
        if (frame == null)
        {
            _logger.Warning("ScreenCaptureController: extraction returned null");
            return null;
        }

        _logger.Debug(
            "ScreenCaptureController: frame {FrameId} from storage {StoreId}: "
            + "{Regions} text regions, {Elements} elements, {LatencyMs}ms extract",
            frame.Id, storedId ?? "<none>",
            frame.TextRegions.Count, frame.Elements.Count,
            frame.ExtractionLatencyMs);

        return new CaptureResult(storedId, frame);
    }
}

public sealed record CaptureResult(string? StorageId, ScreenFrame Frame);
