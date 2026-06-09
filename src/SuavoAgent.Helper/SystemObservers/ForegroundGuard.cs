using System.Runtime.InteropServices;

namespace SuavoAgent.Helper.SystemObservers;

/// <summary>
/// Cheap synchronous "is process X currently foreground" probe. Wraps the
/// Win32 GetForegroundWindow + GetWindowThreadProcessId pair.
///
/// Used by the IPC capture-screen handler as a HIPAA gate: when the user has
/// alt-tabbed away from the PMS, screen captures must NOT fire because the
/// foreground window could contain personal browsing, email, banking, etc.
/// This is a hard requirement separate from the master Vision.Enabled flag —
/// Vision-Enabled means "captures are allowed when conditions are right",
/// foreground match is one of those conditions.
/// </summary>
public static class ForegroundGuard
{
    /// <summary>
    /// Returns true iff <paramref name="expectedPid"/> matches the current
    /// foreground window's owning process. Returns false on any Win32
    /// error (defensive: better to refuse a capture than mis-attribute).
    /// </summary>
    public static bool IsPidForeground(int expectedPid)
    {
        if (expectedPid <= 0) return false;
        if (!OperatingSystem.IsWindows()) return false;

        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;

        GetWindowThreadProcessId(hwnd, out var fgPid);
        return fgPid != 0 && (int)fgPid == expectedPid;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
