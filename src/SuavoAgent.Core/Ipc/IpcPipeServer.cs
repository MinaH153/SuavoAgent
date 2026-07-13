using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
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
    private readonly Func<IpcBrokerAttestationEvidence, bool>? _isBrokerAttestedHelper;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private bool _isConnected;

    public bool IsConnected => _isConnected;
    public string PipeName => _pipeName;

    public IpcPipeServer(
        string pipeName,
        Func<IpcRequest, Task<IpcResponse>> handler,
        ILogger<IpcPipeServer> logger,
        Func<IpcBrokerAttestationEvidence, bool>? isBrokerAttestedHelper = null)
    {
        _pipeName = pipeName;
        _handler = handler;
        _logger = logger;
        _isBrokerAttestedHelper = isBrokerAttestedHelper;
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

                _logger.LogDebug("core.ipc.waiting_for_helper");
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

                        // Verify executable path is under the SuavoAgent install directory (anti-spoofing).
                        // Use QueryFullProcessImageName instead of clientProc.MainModule because the
                        // latter requires PROCESS_VM_READ which SYSTEM->user-context process boundaries
                        // routinely block. QueryFullProcessImageName only needs PROCESS_QUERY_LIMITED_INFORMATION
                        // and works the same across security tokens — caught at Nadim's 2026-04-25.
                        // If both image path APIs fail, the verifier may still accept only a
                        // Broker-attested Helper PID. That preserves the anti-spoofing boundary
                        // on locked-down Windows hosts where even limited process queries are
                        // denied across service/user tokens.
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
                                _logger.LogSafeDebug(ex);
                                clientPath = null;
                            }
                        }

                        IpcBrokerAttestationEvidence? brokerEvidence = null;
                        if (string.IsNullOrWhiteSpace(clientPath) &&
                            TryBuildBrokerEvidence(clientProc, clientPid, out var evidence))
                            brokerEvidence = evidence;

                        var verification = IpcPeerVerifier.Verify(
                            processName: clientName,
                            processId: clientPid,
                            executablePath: clientPath,
                            coreBaseDirectory: AppContext.BaseDirectory,
                            brokerEvidence: brokerEvidence,
                            isBrokerAttestedHelper: _isBrokerAttestedHelper);

                        if (!verification.Accepted)
                        {
                            _logger.LogWarning("core.ipc.connection_rejected");
                            IpcRejectionStats.Record(verification.RejectionReason ?? "verification_failed");
                            pipe.Disconnect();
                            continue;
                        }

                        if (verification.AcceptedByBrokerAttestation)
                        {
                            _logger.LogInformation("core.ipc.broker_attestation_accepted");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogSafeWarning(ex);
                        IpcRejectionStats.Record($"verification_exception:{ex.GetType().Name}");
                        pipe.Disconnect();
                        continue;
                    }
                }

                _isConnected = true;
                _logger.LogInformation("core.ipc.helper_connected");

                await HandleConnection(pipe, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogSafeWarning(ex);
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
            IpcRequest? request = null;
            try
            {
                var json = await IpcFraming.ReadFrameAsync(pipe, ct);
                if (json == null) break; // Client disconnected

                request = JsonSerializer.Deserialize<IpcRequest>(json);
                if (request == null) continue;

                _logger.LogDebug("core.ipc.request_received");

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
                _logger.LogWarning(
                    "IPC message handling error ({ExceptionType}); closing connection",
                    ex.GetType().FullName);
                if (request is not null && pipe.IsConnected)
                {
                    try
                    {
                        var failure = new IpcResponse(
                            request.Id,
                            IpcStatus.InternalError,
                            request.Command,
                            null,
                            new IpcError(
                                "handler_failed",
                                "Core could not persist the request; reconnect and retry.",
                                true,
                                0));
                        await IpcFraming.WriteFrameAsync(
                            pipe,
                            JsonSerializer.Serialize(failure),
                            ct);
                    }
                    catch
                    {
                        // The connection is closed below; the client retries
                        // the unchanged envelope after reconnecting.
                    }
                }
                break;
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

    private static bool TryBuildBrokerEvidence(
        System.Diagnostics.Process process,
        uint processId,
        out IpcBrokerAttestationEvidence evidence)
    {
        evidence = default;
        try
        {
            var helperPath = Path.Combine(AppContext.BaseDirectory, "SuavoAgent.Helper.exe");
            if (!File.Exists(helperPath)) return false;
            if (!SuavoAgent.Diagnostics.Maintenance.AuthenticodePublisherVerifier
                    .Verify(helperPath).IsTrusted)
                return false;
            using var stream = new FileStream(
                helperPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            var startedAt = new DateTimeOffset(process.StartTime.ToUniversalTime());
            var sessionId = checked((uint)process.SessionId);
            evidence = new(processId, sessionId, startedAt, sha256);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Creates the observation/event pipe for its only real clients: the
    /// LocalSystem supervisor and the logged-on interactive Helper. Core is
    /// the server and already owns the created handle, so shared LocalService
    /// and NetworkService client grants are unnecessary horizontal access.
    /// Falls back to default security on non-Windows platforms (build/test).
    /// </summary>
    private static NamedPipeServerStream CreateSecurePipe(string pipeName)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new System.IO.Pipes.PipeSecurity();
            foreach (var sidValue in ObservationPipeAllowedSidValues())
            {
                // Helper runs as the logged-on interactive user (S-1-5-4).
                // SYSTEM retains full control for supervisor diagnostics; the
                // Helper gets only the duplex rights needed for framed IPC.
                var rights = string.Equals(sidValue, "S-1-5-4", StringComparison.Ordinal)
                    ? System.IO.Pipes.PipeAccessRights.ReadWrite
                    : System.IO.Pipes.PipeAccessRights.FullControl;
                security.AddAccessRule(new System.IO.Pipes.PipeAccessRule(
                    new System.Security.Principal.SecurityIdentifier(sidValue),
                    rights,
                    System.Security.AccessControl.AccessControlType.Allow));
            }

            return NamedPipeServerStreamAcl.Create(
                pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                0, 0, security);
        }

        return new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    internal static IReadOnlyList<string> ObservationPipeAllowedSidValues() =>
    [
        "S-1-5-18", // LocalSystem
        "S-1-5-4",  // Interactive
    ];

    [DllImport("kernel32.dll", SetLastError = true)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static extern bool GetNamedPipeClientProcessId(
        Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);
}
