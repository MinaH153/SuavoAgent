using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SuavoAgent.Diagnostics;

namespace SuavoAgent.Core.Workers;

/// <summary>
/// Refreshes Core's liveness beacon on a fixed cadence FROM THE MAIN HOST LOOP — so the timestamp
/// only advances while the process is actually servicing. If Core deadlocks (IPC waiter, DB lock) the
/// beacon goes stale even though the SCM still reports RUNNING, and the Watchdog force-cycles it.
/// Interval is well under the Watchdog's stale threshold so a single missed tick never false-trips.
/// </summary>
public sealed class LivenessBeaconWorker : BackgroundService
{
    public const string Component = "SuavoAgent.Core";

    private readonly ILogger<LivenessBeaconWorker> _logger;
    private readonly LivenessBeaconStore _store;
    private readonly TimeSpan _interval;

    public LivenessBeaconWorker(ILogger<LivenessBeaconWorker> logger, TimeSpan? interval = null, string? beaconDirectory = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _store = new LivenessBeaconStore(beaconDirectory ?? LivenessBeaconStore.DefaultDirectory);
        _interval = interval ?? TimeSpan.FromSeconds(20);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Write immediately on startup so the beacon exists before the Watchdog's grace window expires.
        WriteOnce();
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(_interval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            WriteOnce();
        }
    }

    private void WriteOnce()
    {
        try { _store.Write(Component, DateTimeOffset.UtcNow); }
        catch (Exception ex) { _logger.LogSafeDebug(ex); }
    }
}
