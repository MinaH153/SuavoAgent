using System.IO.Pipes;
using System.Text.Json;
using SuavoAgent.Contracts.Ipc;

namespace SuavoAgent.Core.Ipc;

/// <summary>
/// Test seam over <see cref="IpcCommandClient"/> so workers can be unit-
/// tested without a real named pipe. Production wiring uses the concrete
/// class directly; tests use an in-memory fake.
/// </summary>
public interface IIpcCommandClient
{
    bool IsConnected { get; }

    /// <summary>
    /// True when another command round-trip currently holds this client (a pricing lookup,
    /// discovery scan, …). The actuation-readiness probe checks this FIRST and skips its ping
    /// instead of queueing behind a 30–60s in-flight command — "busy" means the pipe is in
    /// active use, which is not evidence of a strand. Default false so existing test fakes
    /// (which have no internal lock) keep compiling and never report busy.
    /// </summary>
    bool IsBusy => false;

    Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken ct);
    Task<IpcResponse?> SendAsync(IpcRequest request, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// Core-side client that connects to Helper's command pipe.
/// Used to push commands (e.g. pricing lookups) from Core → Helper.
/// One connection per job; dispose after job completes.
/// </summary>
public sealed class IpcCommandClient : IAsyncDisposable, IIpcCommandClient
{
    private readonly string _pipeName;
    private readonly ILogger<IpcCommandClient> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private NamedPipeClientStream? _pipe;

    // Bounded reconnect budget for SendAsync's on-demand recovery — kept short so a
    // genuinely-down Helper fails the call quickly rather than stalling the caller's cycle.
    private static readonly TimeSpan ReconnectTimeout = TimeSpan.FromSeconds(2);

    public bool IsConnected => _pipe?.IsConnected ?? false;

    /// <summary>A command round-trip is in flight (the send lock is held). See <see cref="IIpcCommandClient.IsBusy"/>.</summary>
    public bool IsBusy => _lock.CurrentCount == 0;

    public IpcCommandClient(string pipeName, ILogger<IpcCommandClient> logger)
    {
        _pipeName = pipeName;
        _logger = logger;
    }

    public async Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return await ConnectCoreAsync(timeout, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Disposes any stale pipe, then creates and connects a fresh one. Caller MUST hold
    /// <see cref="_lock"/> (called from <see cref="ConnectAsync"/> and the reconnect path in
    /// <see cref="SendAsync"/>). Genuine cancellation propagates; connect failures return false.
    /// </summary>
    private async Task<bool> ConnectCoreAsync(TimeSpan timeout, CancellationToken ct)
    {
        try { _pipe?.Close(); } catch { }
        _pipe = null;
        try
        {
            // Identification level EXPLICITLY: the Helper's primary identity proof is reading
            // this client's token SID via RunAsClient impersonation. The parameterless overload
            // sends no SQOS at all, leaving the impersonation ceiling to OS defaults — on field
            // boxes that rung then comes back inconclusive and the de-privileged Helper falls
            // through to image-path reads that ACCESS_DENY (the command-pipe strand).
            // Identification is the least privilege that still lets the server read WHO we are.
            var pipe = new NamedPipeClientStream(
                ".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
                System.Security.Principal.TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync((int)timeout.TotalMilliseconds, ct);
            _pipe = pipe;
            _logger.LogInformation("IpcCommandClient connected to Helper on pipe {Name}", _pipeName);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IpcCommandClient failed to connect to pipe {Name}", _pipeName);
            return false;
        }
    }

    /// <summary>
    /// Sends a command and waits for a response. Thread-safe via semaphore.
    /// Reconnects on-demand (bounded) if the pipe is null/broken so a transient Helper drop
    /// self-recovers instead of failing every cycle until the process restarts. Returns null
    /// if reconnect fails or a timeout occurs.
    /// </summary>
    public async Task<IpcResponse?> SendAsync(IpcRequest request, TimeSpan timeout, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_pipe == null || !_pipe.IsConnected)
            {
                if (!await ConnectCoreAsync(ReconnectTimeout, ct))
                {
                    return null;
                }
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            var json = JsonSerializer.Serialize(request);
            await IpcFraming.WriteFrameAsync(_pipe!, json, cts.Token);

            var responseJson = await IpcFraming.ReadFrameAsync(_pipe!, cts.Token);
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
