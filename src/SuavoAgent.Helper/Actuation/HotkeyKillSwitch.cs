using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Serilog;

namespace SuavoAgent.Helper.Actuation;

/// <summary>
/// Local trump card. <c>Ctrl+Shift+Esc</c> registered globally — when any
/// pharmacist presses it (no matter what app has focus) the agent flips
/// <see cref="ActuationGate.TripKillSwitch"/> and stops actuating until
/// the Helper restarts.
///
/// The pre-build decision (locked 2026-05-02) was BOTH local hotkey AND
/// dashboard ABORT — local trumps cloud. This class is the local half.
/// The cloud-side dashboard ABORT button is post-Queen polish; until that
/// ships, this is the only kill path that works without an internet round
/// trip.
///
/// Threading: like <see cref="UserInputObserver"/>, this requires a
/// thread with a Win32 message pump. RegisterHotKey installs on the
/// calling thread; WM_HOTKEY messages dispatch to the same thread.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HotkeyKillSwitch : IDisposable
{
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_NOREPEAT = 0x4000;

    private const int VK_ESCAPE = 0x1B;

    private const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out NativeMessagePump.MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref NativeMessagePump.MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessagePump.MSG lpMsg);

    private const int HotkeyId = 0xBEEF;
    private const uint PM_REMOVE = 1;

    private readonly ActuationGate _gate;
    private readonly ILogger _logger;
    private readonly bool _requireRegistration;
    private bool _registered;

    public event Action? KillSwitchActivated;

    public HotkeyKillSwitch(ActuationGate gate, ILogger logger, bool requireRegistration = true)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _logger = (logger ?? throw new ArgumentNullException(nameof(logger))).ForContext<HotkeyKillSwitch>();
        _requireRegistration = requireRegistration;
    }

    /// <summary>
    /// Registers the hotkey on the CALLING THREAD and pumps messages until
    /// <paramref name="ct"/> fires. Intended to run on a dedicated long-lived
    /// thread alongside <see cref="UserInputObserver"/>.
    ///
    /// On Windows, <c>Ctrl+Shift+Esc</c> is the system shortcut for Task
    /// Manager. RegisterHotKey will FAIL with ERROR_HOTKEY_ALREADY_REGISTERED
    /// in some configurations. We fall back to <c>Ctrl+Shift+F12</c>; if that
    /// also fails we trip the kill switch ourselves so the operator sees a
    /// clean error in the audit log rather than a silent gap.
    /// </summary>
    public void RunPump(CancellationToken ct)
    {
        if (!RegisterHotKey(IntPtr.Zero, HotkeyId, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_ESCAPE))
        {
            var err = Marshal.GetLastWin32Error();
            _logger.Warning(
                "RegisterHotKey Ctrl+Shift+Esc failed (Win32 err={Err}); falling back to Ctrl+Shift+F12",
                err);

            const int VK_F12 = 0x7B;
            if (!RegisterHotKey(IntPtr.Zero, HotkeyId, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_F12))
            {
                var fallbackErr = Marshal.GetLastWin32Error();
                if (_requireRegistration)
                {
                    _logger.Error(
                        "RegisterHotKey fallback also failed (Win32 err={Err}); kill switch unavailable, tripping gate immediately (RequireKillSwitchHotkey=true, fail-closed)",
                        fallbackErr);
                    _gate.TripKillSwitch("hotkey_register_failed");
                    return;
                }
                _logger.Warning(
                    "RegisterHotKey fallback also failed (Win32 err={Err}); local hotkey unavailable but RequireKillSwitchHotkey=false — gate stays open, dashboard ABORT is the kill path",
                    fallbackErr);
                // Run a no-op pump so the thread stays alive matching prior contract.
                while (!ct.IsCancellationRequested) Thread.Sleep(200);
                return;
            }
        }

        _registered = true;
        _logger.Information("HotkeyKillSwitch armed (Ctrl+Shift+Esc, fallback Ctrl+Shift+F12)");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                {
                    if (msg.message == WM_HOTKEY && (int)msg.wParam == HotkeyId)
                    {
                        _gate.TripKillSwitch("ctrl_shift_esc");
                        try { KillSwitchActivated?.Invoke(); }
                        catch (Exception ex)
                        {
                            _logger.Warning(
                                "KillSwitchActivated handler threw ({ErrorType})",
                                ex.GetType().Name);
                        }
                    }
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
                else
                {
                    Thread.Sleep(20);
                }
            }
        }
        finally
        {
            UnregisterHotKey(IntPtr.Zero, HotkeyId);
            _registered = false;
        }
    }

    public void Dispose()
    {
        if (_registered)
        {
            UnregisterHotKey(IntPtr.Zero, HotkeyId);
            _registered = false;
        }
        GC.SuppressFinalize(this);
    }
}
