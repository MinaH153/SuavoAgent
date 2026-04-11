using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Workers;

public sealed class HeartbeatWorker : BackgroundService
{
    private readonly ILogger<HeartbeatWorker> _logger;
    private readonly AgentOptions _options;
    private readonly SuavoCloudClient? _cloudClient;
    private readonly IServiceProvider _serviceProvider;
    private readonly AgentStateDb _stateDb;
    private readonly SignedCommandVerifier? _commandVerifier;
    private int _consecutiveFailures;
    private bool _updateInProgress;
    private long? _decommissionPendingSince;
    private string? _lastUpdateChannel;
    private readonly DateTimeOffset _startTime = DateTimeOffset.UtcNow;
    private int _helperConsecutiveFailures;
    private bool _lastAuditChainValid = true;
    private int _lastRxCount;
    private DateTimeOffset? _lastSyncAt;

    public HeartbeatWorker(
        ILogger<HeartbeatWorker> logger,
        IOptions<AgentOptions> options,
        IServiceProvider serviceProvider,
        AgentStateDb stateDb)
    {
        _logger = logger;
        _options = options.Value;
        _serviceProvider = serviceProvider;
        _stateDb = stateDb;
        _cloudClient = serviceProvider.GetService<SuavoCloudClient>();

        // TODO: Generate separate command-signing key pair at install time. Currently shares update key.
        var agentId = _options.AgentId ?? "";
        var fingerprint = _options.MachineFingerprint ?? "";
        if (!string.IsNullOrEmpty(agentId))
        {
            _commandVerifier = new SignedCommandVerifier(
                new Dictionary<string, string> { ["suavo-cmd-v1"] = SelfUpdater.UpdatePublicKeyDer },
                agentId, fingerprint);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_cloudClient == null)
        {
            _logger.LogWarning("Heartbeat disabled — no cloud client configured");
            return;
        }

        // Cleanup old binary from a previous self-update
        SelfUpdater.CleanupOldBinary(_logger);

        // Verify audit chain integrity on startup (HIPAA 164.312(c))
        _lastAuditChainValid = _stateDb.VerifyAuditChain();
        if (!_lastAuditChainValid)
            _logger.LogWarning("HIPAA ALERT: Audit chain integrity verification FAILED");

        _logger.LogInformation("Heartbeat worker started. Interval: {Interval}s", _options.HeartbeatIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            _stateDb.PruneOldNonces(TimeSpan.FromMinutes(10));

            try
            {
                // Read Rx detection state if available
                var rxWorker = _serviceProvider.GetService<RxDetectionWorker>();
                var sqlConnected = rxWorker?.IsSqlConnected ?? false;
                var rxReadyCount = rxWorker?.LastDetectedCount ?? 0;
                _lastRxCount = rxReadyCount;

                // Read Helper IPC state
                var ipcServer = _serviceProvider.GetService<IpcPipeServer>();

                var payload = new
                {
                    agentId = _options.AgentId,
                    version = _options.Version,
                    pharmacyId = _options.PharmacyId,
                    updateChannel = _lastUpdateChannel ?? "stable",
                    machineFingerprint = _options.MachineFingerprint,
                    uptimeSeconds = (long)(DateTimeOffset.UtcNow - _startTime).TotalSeconds,
                    memoryMb = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024),
                    status = "online",
                    pioneerrxStatus = sqlConnected ? "connected" : "not_connected",
                    sql = new
                    {
                        connected = sqlConnected,
                        lastRxCount = _lastRxCount
                    },
                    helper = new
                    {
                        attached = ipcServer?.IsConnected ?? false,
                        consecutiveFailures = _helperConsecutiveFailures
                    },
                    writeback = new
                    {
                        pending = _stateDb.GetPendingWritebacks().Count,
                        manualReview = 0
                    },
                    audit = new
                    {
                        chainValid = _lastAuditChainValid,
                        entryCount = _stateDb.GetAuditEntryCount()
                    },
                    sync = new
                    {
                        unsyncedBatches = _stateDb.GetPendingBatches().Count,
                        deadLetterCount = _stateDb.GetDeadLetterCount(),
                        lastSyncAt = _lastSyncAt?.ToString("o")
                    }
                };

                var response = await _cloudClient.HeartbeatAsync(payload, stoppingToken);
                _consecutiveFailures = 0;
                _logger.LogDebug("Heartbeat OK");

                // Echo updateChannel from server for canary rollout tracking
                if (response.HasValue &&
                    response.Value.TryGetProperty("data", out var respData) &&
                    respData.TryGetProperty("updateChannel", out var channel))
                {
                    _lastUpdateChannel = channel.GetString();
                }

                // Decommission timeout check (1 hour auto-cancel)
                if (_decommissionPendingSince != null &&
                    Stopwatch.GetElapsedTime(_decommissionPendingSince.Value) > TimeSpan.FromHours(1))
                {
                    _logger.LogInformation("Decommission timed out — cancelling");
                    _stateDb.AppendChainedAuditEntry(new AuditEntry(
                        _options.AgentId ?? "", "decommission", "DecommissionPending", "", "decommission_cancelled_timeout"));
                    _decommissionPendingSince = null;
                }

                // Process signed commands (fetch_patient, decommission, update)
                // All destructive actions require ECDSA-signed command envelope.
                if (response.HasValue)
                    await ProcessSignedCommandAsync(response.Value, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _logger.LogWarning(ex, "Heartbeat failed ({Failures} consecutive)", _consecutiveFailures);
            }

            var jitter = Random.Shared.Next(0, _options.HeartbeatJitterSeconds * 1000);
            var delay = TimeSpan.FromSeconds(_options.HeartbeatIntervalSeconds) + TimeSpan.FromMilliseconds(jitter);

            if (_consecutiveFailures > 0)
            {
                var backoff = Math.Min(_consecutiveFailures * _options.HeartbeatIntervalSeconds, 300);
                delay = TimeSpan.FromSeconds(backoff) + TimeSpan.FromMilliseconds(jitter);
            }

            await Task.Delay(delay, stoppingToken);
        }

        _logger.LogInformation("Heartbeat worker stopped");
    }

