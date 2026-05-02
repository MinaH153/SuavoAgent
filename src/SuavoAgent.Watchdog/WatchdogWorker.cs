using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SuavoAgent.Watchdog;

public sealed class WatchdogOptions
{
    public IReadOnlyList<string> WatchedServices { get; init; } = new[] { "SuavoAgent.Core", "SuavoAgent.Broker" };
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan RepairTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public string? BootstrapPath { get; init; }
    public string? TelemetryPath { get; init; }
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
        var serviceSnapshots = new List<WatchdogServiceTelemetry>();

        foreach (var svc in _options.WatchedServices)
        {
            var observed = _command.Query(svc);
            var ledger = _ledgers[svc];
            var (decision, next) = _engine.Decide(ledger, observed, now);
            bool? restartAccepted = null;
            bool? repairCompleted = null;

            _logger.LogDebug("{Service} observed={Observed} action={Action} reason={Reason}",
                svc, observed, decision.Action, decision.Reason);

            switch (decision.Action)
            {
                case DecisionAction.AttemptRestart:
                    _logger.LogWarning("Restarting {Service} — unhealthy_since={Since}", svc, ledger.UnhealthySince);
                    var ok = _command.Start(svc, _options.StartTimeout);
                    restartAccepted = ok;
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
                        repairCompleted = repaired;
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
            serviceSnapshots.Add(new WatchdogServiceTelemetry(
                ServiceName: svc,
                ObservedState: observed.ToString(),
                Action: decision.Action.ToString(),
                Reason: decision.Reason,
                UnhealthySince: next.UnhealthySince?.ToString("o"),
                LastRestartAttemptAt: next.LastRestartAttemptAt?.ToString("o"),
                ConsecutiveRestartFailures: next.ConsecutiveRestartFailures,
                RepairInvocations: next.RepairInvocations,
                RestartAccepted: restartAccepted,
                RepairCompleted: repairCompleted));
        }

        WriteTelemetry(now, serviceSnapshots);
    }

    internal IReadOnlyDictionary<string, ServiceLedger> LedgersForTests => _ledgers;

    private void WriteTelemetry(DateTimeOffset now, IReadOnlyList<WatchdogServiceTelemetry> services)
    {
        try
        {
            var path = _options.TelemetryPath ?? DefaultTelemetryPath();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var payload = new WatchdogTelemetry(
                Present: true,
                Timestamp: now.ToString("o"),
                Services: services);
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false,
            });

            var tmp = $"{path}.tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(path))
                File.Replace(tmp, path, null);
            else
                File.Move(tmp, path);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Watchdog telemetry write failed");
        }
    }

    private static string DefaultTelemetryPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SuavoAgent",
        "watchdog-health.json");
}

internal sealed record WatchdogTelemetry(
    bool Present,
    string Timestamp,
    IReadOnlyList<WatchdogServiceTelemetry> Services);

internal sealed record WatchdogServiceTelemetry(
    string ServiceName,
    string ObservedState,
    string Action,
    string Reason,
    string? UnhealthySince,
    string? LastRestartAttemptAt,
    int ConsecutiveRestartFailures,
    int RepairInvocations,
    bool? RestartAccepted,
    bool? RepairCompleted);
