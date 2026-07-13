using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Maintenance;

namespace SuavoAgent.Watchdog;

public sealed class WatchdogOptions
{
    public IReadOnlyList<string> WatchedServices { get; init; } = new[] { "SuavoAgent.Core", "SuavoAgent.Broker" };
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan RepairTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public string? TelemetryPath { get; init; }
    public string? RepairRequestPath { get; init; }
    public string? RemoteRepairReplayLedgerPath { get; init; }
    public string? PioneerRxApprovalRequestPath { get; init; }
    public string? PioneerRxApprovalBootstrapRequestPath { get; init; }

    /// <summary>
    /// Production defaults to the compiled command-signing trust registry. Tests may inject
    /// an isolated key without weakening or replacing the production default.
    /// </summary>
    public IReadOnlyDictionary<string, string>? RemoteRepairTrustedPublicKeys { get; init; }

    /// <summary>Deterministic race-injection seam used only by boundary tests.</summary>
    public Action? RemoteRepairAfterValidationForTests { get; init; }

    /// Untrusted LocalService-writable incoming update root and signed request. ReplayLedgerPath is
    /// only a short launch lease; Maintenance keeps the authoritative SYSTEM/Admin-only replay state.
    public string? UpdateRoot { get; init; }
    public string? ActivationRequestPath { get; init; }
    public string? ReplayLedgerPath { get; init; }
    public string? ExpectedAgentId { get; init; }
    public string? ExpectedMachineFingerprint { get; init; }
    public string? CurrentVersion { get; init; }
    public string? MaintenanceRoot { get; init; }
    public string? ActiveClaimPath { get; init; }
    public string? ActivationCompletionPath { get; init; }
    public Func<string, string, bool>? TerminateStaleUpdateRunner { get; init; }

    /// Re-applies the de-privileged Helper's install-dir read carve-out on Watchdog startup. The
    /// SYSTEM maintenance transaction also reasserts this ACL before starting a replacement cohort.
    /// Injectable for tests; input = install directory, result = fully succeeded.
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

public sealed partial class WatchdogWorker : BackgroundService
{
    private readonly ILogger<WatchdogWorker> _logger;
    private readonly IServiceCommand _command;
    private readonly WatchdogOptions _options;
    private readonly Func<string, bool> _reapplyHelperGrant;
    private readonly WatchdogDecisionEngine _engine = new();
    private readonly Dictionary<string, ServiceLedger> _ledgers = new(StringComparer.OrdinalIgnoreCase);
    private WatchdogRemoteRepairTelemetry? _lastRemoteRepair;
    private readonly UpdateActivationGate _updateActivationGate;
    private readonly UpdateReplayLedger _updateReplayLedger;
    private readonly RemoteRepairGate _remoteRepairGate;
    private readonly RemoteRepairReplayLedger _remoteRepairReplayLedger;

    // Hang detection: read the supervised processes' liveness beacons, and track when each was first
    // seen RUNNING (startup grace before a missing beacon counts as hung).
    private readonly SuavoAgent.Diagnostics.LivenessBeaconStore _beaconStore;
    private readonly Dictionary<string, DateTimeOffset> _beaconTrackingSince = new(StringComparer.OrdinalIgnoreCase);

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
        _updateActivationGate = new UpdateActivationGate(
            RemoteCommandTrust.CreateProductionKeyRegistry(),
            UpdateActivationContract.ProductionUpdatePublicKeyDer,
            logger);
        var updateRoot = options.UpdateRoot ?? UpdateActivationContract.DefaultUpdateRoot();
        _updateReplayLedger = new UpdateReplayLedger(
            options.ReplayLedgerPath ?? Path.Combine(
                updateRoot,
                UpdateActivationContract.CoordinatorDirectoryName,
                UpdateActivationContract.ReplayLedgerFileName));
        _remoteRepairGate = new RemoteRepairGate(
            options.RemoteRepairTrustedPublicKeys ?? RemoteCommandTrust.CreateProductionKeyRegistry(),
            logger);
        var maintenanceRoot = options.MaintenanceRoot ?? UpdateActivationContract.DefaultMaintenanceRoot();
        _remoteRepairReplayLedger = new RemoteRepairReplayLedger(
            options.RemoteRepairReplayLedgerPath ?? Path.Combine(
                maintenanceRoot,
                RemoteRepairContract.ReplayLedgerFileName));
    }

    private static string? ResolveInstallDir() =>
        Path.GetDirectoryName(Environment.ProcessPath);

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
            _logger.LogSafeWarning(ex);
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
                _logger.LogSafeError(ex);
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
        ProcessQueuedPioneerRxApprovalRequest();
        ProcessQueuedPioneerRxApprovalBootstrap();

