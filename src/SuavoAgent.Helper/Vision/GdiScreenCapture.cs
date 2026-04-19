using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Options;
using Serilog;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Core.Config;

namespace SuavoAgent.Helper.Vision;

/// <summary>
/// GDI BitBlt screen capture. Uses Graphics.CopyFromScreen — the classic
/// Windows capture path. No hooks installed, no DLL injection into other
/// processes. Indistinguishable from any OS-level accessibility tool that
/// reads the framebuffer (which is legitimate for screen readers, remote
/// desktop viewers, and recording software).
///
/// Rate-limited per VisionOptions.MinIntervalMs so a buggy caller can't burn
/// CPU / disk at unbounded frequency.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GdiScreenCapture : IScreenCapture
{
    private readonly VisionOptions _options;
    private readonly ILogger _logger;
    // Monotonic tick count (Codex M-4) — does NOT jump on NTP sync / DST / manual
    // clock change. Using wall clock here caused rate-limit stalls and bursts.
    private long _lastCaptureTicks = -1;
    private readonly object _rateLock = new();

    public GdiScreenCapture(IOptions<AgentOptions> options, ILogger logger)
    {
        _options = options.Value.Vision;
        _logger = logger;
    }

    public bool IsAvailable => _options.Enabled && OperatingSystem.IsWindows();

    public async Task<ScreenBytes?> CapturePrimaryAsync(CancellationToken ct)
    {
        if (!IsAvailable) return null;
        if (!RateLimiterAllows()) return null;

        return await Task.Run<ScreenBytes?>(() =>
        {
            if (!OperatingSystem.IsWindows()) return null;

            try
            {
                var width = GetSystemMetrics(SM_CXSCREEN);
                var height = GetSystemMetrics(SM_CYSCREEN);
                if (width <= 0 || height <= 0)
                {
                    _logger.Warning("GdiScreenCapture: got non-positive screen metrics ({W}x{H})",
                        width, height);
                    return null;
                }

                using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.CopyFromScreen(0, 0, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
                }

                using var ms = new MemoryStream();
                bitmap.Save(ms, ImageFormat.Png);
                var png = ms.ToArray();

                // Capture hwnd AT THE SAME TIME as the pixels so downstream UIA
                // binds to this specific window, not whatever is frontmost a
                // few milliseconds later (Codex C-2).
                var hwnd = GetForegroundWindow().ToInt64();

                _logger.Debug("GdiScreenCapture: captured {W}x{H}, {Bytes} bytes, hwnd=0x{Hwnd:X}",
                    width, height, png.Length, hwnd);

                return new ScreenBytes(png, width, height, DateTimeOffset.UtcNow, hwnd);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "GdiScreenCapture: capture failed");
                return null;
            }
        }, ct);
    }

    private bool RateLimiterAllows()
    {
        lock (_rateLock)
        {
            // Environment.TickCount64 is monotonic since OS boot; immune to NTP
            // rollback and DST changes (Codex M-4).
            var now = Environment.TickCount64;
            var minInterval = Math.Max(0, _options.MinIntervalMs);
            if (_lastCaptureTicks >= 0)
            {
                var elapsed = now - _lastCaptureTicks;
                if (elapsed < minInterval)
                {
                    _logger.Debug("GdiScreenCapture: rate-limited ({Ms}ms since last)", elapsed);
                    return false;
                }
            }
            _lastCaptureTicks = now;
            return true;
        }
    }

    // --- Win32 interop -------------------------------------------------------

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
