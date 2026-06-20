using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Serilog;

namespace SuavoAgent.Helper.Presence;

/// <summary>Global hotkey (Ctrl+Alt+H) that instantly toggles cursor visibility,
/// cloud-independent — the over-the-shoulder panic-hide. Own thread + message pump.</summary>
[SupportedOSPlatform("windows")]
public sealed class PresenceHotkeyListener : IDisposable
{
    private const int HotkeyId = 0x5A01;          // distinct from HotkeyKillSwitch
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    private const uint VK_H = 0x48, WM_HOTKEY = 0x312, WM_QUIT = 0x12;

    private readonly PresencePreferenceStore _store;
    private readonly ILogger _logger;
    private Thread? _thread;
    private uint _threadId;

    public PresenceHotkeyListener(PresencePreferenceStore store, ILogger logger)
    {
        _store = store;
        _logger = logger.ForContext<PresenceHotkeyListener>();
    }

    public void Start()
    {
        if (!OperatingSystem.IsWindows() || _thread is not null) return;
        _thread = new Thread(Loop) { IsBackground = true };
        _thread.Start();
    }

    private void Loop()
    {
        _threadId = GetCurrentThreadId();
        if (!RegisterHotKey(IntPtr.Zero, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_H))
        {
            _logger.Warning("Presence: failed to register hide hotkey (Ctrl+Alt+H): {Err}", Marshal.GetLastWin32Error());
            return;
        }
        _logger.Information("Presence: hide hotkey registered (Ctrl+Alt+H)");
        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_HOTKEY && (int)msg.wParam == HotkeyId)
                {
                    var next = !_store.Current.CursorVisible;
                    _store.SetVisible(next);
                    _logger.Information("Presence: hotkey toggled cursor visible={Visible}", next);
                }
            }
        }
        finally { UnregisterHotKey(IntPtr.Zero, HotkeyId); }
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(500);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int ptX, ptY; }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
