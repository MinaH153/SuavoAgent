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

/// <summary>Persistent click-through GDI text card for agent narration. One layered
/// window; commands run on an STA thread that blocks when idle (no repaint at rest).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsBubbleRenderer : IBubbleRenderer, IDisposable
{
    private readonly ILogger _logger;
    private readonly BlockingCollection<Action> _commands = new();
    private Thread? _thread;
    private IntPtr _hwnd = IntPtr.Zero;
    private string _text = string.Empty;
    private string _tone = PresenceTones.Acting;
    private const int W = 360, H = 56, CursorPad = 26;

    public WindowsBubbleRenderer(ILogger logger) => _logger = logger.ForContext<WindowsBubbleRenderer>();

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
            _hwnd = CreateWindowEx(
                WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
                "STATIC", string.Empty, WS_POPUP, 0, 0, W, H,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
            ShowWindow(_hwnd, SW_HIDE);
            foreach (var cmd in _commands.GetConsumingEnumerable())
            {
                try { cmd(); } catch (Exception ex) { _logger.Debug(ex, "bubble cmd failed"); }
            }
        }
        catch (Exception ex) { _logger.Warning(ex, "bubble renderer loop ended"); }
        finally { if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd); }
    }

    private void Enqueue(Action a) { if (!_commands.IsAddingCompleted) _commands.Add(a); }

    public void Show(string text, string tone, int x, int y) => Enqueue(() =>
    {
        _text = text ?? string.Empty;
        _tone = tone ?? PresenceTones.Acting;
        Paint(x, y);
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
    });

    public void Reanchor(int x, int y) => Enqueue(() => { if (_text.Length > 0) Paint(x, y); });

    public void Hide() => Enqueue(() => { if (_hwnd != IntPtr.Zero) ShowWindow(_hwnd, SW_HIDE); });

    private void Paint(int anchorX, int anchorY)
    {
        if (_hwnd == IntPtr.Zero) return;
        var accent = ToneColor(_tone);
        using var bmp = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var card = new Rectangle(6, 6, W - 12, H - 12);
            using (var path = RoundedRect(card, 12))
            using (var fill = new SolidBrush(Color.FromArgb(232, 15, 23, 42)))   // charcoal glass
            using (var accentBrush = new SolidBrush(accent))
            {
                g.FillPath(fill, path);
                g.FillRectangle(accentBrush, card.X, card.Y + 6, 4, card.Height - 12); // gold left bar
            }
            using var font = new Font("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Point);
            using var text = new SolidBrush(Color.FromArgb(245, 234, 224));         // cream
            var textRect = new RectangleF(card.X + 16, card.Y, card.Width - 22, card.Height);
            using var fmt = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            g.DrawString(_text, font, text, textRect, fmt);
        }

        // Position above-right of the cursor, clamped to the virtual screen.
        var px = anchorX + CursorPad;
        var py = anchorY - H - CursorPad / 2;
        var vsX = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var vsY = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var vsW = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var vsH = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        px = Math.Clamp(px, vsX, vsX + vsW - W);
        py = Math.Clamp(py, vsY, vsY + vsH - H);

        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(memDc, hBitmap);
        try
        {
            var dst = new PointNative(px, py);
            var sz = new SizeNative(W, H);
            var src = new PointNative(0, 0);
            var blend = new BlendFunction { BlendOp = AC_SRC_OVER, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
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

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
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
