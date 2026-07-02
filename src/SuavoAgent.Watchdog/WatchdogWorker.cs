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

    /// Path to the post-OTA restart-request file (default: &lt;installDir&gt;\watchdog-restart-request.json,
    /// written by Core's SelfUpdater after a binary swap). The install dir ACL denies interactive-user
    /// writes (Users = ReadAndExecute), so the signal can't be spoofed by a logged-in pharmacy user.
    public string? RestartRequestPath { get; init; }

    /// Re-applies the de-privileged Helper's install-dir read carve-out (BUILTIN\Users:RX on the dir +
    /// Helper.exe). An OTA binary swap (File.Move) drops the per-file ACE and Core (LocalService) lacks
    /// WRITE_DAC to restore it, so the LocalSystem Watchdog does — BEFORE cycling the Broker that
    /// relaunches the Helper, and on its own startup (self-heal). Injectable for tests; the default is
    /// the real icacls grant. Input = install dir; returns whether the grant fully succeeded.
    public Func<string, bool>? ReapplyHelperExeGrant { get; init; }

    /// A RUNNING service whose liveness beacon is older than this is treated as hung (deadlocked /
    /// IPC-unresponsive) and force-cycled. Generous enough to never restart a legitimately slow loop.
    public TimeSpan HangStaleThreshold { get; init; } = TimeSpan.FromSeconds(90);

    /// Directory the supervised processes refresh their liveness beacons in (default: ProgramData liveness dir).
    public string? HangBeaconDirectory { get; init; }

    /// Services that EMIT a liveness beacon and may therefore be hang-checked. A service NOT in this set
    /// is never flagged hung for a missing/stale beacon (it simply doesn't emit) — avoids false-positive
    /// restarts. Defaults to Core only; add Broker once it emits its beacon.
    public IReadOnlyCollection<string> HangCheckedServices { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SuavoAgent.Core" };
}

public sealed class WatchdogWorker : BackgroundService
{
    private readonly ILogger<WatchdogWorker> _logger;
    private readonly IServiceCommand _command;
    private readonly WatchdogOptions _options;
    private readonly Func<string, bool> _reapplyHelperGrant;
    private readonly WatchdogDecisionEngine _engine = new();
    private readonly Dictionary<string, ServiceLedger> _ledgers = new(StringComparer.OrdinalIgnoreCase);
    private WatchdogRemoteRepairTelemetry? _lastRemoteRepair;
    private WatchdogUpdateRestartTelemetry? _lastUpdateRestart;

    // Hang detection: read the supervised processes' liveness beacons, and track when each was first
    // seen RUNNING (startup grace before a missing beacon counts as hung).
    private readonly SuavoAgent.Diagnostics.LivenessBeaconStore _beaconStore;
    private readonly Dictionary<string, DateTimeOffset> _beaconTrackingSince = new(StringComparer.OrdinalIgnoreCase);

    // Only the Broker may be cycled by a post-OTA restart request. Core restarts itself via
    // SCM (Environment.Exit after the swap); the Helper is reconciled by the new Broker's #130.
    private static readonly HashSet<string> AllowedRestartServices =
        new(StringComparer.OrdinalIgnoreCase) { "SuavoAgent.Broker" };

    // Reject (and stop retrying) a restart request older than this. Bounds retries when a
    // dependency never comes up; the normal per-service loop then recovers any stopped service.
    private static readonly TimeSpan UpdateRestartTtl = TimeSpan.FromMinutes(10);

    public WatchdogWorker(ILogger<WatchdogWorker> logger, IServiceCommand command, WatchdogOptions options)
    {
        _logger = logger;
        _command = command;
        _options = options;
        _reapplyHelperGrant = options.ReapplyHelperExeGrant
            ?? (dir => SuavoAgent.Diagnostics.HelperExeAclGrant.Apply(
                    dir, m => _logger.LogInformation("Helper ACL re-grant: {Message}", m)));
        _beaconStore = new SuavoAgent.Diagnostics.LivenessBeaconStore(
            options.HangBeaconDirectory ?? SuavoAgent.Diagnostics.LivenessBeaconStore.DefaultDirectory);
    }

