using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Ipc;
using Serilog;

namespace SuavoAgent.Helper;

public sealed class IpcPipeClient : IDisposable
{
    private readonly string _pipeName;
    private readonly ILogger _logger;
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;

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
            _reader = new StreamReader(_pipe, Encoding.UTF8, leaveOpen: true);
            _writer = new StreamWriter(_pipe, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
            _logger.Information("Connected to Core via pipe {Name}", _pipeName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to connect to Core pipe {Name}", _pipeName);
            return false;
        }
    }

    public async Task<IpcResponse?> SendAsync(IpcMessage message, CancellationToken ct)
    {
        if (_writer == null || _reader == null || !IsConnected)
            return null;

        try
        {
            var line = JsonSerializer.Serialize(message);
            await _writer.WriteLineAsync(line.AsMemory(), ct);

            var responseLine = await _reader.ReadLineAsync(ct);
            if (responseLine == null) return null;

            return JsonSerializer.Deserialize<IpcResponse>(responseLine);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "IPC send failed for {Command}", message.Command);
            return null;
        }
    }

    public async Task<IpcResponse?> PingAsync(CancellationToken ct)
    {
        return await SendAsync(
            new IpcMessage(1, Guid.NewGuid().ToString("N"), IpcCommands.Ping, null), ct);
    }

    public void Dispose()
    {
        _writer?.Dispose();
        _reader?.Dispose();
        _pipe?.Dispose();
    }
}
