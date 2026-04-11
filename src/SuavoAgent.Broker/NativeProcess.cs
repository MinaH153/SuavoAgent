using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace SuavoAgent.Broker;

/// <summary>
/// Launches a process in an interactive user session from a service context.
/// Uses WTSQueryUserToken + DuplicateTokenEx + CreateProcessAsUser.
/// Requires the calling service to have SeAssignPrimaryTokenPrivilege
/// (granted to NetworkService by default when running as a Windows service).
/// </summary>
[SupportedOSPlatform("windows")]
public static class NativeProcess
{
    public static int? LaunchInSession(uint sessionId, string exePath, string arguments, ILogger logger)
    {
        nint userToken = 0;
        nint duplicateToken = 0;
        nint environment = 0;

        try
        {
            // 1. Get the user's token for the target session
            if (!WTSQueryUserToken(sessionId, out userToken))
            {
                var err = Marshal.GetLastWin32Error();
                // ERROR_NO_TOKEN (1008) = no user logged into this session
                if (err == 1008)
                {
                    logger.LogDebug("Session {Session} has no logged-in user", sessionId);
                    return null;
                }
                logger.LogWarning("WTSQueryUserToken failed for session {Session}: Win32 error {Error}",
                    sessionId, err);
                return null;
            }

            // 2. Duplicate as a primary token (required for CreateProcessAsUser)
            var sa = new SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                bInheritHandle = false
            };
            if (!DuplicateTokenEx(userToken, 0x02000000 /* MAXIMUM_ALLOWED */,
                ref sa, SECURITY_IMPERSONATION_LEVEL.SecurityIdentification,
                TOKEN_TYPE.TokenPrimary, out duplicateToken))
            {
                logger.LogWarning("DuplicateTokenEx failed: {Error}", Marshal.GetLastWin32Error());
                return null;
            }

            // 3. Create the user's environment block
            if (!CreateEnvironmentBlock(out environment, duplicateToken, false))
            {
                logger.LogWarning("CreateEnvironmentBlock failed: {Error}", Marshal.GetLastWin32Error());
                return null;
            }

            // 4. Launch the process
            var si = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = "winsta0\\default",
                dwFlags = 0x00000001 /* STARTF_USESHOWWINDOW */,
                wShowWindow = 0 /* SW_HIDE */
            };

            var commandLine = $"\"{exePath}\" {arguments}";

            if (!CreateProcessAsUser(
                duplicateToken,
                null,
                commandLine,
                ref sa, ref sa,
                false,
                0x00000400 /* CREATE_UNICODE_ENVIRONMENT */ | 0x00000010 /* CREATE_NEW_CONSOLE */,
                environment,
                null,
                ref si,
                out var pi))
            {
                logger.LogWarning("CreateProcessAsUser failed: {Error}", Marshal.GetLastWin32Error());
                return null;
            }

            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);

            logger.LogInformation("Launched PID {Pid} in session {Session} via CreateProcessAsUser",
                pi.dwProcessId, sessionId);
            return (int)pi.dwProcessId;
        }
        finally
        {
            if (environment != 0) DestroyEnvironmentBlock(environment);
            if (duplicateToken != 0) CloseHandle(duplicateToken);
            if (userToken != 0) CloseHandle(userToken);
        }
    }

    // ── P/Invoke declarations ──

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out nint phToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        nint hToken, string? lpApplicationName, string lpCommandLine,
        ref SECURITY_ATTRIBUTES lpProcessAttributes,
        ref SECURITY_ATTRIBUTES lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags,
        nint lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        nint hExistingToken, uint dwDesiredAccess,
        ref SECURITY_ATTRIBUTES lpTokenAttributes,
        SECURITY_IMPERSONATION_LEVEL impersonationLevel,
        TOKEN_TYPE tokenType, out nint phNewToken);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out nint lpEnvironment, nint hToken, bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(nint lpEnvironment);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);

    private enum SECURITY_IMPERSONATION_LEVEL { SecurityIdentification = 1 }
    private enum TOKEN_TYPE { TokenPrimary = 1 }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public nint lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize;
        public int dwXCountChars, dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public nint lpReserved2;
        public nint hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public nint hProcess;
        public nint hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }
}