    private async Task ProcessSignedCommandAsync(JsonElement response, CancellationToken ct)
    {
        try
        {
            if (!response.TryGetProperty("data", out var data)) return;
            if (!data.TryGetProperty("signedCommand", out var scEl)) return;
            if (scEl.ValueKind == JsonValueKind.Null) return;

            if (_commandVerifier is null)
            {
                _logger.LogWarning("Signed command received but verifier not configured (no AgentId)");
                return;
            }

            var cmd = new SignedCommand(
                Command: scEl.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "",
                AgentId: scEl.TryGetProperty("agentId", out var a) ? a.GetString() ?? "" : "",
                MachineFingerprint: scEl.TryGetProperty("machineFingerprint", out var m) ? m.GetString() ?? "" : "",
                Timestamp: scEl.TryGetProperty("timestamp", out var t) ? t.GetString() ?? "" : "",
                Nonce: scEl.TryGetProperty("nonce", out var n) ? n.GetString() ?? "" : "",
                KeyId: scEl.TryGetProperty("keyId", out var k) ? k.GetString() ?? "" : "",
                Signature: scEl.TryGetProperty("signature", out var s) ? s.GetString() ?? "" : "");

            // Persistent nonce check (survives restarts)
            if (!_stateDb.TryRecordNonce(cmd.Nonce))
            {
                _logger.LogWarning("Command nonce already used: {Nonce}", cmd.Nonce);
                return;
            }

            var result = _commandVerifier.Verify(cmd);
            if (!result.IsValid)
            {
                _logger.LogWarning("Signed command rejected: {Reason}", result.Reason);
                return;
            }

            _logger.LogInformation("Verified signed command: {Command} nonce:{Nonce}", cmd.Command, cmd.Nonce);

            switch (cmd.Command)
            {
                case "fetch_patient":
                    await HandleFetchPatientAsync(scEl, cmd, ct);
                    break;
                case "decommission":
                    await HandleDecommissionAsync(ct);
                    break;
                case "update":
                    await HandleUpdateAsync(scEl, ct);
                    break;
                default:
                    _logger.LogDebug("Unknown signed command: {Command}", cmd.Command);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Signed command processing failed");
        }
    }

    private async Task HandleFetchPatientAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        // rxNumber and requesterId are nested under the "data" sub-object of the signed command envelope
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var rxNumber = dataEl.TryGetProperty("rxNumber", out var rx) ? rx.GetString() ?? "" : "";
        var requesterId = dataEl.TryGetProperty("requesterId", out var ri) ? ri.GetString() ?? "" : "";

        if (string.IsNullOrEmpty(rxNumber) || rxNumber.Length > 20)
        {
            _logger.LogWarning("fetch_patient: invalid rxNumber format");
            return;
        }

        // Audit PHI access before touching any patient data
        _stateDb.AppendChainedAuditEntry(new AuditEntry(
            TaskId: rxNumber,
            EventType: "phi_access",
            FromState: "",
            ToState: "",
            Trigger: "fetch_patient",
            CommandId: cmd.Nonce,
            RequesterId: requesterId,
            RxNumber: rxNumber));

        // Get SQL engine from RxDetectionWorker
        var rxWorker = _serviceProvider.GetService<RxDetectionWorker>();
        var sqlEngine = rxWorker?.SqlEngine;

        if (sqlEngine is null || !rxWorker!.IsSqlConnected)
        {
            _logger.LogWarning("fetch_patient: SQL not connected — cannot query patient for Rx {Rx}", rxNumber);
            return;
        }

        var details = await sqlEngine.PullPatientForRxAsync(rxNumber, ct);

        if (details is null)
        {
            _logger.LogInformation("fetch_patient: no patient found for Rx {Rx}", rxNumber);
            return;
        }

        if (_cloudClient is not null)
        {
            await _cloudClient.SendPatientDetailsAsync(rxNumber, details, cmd.Nonce, ct);
            _logger.LogInformation("fetch_patient: sent patient details for Rx {Rx}", rxNumber);
        }
    }