    /// The install dir = where the post-OTA restart-request lives (Program.cs sets RestartRequestPath
    /// to &lt;installDir&gt;\watchdog-restart-request.json); falls back to this process's own directory.
    private string? ResolveInstallDir() =>
        !string.IsNullOrEmpty(_options.RestartRequestPath)
            ? Path.GetDirectoryName(_options.RestartRequestPath)
            : Path.GetDirectoryName(Environment.ProcessPath);

    /// Best-effort re-grant of the Helper's read carve-out (never throws — a failure is logged and the
    /// caller proceeds; the churn, if any, is visible in heartbeat telemetry).
    private void ReapplyHelperGrant(string? installDir, string context)
    {
        if (string.IsNullOrEmpty(installDir))
        {
            _logger.LogDebug("Helper ACL re-grant skipped ({Context}) — install dir unresolved", context);
            return;
        }
        try
        {
            if (!_reapplyHelperGrant(installDir))
                _logger.LogWarning(
                    "Helper ACL re-grant incomplete ({Context}) for {Dir} — Helper may churn until re-granted",
                    context, installDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Helper ACL re-grant threw ({Context}) — proceeding", context);
        }
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

        // Self-heal on every start: an OTA File.Move (or any drift) may have dropped the de-priv
        // Helper's read carve-out, leaving it unable to self-extract its single-file apphost (it then
        // churns and helper_attached never flips). Re-apply it once here as LocalSystem; idempotent.
        // A churning Helper recovers on its next Broker relaunch (~seconds) once the grant is back.
        ReapplyHelperGrant(ResolveInstallDir(), "startup");

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
        // Post-OTA restart first: cycle the Broker onto the just-swapped binary so its #130
        // orphan-Helper reconcile runs and frees the IPC pipe. Time-critical — do it before
        // per-service decisions so the loop observes START_PENDING and doesn't double-act.
        ProcessQueuedUpdateRestartRequest(now);

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
                    if (ok)
                    {
                        // SCM ACCEPTED (START_PENDING) — NOT proof the process stayed up. Mark pending;
                        // the next tick's Decide counts it as a failure if the service still isn't
                        // Running, so a crash-loop (accept → die → accept …) actually escalates to repair.
                        next = next with { RestartPendingLiveness = true };
                        _logger.LogInformation("Restart of {Service} accepted by SCM — awaiting liveness", svc);
                    }
                    else
                    {
                        // SCM refused the start outright — a hard failure, count immediately.
                        next = _engine.RecordRestartResult(next, succeeded: false);
                        _logger.LogError("Restart of {Service} rejected by SCM (consecutive_failures={Count})",
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

            // Hang detection — for beacon-emitting services only: a process the SCM reports RUNNING but
            // whose liveness beacon is stale is deadlocked (IPC-unresponsive), invisible to the SCM-state
            // engine above. Force-cycle it (stop+start); a plain start is a no-op against a RUNNING service.
            if (observed == ServiceState.Running && _options.HangCheckedServices.Contains(svc))
            {
                if (!_beaconTrackingSince.TryGetValue(svc, out var trackingSince))
                {
                    trackingSince = now;
                    _beaconTrackingSince[svc] = now;
                }

                // A beacon written BEFORE we started tracking this run is from a prior process — ignore it
                // (treat as no-beacon so the startup grace applies). Else a stale leftover .beacon file would
                // bypass the grace and force-cycle a slow-starting Core before its first fresh write (Codex P2).
                var beacon = _beaconStore.Read(svc);
                if (beacon is { } b && b < trackingSince) beacon = null;

                var verdict = HangEvaluator.Evaluate(new LivenessSnapshot(
                    observed, beacon, now, _options.HangStaleThreshold, trackingSince));
                if (verdict == LivenessVerdict.Hung)
                {
                    _logger.LogWarning("{Service} RUNNING but liveness beacon stale (>{Stale}s) — HUNG; force-cycling (stop+start)",
                        svc, (int)_options.HangStaleThreshold.TotalSeconds);

                    // Require a successful Stop before Start — a plain Start is a no-op on a RUNNING service,
                    // so claiming recovery without confirming the stop would mask a still-hung process (Codex P2).
                    if (_command.Stop(svc, _options.StartTimeout))
                    {
                        var cycled = _command.Start(svc, _options.StartTimeout);
                        _logger.LogInformation("{Service} hang force-cycle start accepted={Ok}", svc, cycled);
                        _beaconTrackingSince.Remove(svc); // re-grace after the restart so we don't immediately re-trip
                    }
                    else
                    {
                        _logger.LogCritical("{Service} is HUNG and did NOT stop within timeout — harder intervention (repair/kill) needed", svc);
                    }
                }
            }
            else if (observed != ServiceState.Running)
            {
                _beaconTrackingSince.Remove(svc); // not running ⇒ reset grace; the SCM engine owns recovery
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
                RemoteRepair: _lastRemoteRepair,
                UpdateRestart: _lastUpdateRestart);
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

    /// Post-OTA restart handler. Core's SelfUpdater swaps the binaries on disk and regenerates
    /// the manifest, but only Core is restarted by SCM (it Environment.Exit()s); the Broker keeps
    /// running its OLD in-memory binary and looks healthy, so #130 never re-runs and the orphan
    /// Helper keeps the IPC pipe → ipc_unreachable. This cycles the Broker (LocalSystem-only) so it
    /// reloads the new binary. The request file is KEPT until the restart succeeds (Codex Q3): if
    /// the Broker start is rejected because Core is still START_PENDING, we retry next tick.
    private void ProcessQueuedUpdateRestartRequest(DateTimeOffset now)
    {
        var requestPath = _options.RestartRequestPath ?? DefaultRestartRequestPath();
        if (string.IsNullOrEmpty(requestPath) || !File.Exists(requestPath))
            return;

        UpdateRestartRequest request;
        try
        {
            request = ParseUpdateRestartRequest(File.ReadAllText(requestPath));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unreadable update-restart request — discarding");
            RecordUpdateRestart(requestPath, now, "unknown", null, "rejected_unreadable", Array.Empty<string>(), delete: true);
            return;
        }

        // Defense-in-depth validation (the install-dir ACL already blocks interactive-user writes):
        // schema, exact service allowlist, and TTL freshness so a stale/forged file can't loop.
        if (request.SchemaVersion != 1)
        {
            _logger.LogWarning("update-restart schemaVersion {V} unsupported — discarding", request.SchemaVersion);
            RecordUpdateRestart(requestPath, now, request.Version, request.RequestedAt, "rejected_schema", Array.Empty<string>(), delete: true);
            return;
        }

        if (request.Services.Count == 0 || request.Services.Any(s => !AllowedRestartServices.Contains(s)))
        {
            _logger.LogWarning("update-restart names non-allowlisted service(s) — discarding");
            RecordUpdateRestart(requestPath, now, request.Version, request.RequestedAt, "rejected_service", Array.Empty<string>(), delete: true);
            return;
        }

        if (!DateTimeOffset.TryParse(request.RequestedAt,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var requestedAt))
        {
            _logger.LogWarning("update-restart has unparseable requestedAt — discarding");
            RecordUpdateRestart(requestPath, now, request.Version, request.RequestedAt, "rejected_timestamp", Array.Empty<string>(), delete: true);
            return;
        }

        if (now - requestedAt > UpdateRestartTtl)
        {
            _logger.LogWarning(
                "update-restart expired (age {Age:F1}m > {Ttl}m) — discarding; normal loop recovers any stopped service",
                (now - requestedAt).TotalMinutes, UpdateRestartTtl.TotalMinutes);
            RecordUpdateRestart(requestPath, now, request.Version, request.RequestedAt, "expired", Array.Empty<string>(), delete: true);
            return;
        }

        // The OTA binary swap (SelfUpdater.SwapBinaries → File.Move) landed a fresh Helper.exe that
        // dropped the per-file BUILTIN\Users:RX ACE; Core (LocalService) can't restore it. Re-apply it
        // HERE — as LocalSystem, BEFORE cycling the Broker — so the Broker relaunches a Helper that can
        // read+self-extract its single-file apphost (else it churns and helper_attached never flips).
        // Best-effort: a grant failure is logged but never blocks the restart.
        ReapplyHelperGrant(Path.GetDirectoryName(requestPath), $"post-OTA v{request.Version}");

        // Cycle each target service so the new on-disk binary loads.
        var restarted = new List<string>();
        var allOk = true;
        foreach (var svc in request.Services.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (_command.Query(svc) == ServiceState.Running && !_command.Stop(svc, _options.StartTimeout))
                {
                    _logger.LogWarning("update-restart: failed to stop {Service} — retrying next tick", svc);
                    allOk = false;
                    continue;
                }

                if (_command.Start(svc, _options.StartTimeout))
                {
                    restarted.Add(svc);
                    _logger.LogInformation("update-restart: cycled {Service} onto new binary v{Version} (post-OTA)", svc, request.Version);
                }
                else
                {
                    _logger.LogWarning("update-restart: start of {Service} not accepted yet (dependency may be starting) — retrying next tick", svc);
                    allOk = false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "update-restart: error cycling {Service} — retrying next tick", svc);
                allOk = false;
            }
        }

        if (allOk)
        {
            _logger.LogInformation("update-restart complete for v{Version}: {Services}", request.Version, string.Join(",", restarted));
            RecordUpdateRestart(requestPath, now, request.Version, request.RequestedAt, "restarted", restarted, delete: true);
        }
        else
        {
            // Keep the file; retry next tick (bounded by TTL above).
            RecordUpdateRestart(requestPath, now, request.Version, request.RequestedAt, "pending_retry", restarted, delete: false);
        }
    }

    private void RecordUpdateRestart(
        string requestPath, DateTimeOffset now, string version, string? requestedAt,
        string outcome, IReadOnlyList<string> restarted, bool delete)
    {
        _lastUpdateRestart = new WatchdogUpdateRestartTelemetry(
            Present: true,
            Version: version,
            RequestedAt: requestedAt,
            CompletedAt: now.ToString("o"),
            Outcome: outcome,
            ServicesRestarted: restarted);

        if (delete)
        {
            try { File.Delete(requestPath); }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to delete update-restart request"); }
        }
    }

    private static string? DefaultRestartRequestPath()
    {
        var installDir = Path.GetDirectoryName(Environment.ProcessPath);
        return string.IsNullOrEmpty(installDir)
            ? null
            : Path.Combine(installDir, "watchdog-restart-request.json");
    }

    private static UpdateRestartRequest ParseUpdateRestartRequest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var schema = root.TryGetProperty("schemaVersion", out var sv) && sv.ValueKind == JsonValueKind.Number
            ? sv.GetInt32() : 0;
        var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
            ? SanitizeVersion(v.GetString()) : "unknown";
        var requestedAt = root.TryGetProperty("requestedAt", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString() ?? "" : "";

        var services = new List<string>();
        if (root.TryGetProperty("services", out var svcs) && svcs.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in svcs.EnumerateArray())
                if (el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } s)
                    services.Add(s);
        }

        return new UpdateRestartRequest(schema, version, requestedAt, services);
    }

    private static string SanitizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        var chars = value.Where(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_').Take(40).ToArray();
        return chars.Length == 0 ? "unknown" : new string(chars);
    }

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
    WatchdogRemoteRepairTelemetry? RemoteRepair,
    WatchdogUpdateRestartTelemetry? UpdateRestart);

internal sealed record WatchdogUpdateRestartTelemetry(
    bool Present,
    string Version,
    string? RequestedAt,
    string CompletedAt,
    string Outcome,
    IReadOnlyList<string> ServicesRestarted);

internal sealed record UpdateRestartRequest(
    int SchemaVersion,
    string Version,
    string RequestedAt,
    IReadOnlyList<string> Services);

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
