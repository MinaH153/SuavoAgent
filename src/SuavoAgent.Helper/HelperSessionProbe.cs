using System.Diagnostics;
using System.Runtime.InteropServices;
using SuavoAgent.Contracts.Ipc;

namespace SuavoAgent.Helper;

/// <summary>
/// Reports which Windows session the Helper process is running in so Core's pricing
/// pre-flight can confirm — at probe time — that the Helper is in the interactive
/// console session and can therefore see and drive the screen. A Helper that ends up
/// in Session 0 (the non-interactive services session) is blind: actuation silently
/// goes nowhere. This is the authoritative signal the pre-flight gate checks.
/// </summary>
internal static class HelperSessionProbe
{
    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    public static HelperPingInfo Current()
    {
        var pid = Environment.ProcessId;
        var helperSession = GetProcessSessionId();
        var activeConsole = GetActiveConsoleSessionId();
        // Interactive = the Helper shares the active physical console session AND it isn't
        // Session 0 (services session). Both conditions matter: a non-zero session that no
        // longer matches the active console (e.g. after fast-user-switch) is also not drivable.
        var isInteractive = helperSession != 0 && helperSession == activeConsole;
        return new HelperPingInfo(pid, helperSession, activeConsole, isInteractive);
    }

    private static uint GetActiveConsoleSessionId()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        try { return WTSGetActiveConsoleSessionId(); }
        catch { return 0; }
    }

    private static uint GetProcessSessionId()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        try { return (uint)Process.GetCurrentProcess().SessionId; }
        catch { return 0; }
    }
}
