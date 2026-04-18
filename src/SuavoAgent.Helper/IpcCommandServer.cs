using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Serilog;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Helper.Workflows;

namespace SuavoAgent.Helper;

/// <summary>
/// Helper-side command server. Core connects to this pipe to push commands
/// (e.g. pricing_lookup) and receive results. Reverse direction of the main IPC pipe.
///
/// Security hardening mirrors <see cref="SuavoAgent.Core.Ipc.IpcPipeServer"/>:
///   - ACL restricts pipe to SYSTEM + LocalService + Interactive
///   - Client process name must be SuavoAgent.Core
///   - Client MainModule path must be under the Helper install root
/// Without this, any local process running as the same user could drive UIA
/// automation of PioneerRx.
/// </summary>
public sealed class IpcCommandServer : IDisposable
{
    private readonly string _pipeName;
    private readonly PricingWorkflow _pricing;
    private readonly ILogger _logger;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public IpcCommandServer(string pipeName, PricingWorkflow pricing, ILogger logger)
    {
        _pipeName = pipeName;
        _pricing = pricing;
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

                _logger.Debug("IpcCommandServer: waiting for Core on pipe {Name}", _pipeName);
                await pipe.WaitForConnectionAsync(ct);

                if (!VerifyClientIsCore(pipe))
                {
                    pipe.Disconnect();
                    continue;
                }

                _logger.Information("IpcCommandServer: Core connected on pipe {Name}", _pipeName);

                await HandleConnection(pipe, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "IpcCommandServer: connection error");
                await Task.Delay(1000, ct);
            }
            finally
            {
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
                if (json == null) break;

                var request = JsonSerializer.Deserialize<IpcRequest>(json);
                if (request == null) continue;

                _logger.Debug("IpcCommandServer: received {Command} [{Id}]", request.Command, request.Id);

                var response = await DispatchAsync(request, ct);
                var responseJson = JsonSerializer.Serialize(response);
                await IpcFraming.WriteFrameAsync(pipe, responseJson, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                _logger.Information("IpcCommandServer: Core disconnected");
                break;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "IpcCommandServer: message error");
            }
        }
    }

    private Task<IpcResponse> DispatchAsync(IpcRequest request, CancellationToken ct)
    {
        return request.Command switch
        {
            IpcCommands.PricingLookup => HandlePricingLookupAsync(request, ct),
            IpcCommands.Ping => Task.FromResult(Ok(request.Id, request.Command, null)),
            _ => Task.FromResult(Error(request.Id, request.Command, "unknown_command", $"Unknown command: {request.Command}"))
        };
    }

    private Task<IpcResponse> HandlePricingLookupAsync(IpcRequest request, CancellationToken ct)
    {
        if (request.Data == null)
            return Task.FromResult(Error(request.Id, request.Command, "bad_request", "Missing data"));

        NdcPricingRequest? pricingReq;
        try
        {
            pricingReq = JsonSerializer.Deserialize<NdcPricingRequest>(request.Data.Value);
        }
        catch (Exception ex)
        {
            return Task.FromResult(Error(request.Id, request.Command, "bad_request", ex.Message));
        }

        if (pricingReq == null)
            return Task.FromResult(Error(request.Id, request.Command, "bad_request", "Could not deserialize NdcPricingRequest"));

        // UIA must run on this thread — it's already called from the pipe handler loop
        // which runs on a thread pool thread. FlaUI is fine with this as long as it's
        // single-threaded per automation instance (PricingWorkflow uses its own UIA2Automation).
        var result = _pricing.Lookup(pricingReq);
        var data = JsonSerializer.SerializeToElement(result);
        return Task.FromResult(Ok(request.Id, request.Command, data));
    }

    private static IpcResponse Ok(string id, string command, System.Text.Json.JsonElement? data) =>
        new(id, IpcStatus.Ok, command, data, null);

    private static IpcResponse Error(string id, string command, string code, string message) =>
        new(id, IpcStatus.InternalError, command, null,
            new IpcError(code, message, false, 1));

    /// <summary>
    /// Creates a named pipe restricted to SYSTEM + LocalService + Interactive user.
    /// Falls back to default security on non-Windows platforms (for build/test).
    /// </summary>
    private static NamedPipeServerStream CreateSecurePipe(string pipeName)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(
                new System.Security.Principal.SecurityIdentifier(
                    System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl,
                System.Security.AccessControl.AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                new System.Security.Principal.SecurityIdentifier(
                    System.Security.Principal.WellKnownSidType.LocalServiceSid, null),
                PipeAccessRights.FullControl,
                System.Security.AccessControl.AccessControlType.Allow));
            // Helper runs as the interactive user — needs read/write to operate its own pipe.
            // Core (SYSTEM) connects in, so SYSTEM must also have access (granted above).
            security.AddAccessRule(new PipeAccessRule(
                new System.Security.Principal.SecurityIdentifier(
                    System.Security.Principal.WellKnownSidType.InteractiveSid, null),
                PipeAccessRights.ReadWrite,
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

    /// <summary>
    /// Verifies the connecting client is SuavoAgent.Core and its executable lives under
    /// the SuavoAgent install root. Fail-closed on any read error.
    /// </summary>
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

            var clientProc = System.Diagnostics.Process.GetProcessById((int)clientPid);
            if (clientProc.ProcessName != "SuavoAgent.Core")
            {
                _logger.Warning("IpcCommandServer: rejected connection from {Name} (PID {Pid}) — not SuavoAgent.Core",
                    clientProc.ProcessName, clientPid);
                return false;
            }

            string? clientPath;
            try
            {
                clientPath = clientProc.MainModule?.FileName;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "IpcCommandServer: MainModule unreadable for PID {Pid} — rejecting", clientPid);
                return false;
            }

            if (string.IsNullOrEmpty(clientPath))
            {
                _logger.Warning("IpcCommandServer: empty client path for PID {Pid} — rejecting", clientPid);
                return false;
            }

            // Helper's AppContext.BaseDirectory is the install dir for this binary.
            // Core lives as a sibling subdir under the same install root.
            var installRoot = Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))
                ?? AppContext.BaseDirectory;
            var installDir = installRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!clientPath.StartsWith(installDir, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warning("IpcCommandServer: rejected connection from outside install dir: {Path} (expected under {Dir})",
                    clientPath, installDir);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "IpcCommandServer: client verification error — rejecting");
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static extern bool GetNamedPipeClientProcessId(
        Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
