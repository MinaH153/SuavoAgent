using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SuavoAgent.Helper.Companion;
using Serilog;

namespace SuavoAgent.Helper.SystemTray;

/// <summary>
/// Native Windows workplace-monitoring disclosure surface. The Shell owns the
/// visible notification-area affordance, tooltip, keyboard navigation, and
/// accessibility exposure; a standard native menu opens a standard accessible
/// About dialog containing the complete fixed-copy disclosure.
/// </summary>
public sealed class TrayIndicator : IPandaCompanionView
{
    internal const string TooltipText = "SuavoAgent — status unavailable";
    internal const string AboutTitle = "About SuavoAgent observation";

    private const string WindowClassName = "SuavoAgent.Disclosure.TrayWindow";
    private const uint TrayCallbackMessage = WM_APP + 73;
    private const uint RenderMessage = WM_APP + 74;
    private const uint IconId = 1;
    private const uint AboutCommand = 4301;
    private const uint PauseCommand = 4302;
    private const uint ResumeCommand = 4303;
    private const uint StopCommand = 4304;
    // Stable product identity so Explorer can preserve placement/preferences
    // across Helper restarts and expose one consistent accessibility object.
    private static readonly Guid TrayIconGuid = new("7fe16aa8-7426-4e75-b944-241ba197f44e");

    private readonly ILogger _logger;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly WndProc _windowProc;
    private Thread? _trayThread;
    private IntPtr _hwnd;
    private IntPtr _icon;
    private bool _ownsIcon;
    private bool _iconAdded;
    private uint _taskbarCreatedMessage;
    private int _disposed;
    private volatile CompanionPresentation? _presentation;

    public event Action? PauseRequested;
    public event Action? ResumeRequested;
    public event Action? StopRequested;

    public TrayIndicator(ILogger logger)
    {
        _logger = (logger ?? throw new ArgumentNullException(nameof(logger)))
            .ForContext<TrayIndicator>();
        _windowProc = WindowProc;
    }

