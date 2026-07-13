using Serilog;

namespace SuavoAgent.Helper.Behavioral;

/// <summary>
/// Debounces StructureChanged bursts and executes at most one resnapshot at a
/// time on a worker thread. New signals received during a capture coalesce
/// into one later capture.
/// </summary>
internal sealed class CoalescingResnapshotScheduler : IDisposable
{
    private readonly Action _resnapshot;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _minimumInterval;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _stopToken;

    private long _requestedVersion;
    private long _completedVersion;
    private long _lastCaptureTimestamp;
    private int _workerScheduled;
    private int _disposed;

    internal CoalescingResnapshotScheduler(
        Action resnapshot,
        ILogger logger,
        TimeSpan? debounce = null,
        TimeSpan? minimumInterval = null)
    {
        _resnapshot = resnapshot ?? throw new ArgumentNullException(nameof(resnapshot));
        _logger = logger.ForContext<CoalescingResnapshotScheduler>();
        _stopToken = _stop.Token;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(350);
        _minimumInterval = minimumInterval ?? TimeSpan.FromSeconds(2);
        if (_debounce < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(debounce));
        if (_minimumInterval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(minimumInterval));
    }

    internal long RequestedCount => Volatile.Read(ref _requestedVersion);
    internal long CompletedThrough => Volatile.Read(ref _completedVersion);

    internal void Request()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Interlocked.Increment(ref _requestedVersion);
        EnsureWorker();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _stop.Dispose();
    }

    private void EnsureWorker()
    {
        if (Interlocked.CompareExchange(ref _workerScheduled, 1, 0) != 0) return;
        _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_stopToken.IsCancellationRequested)
            {
                var targetVersion = Volatile.Read(ref _requestedVersion);
                await Task.Delay(_debounce, _stopToken).ConfigureAwait(false);

                // A newer request arrived inside the debounce window.
                if (targetVersion != Volatile.Read(ref _requestedVersion))
                    continue;

                var lastTimestamp = Volatile.Read(ref _lastCaptureTimestamp);
                if (lastTimestamp != 0)
                {
                    var elapsed = TimeSpan.FromSeconds(
                        (System.Diagnostics.Stopwatch.GetTimestamp() - lastTimestamp)
                        / (double)System.Diagnostics.Stopwatch.Frequency);
                    var remaining = _minimumInterval - elapsed;
                    if (remaining > TimeSpan.Zero)
                        await Task.Delay(remaining, _stopToken).ConfigureAwait(false);
                }

                // Signals that arrived while rate-limit waiting are covered by
                // the eventual capture; debounce the newest version instead of
                // running an obsolete capture followed by another one.
                if (targetVersion != Volatile.Read(ref _requestedVersion))
                    continue;

                // The callback always runs off the UIA event thread. This is
                // the only worker, so even a slow 5000-node walk cannot overlap
                // another StructureChanged-triggered walk.
                try
                {
                    _resnapshot();
                }
                catch (Exception ex)
                {
                    _logger.Warning(
                        "Structure resnapshot failed ({ExceptionType})",
                        ex.GetType().FullName);
                }
                finally
                {
                    Volatile.Write(ref _lastCaptureTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());
                    Volatile.Write(ref _completedVersion, targetVersion);
                }

                if (targetVersion == Volatile.Read(ref _requestedVersion))
                    return;
            }
        }
        catch (OperationCanceledException) when (_stopToken.IsCancellationRequested)
        {
        }
        finally
        {
            Interlocked.Exchange(ref _workerScheduled, 0);
            if (!_stopToken.IsCancellationRequested
                && Volatile.Read(ref _completedVersion) < Volatile.Read(ref _requestedVersion))
            {
                EnsureWorker();
            }
        }
    }
}
