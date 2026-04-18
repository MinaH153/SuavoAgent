using System.IO.Pipes;
using System.Text.Json;
using SuavoAgent.Contracts.Ipc;

namespace SuavoAgent.Core.Ipc;

/// <summary>
/// Core-side client that connects to Helper's command pipe.
/// Used to push commands (e.g. pricing lookups) from Core → Helper.
/// One connection per job; dispose after job completes.
/// </summary>
public sealed class IpcCommandClient : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly ILogger<IpcCommandClient> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private NamedPipeClientStream? _pipe;

    public bool IsConnected => _pipe?.IsConnected ?? false;

    public IpcCommandClient(string pipeName, ILogger<IpcCommandClient> logger)
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
            _logger.LogInformation("IpcCommandClient connected to Helper on pipe {Name}", _pipeName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IpcCommandClient failed to connect to pipe {Name}", _pipeName);
            return false;
        }
    }

    /// <summary>
    /// Sends a command and waits for a response. Thread-safe via semaphore.
    /// Returns null if the pipe is disconnected or a timeout occurs.
    /// </summary>
    public async Task<IpcResponse?> SendAsync(IpcRequest request, TimeSpan timeout, CancellationToken ct)
    {
        if (_pipe == null || !IsConnected) return null;

        await _lock.WaitAsync(ct);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            var json = JsonSerializer.Serialize(request);
            await IpcFraming.WriteFrameAsync(_pipe, json, cts.Token);

            var responseJson = await IpcFraming.ReadFrameAsync(_pipe, cts.Token);
            if (responseJson == null)
            {
                // [C-2] Teardown on partial/missing read so stale data can't poison next request
                TeardownPipe();
                return null;
            }

            return JsonSerializer.Deserialize<IpcResponse>(responseJson);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // [C-2] Timeout — teardown so next request doesn't read stale response
            TeardownPipe();
            _logger.LogWarning("IpcCommandClient timeout for {Command}", request.Command);
            return null;
        }
        catch (Exception ex)
        {
            TeardownPipe();
            _logger.LogWarning(ex, "IpcCommandClient send error for {Command}", request.Command);
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    private void TeardownPipe()
    {
        try { _pipe?.Close(); } catch { }
        _pipe = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_pipe != null)
        {
            try { _pipe.Close(); } catch { }
            await _pipe.DisposeAsync();
        }
        _lock.Dispose();
    }
}
