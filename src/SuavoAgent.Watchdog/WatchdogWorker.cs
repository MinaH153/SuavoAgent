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
    public string? RepairRequestPath { get; init; }
}

public sealed class WatchdogWorker : BackgroundService
{
    private readonly ILogger<WatchdogWorker> _logger;
    private readonly IServiceCommand _command;
    private readonly WatchdogOptions _options;
    private readonly WatchdogDecisionEngine _engine = new();
    private readonly Dictionary<string, ServiceLedger> _ledgers = new(StringComparer.OrdinalIgnoreCase);
    private WatchdogRemoteRepairTelemetry? _lastRemoteRepair;

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
        ProcessQueuedRemoteRepairRequest(now);

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
                Services: services,
                RemoteRepair: _lastRemoteRepair);
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

    private void ProcessQueuedRemoteRepairRequest(DateTimeOffset now)
    {
        var requestPath = _options.RepairRequestPath ?? DefaultRepairRequestPath();
        if (!File.Exists(requestPath))
            return;

        var request = ReadRemoteRepairRequest(requestPath, now);
        var bootstrap = _options.BootstrapPath;
        var repairInvoked = false;
        var outcome = "bootstrap_missing";

        try
        {
            if (string.IsNullOrWhiteSpace(bootstrap))
            {
                _logger.LogCritical("Remote repair requested but BootstrapPath is not configured");
            }
            else if (!File.Exists(bootstrap))
            {
                _logger.LogCritical("Remote repair requested but bootstrap script missing at {Path}", bootstrap);
            }
            else
            {
                _logger.LogWarning(
                    "Invoking queued remote bootstrap --repair commandId={CommandId} reason={Reason}",
                    request.CommandId,
                    request.Reason);
                repairInvoked = true;
                outcome = _command.InvokeRepair(bootstrap, _options.RepairTimeout)
                    ? "repair_completed"
                    : "repair_failed";
            }
        }
        catch (Exception ex)
        {
            outcome = "repair_exception";
            _logger.LogError(ex, "Queued remote repair failed");
        }
        finally
        {
            _lastRemoteRepair = new WatchdogRemoteRepairTelemetry(
                Present: true,
                RequestedAt: request.RequestedAt,
                CompletedAt: now.ToString("o"),
                CommandId: request.CommandId,
                Reason: request.Reason,
                Outcome: outcome,
                RepairInvoked: repairInvoked);

            try { File.Delete(requestPath); }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to delete queued remote repair request"); }
        }
    }

    private static RemoteRepairRequest ReadRemoteRepairRequest(string path, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            return new RemoteRepairRequest(
                CommandId: ReadRepairString(root, "commandId", "unknown"),
                Reason: ReadRepairReason(root),
                RequestedAt: ReadRepairString(root, "requestedAt", now.ToString("o")));
        }
        catch
        {
            return new RemoteRepairRequest("unknown", "unreadable_request", now.ToString("o"));
        }
    }

    private static string ReadRepairString(JsonElement root, string propertyName, string fallback)
    {
        if (!root.TryGetProperty(propertyName, out var el) ||
            el.ValueKind != JsonValueKind.String)
            return fallback;

        var value = el.GetString();
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var chars = value
            .Where(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' or ':')
            .Take(80)
            .ToArray();

        return chars.Length == 0 ? fallback : new string(chars);
    }

    private static string ReadRepairReason(JsonElement root)
    {
        var reason = ReadRepairString(root, "reason", "remote_command");
        return reason is
            "remote_command" or
            "watchdog_critical" or
            "cloud_stale" or
            "install_repair" or
            "runtime_health_missing" or
            "operator_requested"
                ? reason
                : "remote_command";
    }

    private static string DefaultRepairRequestPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SuavoAgent",
        "watchdog-repair-request.json");
}

internal sealed record WatchdogTelemetry(
    bool Present,
    string Timestamp,
    IReadOnlyList<WatchdogServiceTelemetry> Services,
    WatchdogRemoteRepairTelemetry? RemoteRepair);

internal sealed record WatchdogRemoteRepairTelemetry(
    bool Present,
    string RequestedAt,
    string CompletedAt,
    string CommandId,
    string Reason,
    string Outcome,
    bool RepairInvoked);

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

internal sealed record RemoteRepairRequest(
    string CommandId,
    string Reason,
    string RequestedAt);
