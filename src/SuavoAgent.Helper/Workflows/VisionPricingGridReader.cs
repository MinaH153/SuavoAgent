using Microsoft.Extensions.Options;
using Serilog;
using SuavoAgent.Core.Config;
using SuavoAgent.Helper.Vision;

namespace SuavoAgent.Helper.Workflows;

/// <summary>
/// Reads the cheapest supplier from the PioneerRx Pricing/Supplier-Catalog window BY SIGHT:
/// window-scoped screen capture (PrintWindow, this HWND only — no other window's pixels leak) →
/// PHI-scrubbing Tesseract OCR → <see cref="VisionSupplierGridParser"/>. This is the vision-primary
/// driver <see cref="PricingWorkflow"/> uses; the exact cost is confirmed against the UIA read by
/// <see cref="VisionExactReconciler"/> so a misread never writes wrong pricing.
///
/// Entirely on-device — the capture, OCR, and result never leave the box (HARD constraint: cloud
/// never sees the PMS). Opt-in via machine registry state (same operator acknowledgement as the observation
/// pipeline). Fail-soft: any capture/OCR failure returns null and the caller degrades to UIA-only.
/// </summary>
public sealed class VisionPricingGridReader : IAsyncDisposable
{
    private readonly IScreenExtractor _extractor;
    private readonly IOptions<AgentOptions> _options;
    private readonly ILogger _logger;

    public VisionPricingGridReader(IScreenExtractor extractor, IOptions<AgentOptions> options, ILogger logger)
    {
        _extractor = extractor;
        _options = options;
        _logger = logger;
    }

    public bool IsAvailable =>
        _options.Value.Vision.Enabled && _extractor.IsReady && OperatingSystem.IsWindows();

    /// <summary>
    /// Captures the given window, OCRs it, and returns the cheapest supplier the vision read found,
    /// or null on any failure (unavailable, capture failed, OCR empty, nothing parsed). Never throws.
    /// </summary>
    public async Task<VisionSupplierGridParser.VisionGridReading?> TryReadCheapestAsync(
        IntPtr hwnd, int expectedPid, CancellationToken ct)
    {
        if (!IsAvailable || hwnd == IntPtr.Zero || expectedPid <= 0) return null;
        try
        {
            // Resolve the current foreground HWND dynamically, require it to equal this exact
            // Edit-Rx-Item window/PID, and re-prove the signed PioneerRx executable immediately
            // before and after PrintWindow. Any focus, HWND, PID, revocation, or trust change
            // degrades to UIA-only without capturing another surface.
            var capture = ApprovedPmsForegroundWindowCapture.ForExpectedForegroundWindow(
                _options, _logger, hwnd, expectedPid);
            if (!capture.IsAvailable) return null;

            var bytes = await capture.CapturePrimaryAsync(ct).ConfigureAwait(false);
            if (bytes is null) return null;

            var frame = _extractor is IPricingScreenExtractor pricingExtractor
                ? await pricingExtractor.ExtractPricingAsync(bytes.Value, ct).ConfigureAwait(false)
                : await _extractor.ExtractAsync(bytes.Value, ct).ConfigureAwait(false);
            if (frame is null || frame.TextRegions.Count == 0) return null;

            var reading = VisionSupplierGridParser.ReadCheapest(frame.TextRegions);
            if (reading is not null)
                _logger.Debug(
                    "VisionPricingGridReader: OCR produced {Rows} usable supplier rows (confidence={Conf:F2})",
                    reading.UsableRowCount, reading.Confidence);
            return reading;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning("VisionPricingGridReader: read failed ({Type}) — degrading to UIA-only", ex.GetType().Name);
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_extractor is IAsyncDisposable ad) await ad.DisposeAsync().ConfigureAwait(false);
        else if (_extractor is IDisposable d) d.Dispose();
    }
}
