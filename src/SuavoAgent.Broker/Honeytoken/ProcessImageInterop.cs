using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace SuavoAgent.Broker.Honeytoken;

/// <summary>
/// Cross-token PID → full-exe-path resolver. 3rd copy of the existing idiom (Helper/IpcCommandServer.cs +
/// Core/Ipc/IpcPipeServer.cs) — flagged for a future Contracts/Core extraction. The Broker runs as LocalSystem,
/// so <c>OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)</c> succeeds on SYSTEM- and user-token holders without
/// PROCESS_VM_READ. A null result (exited / access-denied) flows to the corroborator as an unknown toucher →
/// reversible Degrade, never apoptosis.
/// </summary>
internal static class ProcessImageInterop
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, [Out] StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr hProcess, out long lpCreationTime, out long lpExitTime, out long lpKernelTime, out long lpUserTime);

    /// <summary>
    /// Resolve the image path for <paramref name="processId"/>, but ONLY if the process's creation time is a
    /// plausible issuer of an event at <paramref name="eventUtc"/> — i.e. it started at-or-before the event
    /// (+ a small clock tolerance). This defeats the PID-REUSE race: if the original (benign) toucher exited
    /// and Windows recycled its PID for a shell before the ETW callback ran, the recycled process started
    /// AFTER the event and is rejected (→ null → unknown toucher → reversible Degrade, never apoptosis on an
    /// innocent process). Returns null on exited/access-denied/unresolvable too.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string? Get(uint processId, DateTimeOffset eventUtc, TimeSpan tolerance)
    {
        var hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (hProcess == IntPtr.Zero) return null;
        try
        {
            // Validate start time FIRST — a reused PID must not even have its image read into an attribution.
            if (!GetProcessTimes(hProcess, out var creationFt, out _, out _, out _)) return null;
            var creationUtc = new DateTimeOffset(DateTime.FromFileTimeUtc(creationFt));
            if (!IsPlausibleIssuer(creationUtc, eventUtc, tolerance)) return null;

            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageNameW(hProcess, 0, sb, ref size) ? sb.ToString() : null;
        }
        catch
        {
            return null; // can't validate → unknown toucher → Degrade (the safe direction)
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    /// <summary>Pure, headless-testable: a legit issuer started at-or-before the event (+ tolerance for the
    /// ETW-timestamp vs FILETIME clock-source granularity). A recycled PID's new process starts strictly after
    /// the original exits (after the event), so it fails this.</summary>
    internal static bool IsPlausibleIssuer(DateTimeOffset creationUtc, DateTimeOffset eventUtc, TimeSpan tolerance)
        => creationUtc <= eventUtc + tolerance;
}
