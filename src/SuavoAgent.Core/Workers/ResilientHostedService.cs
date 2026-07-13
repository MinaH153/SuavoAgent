using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SuavoAgent.Core.Workers;

/// <summary>
/// A <see cref="BackgroundService"/> that supervises its own run loop. If <see cref="RunAsync"/>
/// faults, it restarts in-process with bounded exponential backoff instead of dying silently —
/// the failure mode where the Windows service stays "Running" while the worker is dead (Nadim,
/// 2026-04-25, ~12 days). After <see cref="MaxRestarts"/> consecutive faults it escalates via
/// <see cref="OnEscalateAsync"/> (default: request a cloud/watchdog repair). A clean return from
/// <see cref="RunAsync"/> is an intentional stop (no restart); cancellation propagates cleanly.
/// </summary>
public abstract class ResilientHostedService : BackgroundService
{
    private readonly ILogger _log;
    private readonly WorkerHealthRegistry? _healthRegistry;

    protected ResilientHostedService(ILogger log, WorkerHealthRegistry? healthRegistry = null)
    {
        _log = log;
        _healthRegistry = healthRegistry;
    }

    /// <summary>Short name for logs/telemetry.</summary>
    protected abstract string WorkerName { get; }

    /// <summary>
    /// The worker loop. Throw to fault (triggers a supervised restart); return to stop intentionally.
    /// </summary>
    protected abstract Task RunAsync(CancellationToken stoppingToken);

    /// <summary>Max consecutive faults before escalating instead of restarting.</summary>
    protected virtual int MaxRestarts => 5;

    /// <summary>Kill-switch: when false, faults propagate (original behavior) instead of restarting.</summary>
    protected virtual bool RestartOnFault => true;

    /// <summary>Backoff before the Nth restart. Capped at 5 min to avoid runaway delays.</summary>
    protected virtual TimeSpan BackoffFor(int restart)
    {
        var seconds = Math.Min(300d, 5d * Math.Pow(2, Math.Max(0, restart - 1)));
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>Called once when faults exceed <see cref="MaxRestarts"/>. Override to request repair.</summary>
    protected virtual Task OnEscalateAsync() => Task.CompletedTask;

    /// <summary>Count of faults+restarts this process lifetime (surfaced to the heartbeat).</summary>
    internal int RestartCount { get; private set; }

    /// <summary>True once faults exceeded <see cref="MaxRestarts"/> and escalation fired.</summary>
    internal bool Escalated { get; private set; }

    protected sealed override Task ExecuteAsync(CancellationToken stoppingToken)
        => RunSupervisedAsync(stoppingToken);

    internal async Task RunSupervisedAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunAsync(stoppingToken).ConfigureAwait(false);
                return; // clean return = intentional stop, no restart
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!RestartOnFault)
                    throw;

                RestartCount++;
                var willEscalate = RestartCount > MaxRestarts;
                _log.LogSafeCritical(ex);
                _healthRegistry?.RecordFault(WorkerName, RestartCount, willEscalate, DateTimeOffset.UtcNow);

                if (willEscalate)
                {
                    Escalated = true;
                    _log.LogCritical(
                        "Worker {Worker} exceeded {Max} restarts — escalating to repair",
                        WorkerName, MaxRestarts);
                    try
                    {
                        await OnEscalateAsync().ConfigureAwait(false);
                    }
                    catch (Exception escEx)
                    {
                        _log.LogSafeError(escEx);
                    }
                    return;
                }

                try
                {
                    await Task.Delay(BackoffFor(RestartCount), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }
}
