using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Ipc;

namespace SuavoAgent.Core.Ipc;

public sealed class IpcPipeServer : IDisposable
{
    // PROCESS_QUERY_LIMITED_INFORMATION (0x1000) is the minimum-privilege flag
    // for OpenProcess that allows QueryFullProcessImageName. Critically, it
    // does NOT require PROCESS_VM_READ (which clientProc.MainModule does), so
    // Core (running as SYSTEM) can read Helper.exe's image path even when
    // Helper runs as the interactive user with a restricted process token.
    // Caught at Nadim's pharmacy 2026-04-25 — Helper observations were being
    // rejected at IPC peer-validation because MainModule threw Access Denied
    // crossing SYSTEM->user security boundary, blocking all UIA captures.
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, [Out] StringBuilder lpExeName, ref uint lpdwSize);

    private static string? GetProcessImagePath(uint processId)
    {
        var hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (hProcess == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageNameW(hProcess, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    private readonly string _pipeName;
    private readonly ILogger<IpcPipeServer> _logger;
    private readonly Func<IpcRequest, Task<IpcResponse>> _handler;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private bool _isConnected;

    public bool IsConnected => _isConnected;
    public string PipeName => _pipeName;

    public IpcPipeServer(string pipeName, Func<IpcRequest, Task<IpcResponse>> handler, ILogger<IpcPipeServer> logger)
    {
        _pipeName = pipeName;
        _handler = handler;
        _logger = logger;
    }

    public void Start(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listenTask = ListenLoop(_cts.Token);
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreateSecurePipe(_pipeName);

                _logger.LogDebug("Waiting for Helper connection on pipe {Name}...", _pipeName);
                await pipe.WaitForConnectionAsync(ct);

                // Verify connecting process is a known SuavoAgent binary
                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid))
                            throw new InvalidOperationException("GetNamedPipeClientProcessId failed");

                        var clientProc = System.Diagnostics.Process.GetProcessById((int)clientPid);
                        var clientName = clientProc.ProcessName;

                        // Verify process name is a known SuavoAgent binary
                        if (clientName != "SuavoAgent.Helper" && clientName != "SuavoAgent.Broker")
                        {
                            _logger.LogWarning("IPC: Rejected connection from unauthorized process {Name} (PID {Pid})",
                                clientName, clientPid);
                            pipe.Disconnect();
                            continue;
                        }

                        // Verify executable path is under the SuavoAgent install directory (anti-spoofing).
                        // Use QueryFullProcessImageName instead of clientProc.MainModule because the
                        // latter requires PROCESS_VM_READ which SYSTEM->user-context process boundaries
                        // routinely block. QueryFullProcessImageName only needs PROCESS_QUERY_LIMITED_INFORMATION
                        // and works the same across security tokens — caught at Nadim's 2026-04-25.
                        // Fail-closed: if path still cannot be read, reject — a cross-arch or ACL-denied
                        // binary is exactly the shape an attacker would wear.
                        var clientPath = GetProcessImagePath(clientPid);
                        if (string.IsNullOrEmpty(clientPath))
                        {
                            // Fallback to MainModule for non-Windows or other quirks.
                            try
                            {
                                clientPath = clientProc.MainModule?.FileName;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "IPC: process image path unreadable for PID {Pid} — rejecting", clientPid);
                                pipe.Disconnect();
                                continue;
                            }
                        }

                        if (string.IsNullOrEmpty(clientPath))
                        {
                            _logger.LogWarning("IPC: Empty client path for PID {Pid} — rejecting", clientPid);
                            pipe.Disconnect();
                            continue;
                        }

                        // Use parent dir as install root (Core/Helper/Broker are sibling subdirs).
                        // Append separator so "C:\Suavo\" doesn't match "C:\SuavoEvil\...".
                        var installRoot = Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))
                            ?? AppContext.BaseDirectory;
                        var installDir = installRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        if (!clientPath.StartsWith(installDir, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogWarning("IPC: Rejected connection from outside install dir: {Path} (expected under {Dir})",
                                clientPath, installDir);
                            pipe.Disconnect();
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "IPC: Could not verify client process — rejecting");
                        pipe.Disconnect();
                        continue;
                    }
                }

                _isConnected = true;
                _logger.LogInformation("Helper connected on pipe {Name}", _pipeName);

                await HandleConnection(pipe, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pipe connection error");
                _isConnected = false;
                await Task.Delay(1000, ct);
            }
            finally
            {
                _isConnected = false;
                pipe?.Dispose();
            }
        }
    }

    private async Task HandleConnection(NamedPipeServerStream pipe, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && pipe.IsConnected)
        {
            try
            {
                var json = await IpcFraming.ReadFrameAsync(pipe, ct);
                if (json == null) break; // Client disconnected

                var request = JsonSerializer.Deserialize<IpcRequest>(json);
                if (request == null) continue;

                _logger.LogDebug("IPC received: {Command} [{Id}]", request.Command, request.Id);

                var response = await _handler(request);
                var responseJson = JsonSerializer.Serialize(response);
                await IpcFraming.WriteFrameAsync(pipe, responseJson, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                _logger.LogInformation("Helper disconnected");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "IPC message handling error");
            }
        }

        _isConnected = false;
        _logger.LogInformation("Helper connection closed");
    }

    public async Task<IpcResponse?> SendCommandAsync(IpcRequest command, TimeSpan timeout)
    {
        // Push model not yet implemented -- protocol is currently request-response (Helper sends, Core responds)
        _logger.LogDebug("SendCommand not yet implemented for push model");
        return await Task.FromResult<IpcResponse?>(null);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }

    /// <summary>
    /// Creates a named pipe with ACL restricted to SYSTEM + LocalService.
    /// Prevents arbitrary local processes from connecting.
    /// Falls back to default security on non-Windows platforms (build/test).
    /// </summary>
    private static NamedPipeServerStream CreateSecurePipe(string pipeName)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new System.IO.Pipes.PipeSecurity();
            security.AddAccessRule(new System.IO.Pipes.PipeAccessRule(
                new System.Security.Principal.SecurityIdentifier(
                    System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
                System.IO.Pipes.PipeAccessRights.FullControl,
                System.Security.AccessControl.AccessControlType.Allow));
            security.AddAccessRule(new System.IO.Pipes.PipeAccessRule(
                new System.Security.Principal.SecurityIdentifier(
                    System.Security.Principal.WellKnownSidType.LocalServiceSid, null),
                System.IO.Pipes.PipeAccessRights.FullControl,
                System.Security.AccessControl.AccessControlType.Allow));
            // Broker runs as NetworkService — needs full pipe access.
            security.AddAccessRule(new System.IO.Pipes.PipeAccessRule(
                new System.Security.Principal.SecurityIdentifier(
                    System.Security.Principal.WellKnownSidType.NetworkServiceSid, null),
                System.IO.Pipes.PipeAccessRights.FullControl,
                System.Security.AccessControl.AccessControlType.Allow));
            // Helper runs as the logged-on interactive user (launched by Broker via CreateProcessAsUser).
            // Interactive SID (S-1-5-4) covers physical + RDP sessions but excludes service accounts,
            // network logons, and anonymous logons — strictly narrower than AuthenticatedUser.
            // Binary identity is still pinned via MainModule path check in the listen loop.
            security.AddAccessRule(new System.IO.Pipes.PipeAccessRule(
                new System.Security.Principal.SecurityIdentifier(
                    System.Security.Principal.WellKnownSidType.InteractiveSid, null),
                System.IO.Pipes.PipeAccessRights.ReadWrite,
                System.Security.AccessControl.AccessControlType.Allow));

            return NamedPipeServerStreamAcl.Create(
                pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                0, 0, security);
        }

        return new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static extern bool GetNamedPipeClientProcessId(
        Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);
}
