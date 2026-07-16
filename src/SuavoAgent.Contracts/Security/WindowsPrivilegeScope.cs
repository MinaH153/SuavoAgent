using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SuavoAgent.Contracts.Security;

/// <summary>
/// Temporarily enables an installer privilege and restores its prior token
/// state. Assigning LocalSystem as an arbitrary owner requires
/// SeRestorePrivilege; backup semantics also needs SeBackupPrivilege to open a
/// legacy object whose old DACL denies the elevated administrator.
/// </summary>
internal sealed class WindowsPrivilegeScope : IDisposable
{
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const int ErrorNotAllAssigned = 1300;

    private IntPtr _token;
    private TokenPrivileges _previous;
    private readonly bool _restore;

    private WindowsPrivilegeScope(IntPtr token, TokenPrivileges previous, bool restore)
    {
        _token = token;
        _previous = previous;
        _restore = restore;
    }

    [SupportedOSPlatform("windows")]
    internal static WindowsPrivilegeScope Enable(string privilege)
    {
        if (!OpenProcessToken(
                GetCurrentProcess(),
                TokenAdjustPrivileges | TokenQuery,
                out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!LookupPrivilegeValueW(null, privilege, out var luid))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var requested = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled,
            };
            if (!AdjustTokenPrivileges(
                    token,
                    disableAllPrivileges: false,
                    ref requested,
                    Marshal.SizeOf<TokenPrivileges>(),
                    out var previous,
                    out var returnedLength))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (Marshal.GetLastWin32Error() == ErrorNotAllAssigned)
                throw new UnauthorizedAccessException(
                    $"Required Windows privilege is unavailable: {privilege}.");
            return new(token, previous, returnedLength != 0);
        }
        catch
        {
            _ = CloseHandle(token);
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    public void Dispose()
    {
        var token = Interlocked.Exchange(ref _token, IntPtr.Zero);
        if (token == IntPtr.Zero) return;
        try
        {
            if (_restore)
            {
                _ = AdjustTokenPrivileges(
                    token,
                    disableAllPrivileges: false,
                    ref _previous,
                    0,
                    out _,
                    out _);
            }
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        internal uint LowPart;
        internal int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        internal uint PrivilegeCount;
        internal Luid Luid;
        internal uint Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        IntPtr process,
        uint desiredAccess,
        out IntPtr token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(
        string? systemName,
        string name,
        out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr token,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState,
        int bufferLength,
        out TokenPrivileges previousState,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
