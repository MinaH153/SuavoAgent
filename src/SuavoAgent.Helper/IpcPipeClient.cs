using System.IO.Pipes;
using System.Text.Json;
using SuavoAgent.Contracts.Ipc;
using Serilog;

namespace SuavoAgent.Helper;

public sealed class IpcPipeClient : IDisposable
{
    private readonly string _pipeName;
    private readonly ILogger _logger;
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private bool _disposed;

    public bool IsConnected => _pipe?.IsConnected ?? false;

    public IpcPipeClient(
        string pipeName,
        ILogger logger,
        TimeSpan? requestTimeout = null)
    {
        _pipeName = pipeName;
        _logger = logger;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(10);
        if (_requestTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
    }

    public async Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken ct)
    {
        await _connectionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return false;
            if (IsConnected) return true;

            ResetPipe();
            var candidate = new NamedPipeClientStream(
                ".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await candidate.ConnectAsync((int)timeout.TotalMilliseconds, ct)
                    .ConfigureAwait(false);
            }
            catch
            {
                candidate.Dispose();
                throw;
            }

            if (_disposed)
            {
                candidate.Dispose();
                return false;
            }
            _pipe = candidate;
            _logger.Information("Connected to Core via pipe {Name}", _pipeName);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(
                "Failed to connect to Core pipe ({ExceptionType})",
                ex.GetType().Name);
            return false;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<IpcResponse?> SendAsync(IpcRequest request, CancellationToken ct)
    {
        if (_pipe == null || !IsConnected)
            return null;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_requestTimeout);
        var lockAcquired = false;
        try
        {
            await _writeLock.WaitAsync(deadline.Token).ConfigureAwait(false);
            lockAcquired = true;
            var json = JsonSerializer.Serialize(request);
            await IpcFraming.WriteFrameAsync(_pipe, json, deadline.Token).ConfigureAwait(false);

            var responseJson = await IpcFraming.ReadFrameAsync(_pipe, deadline.Token).ConfigureAwait(false);
            if (responseJson == null) return null;

            return JsonSerializer.Deserialize<IpcResponse>(responseJson);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ResetPipe();
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.Warning(
                "IPC request timed out for {Command} after {TimeoutSeconds}s",
                request.Command,
                _requestTimeout.TotalSeconds);
            ResetPipe();
            return null;
        }
        catch (Exception ex)
        {
            _logger.Warning(
                "IPC send failed for {Command} ({ExceptionType})",
                request.Command,
                ex.GetType().Name);
            ResetPipe();
            return null;
        }
        finally
        {
            if (lockAcquired)
                _writeLock.Release();
        }
    }

    public async Task<IpcResponse?> PingAsync(CancellationToken ct)
    {
        return await SendAsync(
            new IpcRequest(Guid.NewGuid().ToString("N"), IpcCommands.Ping, 1, null), ct);
    }

    /// <summary>
    /// Auto-connects if needed and returns Core's acknowledgement. Callers that
    /// need reliable delivery must retain their payload until a successful
    /// response is returned.
    /// </summary>
    public async Task<IpcResponse?> TrySendAsync(string command, string? payload, CancellationToken ct)
    {
        try
        {
            if (!IsConnected)
                await ConnectAsync(TimeSpan.FromSeconds(3), ct);

            if (!IsConnected) return null;

            JsonElement? data = null;
            if (payload != null)
            {
                using var document = JsonDocument.Parse(payload);
                data = document.RootElement.Clone();
            }

            return await SendAsync(
                new IpcRequest(Guid.NewGuid().ToString("N"), command, 1, data), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Debug(
                "IPC best-effort send failed for {Command} ({ExceptionType})",
                command,
                ex.GetType().Name);
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ResetPipe();
    }

    private void ResetPipe()
    {
        var pipe = Interlocked.Exchange(ref _pipe, null);
        try { pipe?.Dispose(); }
        catch { }
    }
}