    public void Start()
    {
        if (!OperatingSystem.IsWindows() ||
            _trayThread is not null ||
            Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _trayThread = new Thread(RunTrayLoop)
        {
            IsBackground = true,
            Name = "SuavoAgent-Disclosure-Tray",
        };
        _trayThread.SetApartmentState(ApartmentState.STA);
        _trayThread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(3)))
            _logger.Warning("Native disclosure tray did not become ready within the startup budget");
    }

    public void Render(CompanionPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        _presentation = presentation;
        var hwnd = _hwnd;
        if (hwnd != IntPtr.Zero)
            PostMessage(hwnd, RenderMessage, IntPtr.Zero, IntPtr.Zero);
    }

    private void RunTrayLoop()
    {
        var instance = GetModuleHandle(null);
        try
        {
            _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
            var windowClass = new WndClassEx
            {
                Size = (uint)Marshal.SizeOf<WndClassEx>(),
                WindowProc = _windowProc,
                Instance = instance,
                ClassName = WindowClassName,
            };

            var atom = RegisterClassEx(ref windowClass);
            if (atom == 0 && Marshal.GetLastWin32Error() != ErrorClassAlreadyExists)
                throw new InvalidOperationException("tray_window_class_registration_failed");

            // A never-shown tool window stays out of Alt-Tab while remaining a
            // valid native owner for the keyboard-accessible popup and About
            // dialog (message-only HWNDs cannot reliably own foreground menus).
            _hwnd = CreateWindowEx(
                WS_EX_TOOLWINDOW,
                WindowClassName,
                "SuavoAgent disclosure",
                0,
                0,
                0,
                0,
                0,
                IntPtr.Zero,
                IntPtr.Zero,
                instance,
                IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
                throw new InvalidOperationException("tray_message_window_creation_failed");

            _icon = CreatePandaIcon(out _ownsIcon);
            if (_icon == IntPtr.Zero)
                throw new InvalidOperationException("tray_icon_creation_failed");

            if (!AddIcon())
                throw new InvalidOperationException("tray_icon_registration_failed");

            _ready.Set();
            _logger.Information("Native disclosure tray is active");

            if (Volatile.Read(ref _disposed) != 0)
            {
                PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }

            while (true)
            {
                var result = GetMessage(out var message, IntPtr.Zero, 0, 0);
                if (result == 0) break;
                if (result < 0)
                {
                    _logger.Warning("Native disclosure tray message loop failed");
                    break;
                }
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception ex)
        {
            // The exception object/message can include mutable desktop or
            // filesystem context. Emit only a stable runtime type.
            _logger.Warning(
                "Native disclosure tray stopped ({ErrorType})",
                ex.GetType().Name);
        }
        finally
        {
            _ready.Set();
            RemoveIcon();

            var hwnd = _hwnd;
            _hwnd = IntPtr.Zero;
            if (hwnd != IntPtr.Zero && IsWindow(hwnd))
                DestroyWindow(hwnd);

            if (_ownsIcon && _icon != IntPtr.Zero)
                DestroyIcon(_icon);
            _icon = IntPtr.Zero;
            _ownsIcon = false;
            UnregisterClass(WindowClassName, instance);
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (_taskbarCreatedMessage != 0 && message == _taskbarCreatedMessage)
            {
                // Explorer restart destroys notification-area state. Re-add
                // from the same fixed identity instead of silently vanishing.
                _iconAdded = false;
                if (!AddIcon())
                    _logger.Warning("Native disclosure tray could not recover after Explorer restart");
                return IntPtr.Zero;
            }

            switch (message)
            {
                case RenderMessage:
                    UpdateIcon();
                    return IntPtr.Zero;
                case TrayCallbackMessage:
                    HandleTrayCallback(hwnd, wParam, lParam);
                    return IntPtr.Zero;
                case WM_COMMAND:
                    if (LowWord(wParam) == AboutCommand)
                    {
                        ShowAbout(hwnd);
                        return IntPtr.Zero;
                    }
                    break;
                case WM_CLOSE:
                    DestroyWindow(hwnd);
                    return IntPtr.Zero;
                case WM_DESTROY:
                    RemoveIcon();
                    _hwnd = IntPtr.Zero;
                    PostQuitMessage(0);
                    return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(
                "Native disclosure tray message failed ({ErrorType})",
                ex.GetType().Name);
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void HandleTrayCallback(IntPtr hwnd, IntPtr wParam, IntPtr lParam)
    {
        var notification = unchecked((uint)(long)lParam) & 0xFFFF;
        if (notification is WM_LBUTTONUP or WM_RBUTTONUP or WM_CONTEXTMENU or NIN_SELECT or NIN_KEYSELECT)
        {
            // NOTIFYICON_VERSION_4 supplies event coordinates in wParam,
            // including keyboard selection. Fall back to the cursor only when
            // an older Shell gives no usable point.
            var point = PointFromVersionFourCallback(wParam);
            ShowMenu(hwnd, point);
        }
    }

    private void ShowMenu(IntPtr hwnd, PointNative? callbackPoint)
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            var presentation = _presentation;
            AppendMenu(menu, MF_STRING | MF_GRAYED, UIntPtr.Zero,
                presentation is null
                    ? "SuavoAgent observation"
                    : $"SuavoAgent — {presentation.Title}");
            if (presentation is not null)
            {
                AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                AppendMenu(menu, MF_STRING | (presentation.CanPause ? 0u : MF_GRAYED),
                    (UIntPtr)PauseCommand, "Pause Autopilot");
                AppendMenu(menu, MF_STRING | (presentation.CanResume ? 0u : MF_GRAYED),
                    (UIntPtr)ResumeCommand, "Resume Autopilot");
                AppendMenu(menu, MF_STRING | (presentation.CanStop ? 0u : MF_GRAYED),
                    (UIntPtr)StopCommand, "Stop Autopilot");
            }
            AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenu(menu, MF_STRING, (UIntPtr)AboutCommand, "About observation…");

            var cursor = callbackPoint ?? default;
            if (callbackPoint is null || (cursor.X == 0 && cursor.Y == 0))
                GetCursorPos(out cursor);
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
                case AboutCommand:
                    ShowAbout(hwnd);
                    break;
                case PauseCommand when presentation?.CanPause == true:
                    PauseRequested?.Invoke();
                    break;
                case ResumeCommand when presentation?.CanResume == true:
                    ResumeRequested?.Invoke();
                    break;
                case StopCommand when presentation?.CanStop == true:
                    StopRequested?.Invoke();
                    break;
            }

            var iconData = CreateNotifyIconData();
            ShellNotifyIcon(NIM_SETFOCUS, ref iconData);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static void ShowAbout(IntPtr owner)
    {
        MessageBox(
            owner,
            GetDisclosureText(),
            AboutTitle,
            MB_OK | MB_ICONINFORMATION | MB_SETFOREGROUND);
    }

    private bool AddIcon()
    {
        if (_hwnd == IntPtr.Zero || _icon == IntPtr.Zero) return false;

        var data = CreateNotifyIconData();
        if (!ShellNotifyIcon(NIM_ADD, ref data)) return false;

        data.TimeoutOrVersion = NOTIFYICON_VERSION_4;
        if (!ShellNotifyIcon(NIM_SETVERSION, ref data))
        {
            ShellNotifyIcon(NIM_DELETE, ref data);
            return false;
        }

        _iconAdded = true;
        return true;
    }

    private void UpdateIcon()
    {
        if (!_iconAdded || _hwnd == IntPtr.Zero) return;
        var data = CreateNotifyIconData();
        if (!ShellNotifyIcon(NIM_MODIFY, ref data))
            _logger.Warning("Native disclosure tray status update failed");
    }

    private void RemoveIcon()
    {
        if (!_iconAdded || _hwnd == IntPtr.Zero) return;
        var data = CreateNotifyIconData();
        ShellNotifyIcon(NIM_DELETE, ref data);
        _iconAdded = false;
    }

    private NotifyIconData CreateNotifyIconData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = _hwnd,
        Id = IconId,
        Flags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_GUID | NIF_SHOWTIP,
        CallbackMessage = TrayCallbackMessage,
        Icon = _icon,
        Tip = CurrentTooltip(),
        Info = string.Empty,
        InfoTitle = string.Empty,
        GuidItem = TrayIconGuid,
    };

    private string CurrentTooltip() => TooltipFor(_presentation);

    /// <summary>
    /// Fixed, PHI-free accessibility text derived only from proved runtime
    /// state. Offline and faulted states never claim observation is active.
    /// </summary>
    internal static string TooltipFor(CompanionPresentation? presentation)
    {
        var value = presentation?.State switch
        {
            CompanionState.Watching => "SuavoAgent — Watching; observation is active",
            CompanionState.Learning => "SuavoAgent — Learning; observation is active",
            CompanionState.Working => "SuavoAgent — Working; Autopilot is active",
            CompanionState.Paused => "SuavoAgent — Paused; observation continues, Autopilot is paused",
            CompanionState.NeedsAttention => "SuavoAgent — Needs attention; Autopilot is stopped",
            CompanionState.Offline => "SuavoAgent — Offline; observation is not active",
            _ => TooltipText,
        };
        return value.Length <= 127 ? value : value[..127];
    }

    private static IntPtr CreatePandaIcon(out bool ownsIcon)
    {
        ownsIcon = false;
        try
        {
            using var stream = typeof(WindowsPandaCompanion).Assembly
                .GetManifestResourceStream(WindowsPandaCompanion.AssetResourceName);
            if (stream is null) return LoadIcon(IntPtr.Zero, IdiApplication);

            using var source = new Bitmap(stream);
            using var target = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(target))
            {
                graphics.Clear(Color.Transparent);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(source, new Rectangle(0, 0, 32, 32));
            }

            var icon = target.GetHicon();
            if (icon == IntPtr.Zero)
                return LoadIcon(IntPtr.Zero, IdiApplication);
            ownsIcon = true;
            return icon;
        }
        catch
        {
            return LoadIcon(IntPtr.Zero, IdiApplication);
        }
    }

    /// <summary>Fixed PHI-free copy shown in the native About dialog.</summary>
    public static string GetDisclosureText() => """
        SuavoAgent Workplace Monitoring Disclosure

        This workstation runs SuavoAgent, a workflow optimization tool installed
        by your employer. SuavoAgent observes the following:

        WHAT IS COLLECTED:
        - Which applications are in use and for how long (app names and durations)
        - Workstation hardware profile (monitor count, RAM, OS version)
        - Login/logout and lock/unlock timing (shift patterns)
        - Browser app focus and whether the authenticated browser connector is
          available. Domain categories are observed only through that connector;
          window titles are never treated as URLs and specific URLs are not collected.
        - Print event counts (NOT document content or names)
        - Spreadsheet file types opened (e.g., "xlsx" — NOT file names or cell content)
        - Coarse keyboard categories and timing while the approved pharmacy app is
          in front (for example, Tab/Enter/letter/digit — NEVER the actual key or text)
        - Structural controls in approved work apps (buttons, fields, grids; visible
          labels are one-way hashed before leaving this computer)

        WHEN ENABLED BY YOUR WORKPLACE:
        - The exact foreground window of the signed, locally approved pharmacy
          application may be captured locally so SuavoAgent can read and verify
          the workflow. Any retained capture is encrypted, access-controlled,
          time-limited, and capture stops if the foreground window, process, or
          signed approval changes.
        - When an authorized user asks SuavoAgent to find a work file, it may
          read spreadsheet headers and a limited local row sample to identify
          the correct file. Raw sample values are not sent as monitoring events.

        WHAT IS NEVER COLLECTED:
        - Passwords, actual keys, or typed text
        - Screenshots of unrelated or personal applications
        - Email content, message text, or chat content
        - Personal browsing history or specific URLs
        - Employee passwords or personal-account content

        Observation events prefer structural or one-way hashed signals. Screen-
        derived text is processed by on-device PHI scrubbing before a frame may
        leave the Helper. Approved pharmacy data used to complete delivery work is
        handled under role, audit, encryption, and retention controls.

        For questions or concerns, contact your workplace administrator.
        """;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var hwnd = _hwnd;
        if (hwnd != IntPtr.Zero)
            PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        var joined = _trayThread?.Join(TimeSpan.FromSeconds(2)) ?? true;
        if (joined) _ready.Dispose();
    }

    private static uint LowWord(IntPtr value) => unchecked((uint)(long)value) & 0xFFFF;

    private static PointNative PointFromVersionFourCallback(IntPtr value)
    {
        var raw = unchecked((long)value);
        return new PointNative
        {
            X = unchecked((short)(raw & 0xFFFF)),
            Y = unchecked((short)((raw >> 16) & 0xFFFF)),
        };
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;
        public uint InfoFlags;
        public Guid GuidItem;
        public IntPtr BalloonIcon;
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
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative
    {
        public int X;
        public int Y;
    }

    private static readonly IntPtr IdiApplication = new(32512);

    private const int ErrorClassAlreadyExists = 1410;
    private const uint WM_NULL = 0x0000;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_CONTEXTMENU = 0x007B;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_APP = 0x8000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint NIN_SELECT = 0x0400;
    private const uint NIN_KEYSELECT = 0x0401;
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIM_SETFOCUS = 0x00000003;
    private const uint NIM_SETVERSION = 0x00000004;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_GUID = 0x00000020;
    private const uint NIF_SHOWTIP = 0x00000080;
    private const uint NOTIFYICON_VERSION_4 = 4;
    private const uint MF_STRING = 0x00000000;
    private const uint MF_GRAYED = 0x00000001;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_NONOTIFY = 0x0080;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONINFORMATION = 0x00000040;
    private const uint MB_SETFOREGROUND = 0x00010000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Message message, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(
        IntPtr menu,
        uint flags,
        int x,
        int y,
        int reserved,
        IntPtr hwnd,
        IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointNative point);

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hwnd, string text, string caption, uint type);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
