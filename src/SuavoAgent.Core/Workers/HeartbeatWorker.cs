using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.Learning;
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
    private readonly Intelligence.ContextAssembler? _contextAssembler;
    private DateTimeOffset _lastContextSync = DateTimeOffset.MinValue;
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
        _contextAssembler = new Intelligence.ContextAssembler(stateDb);

        var agentId = _options.AgentId ?? "";
        var fingerprint = _options.MachineFingerprint ?? "";
        if (!string.IsNullOrEmpty(agentId))
        {
            _commandVerifier = new SignedCommandVerifier(
                new Dictionary<string, string> { ["suavo-cmd-v1"] = SelfUpdater.CommandPublicKeyDer },
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

        // Cleanup old binaries from a previous self-update
        SelfUpdater.CleanupOldBinaries(_logger);

        // Verify audit chain integrity on startup (HIPAA 164.312(c))
        _lastAuditChainValid = _stateDb.VerifyAuditChain();
        if (!_lastAuditChainValid)
            _logger.LogWarning("HIPAA ALERT: Audit chain integrity verification FAILED");

        _logger.LogInformation("Heartbeat worker started. Interval: {Interval}s", _options.HeartbeatIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            _stateDb.PruneOldNonces(TimeSpan.FromMinutes(10));

            // Hoist canaryHold so the delay block can read it even if the try throws
            (string Severity, int BlockedCycles, string DriftHoldSince)? canaryHold = null;

            try
            {
                // Read Rx detection state if available
                var rxWorker = _serviceProvider.GetService<RxDetectionWorker>();
                var sqlConnected = rxWorker?.IsSqlConnected ?? false;
                var rxReadyCount = rxWorker?.LastDetectedCount ?? 0;
                _lastRxCount = rxReadyCount;

                // Read Helper IPC state
                var ipcServer = _serviceProvider.GetService<IpcPipeServer>();

                canaryHold = _stateDb.GetCanaryHold(_options.PharmacyId ?? "", "pioneerrx");

                // Include intelligence context every 5 minutes (not every heartbeat)
                string? intelligenceContext = null;
                if (_contextAssembler != null && DateTimeOffset.UtcNow - _lastContextSync > TimeSpan.FromMinutes(5))
                {
                    try
                    {
                        var ctx = _contextAssembler.AssembleContext(_options.PharmacyId ?? "unknown");
                        intelligenceContext = _contextAssembler.SerializeAndValidate(ctx);
                        if (intelligenceContext != null)
                            _lastContextSync = DateTimeOffset.UtcNow;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Intelligence context assembly failed — non-critical");
                    }
                }

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
                        manualReview = 0,
                        writebackEnabled = true, // simplified — full check would need engine reference
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
                    },
                    canary = new
                    {
                        status = canaryHold != null ? "drift_hold" : "clean",
                        severity = canaryHold?.Severity ?? "none",
                        blockedCycles = canaryHold?.BlockedCycles ?? 0,
                        driftHoldSince = canaryHold?.DriftHoldSince,
                        lastVerifiedAt = DateTimeOffset.UtcNow.ToString("o"),
                    },
                    intelligenceContext = intelligenceContext
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

            if (canaryHold != null)
            {
                // Errata E11: 15s during drift_hold for faster operator feedback
                delay = TimeSpan.FromSeconds(15) + TimeSpan.FromMilliseconds(jitter);
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

            // Compute data hash from the raw JSON data payload for signature verification.
            // This prevents payload tampering — the hash is included in the signed canonical.
            var dataHashValue = "";
            if (scEl.TryGetProperty("data", out var dataEl) && dataEl.ValueKind != JsonValueKind.Null)
                dataHashValue = SignedCommandVerifier.ComputeDataHash(dataEl.GetRawText());
            else
                dataHashValue = SignedCommandVerifier.ComputeDataHash(null);

            var cmd = new SignedCommand(
                Command: scEl.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "",
                AgentId: scEl.TryGetProperty("agentId", out var a) ? a.GetString() ?? "" : "",
                MachineFingerprint: scEl.TryGetProperty("machineFingerprint", out var m) ? m.GetString() ?? "" : "",
                Timestamp: scEl.TryGetProperty("timestamp", out var t) ? t.GetString() ?? "" : "",
                Nonce: scEl.TryGetProperty("nonce", out var n) ? n.GetString() ?? "" : "",
                KeyId: scEl.TryGetProperty("keyId", out var k) ? k.GetString() ?? "" : "",
                Signature: scEl.TryGetProperty("signature", out var s) ? s.GetString() ?? "" : "",
                DataHash: dataHashValue);

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
                case "approve_pom":
                    await HandleApprovePomAsync(scEl, ct);
                    break;
                case "acknowledge_drift":
                    await HandleAcknowledgeDriftAsync(scEl, ct);
                    break;
                case "delivery_writeback":
                    await HandleDeliveryWritebackAsync(scEl, cmd, ct);
                    break;
                case "approve_candidate":
                    HandleFeedbackCommand(scEl, cmd, DirectiveType.Promote);
                    break;
                case "reject_candidate":
                    HandleFeedbackCommand(scEl, cmd, DirectiveType.Demote);
                    break;
                case "reapprove_candidate":
                    HandleFeedbackCommand(scEl, cmd, DirectiveType.Promote);
                    break;
                case "force_relearn":
                    HandleFeedbackCommand(scEl, cmd, DirectiveType.ReLearn);
                    break;
                case "adjust_window":
                    HandleFeedbackCommand(scEl, cmd, DirectiveType.Recalibrate);
                    break;
                case "acknowledge_stale":
                    HandleFeedbackCommand(scEl, cmd, DirectiveType.Prune);
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

        // Hash Rx number before audit/logging — Rx numbers are PHI when linked to patient context
        var hashedRx = PhiScrubber.HmacHash(rxNumber, _options.HmacSalt ?? "");

        // Audit PHI access before touching any patient data
        _stateDb.AppendChainedAuditEntry(new AuditEntry(
            TaskId: hashedRx,
            EventType: "phi_access",
            FromState: "",
            ToState: "",
            Trigger: "fetch_patient",
            CommandId: cmd.Nonce,
            RequesterId: requesterId,
            RxNumber: hashedRx));

        // Get SQL engine from RxDetectionWorker
        var rxWorker = _serviceProvider.GetService<RxDetectionWorker>();
        var sqlEngine = rxWorker?.SqlEngine;

        if (sqlEngine is null || !rxWorker!.IsSqlConnected)
        {
            _logger.LogWarning("fetch_patient: SQL not connected — cannot query patient for Rx {RxHash}", hashedRx[..12]);
            return;
        }

        var details = await sqlEngine.PullPatientForRxAsync(rxNumber, ct);

        if (details is null)
        {
            _logger.LogInformation("fetch_patient: no patient found for Rx {RxHash}", hashedRx[..12]);
            return;
        }

        if (_cloudClient is not null)
        {
            await _cloudClient.SendPatientDetailsAsync(rxNumber, details, cmd.Nonce, ct);
            _logger.LogInformation("fetch_patient: sent details for Rx {RxHash}", hashedRx[..12]);
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
                // Stop services via sc.exe — direct, no PowerShell
                foreach (var svc in new[] { "SuavoAgent.Broker", "SuavoAgent.Core" })
                {
                    try
                    {
                        var stopPsi = new System.Diagnostics.ProcessStartInfo("sc.exe", $"stop {svc}")
                            { CreateNoWindow = true, UseShellExecute = false };
                        System.Diagnostics.Process.Start(stopPsi)?.WaitForExit(10000);
                    }
                    catch { /* service may already be stopped */ }
                }

                // Derive paths from runtime location — never hardcode drive letters
                var installDir = Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))
                    ?? AppContext.BaseDirectory;
                var dataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "SuavoAgent");

                // Use cmd.exe with delayed cleanup — avoids PowerShell entirely
                var cleanupCmd = $"/C timeout /t 5 /nobreak >nul & sc delete SuavoAgent.Core & sc delete SuavoAgent.Broker & rmdir /s /q \"{installDir}\" & rmdir /s /q \"{dataDir}\"";
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", cleanupCmd)
                    { CreateNoWindow = true, UseShellExecute = false };
                System.Diagnostics.Process.Start(psi);

                _logger.LogWarning("Decommission cleanup launched — agent will terminate in ~5 seconds");
                Environment.Exit(0);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Decommission handling failed");
        }
    }

    /// <summary>
    /// 3-binary package update via signed command envelope. Parses UpdateManifest
    /// from the command's data fields, downloads all binaries, writes sentinel, exits.
    /// CheckPendingUpdate in Program.cs finishes the swap on restart.
    /// Only reachable via ECDSA-signed command envelope.
    /// </summary>
    private async Task HandleUpdateAsync(JsonElement scEl, CancellationToken ct)
    {
        if (_updateInProgress) return;

        try
        {
            var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
            var manifestStr = dataEl.TryGetProperty("manifest", out var m) ? m.GetString() : null;
            var signatureHex = dataEl.TryGetProperty("manifestSignature", out var sig) ? sig.GetString() : null;

            if (string.IsNullOrEmpty(manifestStr))
            {
                _logger.LogWarning("Signed update command missing manifest — rejecting");
                return;
            }

            var manifest = UpdateManifest.Parse(manifestStr);
            if (manifest is null)
            {
                _logger.LogWarning("Signed update command has malformed manifest — rejecting");
                return;
            }

            if (manifest.Version == _options.Version)
            {
                _logger.LogDebug("Already running v{Version} — skipping update", manifest.Version);
                return;
            }

            // Canary channel validation: only apply updates matching our assigned channel.
            // Cloud assigns channel (stable/canary/beta) via heartbeat response.
            var targetChannel = dataEl.TryGetProperty("channel", out var ch) ? ch.GetString() : "stable";
            var myChannel = _lastUpdateChannel ?? _options.UpdateChannel;
            if (!string.Equals(targetChannel, myChannel, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(targetChannel, "stable", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Update channel mismatch: target={Target}, mine={Mine} — skipping",
                    targetChannel, myChannel);
                return;
            }

            _updateInProgress = true;
            _logger.LogInformation("Signed package update: v{Version} ({Count} binaries)",
                manifest.Version, 3);

            await SelfUpdater.TryApplyPackageUpdateAsync(manifest, signatureHex ?? "", _logger, ct);
            // If we get here, update failed (TryApplyPackageUpdateAsync exits on success)
            _updateInProgress = false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Signed update command failed");
            _updateInProgress = false;
        }
    }

    private async Task HandleApprovePomAsync(JsonElement scEl, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var sessionId = dataEl.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
        var digest = dataEl.TryGetProperty("approvedModelDigest", out var dig) ? dig.GetString() : null;
        var approvedBy = dataEl.TryGetProperty("approvedBy", out var ab) ? ab.GetString() ?? "unknown" : "unknown";

        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(digest))
        {
            _logger.LogWarning("approve_pom: missing sessionId or digest");
            return;
        }

        var session = _stateDb.GetLearningSession(sessionId);
        if (session is null)
        {
            _logger.LogWarning("approve_pom: session {Id} not found", sessionId);
            return;
        }

        // Verify digest against FROZEN snapshot (CRITICAL-6), not live data
        var pomJson = _stateDb.GetPomSnapshot(sessionId);
        if (string.IsNullOrEmpty(pomJson))
        {
            _logger.LogWarning("approve_pom: no frozen POM snapshot for session {Id} — cannot verify", sessionId);
            return;
        }

        var localDigest = PomExporter.ComputeDigest(
            _options.PharmacyId ?? "", sessionId, pomJson);

        if (!string.Equals(localDigest, digest, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("approve_pom: digest mismatch — local={Local} approved={Approved}. " +
                "POM may have been mutated after review. Rejecting activation.",
                localDigest[..12], digest[..12]);
            return;
        }

        // Persist approval digest (CRITICAL-5)
        _stateDb.SetApprovalDigest(sessionId, digest, approvedBy);

        // Store approved digest and transition phase
        _stateDb.UpdateLearningPhase(sessionId, "approved");
        _stateDb.UpdateLearningMode(sessionId, "supervised");

        _stateDb.AppendLearningAudit(sessionId, "worker", "pom_approved",
            $"digest:{digest[..12]},by:{approvedBy}", phiScrubbed: false);

        _logger.LogInformation("POM approved for session {Session} — transitioning to supervised mode", sessionId);

        await Task.CompletedTask;
    }

    private async Task HandleDeliveryWritebackAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var transition = dataEl.TryGetProperty("transition", out var tr) ? tr.GetString() ?? "" : "";
        var rxNumberStr = dataEl.TryGetProperty("rxNumber", out var rx) ? rx.GetInt32().ToString() : "";
        var fillNumber = dataEl.TryGetProperty("fillNumber", out var fn) ? fn.GetInt32() : 0;
        var taskId = dataEl.TryGetProperty("taskId", out var tid) ? tid.GetString() ?? "" : "";
        var isControlled = dataEl.TryGetProperty("isControlledSubstance", out var cs) && cs.GetBoolean();

        if (string.IsNullOrEmpty(transition) || string.IsNullOrEmpty(rxNumberStr))
        {
            _logger.LogWarning("delivery_writeback: missing transition or rxNumber");
            return;
        }

        var hashedRx = PhiScrubber.HmacHash(rxNumberStr, _options.HmacSalt ?? "");

        _stateDb.AppendChainedAuditEntry(new AuditEntry(
            TaskId: hashedRx,
            EventType: "writeback_command_received",
            FromState: "",
            ToState: transition,
            Trigger: "delivery_writeback",
            CommandId: cmd.Nonce,
            RxNumber: hashedRx));

        DateTimeOffset? deliveredAt = null;
        if (transition == "complete" && dataEl.TryGetProperty("deliveredAt", out var da))
        {
            if (DateTimeOffset.TryParse(da.GetString(), out var parsed))
                deliveredAt = parsed;
        }

        var writebackProcessor = _serviceProvider.GetService<WritebackProcessor>();
        if (writebackProcessor != null)
        {
            writebackProcessor.EnqueueWriteback(taskId, rxNumberStr, fillNumber, transition, deliveredAt);
            _logger.LogInformation("delivery_writeback enqueued: {Transition} Rx {RxHash}",
                transition, hashedRx[..12]);
        }
        else
        {
            _logger.LogWarning("delivery_writeback: WritebackProcessor not available");
        }

        if (isControlled)
            _logger.LogInformation("Controlled substance delivery — POS entry required for Rx {RxHash}", hashedRx[..12]);

        await Task.CompletedTask;
    }

    private void HandleFeedbackCommand(JsonElement scEl, SignedCommand cmd, DirectiveType directiveType)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var correlationKey = dataEl.TryGetProperty("correlationKey", out var ck) ? ck.GetString() ?? "" : "";
        var sessionId = _stateDb.GetActiveSessionId(_options.PharmacyId ?? "");

        if (string.IsNullOrEmpty(correlationKey) || string.IsNullOrEmpty(sessionId))
        {
            _logger.LogWarning("{Command}: missing correlationKey or no active session", cmd.Command);
            return;
        }

        var payloadJson = dataEl.ValueKind != JsonValueKind.Undefined
            ? dataEl.GetRawText()
            : null;

        var evt = new FeedbackEvent(
            SessionId: sessionId,
            EventType: "operator_command",
            Source: "operator",
            SourceId: cmd.Nonce,
            TargetType: "correlation_key",
            TargetId: correlationKey,
            PayloadJson: payloadJson,
            DirectiveType: directiveType,
            DirectiveJson: payloadJson,
            CausalChainJson: null);

        _stateDb.InsertFeedbackEvent(evt);

        _logger.LogInformation("Feedback command {Command} for {Key} queued as directive {Directive}",
            cmd.Command, correlationKey, directiveType);

        _stateDb.AppendChainedAuditEntry(new AuditEntry(
            TaskId: correlationKey,
            EventType: "feedback_command",
            FromState: "",
            ToState: directiveType.ToString(),
            Trigger: cmd.Command,
            CommandId: cmd.Nonce,
            RequesterId: "operator"));
    }

    private async Task HandleAcknowledgeDriftAsync(JsonElement scEl, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var action = dataEl.TryGetProperty("action", out var a) ? a.GetString() : null;
        var incidentId = dataEl.TryGetProperty("incidentId", out var iid) ? iid.GetString() : null;
        var pharmacyId = _options.PharmacyId ?? "";

        if (string.IsNullOrEmpty(action))
        {
            _logger.LogWarning("acknowledge_drift: missing action");
            return;
        }

        _stateDb.AppendChainedAuditEntry(new AuditEntry(
            pharmacyId, "canary_ack", "drift_hold", action,
            $"acknowledge_drift:{action}",
            CommandId: incidentId));

        if (action == "resume_supervised")
        {
            _stateDb.ClearCanaryHold(pharmacyId, "pioneerrx");
            _logger.LogInformation("Drift acknowledged — resuming in supervised mode");
        }
        else if (action == "approve_new_baseline")
        {
            var targetEpoch = dataEl.TryGetProperty("targetSchemaEpoch", out var te) ? te.GetInt32() : 0;
            _stateDb.ClearCanaryHold(pharmacyId, "pioneerrx");
            _logger.LogInformation("Drift acknowledged — new baseline approved, epoch {Epoch}", targetEpoch);
        }
        else
        {
            _logger.LogWarning("acknowledge_drift: unknown action '{Action}'", action);
        }

        await Task.CompletedTask;
    }
}
