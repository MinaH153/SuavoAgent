using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;
using SuavoAgent.Contracts.Behavioral;

namespace SuavoAgent.Helper.Behavioral;

/// <summary>
/// WH_KEYBOARD_LL hook that captures keystroke categories only.
/// VK code is classified and immediately discarded — never stored.
/// Only fires when PMS process has foreground focus.
/// </summary>
public sealed class KeyboardCategoryHook : IDisposable
{
    // ── P/Invoke ─────────────────────────────────────────────────────────────

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    private delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    // ── Coalesce thresholds ───────────────────────────────────────────────────

    private static readonly TimeSpan RapidThreshold = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PauseThreshold = TimeSpan.FromSeconds(2);

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly BehavioralEventBuffer _buffer;
    private readonly ILogger _logger;
    private readonly int _pmsProcessId;

    private nint _hookHandle = nint.Zero;
    private LowLevelKeyboardProc? _hookCallback; // keep alive — GC must not collect delegate

    private KeystrokeCategory _currentCategory;
    private int _currentCount;
    private DateTimeOffset _lastKeystrokeTime;
    private bool _hasSequence;

    private readonly object _sequenceLock = new();

    private bool _disposed;

    public KeyboardCategoryHook(
        BehavioralEventBuffer buffer,
        ILogger logger,
        int pmsProcessId)
    {
        _buffer = buffer;
        _logger = logger.ForContext<KeyboardCategoryHook>();
        _pmsProcessId = pmsProcessId;
    }

    /// <summary>
    /// Installs WH_KEYBOARD_LL. No-op on non-Windows platforms.
    /// </summary>
    public void Install()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _logger.Debug("KeyboardCategoryHook: skipping install (not Windows)");
            return;
        }

        if (_hookHandle != nint.Zero)
        {
            _logger.Warning("KeyboardCategoryHook: already installed");
            return;
        }

        _hookCallback = HookCallback;

        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        var hMod = GetModuleHandle(curModule.ModuleName);

        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookCallback, hMod, 0);

        if (_hookHandle == nint.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            _logger.Error("KeyboardCategoryHook: SetWindowsHookEx failed, error={Error}", err);
            return;
        }

        _logger.Information("KeyboardCategoryHook: installed for PMS PID {Pid}", _pmsProcessId);
    }

    /// <summary>
    /// Uninstalls the hook and flushes any pending sequence.
    /// Called on focus loss and shutdown.
    /// </summary>
    public void Uninstall()
    {
        FlushCurrentSequence();

        if (_hookHandle == nint.Zero) return;

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        if (!UnhookWindowsHookEx(_hookHandle))
        {
            var err = Marshal.GetLastWin32Error();
            _logger.Warning("KeyboardCategoryHook: UnhookWindowsHookEx failed, error={Error}", err);
        }

        _hookHandle = nint.Zero;
        _logger.Information("KeyboardCategoryHook: uninstalled");
    }

    /// <summary>
    /// Emits the current coalesced sequence to the buffer (if any).
    /// </summary>
    public void FlushCurrentSequence()
    {
        lock (_sequenceLock)
        {
            FlushLocked();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Uninstall();
    }

    // ── VK Classification ─────────────────────────────────────────────────────

    /// <summary>
    /// Classifies a Windows Virtual Key code to a HIPAA-safe keystroke category.
    /// The VK code is not stored after this call.
    /// </summary>
    public static KeystrokeCategory ClassifyVkCode(int vkCode) => vkCode switch
    {
        >= 0x41 and <= 0x5A => KeystrokeCategory.Alpha,              // A–Z
        >= 0x30 and <= 0x39 => KeystrokeCategory.Digit,              // 0–9 (top row)
        >= 0x60 and <= 0x69 => KeystrokeCategory.Digit,              // numpad 0–9
        0x09                => KeystrokeCategory.Tab,
        0x0D                => KeystrokeCategory.Enter,
        0x1B                => KeystrokeCategory.Escape,
        >= 0x70 and <= 0x87 => KeystrokeCategory.FunctionKey,        // F1–F24
        >= 0x25 and <= 0x28 => KeystrokeCategory.Navigation,         // arrow keys
        >= 0x21 and <= 0x24 => KeystrokeCategory.Navigation,         // PgUp/PgDn/Home/End
        >= 0x10 and <= 0x12 => KeystrokeCategory.Modifier,           // Shift/Ctrl/Alt
        >= 0xA0 and <= 0xA5 => KeystrokeCategory.Modifier,           // L/R Shift/Ctrl/Alt
        _                   => KeystrokeCategory.Other
    };

    // ── Hook callback ─────────────────────────────────────────────────────────

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0
            && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN)
            && IsPmsForeground())
        {
            try
            {
                var kbStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var vkCode = (int)kbStruct.vkCode;

                // Classify and immediately discard VK code
                var category = ClassifyVkCode(vkCode);

                ProcessKeystroke(category);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "KeyboardCategoryHook: callback error");
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private bool IsPmsForeground()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == nint.Zero) return false;

            GetWindowThreadProcessId(hwnd, out var pid);
            return (int)pid == _pmsProcessId;
        }
        catch
        {
            return false;
        }
    }

    private void ProcessKeystroke(KeystrokeCategory category)
    {
        var now = DateTimeOffset.UtcNow;

        lock (_sequenceLock)
        {
            if (!_hasSequence)
            {
                // Start fresh sequence
                _currentCategory = category;
                _currentCount = 1;
                _lastKeystrokeTime = now;
                _hasSequence = true;
                return;
            }

            var elapsed = now - _lastKeystrokeTime;
            var timing = ClassifyTiming(elapsed);

            // Flush when category changes OR timing shifts out of Rapid
            if (category != _currentCategory || timing != TimingBucket.Rapid)
            {
                FlushLocked();

                // Start new sequence
                _currentCategory = category;
                _currentCount = 1;
                _lastKeystrokeTime = now;
                _hasSequence = true;
            }
            else
            {
                // Coalesce — same category, still Rapid
                _currentCount++;
                _lastKeystrokeTime = now;
            }
        }
    }

    private static TimingBucket ClassifyTiming(TimeSpan elapsed)
    {
        if (elapsed < RapidThreshold) return TimingBucket.Rapid;
        if (elapsed < PauseThreshold) return TimingBucket.Normal;
        return TimingBucket.Pause;
    }

    /// <summary>Must be called under _sequenceLock.</summary>
    private void FlushLocked()
    {
        if (!_hasSequence) return;

        // Determine timing bucket for the flushed sequence using the gap since last keystroke
        var elapsed = DateTimeOffset.UtcNow - _lastKeystrokeTime;
        var timing = _currentCount == 1
            ? TimingBucket.Normal // single keystroke — use Normal as default bucket
            : ClassifyTiming(elapsed);

        // BAA clause: digit sequences capped at 3 to prevent identifier reconstruction
        var reportCount = (_currentCategory == KeystrokeCategory.Digit && _currentCount > 3)
            ? 3 : _currentCount;
        var ev = BehavioralEvent.Keystroke(_currentCategory, timing, reportCount);
        _buffer.Enqueue(ev);

        _hasSequence = false;
        _currentCount = 0;
    }
}
