using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Serilog;
using SuavoAgent.Contracts.Discovery;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Core.Discovery;
using SuavoAgent.Helper.Actuation;
using SuavoAgent.Helper.IntentCursor;
using SuavoAgent.Helper.Vision;
using SuavoAgent.Helper.Workflows;

namespace SuavoAgent.Helper;

public sealed partial class IpcCommandServer
{
    // One active connection plus one already-listening successor. Keeping the
    // successor open prevents a client from connecting to the retiring OS pipe
    // listener and then receiving EOF before a replacement instance exists.
    private const int CommandPipeServerInstances = 2;

    private static IpcResponse Ok(string id, string command, System.Text.Json.JsonElement? data) =>
        new(id, IpcStatus.Ok, command, data, null);

    private static IpcResponse Error(string id, string command, string code, string message, int status = IpcStatus.InternalError) =>
        new(id, status, command, null,
            new IpcError(code, message, false, 1));

    /// <summary>
    /// Creates a named pipe restricted to SYSTEM + the exact Core service SID.
    /// The interactive Helper already owns the server handle; it does not need
    /// an ACE that would let any other interactive process open the pipe.
    /// Falls back to default security on non-Windows platforms (for build/test).
    /// </summary>
    private static NamedPipeServerStream CreateSecurePipe(string pipeName)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new PipeSecurity();
            foreach (var sidValue in CommandPipeAllowedSidValues())
            {
                var rights = string.Equals(sidValue, "S-1-5-18", StringComparison.Ordinal)
                    ? PipeAccessRights.FullControl
                    : PipeAccessRights.ReadWrite;
                security.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(sidValue),
                    rights,
                    System.Security.AccessControl.AccessControlType.Allow));
            }

            return NamedPipeServerStreamAcl.Create(
                pipeName, PipeDirection.InOut, CommandPipeServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                0, 0, security);
        }

        return new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, CommandPipeServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    internal static IReadOnlyList<string> CommandPipeAllowedSidValues() =>
    [
        "S-1-5-18", // LocalSystem maintenance/supervisor diagnostics
        CoreServiceIdentity.ServiceSid,
    ];

    /// <summary>
    /// True only when the connected client's token groups contain the enabled,
    /// exact NT SERVICE\SuavoAgent.Core SID. TokenUser is intentionally not used:
    /// it remains the shared LocalService SID even after SCM enables a per-service
    /// identity. Reading the connected peer token also avoids PID-reuse races.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private bool TryClientHasRequiredCoreServiceSid(
        NamedPipeServerStream pipe,
        uint clientPid,
        out string sidLabel)
    {
        sidLabel = "";
        // Read the client's token SID directly off the pipe's impersonation token — NOT via
        // NamedPipeServerStream.RunAsClient. RunAsClient runs the callback under FULL
        // impersonation, which the client must grant at TokenImpersonationLevel.Impersonation;
        // Core connects at Identification (deliberately — granting Impersonation from SYSTEM
        // Core to the de-privileged user Helper would let a compromised Helper act AS SYSTEM).
        // At Identification, RunAsClient/WindowsIdentity.GetCurrent throw (observed on the box
        // as an empty FileNotFoundException), which is the real strand cause. ImpersonateNamedPipeClient
        // + OpenThreadToken(TOKEN_QUERY) + GetTokenInformation(TokenGroups) reads service groups at
        // Identification level (identity query is allowed there) with NO process handle — so it
        // also sidesteps the OpenProcess ACCESS_DENIED that makes the image-path rung unusable
        // against SYSTEM Core.
        var impersonating = false;
        var threadToken = IntPtr.Zero;
        var sidBuffer = IntPtr.Zero;
        try
        {
            if (!ImpersonateNamedPipeClient(pipe.SafePipeHandle))
            {
                _logger.Warning("IpcCommandServer: ImpersonateNamedPipeClient failed for PID {Pid} (Win32 {Err})",
                    clientPid, Marshal.GetLastWin32Error());
                return false;
            }
            impersonating = true;

            if (!OpenThreadToken(GetCurrentThread(), TOKEN_QUERY, /*OpenAsSelf*/ true, out threadToken))
            {
                _logger.Warning("IpcCommandServer: OpenThreadToken failed for PID {Pid} (Win32 {Err})",
                    clientPid, Marshal.GetLastWin32Error());
                return false;
            }

            // Two-call pattern: size, then read TOKEN_GROUPS.
            GetTokenInformation(threadToken, TokenGroupsClass, IntPtr.Zero, 0, out var needed);
            if (needed <= 0) return false;
            sidBuffer = Marshal.AllocHGlobal(needed);
            if (!GetTokenInformation(threadToken, TokenGroupsClass, sidBuffer, needed, out _))
            {
                _logger.Warning("IpcCommandServer: GetTokenInformation(TokenGroups) failed for PID {Pid} (Win32 {Err})",
                    clientPid, Marshal.GetLastWin32Error());
                return false;
            }

            var groupCount = unchecked((uint)Marshal.ReadInt32(sidBuffer));
            var groupsOffset = Marshal.OffsetOf<TokenGroupsLayout>(
                nameof(TokenGroupsLayout.FirstGroupSid)).ToInt32();
            var groupSize = Marshal.SizeOf<SidAndAttributes>();
            var maxGroups = Math.Max(0, (needed - groupsOffset) / groupSize);
            if (groupCount > (uint)maxGroups)
            {
                _logger.Warning(
                    "IpcCommandServer: malformed TokenGroups for PID {Pid} ({Count} > {Max}) — rejecting",
                    clientPid, groupCount, maxGroups);
                return false;
            }

            for (uint i = 0; i < groupCount; i++)
            {
                var entry = Marshal.PtrToStructure<SidAndAttributes>(
                    IntPtr.Add(sidBuffer, groupsOffset + checked((int)i * groupSize)));
                if (entry.Sid == IntPtr.Zero) continue;
                var sid = new SecurityIdentifier(entry.Sid);
                if (!IsRequiredCoreServiceGroup(sid.Value, entry.Attributes)) continue;
                sidLabel = sid.Value;
                return true;
            }

            _logger.Warning(
                "IpcCommandServer: client PID {Pid} lacks the enabled SuavoAgent.Core service SID — rejecting",
                clientPid);
            return false;
        }
        catch (Exception ex)
        {
            // Warning, not Debug: on field boxes this rung failing silently is exactly how the
            // command-pipe strand hid for four releases — the log then shows only the final
            // C5 reject with no cause. Surface the WHY at default log level.
            _logger.Warning(
                "IpcCommandServer: exact Core service-SID check inconclusive for PID {Pid} ({ExceptionType}) — rejecting",
                clientPid,
                ex.GetType().Name);
            return false;
        }
        finally
        {
            if (sidBuffer != IntPtr.Zero) Marshal.FreeHGlobal(sidBuffer);
            if (threadToken != IntPtr.Zero) CloseHandleNative(threadToken);
            // CRITICAL: always drop impersonation before returning to the read loop.
            if (impersonating && !RevertToSelf())
            {
                var error = Marshal.GetLastWin32Error();
                _logger.Fatal(
                    "IpcCommandServer: RevertToSelf FAILED after exact Core SID check for PID {Pid} (Win32 {Err}) — terminating Helper fail-closed",
                    clientPid, error);
                Environment.FailFast("Named-pipe client impersonation could not be reverted.");
            }
        }
    }

    internal static bool IsRequiredCoreServiceGroup(string sidValue, uint attributes) =>
        string.Equals(sidValue, CoreServiceIdentity.ServiceSid, StringComparison.Ordinal) &&
        (attributes & SeGroupEnabled) != 0;

    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenGroupsClass = 2; // TOKEN_INFORMATION_CLASS.TokenGroups
    private const uint SeGroupEnabled = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenGroupsLayout
    {
        public uint GroupCount;
        public IntPtr FirstGroupSid;
        public uint FirstGroupAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImpersonateNamedPipeClient(Microsoft.Win32.SafeHandles.SafePipeHandle hNamedPipe);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RevertToSelf();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenThreadToken(IntPtr ThreadHandle, uint DesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool OpenAsSelf, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass,
        IntPtr TokenInformation, int TokenInformationLength, out int ReturnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "CloseHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandleNative(IntPtr hObject);

    private bool VerifyClientIsCore(NamedPipeServerStream pipe)
    {
        if (!OperatingSystem.IsWindows()) return true;

        try
        {
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid))
            {
                _logger.Warning("IpcCommandServer: GetNamedPipeClientProcessId failed — rejecting");
                return false;
            }

            using var clientProc = System.Diagnostics.Process.GetProcessById((int)clientPid);
            if (!string.Equals(
                    clientProc.ProcessName,
                    CoreServiceIdentity.ServiceName,
                    StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warning("IpcCommandServer: rejected connection from {Name} (PID {Pid}) — not SuavoAgent.Core",
                    clientProc.ProcessName, clientPid);
                return false;
            }

            // The DACL is the first exact-SID boundary; independently prove the
            // connected token still carries that enabled service group. A shared
            // LocalService/NetworkService/SYSTEM user SID is never accepted here.
            if (!TryClientHasRequiredCoreServiceSid(pipe, clientPid, out var serviceSidLabel))
            {
                return false;
            }

            // Exact service identity is necessary but not sufficient: pin the
            // connected PID to the exact installed Core binary as a second,
            // independent check. An unreadable path is a rejection, never proof.
            var clientPath = ProcessImageInterop.Get(clientPid, out var imageReadError);
            if (string.IsNullOrEmpty(clientPath) && imageReadError != 0)
                _logger.Warning(
                    "IpcCommandServer: QueryFullProcessImageName failed for PID {Pid} (Win32 error {Err}) — trying MainModule",
                    clientPid, imageReadError);
            if (string.IsNullOrEmpty(clientPath))
            {
                try
                {
                    clientPath = clientProc.MainModule?.FileName;
                }
                catch (Exception ex)
                {
                    _logger.Warning(
                        "IpcCommandServer: Core image path unreadable for PID {Pid} after exact-SID proof — rejecting ({ExceptionType})",
                        clientPid,
                        ex.GetType().Name);
                    return false;
                }
            }

            if (string.IsNullOrEmpty(clientPath))
            {
                // QA C5: empty client path is never accepting evidence; relax flag no longer accepts.
                _logger.Warning("IpcCommandServer: empty client path for PID {Pid} — rejecting", clientPid);
                return false;
            }

            if (!IsExpectedCoreExecutablePath(clientPath, AppContext.BaseDirectory))
            {
                _logger.Warning(
                    "IpcCommandServer: rejected Core PID {Pid} at unexpected image path {Path}",
                    clientPid, clientPath);
                return false;
            }

            _logger.Information(
                "IpcCommandServer: accepted Core PID {Pid} with exact service SID {Sid} and exact installed binary path",
                clientPid, serviceSidLabel);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning(
                "IpcCommandServer: client verification error — rejecting ({ExceptionType})",
                ex.GetType().Name);
            return false;
        }
    }

    internal static bool IsExpectedCoreExecutablePath(
        string clientPath,
        string helperBaseDirectory)
    {
        if (string.IsNullOrWhiteSpace(clientPath) || string.IsNullOrWhiteSpace(helperBaseDirectory))
            return false;
        var expected = Path.GetFullPath(Path.Combine(
            helperBaseDirectory,
            CoreServiceIdentity.ExecutableName));
        return string.Equals(
            Path.GetFullPath(clientPath),
            expected,
            StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static extern bool GetNamedPipeClientProcessId(
        Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);

}
