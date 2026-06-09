using System.Runtime.InteropServices;
using System.Text;
using SuavoAgent.Contracts.Ipc;

namespace SuavoAgent.Helper.Actuation;

/// <summary>
/// Resolves the EFFECTIVE sandbox-app process behind a window — drilling the Windows 11 UWP host case
/// where the VISIBLE top-level window is owned by <c>ApplicationFrameHost</c> and the real app (e.g.
/// <c>CalculatorApp</c>) owns a child <c>Windows.UI.Core.CoreWindow</c>. The sandbox capture path uses this
/// so the allowlist / foreground / TOCTOU checks see the REAL app, not the AFH frame — otherwise UWP apps
/// (Win11 Calculator) are wrongly refused because "ApplicationFrameHost" isn't an allowlisted process.
///
/// Win32-touching members no-op off Windows (runtime-guarded). The pure allowlist-name matcher
/// (<see cref="IsAllowlistedSandboxProcess"/>) is platform-independent and unit-tested on the Linux CI gate.
/// </summary>
public static class SandboxWindowResolver
{
    private const string ApplicationFrameHost = "ApplicationFrameHost";
    private const string CoreWindowClass = "Windows.UI.Core.CoreWindow";

    /// <summary>
    /// PID of the real application owning/behind <paramref name="hwnd"/>. Classic Win32 window ⇒ the
    /// window's owner PID; UWP window hosted by ApplicationFrameHost ⇒ the PID of the child CoreWindow
    /// (the actual app). Returns 0 on non-Windows / error / unresolved.
    /// </summary>
    public static int EffectiveAppPid(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindows()) return 0;
        GetWindowThreadProcessId(hwnd, out var framePid);
        if (framePid == 0) return 0;

        string? frameProc = null;
        try { using var p = System.Diagnostics.Process.GetProcessById((int)framePid); frameProc = p.ProcessName; }
        catch { /* process gone between query + read */ }

        // Classic Win32 app (incl. Win11 Store Notepad, whose process is still "Notepad"): frame IS the app.
        if (!string.Equals(frameProc, ApplicationFrameHost, StringComparison.OrdinalIgnoreCase))
            return (int)framePid;

        // UWP: the real app owns a child Windows.UI.Core.CoreWindow under the AFH frame.
        uint corePid = 0;
        try
        {
            EnumChildWindows(hwnd, (child, _) =>
            {
                if (IsCoreWindow(child))
                {
                    GetWindowThreadProcessId(child, out var cp);
                    if (cp != 0 && cp != framePid) { corePid = cp; return false; } // found the app → stop
                }
                return true;
            }, IntPtr.Zero);
        }
        catch { /* best-effort enumeration */ }

        return corePid != 0 ? (int)corePid : (int)framePid;
    }

    /// <summary>
    /// True iff the CURRENT foreground app is the effective sandbox app (UWP-robust: for a UWP app the
    /// foreground HWND is the AFH frame, so we compare the EFFECTIVE app PID, not the frame owner).
    /// </summary>
    public static bool IsSandboxAppForeground(int effectiveAppPid)
    {
        if (effectiveAppPid <= 0 || !OperatingSystem.IsWindows()) return false;
        var fg = GetForegroundWindow();
        return fg != IntPtr.Zero && EffectiveAppPid(fg) == effectiveAppPid;
    }

    /// <summary>
    /// True iff <paramref name="processName"/> (image base name, no .exe) is an allowlisted sandbox app OR a
    /// known packaged alias of one (e.g. "CalculatorApp"/"Calculator" for the calc launcher). Pure data
    /// lookup over <see cref="ActuationAllowlistedSandboxApps.ProcessNames"/> × <see cref="PackagedAppAliases"/>.
    /// </summary>
    public static bool IsAllowlistedSandboxProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        foreach (var kv in ActuationAllowlistedSandboxApps.ProcessNames)
        {
            if (string.Equals(kv.Key, processName, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var cand in PackagedAppAliases.CandidateProcessNames(kv.Value))
                if (string.Equals(cand, processName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool IsCoreWindow(IntPtr hwnd)
    {
        var sb = new StringBuilder(64);
        var len = GetClassName(hwnd, sb, sb.Capacity);
        return len > 0 && string.Equals(sb.ToString(), CoreWindowClass, StringComparison.Ordinal);
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
