using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Serilog;
using SuavoAgent.Contracts.Ipc;

namespace SuavoAgent.Helper.IntentCursor;

/// <summary>
/// Windows-only visual overlay for agent pointer intent. It creates a
/// topmost, no-activate, transparent layered window and paints a short-lived
/// halo. It never calls SendInput, SetCursorPos, UIA Invoke, or mouse hooks.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsIntentCursorRenderer : IIntentCursorRenderer
{
    private readonly ILogger _logger;

    public WindowsIntentCursorRenderer(ILogger logger)
    {
        _logger = logger;
    }

    public Task ShowAsync(IntentCursorRenderRequest request, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || ct.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        var thread = new Thread(() =>
        {
            try
            {
                RenderOnce(request);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Intent cursor overlay failed");
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return Task.CompletedTask;
    }

    private static void RenderOnce(IntentCursorRenderRequest request)
    {
        var windowSize = request.DiameterPx + 20;
        var left = request.X - windowSize / 2;
        var top = request.Y - windowSize / 2;

        var hwnd = CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
            "STATIC",
            string.Empty,
            WS_POPUP,
            left,
            top,
            windowSize,
            windowSize,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        }

        try
        {
            PaintLayeredWindow(hwnd, request with { Opacity = 0 }, windowSize);
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);

            var startedAt = Environment.TickCount64;
            while (true)
            {
                var elapsedMs = (int)(Environment.TickCount64 - startedAt);
                if (elapsedMs >= request.DurationMs)
                {
                    break;
                }

                var opacity = IntentCursorPolicy.OpacityAt(request.Opacity, elapsedMs, request.DurationMs);
                PaintLayeredWindow(hwnd, request with { Opacity = opacity }, windowSize);

                while (NativeMessagePump.PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                {
                    NativeMessagePump.TranslateMessage(ref msg);
                    NativeMessagePump.DispatchMessage(ref msg);
                }

                Thread.Sleep(16);
            }
        }
        finally
        {
            DestroyWindow(hwnd);
        }
    }

    private static void PaintLayeredWindow(IntPtr hwnd, IntentCursorRenderRequest request, int windowSize)
    {
        using var bitmap = new Bitmap(windowSize, windowSize, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;

            var color = ToneColor(request.Tone);
            var center = windowSize / 2f;
            var outer = request.DiameterPx;
            var inner = Math.Max(6, request.DiameterPx / 3);
            var outerRect = new RectangleF(
                center - outer / 2f,
                center - outer / 2f,
                outer,
                outer);
            var innerRect = new RectangleF(
                center - inner / 2f,
                center - inner / 2f,
                inner,
                inner);

            using var haloBrush = new SolidBrush(Color.FromArgb(70, color));
            using var ringPen = new Pen(Color.FromArgb(220, color), 2.5f);
            using var dotBrush = new SolidBrush(Color.FromArgb(245, color));

            graphics.FillEllipse(haloBrush, outerRect);
            graphics.DrawEllipse(ringPen, outerRect);
            graphics.FillEllipse(dotBrush, innerRect);
        }

        var screenDc = GetDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        var hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
        var oldBitmap = SelectObject(memoryDc, hBitmap);

        try
        {
            var dst = new PointNative(request.X - windowSize / 2, request.Y - windowSize / 2);
            var size = new SizeNative(windowSize, windowSize);
            var src = new PointNative(0, 0);
            var blend = new BlendFunction
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = (byte)Math.Round(request.Opacity * 255),
                AlphaFormat = AC_SRC_ALPHA,
            };

            if (!UpdateLayeredWindow(hwnd, screenDc, ref dst, ref size, memoryDc, ref src, 0, ref blend, ULW_ALPHA))
            {
                throw new InvalidOperationException($"UpdateLayeredWindow failed: {Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            SelectObject(memoryDc, oldBitmap);
            DeleteObject(hBitmap);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static Color ToneColor(string tone) =>
        tone switch
        {
            IntentCursorTones.Attention => Color.FromArgb(37, 99, 235),
            IntentCursorTones.Success => Color.FromArgb(5, 150, 105),
            IntentCursorTones.Warning => Color.FromArgb(217, 119, 6),
            _ => Color.FromArgb(14, 165, 233),
        };

    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int SW_SHOWNOACTIVATE = 4;
    private const int PM_REMOVE = 1;
    private const int ULW_ALPHA = 0x00000002;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative
    {
        public int X;
        public int Y;

        public PointNative(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SizeNative
    {
        public int X;
        public int Y;

        public SizeNative(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDc);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd,
        IntPtr hdcDst,
        ref PointNative pptDst,
        ref SizeNative psize,
        IntPtr hdcSrc,
        ref PointNative pptSrc,
        int crKey,
        ref BlendFunction pblend,
        int dwFlags);
}
