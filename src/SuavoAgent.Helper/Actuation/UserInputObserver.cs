using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Serilog;

namespace SuavoAgent.Helper.Actuation;

/// <summary>
/// Installs WH_KEYBOARD_LL + WH_MOUSE_LL low-level hooks so the agent can
/// detect the pharmacist actually using the keyboard/mouse and back off
/// for 5 minutes (configurable via
/// <see cref="ActuationConfig.UserInputPauseWindow"/>).
///
/// Implementation note — this is a SAFETY OBSERVER, not telemetry. The
/// hook callbacks must run fast (Win32 SetWindowsHookEx times out after
/// ~300ms). We never inspect key codes, never log content, never even
/// classify by category. Just bump <see cref="ActuationGate.NotifyUserInputDetected"/>
/// and return.
///
/// One subtle bug class: SendInput calls we make ourselves WILL re-enter
/// these hooks. We filter our own injections by checking
/// <c>LLKHF_INJECTED</c> / <c>LLMHF_INJECTED</c> in the hook struct flags.
/// Without that filter, the agent would pause itself on every keystroke
/// it types. CrowdStrike-class bug if missed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UserInputObserver : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;

    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_MOUSEWHEEL = 0x020A;

    private const uint LLKHF_INJECTED = 0x00000010;
    private const uint LLMHF_INJECTED = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public int x;
        public int y;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? name);

    private readonly ActuationGate _gate;
    private readonly ILogger _logger;
    private readonly TimeSpan _coalesceWindow;
    // Optional cheap callback for the presence layer (mode flip → Observing). Must do near-nothing
    // (an interlocked timestamp write) — this fires on the low-level hook path.
    private readonly Action? _onUserInput;

    private DateTimeOffset _lastNotifyUtc = DateTimeOffset.MinValue;

    private LowLevelProc? _keyboardCallback;
    private LowLevelProc? _mouseCallback;
    private IntPtr _keyboardHook = IntPtr.Zero;
    private IntPtr _mouseHook = IntPtr.Zero;
    private bool _disposed;

    public UserInputObserver(ActuationGate gate, ILogger logger, TimeSpan? coalesceWindow = null, Action? onUserInput = null)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _logger = (logger ?? throw new ArgumentNullException(nameof(logger))).ForContext<UserInputObserver>();
        _coalesceWindow = coalesceWindow ?? TimeSpan.FromSeconds(2);
        _onUserInput = onUserInput;
    }

    /// <summary>
    /// Installs both hooks on the calling thread. Caller must keep a
    /// message pump running on this thread (use
    /// <see cref="NativeMessagePump"/>) for the callbacks to fire.
    /// </summary>
    public void Install()
    {
        if (_keyboardHook != IntPtr.Zero || _mouseHook != IntPtr.Zero)
        {
            throw new InvalidOperationException("UserInputObserver already installed");
        }

        _keyboardCallback = OnKeyboardEvent;
        _mouseCallback = OnMouseEvent;

        var hMod = GetModuleHandle(null);
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardCallback, hMod, 0);
        if (_keyboardHook == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"SetWindowsHookEx WH_KEYBOARD_LL failed: {Marshal.GetLastWin32Error()}");
        }

        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseCallback, hMod, 0);
        if (_mouseHook == IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
            throw new InvalidOperationException(
                $"SetWindowsHookEx WH_MOUSE_LL failed: {Marshal.GetLastWin32Error()}");
        }

        _logger.Information("UserInputObserver installed: WH_KEYBOARD_LL + WH_MOUSE_LL");
    }

    public void Uninstall()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        _logger.Information("UserInputObserver uninstalled");
    }

    private IntPtr OnKeyboardEvent(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        var msg = (int)wParam;
        if (msg != WM_KEYDOWN && msg != WM_SYSKEYDOWN)
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        try
        {
            var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if ((hookStruct.flags & LLKHF_INJECTED) != 0)
            {
                // Don't pause on our own SendInput-injected keys.
                return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
            }
            NotifyCoalesced("keyboard");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "OnKeyboardEvent failed (continuing)");
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr OnMouseEvent(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        var msg = (int)wParam;
        // Mouse moves alone are noisy; only count clicks + wheel as "user activity".
        // Bare moves still bump the coalesced notify because operators expect "I
        // touched my mouse so the agent paused", but we coalesce on a 2-second
        // window so we don't flood.
        if (msg != WM_LBUTTONDOWN && msg != WM_RBUTTONDOWN && msg != WM_MBUTTONDOWN
            && msg != WM_MOUSEWHEEL && msg != WM_MOUSEMOVE)
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        try
        {
            var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if ((hookStruct.flags & LLMHF_INJECTED) != 0)
            {
                return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }
            NotifyCoalesced("mouse");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "OnMouseEvent failed (continuing)");
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void NotifyCoalesced(string source)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastNotifyUtc < _coalesceWindow) return;
        _lastNotifyUtc = now;
        _gate.NotifyUserInputDetected(source);
        try { _onUserInput?.Invoke(); } catch { /* presence is cosmetic — never break the safety observer */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Uninstall();
        GC.SuppressFinalize(this);
    }
}
