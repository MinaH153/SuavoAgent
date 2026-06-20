using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Serilog;

namespace SuavoAgent.Helper.Presence;

/// <summary>Persistent click-through GDI overlay for the presence cursor. One
/// session-long layered window; commands run on a dedicated STA thread that
/// blocks when idle (zero repaint at rest). Phase 1.5 migrates to DirectComposition.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPresenceRenderer : IPresenceRenderer, IDisposable
{
    private readonly ILogger _logger;
    private readonly BlockingCollection<Action> _commands = new();
    private Thread? _thread;
    private IntPtr _hwnd = IntPtr.Zero;
    private bool _visible = true;
    private const int WindowPad = 24;
    private const int MaxDiameter = 200;

    public WindowsPresenceRenderer(ILogger logger) => _logger = logger.ForContext<WindowsPresenceRenderer>();

    public void Start()
    {
        if (!OperatingSystem.IsWindows() || _thread is not null) return;
        _thread = new Thread(Loop) { IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Loop()
    {
        try
        {
            CreateOverlayWindow();
            foreach (var cmd in _commands.GetConsumingEnumerable()) // blocks at rest = no CPU
            {
                try { cmd(); } catch (Exception ex) { _logger.Debug(ex, "presence cmd failed"); }
            }
        }
        catch (Exception ex) { _logger.Warning(ex, "presence renderer loop ended"); }
        finally { if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd); }
    }

    private void Enqueue(Action a) { if (!_commands.IsAddingCompleted) _commands.Add(a); }

    public void Glide(int fromX, int fromY, int toX, int toY, int durationMs, string easing, string tone, int diameterPx)
        => Enqueue(() =>
        {
            if (!_visible) return;
            var startedAt = Environment.TickCount64;
            while (true)
            {
                var elapsed = (int)(Environment.TickCount64 - startedAt);
                var progress = durationMs <= 0 ? 1.0 : Math.Clamp(elapsed / (double)durationMs, 0, 1);
                var eased = Ease(easing, progress);
                var x = (int)Math.Round(fromX + (toX - fromX) * eased);
                var y = (int)Math.Round(fromY + (toY - fromY) * eased);
                Paint(x, y, diameterPx, tone, ringOnly: false, haloScale: 1.0);
                if (progress >= 1.0) break;
                Thread.Sleep(16);
            }
        });

    public void Reticle(int x, int y, int diameterPx, string tone)
        => Enqueue(() =>
        {
            if (!_visible) return;
            Paint(x, y, diameterPx, tone, ringOnly: false, haloScale: 1.0);
        });

    public void ClickPulse(int x, int y, string tone)
        => Enqueue(() =>
        {
            if (!_visible) return;
            var startedAt = Environment.TickCount64;
            const int pulseMs = 260;
            while (true)
            {
                var elapsed = (int)(Environment.TickCount64 - startedAt);
                var p = Math.Clamp(elapsed / (double)pulseMs, 0, 1);
                Paint(x, y, 34, tone, ringOnly: true, haloScale: 1.0 + p * 1.4); // expanding ring
                if (p >= 1.0) break;
                Thread.Sleep(16);
            }
            Paint(x, y, 34, tone, ringOnly: false, haloScale: 1.0); // settle back to resting dot
        });

    public void Hide() => Enqueue(() => { _visible = false; if (_hwnd != IntPtr.Zero) ShowWindow(_hwnd, SW_HIDE); });
    public void Show() => Enqueue(() => { _visible = true; if (_hwnd != IntPtr.Zero) ShowWindow(_hwnd, SW_SHOWNOACTIVATE); });

    private void CreateOverlayWindow()
    {
        var size = MaxDiameter + WindowPad * 2;
        _hwnd = CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
            "STATIC", string.Empty, WS_POPUP, 0, 0, size, size,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
    }

    private void Paint(int cx, int cy, int diameterPx, string tone, bool ringOnly, double haloScale)
    {
        if (_hwnd == IntPtr.Zero) return;
        var winSize = MaxDiameter + WindowPad * 2;
        var color = ToneColor(tone);
        var outer = Math.Clamp((int)Math.Round(diameterPx * haloScale), 6, MaxDiameter);
        var inner = Math.Max(6, diameterPx / 3);
        var center = winSize / 2f;

        using var bitmap = new Bitmap(winSize, winSize, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var oRect = new RectangleF(center - outer / 2f, center - outer / 2f, outer, outer);
            using var halo = new SolidBrush(Color.FromArgb(70, color));
            using var ring = new Pen(Color.FromArgb(220, color), 2.5f);
            if (!ringOnly) g.FillEllipse(halo, oRect);
            g.DrawEllipse(ring, oRect);
            if (!ringOnly)
            {
                var iRect = new RectangleF(center - inner / 2f, center - inner / 2f, inner, inner);
                using var dot = new SolidBrush(Color.FromArgb(245, color));
                g.FillEllipse(dot, iRect);
            }
        }

        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(memDc, hBitmap);
        try
        {
            var dst = new PointNative(cx - winSize / 2, cy - winSize / 2);
            var sz = new SizeNative(winSize, winSize);
            var src = new PointNative(0, 0);
            var blend = new BlendFunction
            {
                BlendOp = AC_SRC_OVER, BlendFlags = 0,
                SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA,
            };
            UpdateLayeredWindow(_hwnd, screenDc, ref dst, ref sz, memDc, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static double Ease(string easing, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return easing == "linear"
            ? t
            : (t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2); // ease-in-out-cubic
    }

    private static Color ToneColor(string tone) => tone switch
    {
        PresenceTones.Acting => Color.FromArgb(200, 169, 106),    // gold #C8A96A
        PresenceTones.Observing => Color.FromArgb(122, 158, 126), // sage
        PresenceTones.Confirm => Color.FromArgb(140, 40, 50),     // wine
        _ => Color.FromArgb(200, 169, 106),
    };

    public void Dispose()
    {
        try { _commands.CompleteAdding(); } catch { }
        _thread?.Join(500);
        _commands.Dispose();
    }

    // ── Win32 ──
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80,
        WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int SW_SHOWNOACTIVATE = 4, SW_HIDE = 0, ULW_ALPHA = 0x2;
    private const byte AC_SRC_OVER = 0x00, AC_SRC_ALPHA = 0x01;

    [StructLayout(LayoutKind.Sequential)] private struct PointNative { public int X, Y; public PointNative(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] private struct SizeNative { public int X, Y; public SizeNative(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BlendFunction { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int w, int h, IntPtr p, IntPtr m, IntPtr i, IntPtr lp);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dcDst, ref PointNative dst, ref SizeNative sz,
        IntPtr dcSrc, ref PointNative src, int crKey, ref BlendFunction blend, int flags);
}
