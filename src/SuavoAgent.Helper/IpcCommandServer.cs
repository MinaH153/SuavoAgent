using System.IO.Pipes;
using System.Text.Json;
using Serilog;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Helper.Workflows;

namespace SuavoAgent.Helper;

/// <summary>
/// Helper-side command server. Core connects to this pipe to push commands
/// (e.g. pricing_lookup) and receive results. Reverse direction of the main IPC pipe.
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
                pipe = new NamedPipeServerStream(
                    _pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                _logger.Debug("IpcCommandServer: waiting for Core on pipe {Name}", _pipeName);
                await pipe.WaitForConnectionAsync(ct);
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

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
