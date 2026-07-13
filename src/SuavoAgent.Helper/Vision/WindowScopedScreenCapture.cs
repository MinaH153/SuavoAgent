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
/// Window-scoped capture via Win32 <c>PrintWindow(hwnd)</c>. Captures ONLY the pixels
/// belonging to the one specified window — no other window's content can leak even if
/// it is visible behind/beside the target. This is the HIPAA-critical difference from
/// framebuffer capture, which could include a PMS / banking / email window behind
/// or beside the intended application.
///
/// Authorized callers are: (1) explore_sandbox on a NON-PHI box, with the target HWND
/// resolved from the allowlisted sandbox app; and (2) live PMS vision through
/// <see cref="ApprovedPmsForegroundWindowCapture"/>, which supplies a dynamic authorizer
/// that re-proves the exact foreground HWND, PID, and signed PioneerRx process identity.
///
/// UWP note: Win11 Calculator's visible HWND is owned by ApplicationFrameHost; PrintWindow
/// with <c>PW_RENDERFULLCONTENT</c> forces DWM to render the composited content into the HDC.
/// Validated on Notepad (Win32); Calculator (UWP) needs on-box verification (PARTIAL).
///
/// Fail-closed: returns null on any failure (not available, rate-limited, window gone,
/// PrintWindow failure, GDI error). Never throws for capture failures.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowScopedScreenCapture : IScreenCapture
{
    private readonly VisionOptions _options;
    private readonly ILogger _logger;
    private readonly IntPtr _targetHwnd;
    // PID that owned _targetHwnd when this capturer was built (IPC-validated as an allowlisted sandbox app).
    // Re-checked immediately before PrintWindow to close the HWND-reuse TOCTOU. 0 = skip (tests only).
    private readonly int _expectedPid;
    private readonly Func<IntPtr, int, bool>? _authorizeImmediatelyBeforePrintWindow;
    // Monotonic tick count (Codex M-4) — immune to NTP/DST.
    private long _lastCaptureTicks = -1;
    private readonly object _rateLock = new();

    public WindowScopedScreenCapture(
        IOptions<AgentOptions> options,
        ILogger logger,
        IntPtr targetHwnd,
        int expectedPid = 0,
        Func<IntPtr, int, bool>? authorizeImmediatelyBeforePrintWindow = null)
    {
        _options = options.Value.Vision;
        _logger = logger;
        _targetHwnd = targetHwnd;
        _expectedPid = expectedPid;
        _authorizeImmediatelyBeforePrintWindow = authorizeImmediatelyBeforePrintWindow;
    }

    public bool IsAvailable => _options.Enabled && OperatingSystem.IsWindows() && _targetHwnd != IntPtr.Zero;

    public async Task<ScreenBytes?> CapturePrimaryAsync(CancellationToken ct)
    {
        if (!IsAvailable) return null;
        if (!RateLimiterAllows()) return null;

        return await Task.Run<ScreenBytes?>(() =>
        {
            if (!OperatingSystem.IsWindows()) return null;

            try
            {
                if (!IsWindowVisible(_targetHwnd))
                {
                    _logger.Warning("WindowScopedScreenCapture: target hwnd=0x{Hwnd:X} is not visible",
                        _targetHwnd.ToInt64());
                    return null;
                }

                var rect = new RECT();
                if (!GetWindowRect(_targetHwnd, ref rect))
                {
                    _logger.Warning("WindowScopedScreenCapture: GetWindowRect failed for 0x{Hwnd:X}",
                        _targetHwnd.ToInt64());
                    return null;
                }

                var width = rect.Right - rect.Left;
                var height = rect.Bottom - rect.Top;
                if (width <= 0 || height <= 0)
                {
                    _logger.Warning("WindowScopedScreenCapture: non-positive rect ({W}x{H}) for 0x{Hwnd:X}",
                        width, height, _targetHwnd.ToInt64());
                    return null;
                }

                using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bitmap))
                {
                    var hdc = g.GetHdc();
                    try
                    {
                        // TOCTOU guard: re-verify the HWND STILL belongs to the expected (IPC-validated,
                        // allowlisted) PID IMMEDIATELY before each PrintWindow — same thread, no await, no GDI
                        // setup in between. Windows reuses HWND values; a reused HWND would have a different
                        // owner PID → refuse. This is the practical floor (PrintWindow itself takes no PID).
                        if (EffectiveAppPidMismatch() || DynamicAuthorizationFailed()) return null;

                        // PW_RENDERFULLCONTENT forces DWM to render the full composited window content
                        // (needed for DWM-composited / UWP apps). Fall back to flag 0 (pre-Win8.1) before
                        // giving up — re-checking the owner PID again before the fallback call.
                        if (!PrintWindow(_targetHwnd, hdc, PW_RENDERFULLCONTENT))
                        {
                            if (EffectiveAppPidMismatch() || DynamicAuthorizationFailed()) return null;
                            if (!PrintWindow(_targetHwnd, hdc, 0))
                            {
                                _logger.Warning("WindowScopedScreenCapture: PrintWindow failed for 0x{Hwnd:X}",
                                    _targetHwnd.ToInt64());
                                return null;
                            }
                        }
                    }
                    finally
                    {
                        g.ReleaseHdc(hdc);
                    }
                }

                using var ms = new MemoryStream();
                bitmap.Save(ms, ImageFormat.Png);
                var png = ms.ToArray();

                _logger.Debug("WindowScopedScreenCapture: captured 0x{Hwnd:X} -> {W}x{H}, {Bytes} bytes",
                    _targetHwnd.ToInt64(), width, height, png.Length);

                // Hwnd = the captured target window (not a re-queried GetForegroundWindow) so the
                // downstream UIA extractor binds to THIS window's tree (Codex C-2 contract).
                return new ScreenBytes(png, width, height, DateTimeOffset.UtcNow, _targetHwnd.ToInt64());
            }
            catch (Exception ex)
            {
                _logger.Warning(
                    "WindowScopedScreenCapture: capture failed ({ErrorType})",
                    ex.GetType().Name);
                return null;
            }
        }, ct);
    }

    /// <summary>
    /// True when the TOCTOU guard should REFUSE: the EFFECTIVE app behind the target HWND no longer equals
    /// the expected (IPC-validated, allowlisted) app PID. UWP-aware (drills ApplicationFrameHost → CoreWindow),
    /// so a reused HWND now hosting a different app is caught. _expectedPid &lt;= 0 (tests) skips. Called
    /// same-thread immediately before each PrintWindow.
    /// </summary>
    private bool EffectiveAppPidMismatch()
    {
        if (_expectedPid <= 0) return false;
        var now = SuavoAgent.Helper.Actuation.SandboxWindowResolver.EffectiveAppPid(_targetHwnd);
        if (now != 0 && now == _expectedPid) return false;
        _logger.Warning(
            "WindowScopedScreenCapture: effective app pid changed (now={Now}, expected={Exp}) for 0x{Hwnd:X} — refusing",
            now, _expectedPid, _targetHwnd.ToInt64());
        return true;
    }

    private bool DynamicAuthorizationFailed()
    {
        if (_authorizeImmediatelyBeforePrintWindow is null) return false;
        if (_authorizeImmediatelyBeforePrintWindow(_targetHwnd, _expectedPid)) return false;
        _logger.Information("WindowScopedScreenCapture: dynamic target authorization failed — refusing");
        return true;
    }

    private bool RateLimiterAllows()
    {
        lock (_rateLock)
        {
            var now = Environment.TickCount64;
            var minInterval = Math.Max(0, _options.MinIntervalMs);
            if (_lastCaptureTicks >= 0)
            {
                var elapsed = now - _lastCaptureTicks;
                if (elapsed < minInterval)
                {
                    _logger.Debug("WindowScopedScreenCapture: rate-limited ({Ms}ms since last)", elapsed);
                    return false;
                }
            }
            _lastCaptureTicks = now;
            return true;
        }
    }

    // --- Win32 interop -------------------------------------------------------

    // PW_RENDERFULLCONTENT (0x2): render the full window incl. DWM-composited / UWP content.
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, ref RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
}
