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
            var ok = SetForegroundWindow(hwnd);
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

    // ── Win32 surface ───────────────────────────────────────────────────────
    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

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
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
