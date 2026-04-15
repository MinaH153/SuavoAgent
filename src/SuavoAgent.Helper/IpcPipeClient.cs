using System.IO.Pipes;
using System.Text.Json;
using SuavoAgent.Contracts.Ipc;
using Serilog;

namespace SuavoAgent.Helper;

public sealed class IpcPipeClient : IDisposable
{
    private readonly string _pipeName;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
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

        await _writeLock.WaitAsync(ct);
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
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IpcResponse?> PingAsync(CancellationToken ct)
    {
        return await SendAsync(
            new IpcRequest(Guid.NewGuid().ToString("N"), IpcCommands.Ping, 1, null), ct);
    }

    /// <summary>
    /// Best-effort send — auto-connects if needed, swallows failures.
    /// Used for non-critical status reporting (attachment events).
    /// </summary>
    public async Task TrySendAsync(string command, string? payload, CancellationToken ct)
    {
        try
        {
            if (!IsConnected)
                await ConnectAsync(TimeSpan.FromSeconds(3), ct);

            if (!IsConnected) return;

            JsonElement? data = payload != null
                ? JsonDocument.Parse(payload).RootElement.Clone()
                : null;

            await SendAsync(
                new IpcRequest(Guid.NewGuid().ToString("N"), command, 1, data), ct);
        }
        catch
        {
            // Non-critical — Core may not be listening yet
        }
    }

    public void Dispose()
    {
        _pipe?.Dispose();
    }
}
