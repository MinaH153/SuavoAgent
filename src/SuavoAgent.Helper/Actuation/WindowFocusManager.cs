using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Serilog;

namespace SuavoAgent.Helper.Actuation;

/// <summary>
/// Resolves the real top-level window of a freshly-launched sandbox app and forces it to the
/// foreground — robust to Windows packaged (Store) apps where the launch exe is a STUB that hands
/// off to a differently-named process (see <see cref="PackagedAppAliases"/>).
///
/// Resolution order (polled until a window appears or the timeout elapses):
///   1. a process whose name matches the launched exe or a known packaged-app alias AND that owns a
///      VISIBLE main window (covers both fresh launch and single-instance re-activation);
///   2. the launcher's own <see cref="Process.MainWindowHandle"/> (classic Win32 apps);
///   3. any NEW visible, titled top-level window that wasn't present immediately before launch.
///
/// <see cref="ForceForeground"/> uses the AttachThreadInput dance because a background/service
/// thread cannot otherwise steal foreground under Windows' foreground-lock rules — a plain
/// <c>SetForegroundWindow</c> from the Helper silently fails, which is the other half of why the
/// original launch-only focus fix didn't hold.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowFocusManager
{
    public readonly record struct ResolvedWindow(IntPtr Hwnd, int Pid);

    /// <summary>Snapshot of currently-visible top-level windows, taken BEFORE launch so we can
    /// recognise a window that appears afterward.</summary>
    public static HashSet<IntPtr> CaptureVisibleTopLevelWindows()
    {
        var set = new HashSet<IntPtr>();
        try
        {
            EnumWindows((h, _) => { if (IsWindowVisible(h)) set.Add(h); return true; }, IntPtr.Zero);
        }
        catch { /* best-effort snapshot */ }
        return set;
    }

    public static ResolvedWindow? ResolveAppWindow(
        Process? launcher, string processExe, HashSet<IntPtr> preLaunch, int timeoutMs, CancellationToken ct, ILogger log)
    {
        var candidates = PackagedAppAliases.CandidateProcessNames(processExe);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs && !ct.IsCancellationRequested)
        {
            // (1) process-by-name with a visible main window.
            foreach (var name in candidates)
            {
                Process[] procs;
                try { procs = Process.GetProcessesByName(name); }
                catch { continue; }
                try
                {
                    foreach (var pr in procs)
                    {
                        try
                        {
                            var h = pr.MainWindowHandle;
                            if (h != IntPtr.Zero && IsWindowVisible(h))
                                return new ResolvedWindow(h, pr.Id);
                        }
                        catch { /* process may have exited between enumerate + read */ }
                    }
                }
                finally
                {
                    foreach (var pr in procs) pr.Dispose();
                }
            }

            // (2) the launcher's own window (classic Win32 apps that aren't launcher stubs).
            try
            {
                if (launcher is not null)
                {
                    launcher.Refresh();
                    if (!launcher.HasExited && launcher.MainWindowHandle != IntPtr.Zero)
                        return new ResolvedWindow(launcher.MainWindowHandle, launcher.Id);
                }
            }
            catch { /* launcher handle gone */ }

            // (3) any NEW visible, titled top-level window since launch.
            var fresh = FindNewVisibleWindow(preLaunch);
            if (fresh != IntPtr.Zero)
            {
                GetWindowThreadProcessId(fresh, out var pid);
                return new ResolvedWindow(fresh, (int)pid);
            }

            Thread.Sleep(120);
        }
        log.Warning("ResolveAppWindow: no window for '{Exe}' (candidates=[{Cands}]) within {Timeout}ms",
            processExe, string.Join(",", candidates), timeoutMs);
        return null;
    }

    private static IntPtr FindNewVisibleWindow(HashSet<IntPtr> preLaunch)
    {
        var found = IntPtr.Zero;
        try
        {
            EnumWindows((h, _) =>
            {
                if (preLaunch.Contains(h)) return true;
                if (!IsWindowVisible(h)) return true;
                if (GetWindowTextLength(h) == 0) return true; // skip chromeless / utility windows
                found = h;
                return false; // stop enumeration
            }, IntPtr.Zero);
        }
        catch { /* best-effort */ }
        return found;
    }

    /// <summary>
    /// Bring <paramref name="hwnd"/> to the foreground, defeating Windows' foreground lock via
    /// AttachThreadInput. Best-effort: returns whether <c>SetForegroundWindow</c> reported success —
    /// callers still VERIFY with <c>ForegroundGuard.IsPidForeground</c> before trusting it.
    /// </summary>
    public static bool ForceForeground(IntPtr hwnd, ILogger log)
    {
        if (hwnd == IntPtr.Zero) return false;
        var ourThread = GetCurrentThreadId();
        var fgThread = 0u;
        var targetThread = 0u;
        try
        {
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            ShowWindow(hwnd, SW_SHOW);

            var fg = GetForegroundWindow();
            fgThread = GetWindowThreadProcessId(fg, out _);
            targetThread = GetWindowThreadProcessId(hwnd, out _);

            if (fgThread != 0 && fgThread != ourThread) AttachThreadInput(ourThread, fgThread, true);
            if (targetThread != 0 && targetThread != ourThread && targetThread != fgThread)
                AttachThreadInput(ourThread, targetThread, true);

            BringWindowToTop(hwnd);
            // VISUAL Z-ORDER RAISE: SetForegroundWindow + AttachThreadInput set the *logical* foreground
            // (GetForegroundWindow returns hwnd) but from a background process they do NOT reliably raise
            // the window above others in the visible Z-order. Observed live: Notepad reported foreground
            // yet stayed BEHIND a PowerShell window, so the focus-click at its client centre landed on
            // PowerShell — which then stole the foreground (type refused, failed closed). The TOPMOST →
            // NOTOPMOST toggle forces the window to the very top of the Z-order and then drops the
            // always-on-top flag, leaving it genuinely on top of the normal stack so a client-area click
            // lands on it.
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            var ok = SetForegroundWindow(hwnd);
            // While input queues are attached, also set keyboard focus to the window so apps that route
            // focus to a default child control (text editors) become input-ready. No side effect — unlike
            // a content click, SetFocus can't trigger a control. The TYPE path additionally clicks the
            // client area for a stable edit-control focus; this helps the no-click (press_keys) path.
            SetFocus(hwnd);
            return ok;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "ForceForeground failed for hwnd=0x{Hwnd:X}", hwnd.ToInt64());
            return false;
        }
        finally
        {
            try
            {
                if (targetThread != 0 && targetThread != ourThread && targetThread != fgThread)
                    AttachThreadInput(ourThread, targetThread, false);
                if (fgThread != 0 && fgThread != ourThread) AttachThreadInput(ourThread, fgThread, false);
            }
            catch { /* detach is best-effort */ }
        }
    }

    /// <summary>
    /// Screen-coordinate centre of <paramref name="hwnd"/>'s client area, used to place a focus-click
    /// inside the window's content (e.g. a text editor's edit surface) rather than on its chrome.
    /// Returns null if the client rect can't be read or is degenerate.
    /// </summary>
    public static (int X, int Y)? GetClientCenterScreen(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        try
        {
            if (!GetClientRect(hwnd, out var rc)) return null;
            if (rc.Right <= rc.Left || rc.Bottom <= rc.Top) return null;
            var pt = new POINT { X = (rc.Right - rc.Left) / 2, Y = (rc.Bottom - rc.Top) / 2 };
            if (!ClientToScreen(hwnd, ref pt)) return null;
            return (pt.X, pt.Y);
        }
        catch { return null; }
    }

    /// <summary>
    /// Describe the window that currently owns the foreground, for fail-closed diagnostics: when an
    /// actuation verb cannot bring its target to the foreground we want to know WHO won it (the user's
    /// browser? a stale PowerShell? a modal?) — "name the field that blocked you". PHI-safe: returns
    /// the foreground PROCESS NAME + hwnd + title LENGTH only, never the title text (a PMS window title
    /// can carry patient identifiers). Local-log use only; never put this in a cloud-facing reason.
    /// </summary>
    public static string DescribeForegroundWindow()
    {
        try
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return "foreground=<none>";
            GetWindowThreadProcessId(fg, out var pid);
            var titleLen = GetWindowTextLength(fg);
            var proc = "?";
            try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); proc = p.ProcessName; }
            catch { /* process exited / access denied — leave as "?" */ }
            return $"foreground=proc:{proc} pid:{pid} hwnd:0x{fg.ToInt64():X} titleLen:{titleLen}";
        }
        catch { return "foreground=<unavailable>"; }
    }

    // ── Win32 surface ───────────────────────────────────────────────────────
    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    // Z-order raise: HWND_TOPMOST(-1) then HWND_NOTOPMOST(-2) forces the window to the front of the
    // visible stack without leaving it permanently always-on-top.
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
}