    /// <summary>
    /// Two-phase decommission with audit archive preservation (HIPAA 164.530(j)).
    /// Phase 1: enter pending state, audit logged, wait 5+ minutes.
    /// Phase 2: archive audit chain to cloud, verify ACK digest, then cleanup.
    /// Blocks if archive upload fails. 1h timeout auto-cancels in main loop.
    /// Only reachable via ECDSA-signed command envelope.
    /// </summary>
    private async Task HandleDecommissionAsync(CancellationToken ct)
    {
        try
        {
            var agentId = _options.AgentId ?? "";

            // Phase 1: first decommission command — enter pending state
            if (_decommissionPendingSince == null)
            {
                _decommissionPendingSince = Stopwatch.GetTimestamp();
                _stateDb.AppendChainedAuditEntry(new AuditEntry(
                    agentId, "decommission", "", "DecommissionPending", "decommission_phase1"));
                _logger.LogWarning("DECOMMISSION phase 1 — awaiting confirmation (5+ min)");
                return;
            }

            // Phase 2: second command — must be 5+ minutes after phase 1
            var elapsed = Stopwatch.GetElapsedTime(_decommissionPendingSince.Value);
            if (elapsed < TimeSpan.FromMinutes(5))
            {
                _logger.LogInformation("Decommission phase 2 too early ({Elapsed}) — waiting", elapsed);
                return;
            }

            _logger.LogWarning("DECOMMISSION phase 2 — archiving audit data");
            var chainValid = _stateDb.VerifyAuditChain();
            var auditJson = _stateDb.ExportAuditArchiveJson();
            var statesJson = _stateDb.ExportWritebackStatesJson();
            var archivePayload = System.Text.Json.JsonSerializer.Serialize(new
            {
                agentId,
                auditEntries = auditJson,
                writebackStates = statesJson,
                auditChainValid = chainValid
            });
            var digest = Convert.ToBase64String(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(archivePayload)));

            var ack = _cloudClient != null
                ? await _cloudClient.UploadAuditArchiveAsync(archivePayload, digest, ct)
                : null;
            if (ack == null || ack.ArchiveDigest != digest)
            {
                _logger.LogWarning("Decommission BLOCKED — archive upload failed or ACK mismatch");
                _decommissionPendingSince = null;
                return;
            }

            _stateDb.AppendChainedAuditEntry(new AuditEntry(
                agentId, "decommission", "DecommissionPending", "Decommissioned", "decommission_phase2"));
            _logger.LogWarning("Audit archived (id={ArchiveId}) — removing agent", ack.ArchiveId);

            // Proceed with cleanup
            if (OperatingSystem.IsWindows())
            {
                var script = @"
                    Start-Sleep 3
                    Stop-Service SuavoAgent.Core -Force -ErrorAction SilentlyContinue
                    Stop-Service SuavoAgent.Broker -Force -ErrorAction SilentlyContinue
                    Start-Sleep 2
                    sc.exe delete SuavoAgent.Core 2>$null
                    sc.exe delete SuavoAgent.Broker 2>$null
                    Remove-Item 'C:\Program Files\Suavo' -Recurse -Force -ErrorAction SilentlyContinue
                    Remove-Item ""$env:ProgramData\SuavoAgent"" -Recurse -Force -ErrorAction SilentlyContinue
                ";

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"{script.Replace("\"", "\\\"")}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                System.Diagnostics.Process.Start(psi);

                _logger.LogWarning("Decommission script launched — agent will terminate in ~5 seconds");
                Environment.Exit(0);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Decommission handling failed");
        }
    }

    /// <summary>
    /// Self-update via signed command envelope. Extracts url/sha256/version
    /// from the signed command's data fields and delegates to SelfUpdater.
    /// Only reachable via ECDSA-signed command envelope.
    /// </summary>
    private async Task HandleUpdateAsync(JsonElement scEl, CancellationToken ct)
    {
        if (_updateInProgress) return;

        try
        {
            var url = scEl.TryGetProperty("url", out var u) ? u.GetString() : null;
            var sha256 = scEl.TryGetProperty("sha256", out var s) ? s.GetString() : null;
            var version = scEl.TryGetProperty("version", out var v) ? v.GetString() : null;
            var signature = scEl.TryGetProperty("binarySignature", out var sig) ? sig.GetString() : null;

            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(sha256))
            {
                _logger.LogWarning("Signed update command missing url or sha256 — rejecting");
                return;
            }

            // Don't update to the same version we're already running
            if (version == _options.Version)
            {
                _logger.LogDebug("Already running v{Version} — skipping update", version);
                return;
            }

            _updateInProgress = true;
            _logger.LogInformation("Signed update command: v{Version} sha:{Sha}", version, sha256);

            await SelfUpdater.TryApplyUpdateAsync(url, sha256, version ?? "unknown", signature, _logger, ct);
            // If we get here, update failed (TryApplyUpdateAsync exits on success)
            _updateInProgress = false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Signed update command failed");
            _updateInProgress = false;
        }
    }
}
