using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Serilog;

namespace SuavoAgent.Helper.Companion;

/// <summary>
/// Small native, always-on-top pharmacist panda. The layered window renders
/// only the embedded asset and the fixed-copy <see cref="CompanionPresentation"/>;
/// it never receives screen pixels, window text, workflow labels, or PHI.
/// Clicking the panda opens explicit Autopilot pause/resume/stop controls.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPandaCompanion : IPandaCompanionView
{
    internal const string AssetResourceName = "SuavoAgent.Helper.Assets.pharmacist-panda-v2.png";

    private const int Width = 300;
    private const int Height = 318;
    private const int Margin = 14;
    private const int RenderMessage = WM_APP + 41;
    private const int PauseCommand = 4101;
    private const int ResumeCommand = 4102;
    private const int StopCommand = 4103;
    private const string WindowClassName = "SuavoAgent.PharmacistPanda.Companion";

    private readonly ILogger _logger;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly WndProc _windowProc;
    private Thread? _thread;
    private IntPtr _hwnd;
    private Bitmap? _panda;
    private volatile CompanionPresentation? _presentation;
    private int _disposed;

    public event Action? PauseRequested;
    public event Action? ResumeRequested;
    public event Action? StopRequested;

    public WindowsPandaCompanion(ILogger logger)
    {
        _logger = (logger ?? throw new ArgumentNullException(nameof(logger)))
            .ForContext<WindowsPandaCompanion>();
        _windowProc = WindowProc;
    }

    public void Start()
    {
        if (!OperatingSystem.IsWindows() || _thread is not null || Volatile.Read(ref _disposed) != 0)
            return;

        _thread = new Thread(WindowLoop)
        {
            IsBackground = true,
            Name = "SuavoAgent-PharmacistPanda",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(3)))
            _logger.Warning("Pharmacist panda window did not become ready within the startup budget");
    }

    public void Render(CompanionPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        _presentation = presentation;
        var hwnd = _hwnd;
        if (hwnd != IntPtr.Zero)
            PostMessage(hwnd, RenderMessage, IntPtr.Zero, IntPtr.Zero);
    }

    private void WindowLoop()
    {
        var instance = GetModuleHandle(null);
        try
        {
            _panda = LoadEmbeddedPanda();
            var windowClass = new WndClassEx
            {
                Size = (uint)Marshal.SizeOf<WndClassEx>(),
                WindowProc = _windowProc,
                Instance = instance,
                Cursor = LoadCursor(IntPtr.Zero, (IntPtr)IDC_HAND),
                ClassName = WindowClassName,
            };

            var atom = RegisterClassEx(ref windowClass);
            if (atom == 0)
            {
                var error = Marshal.GetLastWin32Error();
                // ERROR_CLASS_ALREADY_EXISTS is safe after a quick Helper restart.
                if (error != ERROR_CLASS_ALREADY_EXISTS)
                    throw new InvalidOperationException($"RegisterClassEx failed: {error}");
            }

            _hwnd = CreateWindowEx(
                WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                WindowClassName,
                "SuavoAgent companion",
                WS_POPUP,
                0,
                0,
                Width,
                Height,
                IntPtr.Zero,
                IntPtr.Zero,
                instance,
                IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
                throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");

            _ready.Set();
            if (Volatile.Read(ref _disposed) != 0)
            {
                DestroyWindow(_hwnd);
                return;
            }

            Paint();
            ShowWindow(_hwnd, SW_SHOWNOACTIVATE);

            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception ex)
        {
            // Win32/GDI exception messages can contain mutable file or desktop
            // context. The companion needs only the fixed runtime type for
            // diagnostics; never attach the exception object to a workstation log.
            _logger.Warning(
                "Pharmacist panda companion window stopped ({ErrorType})",
                ex.GetType().Name);
        }
        finally
        {
            _ready.Set();
            var hwnd = _hwnd;
            _hwnd = IntPtr.Zero;
            if (hwnd != IntPtr.Zero && IsWindow(hwnd))
                DestroyWindow(hwnd);
            _panda?.Dispose();
            _panda = null;
            UnregisterClass(WindowClassName, instance);
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (message)
            {
                case RenderMessage:
                    Paint();
                    return IntPtr.Zero;
                case WM_LBUTTONUP:
                case WM_RBUTTONUP:
                case WM_CONTEXTMENU:
                    ShowControlMenu(hwnd);
                    return IntPtr.Zero;
                case WM_SETCURSOR:
                    SetCursor(LoadCursor(IntPtr.Zero, (IntPtr)IDC_HAND));
                    return (IntPtr)1;
                case WM_CLOSE:
                    DestroyWindow(hwnd);
                    return IntPtr.Zero;
                case WM_DESTROY:
                    _hwnd = IntPtr.Zero;
                    PostQuitMessage(0);
                    return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(
                "Pharmacist panda window message failed ({ErrorType})",
                ex.GetType().Name);
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void ShowControlMenu(IntPtr hwnd)
    {
        var presentation = _presentation;
        if (presentation is null) return;

        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenu(menu, MF_STRING | MF_GRAYED, UIntPtr.Zero,
                $"SuavoAgent — {presentation.Title}");
            AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenu(menu, MF_STRING | (presentation.CanPause ? 0u : MF_GRAYED),
                (UIntPtr)PauseCommand, "Pause Autopilot");
            AppendMenu(menu, MF_STRING | (presentation.CanResume ? 0u : MF_GRAYED),
                (UIntPtr)ResumeCommand, "Resume Autopilot");
            AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenu(menu, MF_STRING | (presentation.CanStop ? 0u : MF_GRAYED),
                (UIntPtr)StopCommand, "Stop Autopilot");

            GetCursorPos(out var cursor);
            SetForegroundWindow(hwnd);
            var selected = TrackPopupMenu(
                menu,
                TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON,
                cursor.X,
                cursor.Y,
                0,
                hwnd,
                IntPtr.Zero);
            PostMessage(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            switch (selected)
            {
                case PauseCommand when presentation.CanPause:
                    PauseRequested?.Invoke();
                    break;
                case ResumeCommand when presentation.CanResume:
                    ResumeRequested?.Invoke();
                    break;
                case StopCommand when presentation.CanStop:
                    StopRequested?.Invoke();
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void Paint()
    {
        var hwnd = _hwnd;
        var panda = _panda;
        var presentation = _presentation;
        if (hwnd == IntPtr.Zero || panda is null || presentation is null) return;

        using var canvas = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(canvas))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            // A soft sapphire halo keeps the transparent character legible over
            // both light and dark desktops without covering workspace content.
            using (var halo = new SolidBrush(Color.FromArgb(42, 37, 99, 235)))
                graphics.FillEllipse(halo, 56, 14, 188, 218);
            graphics.DrawImage(panda, new Rectangle(60, -4, 180, 270));

            var card = new Rectangle(10, 218, Width - 20, 90);
            using (var shadowPath = RoundedRectangle(new Rectangle(card.X + 2, card.Y + 3, card.Width, card.Height), 18))
            using (var shadow = new SolidBrush(Color.FromArgb(72, 0, 0, 0)))
                graphics.FillPath(shadow, shadowPath);
            using (var cardPath = RoundedRectangle(card, 18))
            using (var cardFill = new SolidBrush(Color.FromArgb(242, 15, 23, 42)))
                graphics.FillPath(cardFill, cardPath);

            var accent = AccentColor(presentation.State);
            using (var accentBrush = new SolidBrush(accent))
                graphics.FillEllipse(accentBrush, card.X + 14, card.Y + 15, 10, 10);

            using var titleFont = new Font("Segoe UI Semibold", 11.5f, FontStyle.Bold, GraphicsUnit.Point);
            using var statusFont = new Font("Segoe UI", 8.25f, FontStyle.Regular, GraphicsUnit.Point);
            using var footerFont = new Font("Segoe UI", 7.25f, FontStyle.Regular, GraphicsUnit.Point);
            using var titleBrush = new SolidBrush(Color.FromArgb(255, 248, 250, 252));
            using var statusBrush = new SolidBrush(Color.FromArgb(235, 203, 213, 225));
            using var footerBrush = new SolidBrush(Color.FromArgb(215, 147, 197, 253));

            graphics.DrawString(
                presentation.Title,
                titleFont,
                titleBrush,
                new RectangleF(card.X + 31, card.Y + 9, card.Width - 42, 23),
                new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
            graphics.DrawString(
                presentation.Status,
                statusFont,
                statusBrush,
                new RectangleF(card.X + 14, card.Y + 34, card.Width - 28, 34),
                new StringFormat { Trimming = StringTrimming.EllipsisWord });
            graphics.DrawString(
                "Click for controls  •  Stop is immediate",
                footerFont,
                footerBrush,
                new RectangleF(card.X + 14, card.Bottom - 21, card.Width - 28, 15),
                new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
        }

        PositionAndBlend(hwnd, canvas);
    }

    private static Bitmap LoadEmbeddedPanda()
    {
        using var stream = typeof(WindowsPandaCompanion).Assembly
            .GetManifestResourceStream(AssetResourceName)
            ?? throw new InvalidOperationException($"Embedded companion asset missing: {AssetResourceName}");
        using var decoded = new Bitmap(stream);
        return new Bitmap(decoded);
    }

    private static void PositionAndBlend(IntPtr hwnd, Bitmap canvas)
    {
        var workArea = new Rect();
        if (!SystemParametersInfo(SPI_GETWORKAREA, 0, ref workArea, 0))
        {
            workArea = new Rect
            {
                Left = 0,
                Top = 0,
                Right = GetSystemMetrics(SM_CXSCREEN),
                Bottom = GetSystemMetrics(SM_CYSCREEN),
            };
        }

        var destination = new PointNative(
            Math.Max(workArea.Left, workArea.Right - Width - Margin),
            Math.Max(workArea.Top, workArea.Bottom - Height - Margin));
        var size = new SizeNative(Width, Height);
        var source = new PointNative(0, 0);
        var blend = new BlendFunction
        {
            BlendOp = AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = AC_SRC_ALPHA,
        };

        var screenDc = GetDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        var bitmap = canvas.GetHbitmap(Color.FromArgb(0));
        var previous = SelectObject(memoryDc, bitmap);
        try
        {
            UpdateLayeredWindow(
                hwnd,
                screenDc,
                ref destination,
                ref size,
                memoryDc,
                ref source,
                0,
                ref blend,
                ULW_ALPHA);
        }
        finally
        {
            SelectObject(memoryDc, previous);
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static GraphicsPath RoundedRectangle(Rectangle rectangle, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Color AccentColor(CompanionState state) => state switch
    {
        CompanionState.Watching => Color.FromArgb(255, 37, 99, 235),
        CompanionState.Learning => Color.FromArgb(255, 34, 197, 94),
        CompanionState.Working => Color.FromArgb(255, 96, 165, 250),
        CompanionState.Paused => Color.FromArgb(255, 148, 163, 184),
        CompanionState.NeedsAttention => Color.FromArgb(255, 185, 28, 28),
        CompanionState.Offline => Color.FromArgb(255, 71, 85, 105),
        _ => Color.FromArgb(255, 71, 85, 105),
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var hwnd = _hwnd;
        if (hwnd != IntPtr.Zero)
            PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(TimeSpan.FromSeconds(2));
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public WndProc WindowProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint Value;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public PointNative Point;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative
    {
        public int X;
        public int Y;
        public PointNative(int x, int y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SizeNative
    {
        public int X;
        public int Y;
        public SizeNative(int x, int y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int SW_SHOWNOACTIVATE = 4;
    private const int ULW_ALPHA = 0x00000002;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;
    private const int WM_NULL = 0x0000;
    private const int WM_CLOSE = 0x0010;
    private const int WM_DESTROY = 0x0002;
    private const int WM_SETCURSOR = 0x0020;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_APP = 0x8000;
    private const uint MF_STRING = 0x00000000;
    private const uint MF_GRAYED = 0x00000001;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_NONOTIFY = 0x0080;
    private const uint TPM_RETURNCMD = 0x0100;
    private const int SPI_GETWORKAREA = 0x0030;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int IDC_HAND = 32649;
    private const int ERROR_CLASS_ALREADY_EXISTS = 1410;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int extendedStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Message message, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr itemId, string? text);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(
        IntPtr menu,
        uint flags,
        int x,
        int y,
        int reserved,
        IntPtr hwnd,
        IntPtr rectangle);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointNative point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, ref Rect value, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr value);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd,
        IntPtr destinationDc,
        ref PointNative destination,
        ref SizeNative size,
        IntPtr sourceDc,
        ref PointNative source,
        int colorKey,
        ref BlendFunction blend,
        int flags);
}