        ProcessActiveUpdateClaim(now);

        ProcessQueuedUpdateActivationRequest(now);

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
                    _logger.LogWarning(
                        "Invoking native maintenance repair for {Service} (reason={Reason})",
                        svc,
                        decision.Reason);
                    var repaired = _command.InvokeRepair(
                        MaintenanceReason.ServiceRestartFailed,
                        _options.RepairTimeout);
                    repairCompleted = repaired;
                    _logger.LogInformation("Repair run for {Service} completed={Completed}", svc, repaired);
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

    private void ProcessQueuedUpdateActivationRequest(DateTimeOffset now)
    {
        var updateRoot = _options.UpdateRoot ?? UpdateActivationContract.DefaultUpdateRoot();
        var requestPath = _options.ActivationRequestPath
                          ?? Path.Combine(updateRoot, UpdateActivationContract.ActivationRequestFileName);
        if (!File.Exists(requestPath)) return;

        if (string.IsNullOrWhiteSpace(_options.ExpectedAgentId) ||
            string.IsNullOrWhiteSpace(_options.ExpectedMachineFingerprint) ||
            string.IsNullOrWhiteSpace(_options.CurrentVersion))
        {
            _logger.LogCritical(
                "SYSTEM update request present but installed identity/version is unavailable; refusing activation");
            return;
        }

        var validation = _updateActivationGate.Validate(
            requestPath,
            updateRoot,
            _updateReplayLedger,
            _options.ExpectedAgentId,
            _options.ExpectedMachineFingerprint,
            _options.CurrentVersion,
            now);
        if (!validation.IsValid)
        {
            if (validation.Code == "request_replay")
            {
                _logger.LogDebug("SYSTEM update request already reserved/launched; awaiting coordinator completion");
                return;
            }

            _logger.LogError("SYSTEM update activation rejected: {Code}", validation.Code);
            // Permanent invalid/stale inputs cannot become valid. Removing only the request unblocks
            // a future signed command; untrusted staging is inert and may be scavenged separately.
            try { File.Delete(requestPath); }
            catch (Exception ex) { _logger.LogSafeWarning(ex); }
            return;
        }

        var replayId = validation.ReplayId!;
        try
        {
            if (!_updateReplayLedger.TryReserve(replayId, now))
            {
                _logger.LogWarning("SYSTEM update replay reservation lost a race; refusing duplicate launch");
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogSafeCritical(ex);
            return;
        }

        if (!_command.InvokeUpdateCoordinator(requestPath))
        {
            try { _updateReplayLedger.Release(replayId); }
            catch (Exception ex) { _logger.LogSafeCritical(ex); }
            _logger.LogError("Trusted native SYSTEM update coordinator failed to launch");
            return;
        }

        _logger.LogWarning(
            "Launched trusted native SYSTEM update coordinator for v{Version}; awaiting durable completion",
            validation.Manifest!.Version);
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
                UpdateActivation: _lastUpdateActivation);
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
            _logger.LogSafeDebug(ex);
        }
    }

    private static string DefaultTelemetryPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SuavoAgent",
        "watchdog-health.json");

    private void ProcessQueuedRemoteRepairRequest(DateTimeOffset now)
    {
        var requestPath = _options.RepairRequestPath ?? DefaultRepairRequestPath();
        if (!RemoteRepairRequestEntryExists(requestPath))
            return;

        var gate = _remoteRepairGate.Validate(
            requestPath,
            _options.ExpectedAgentId,
            _options.ExpectedMachineFingerprint,
            now);
        if (!gate.IsValid)
        {
            _logger.LogWarning(
                "Queued remote repair rejected before maintenance invocation code={Code}",
                gate.Code);
            _lastRemoteRepair = RejectedRemoteRepairTelemetry(now, gate.Code);
            ConsumeRemoteRepairRequest(requestPath, "rejected", gate.RequestDigest);
            return;
        }

        _options.RemoteRepairAfterValidationForTests?.Invoke();
        var request = gate.Request!;
        var replay = _remoteRepairReplayLedger.TryRecord(gate.ReplayId!, now);
        if (!replay.Recorded)
        {
            _logger.LogWarning(
                "Queued remote repair rejected before maintenance invocation code={Code}",
                replay.Code);
            _lastRemoteRepair = new WatchdogRemoteRepairTelemetry(
                Present: true,
                RequestedAt: request.RequestedAtUtc,
                CompletedAt: now.ToString("O"),
                CommandId: request.CommandId,
                Reason: request.Reason,
                Outcome: replay.Code,
                RepairInvoked: false);
            ConsumeRemoteRepairRequest(requestPath, "rejected", gate.RequestDigest);
            return;
        }

        var repairInvoked = false;
        var outcome = "repair_failed";

        try
        {
            _logger.LogWarning(
                "Invoking queued native maintenance repair commandId={CommandId} reason={Reason}",
                request.CommandId,
                request.Reason);
            repairInvoked = true;
            outcome = _command.InvokeRepair(
                    MaintenanceReason.RemoteRepairRequested,
                    _options.RepairTimeout)
                ? "repair_completed"
                : "repair_failed";
        }
        catch (Exception ex)
        {
            outcome = "repair_exception";
            _logger.LogSafeError(ex);
        }
        finally
        {
            _lastRemoteRepair = new WatchdogRemoteRepairTelemetry(
                Present: true,
                RequestedAt: request.RequestedAtUtc,
                CompletedAt: DateTimeOffset.UtcNow.ToString("O"),
                CommandId: request.CommandId,
                Reason: request.Reason,
                Outcome: outcome,
                RepairInvoked: repairInvoked);

            ConsumeRemoteRepairRequest(requestPath, "consumed", gate.RequestDigest);
        }
    }

    private void ProcessQueuedPioneerRxApprovalRequest()
    {
        var requestPath = _options.PioneerRxApprovalRequestPath
                          ?? PioneerRxApprovalMaintenanceContract.DefaultRequestPath();
        if (!File.Exists(requestPath)) return;

        try
        {
            var installed = _command.InvokePioneerRxApprovalInstaller(
                requestPath,
                _options.RepairTimeout);
            if (installed)
                _logger.LogInformation(
                    "SYSTEM PioneerRx approval transaction completed; Core will acknowledge its signed command");
            else
                _logger.LogWarning(
                    "SYSTEM PioneerRx approval transaction did not complete; request remains retryable");
        }
        catch (Exception exception)
        {
            _logger.LogSafeError(exception);
        }
    }

    private void ProcessQueuedPioneerRxApprovalBootstrap()
    {
        var requestPath = _options.PioneerRxApprovalBootstrapRequestPath
                          ?? PioneerRxApprovalBootstrapContract.DefaultRequestPath();
        if (!File.Exists(requestPath)) return;
        try
        {
            if (!_command.InvokePioneerRxApprovalBootstrap(requestPath, _options.RepairTimeout))
                _logger.LogWarning(
                    "SYSTEM PioneerRx human-approval bootstrap did not complete; request remains retryable");
        }
        catch (Exception exception)
        {
            _logger.LogSafeError(exception);
        }
    }

    private static WatchdogRemoteRepairTelemetry RejectedRemoteRepairTelemetry(
        DateTimeOffset now,
        string rejectionCode) => new(
            Present: true,
            RequestedAt: now.ToString("O"),
            CompletedAt: now.ToString("O"),
            CommandId: "not_available",
            Reason: "validation_rejected",
            Outcome: rejectionCode,
            RepairInvoked: false);

    private static bool RemoteRepairRequestEntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch
        {
            // Access, malformed-path, and other unexpected failures are real entries/problems
            // for the privileged gate to reject and surface, never reasons to silently ignore.
            return true;
        }
    }

    private void ConsumeRemoteRepairRequest(
        string path,
        string disposition,
        string? expectedDigest)
    {
        if (!string.IsNullOrWhiteSpace(expectedDigest) &&
            !CurrentRemoteRepairRequestMatches(path, expectedDigest))
        {
            _logger.LogInformation(
                "Remote repair request changed after validation; leaving the newer entry for the next tick");
            return;
        }

        try { File.Delete(path); }
        catch (Exception)
        {
            try
            {
                var quarantinePath = $"{path}.{disposition}";
                File.Move(path, quarantinePath, overwrite: true);
                _logger.LogWarning(
                    "Remote repair request could not be deleted and was quarantined disposition={Disposition}",
                    disposition);
            }
            catch (Exception quarantineException)
            {
                _logger.LogSafeError(quarantineException);
            }
        }
    }

    private static bool CurrentRemoteRepairRequestMatches(string path, string expectedDigest)
    {
        try
        {
            var current = BoundedRegularFile.Read(path, RemoteRepairContract.MaxRequestBytes);
            var actual = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(current)).ToLowerInvariant();
            return actual.Length == expectedDigest.Length &&
                   System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                       Convert.FromHexString(actual),
                       Convert.FromHexString(expectedDigest));
        }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch { return false; }
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
    WatchdogUpdateActivationTelemetry? UpdateActivation);

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
