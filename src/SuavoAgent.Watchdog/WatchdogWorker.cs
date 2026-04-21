using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SuavoAgent.Watchdog;

public sealed class WatchdogOptions
{
    public IReadOnlyList<string> WatchedServices { get; init; } = new[] { "SuavoAgent.Core", "SuavoAgent.Broker" };
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan RepairTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public string? BootstrapPath { get; init; }
}

public sealed class WatchdogWorker : BackgroundService
{
    private readonly ILogger<WatchdogWorker> _logger;
    private readonly IServiceCommand _command;
    private readonly WatchdogOptions _options;
    private readonly WatchdogDecisionEngine _engine = new();
    private readonly Dictionary<string, ServiceLedger> _ledgers = new(StringComparer.OrdinalIgnoreCase);

    public WatchdogWorker(ILogger<WatchdogWorker> logger, IServiceCommand command, WatchdogOptions options)
    {
        _logger = logger;
        _command = command;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var svc in _options.WatchedServices)
        {
            _ledgers[svc] = ServiceLedger.Initial(svc, now);
        }

        _logger.LogInformation(
            "Watchdog started — watching {Services}, poll={Poll}s, grace={Grace}m, escalate={EscalateAfter} failures",
            string.Join(",", _options.WatchedServices),
            _options.PollInterval.TotalSeconds,
            _engine.UnhealthyGrace.TotalMinutes,
            _engine.EscalateAfterConsecutiveFailures);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                TickOnce(DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Watchdog tick failed — swallowing so the loop survives");
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Watchdog stopping");
    }

    internal void TickOnce(DateTimeOffset now)
    {
        foreach (var svc in _options.WatchedServices)
        {
            var observed = _command.Query(svc);
            var ledger = _ledgers[svc];
            var (decision, next) = _engine.Decide(ledger, observed, now);

            _logger.LogDebug("{Service} observed={Observed} action={Action} reason={Reason}",
                svc, observed, decision.Action, decision.Reason);

            switch (decision.Action)
            {
                case DecisionAction.AttemptRestart:
                    _logger.LogWarning("Restarting {Service} — unhealthy_since={Since}", svc, ledger.UnhealthySince);
                    var ok = _command.Start(svc, _options.StartTimeout);
                    next = _engine.RecordRestartResult(next, ok);
                    if (ok)
                    {
                        _logger.LogInformation("Restart of {Service} accepted by SCM", svc);
                    }
                    else
                    {
                        _logger.LogError("Restart of {Service} failed (consecutive_failures={Count})",
                            svc, next.ConsecutiveRestartFailures);
                    }
                    break;

                case DecisionAction.EscalateRepair:
                    var bootstrap = _options.BootstrapPath;
                    if (string.IsNullOrWhiteSpace(bootstrap))
                    {
                        _logger.LogCritical("Repair escalation requested for {Service} but BootstrapPath is not configured — firing Alert", svc);
                    }
                    else if (!File.Exists(bootstrap))
                    {
                        _logger.LogCritical("Repair escalation requested for {Service} but bootstrap script missing at {Path}", svc, bootstrap);
                    }
                    else
                    {
                        _logger.LogWarning("Invoking bootstrap --repair for {Service} (reason={Reason})", svc, decision.Reason);
                        var repaired = _command.InvokeRepair(bootstrap, _options.RepairTimeout);
                        _logger.LogInformation("Repair run for {Service} completed={Completed}", svc, repaired);
                    }
                    break;

                case DecisionAction.ObserveStartPending:
                    _logger.LogInformation("{Service} is START_PENDING — waiting out Windows", svc);
                    break;

                case DecisionAction.Alert:
                    _logger.LogCritical("{Service} unhealthy with no automatic remediation path — human intervention required", svc);
                    break;
            }

            _ledgers[svc] = next;
        }
    }

    internal IReadOnlyDictionary<string, ServiceLedger> LedgersForTests => _ledgers;
}
