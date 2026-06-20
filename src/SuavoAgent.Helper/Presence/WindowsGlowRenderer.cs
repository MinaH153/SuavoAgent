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

/// <summary>Full-screen FSD edge glow. One layered click-through window covering the virtual
/// desktop; the edge-gradient bitmap is rendered once per tone, then "breathes" by varying
/// UpdateLayeredWindow's SourceConstantAlpha only (no per-frame bitmap re-render). Non-obscuring
/// (edges only, transparent center). Idle = hidden (no repaint).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsGlowRenderer : IGlowRenderer, IDisposable
{
    private readonly ILogger _logger;
    private readonly BlockingCollection<Action> _commands = new();
    private Thread? _thread;
    private IntPtr _hwnd = IntPtr.Zero;
    private IntPtr _hBitmap = IntPtr.Zero;   // cached edge bitmap for the current tone
    private string? _bitmapTone;
    private int _vx, _vy, _vw, _vh;
    private volatile bool _breathing;
    private double _intensity = 0.6;

    public WindowsGlowRenderer(ILogger logger) => _logger = logger.ForContext<WindowsGlowRenderer>();

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
            _vx = GetSystemMetrics(SM_XVIRTUALSCREEN); _vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            _vw = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN)); _vh = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));
            _hwnd = CreateWindowEx(
                WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
                "STATIC", string.Empty, WS_POPUP, _vx, _vy, _vw, _vh,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
            ShowWindow(_hwnd, SW_HIDE);
            foreach (var cmd in _commands.GetConsumingEnumerable())
            {
                try { cmd(); } catch (Exception ex) { _logger.Debug(ex, "glow cmd failed"); }
            }
        }
        catch (Exception ex) { _logger.Warning(ex, "glow renderer loop ended"); }
        finally { ReleaseBitmap(); if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd); }
    }

    private void Enqueue(Action a) { if (!_commands.IsAddingCompleted) _commands.Add(a); }

    public void Show(string tone, double intensity) => Enqueue(() =>
    {
        _intensity = Math.Clamp(intensity, 0.05, 1.0);
        EnsureBitmap(tone ?? PresenceTones.Acting);
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        if (_breathing) return;          // already breathing; tone/intensity updated for next frame
        _breathing = true;
        Breathe();
    });

    public void Hide() => Enqueue(() => { _breathing = false; if (_hwnd != IntPtr.Zero) ShowWindow(_hwnd, SW_HIDE); });

    private void Breathe()
    {
        var startedAt = Environment.TickCount64;
        while (_breathing)
        {
            var t = ((Environment.TickCount64 - startedAt) % 3000) / 3000.0;  // 3s cycle
            var wave = 0.5 - 0.5 * Math.Cos(t * 2 * Math.PI);                  // 0 → 1 → 0
            var lo = Math.Min(0.30, _intensity);
            var a = lo + (_intensity - lo) * wave;
            Blend((byte)Math.Round(255 * Math.Clamp(a, 0.0, 1.0)));
            // Drain queued commands (tone change / hide) without blocking the breath.
            while (_commands.TryTake(out var cmd)) { try { cmd(); } catch { } }
            if (!_breathing) break;
            Thread.Sleep(80);                                                  // ~12fps
        }
    }

    private void EnsureBitmap(string tone)
    {
        if (_bitmapTone == tone && _hBitmap != IntPtr.Zero) return;
        ReleaseBitmap();
        var color = ToneColor(tone);
        using var bmp = new Bitmap(_vw, _vh, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            var band = Math.Max(24, Math.Min(_vw, _vh) / 12); // edge thickness
            DrawEdge(g, color, new Rectangle(0, 0, _vw, band), 90f, false);            // top
            DrawEdge(g, color, new Rectangle(0, _vh - band, _vw, band), 90f, true);    // bottom
            DrawEdge(g, color, new Rectangle(0, 0, band, _vh), 0f, false);             // left
            DrawEdge(g, color, new Rectangle(_vw - band, 0, band, _vh), 0f, true);     // right
        }
        _hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
        _bitmapTone = tone;
    }

    private static void DrawEdge(Graphics g, Color color, Rectangle r, float angle, bool reverse)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        var c0 = Color.FromArgb(150, color);
        var c1 = Color.FromArgb(0, color);
        using var brush = new LinearGradientBrush(r, reverse ? c1 : c0, reverse ? c0 : c1, angle);
        g.FillRectangle(brush, r);
    }

    private void Blend(byte alpha)
    {
        if (_hwnd == IntPtr.Zero || _hBitmap == IntPtr.Zero) return;
        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var old = SelectObject(memDc, _hBitmap);
        try
        {
            var dst = new PointNative(_vx, _vy);
            var sz = new SizeNative(_vw, _vh);
            var src = new PointNative(0, 0);
            var blend = new BlendFunction { BlendOp = AC_SRC_OVER, BlendFlags = 0, SourceConstantAlpha = alpha, AlphaFormat = AC_SRC_ALPHA };
            UpdateLayeredWindow(_hwnd, screenDc, ref dst, ref sz, memDc, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally { SelectObject(memDc, old); DeleteDC(memDc); ReleaseDC(IntPtr.Zero, screenDc); }
    }

    private void ReleaseBitmap()
    {
        if (_hBitmap != IntPtr.Zero) { DeleteObject(_hBitmap); _hBitmap = IntPtr.Zero; _bitmapTone = null; }
    }

    private static Color ToneColor(string tone) => tone switch
    {
        PresenceTones.Acting => Color.FromArgb(200, 169, 106),
        PresenceTones.Observing => Color.FromArgb(122, 158, 126),
        PresenceTones.Confirm => Color.FromArgb(140, 40, 50),
        _ => Color.FromArgb(200, 169, 106),
    };

    public void Dispose()
    {
        _breathing = false;
        try { _commands.CompleteAdding(); } catch { }
        _thread?.Join(500);
        _commands.Dispose();
    }

    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80,
        WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int SW_SHOWNOACTIVATE = 4, SW_HIDE = 0, ULW_ALPHA = 0x2;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    private const byte AC_SRC_OVER = 0x00, AC_SRC_ALPHA = 0x01;

    [StructLayout(LayoutKind.Sequential)] private struct PointNative { public int X, Y; public PointNative(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] private struct SizeNative { public int X, Y; public SizeNative(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BlendFunction { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle, int x, int y, int w, int h, IntPtr p, IntPtr m, IntPtr i, IntPtr lp);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dcDst, ref PointNative dst, ref SizeNative sz, IntPtr dcSrc, ref PointNative src, int crKey, ref BlendFunction blend, int flags);
}
