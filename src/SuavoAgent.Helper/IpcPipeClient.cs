using System.IO.Pipes;
using System.Text.Json;
using SuavoAgent.Contracts.Ipc;
using Serilog;

namespace SuavoAgent.Helper;

public sealed class IpcPipeClient : IDisposable
{
    private readonly string _pipeName;
    private readonly ILogger _logger;
    private NamedPipeClientStream? _pipe;

    public bool IsConnected => _pipe?.IsConnected ?? false;

    public IpcPipeClient(string pipeName, ILogger logger)
    {
        _pipeName = pipeName;
        _logger = logger;
    }

    public async Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await _pipe.ConnectAsync((int)timeout.TotalMilliseconds, ct);
            _logger.Information("Connected to Core via pipe {Name}", _pipeName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to connect to Core pipe {Name}", _pipeName);
            return false;
        }
    }

    public async Task<IpcResponse?> SendAsync(IpcRequest request, CancellationToken ct)
    {
        if (_pipe == null || !IsConnected)
            return null;

        try
        {
            var json = JsonSerializer.Serialize(request);
            await IpcFraming.WriteFrameAsync(_pipe, json, ct);

            var responseJson = await IpcFraming.ReadFrameAsync(_pipe, ct);
            if (responseJson == null) return null;

            return JsonSerializer.Deserialize<IpcResponse>(responseJson);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "IPC send failed for {Command}", request.Command);
            return null;
        }
    }

    public async Task<IpcResponse?> PingAsync(CancellationToken ct)
    {
        return await SendAsync(
            new IpcRequest(Guid.NewGuid().ToString("N"), IpcCommands.Ping, 1, null), ct);
    }

    public void Dispose()
    {
        _pipe?.Dispose();
    }
}
