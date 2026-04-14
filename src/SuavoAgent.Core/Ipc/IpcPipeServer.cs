using System.IO.Pipes;
using System.Text.Json;
using SuavoAgent.Contracts.Ipc;

namespace SuavoAgent.Core.Ipc;

public sealed class IpcPipeServer : IDisposable
{
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
            // Helper runs as interactive user (launched by Broker via CreateProcessAsUser).
            // Without this rule, Helper gets Access Denied on pipe connect.
            security.AddAccessRule(new System.IO.Pipes.PipeAccessRule(
                new System.Security.Principal.SecurityIdentifier(
                    System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, null),
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
}
