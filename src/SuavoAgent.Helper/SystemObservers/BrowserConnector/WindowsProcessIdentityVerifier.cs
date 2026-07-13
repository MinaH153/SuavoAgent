using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace SuavoAgent.Helper.SystemObservers.BrowserConnector;

internal readonly record struct WindowsProcessIdentityEvidence(
    string UserSid,
    uint SessionId);

/// <summary>
/// Kernel-token and session comparison shared by local browser transports.
/// Missing process, token, SID, or session evidence always fails closed.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsProcessIdentityVerifier
{
    public static bool TryGetCurrent(out WindowsProcessIdentityEvidence identity) =>
        TryGet((uint)Environment.ProcessId, out identity);

    public static bool MatchesCurrent(uint processId) =>
        TryGetCurrent(out var current) && Matches(processId, current);

    public static bool Matches(
        uint processId,
        WindowsProcessIdentityEvidence expected) =>
        processId != 0 &&
        expected.UserSid.Length > 0 &&
        TryGet(processId, out var observed) &&
        observed.SessionId == expected.SessionId &&
        string.Equals(
            observed.UserSid,
            expected.UserSid,
            StringComparison.Ordinal);

    private static bool TryGet(
        uint processId,
        out WindowsProcessIdentityEvidence identity)
    {
        identity = default;
        if (processId == 0 ||
            !ProcessIdToSessionId(processId, out var sessionId) ||
            !TryGetUserSid(processId, out var userSid))
        {
            return false;
        }

        identity = new WindowsProcessIdentityEvidence(userSid, sessionId);
        return true;
    }

    private static bool TryGetUserSid(uint processId, out string sid)
    {
        sid = string.Empty;
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == nint.Zero)
            return false;
        nint token = nint.Zero;
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out token) || token == nint.Zero)
                return false;
            using var identity = new WindowsIdentity(token);
            sid = identity.User?.Value ?? string.Empty;
            return sid.Length > 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (token != nint.Zero)
                _ = CloseHandle(token);
            _ = CloseHandle(process);
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(
        uint processId,
        out uint sessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        nint processHandle,
        uint desiredAccess,
        out nint tokenHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
