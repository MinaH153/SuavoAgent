using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SuavoAgent.Contracts.Discovery;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Models;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.ActionGrammarV1;
using SuavoAgent.Core.ActionGrammarV1.Workflows;
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Health;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.Mission;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.Receipts;
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
    private readonly Intelligence.EfficiencyCalculator? _efficiencyCalc;
    private readonly Intelligence.FleetDataChannels? _fleetChannels;
    private readonly IPricingJobExecutor? _pricingJobExecutor;
    private readonly PricingJobCloudUploader? _pricingJobCloudUploader;
    private readonly IpcCommandClient? _ipcCommandClient;
    private readonly IIntentCursorClient? _intentCursorClient;
    private readonly SuavoAgent.Core.Discovery.DiscoveryClient? _discoveryClient;
    // Wave 1B Track 1.4 — health composite. Both optional so the worker
    // still starts if Program.cs hasn't wired the health module yet (matches
    // the existing optional-deps pattern used for cloud client, pricing,
    // IPC command client, etc).
    private readonly IHealthSignals? _healthSignals;
    private readonly HealthCompositeCalculator? _healthCompositeCalculator;
    private readonly CloudAuthRecoveryCoordinator? _cloudAuthRecovery;
    private readonly WorkflowExecutor? _workflowExecutor;
    private readonly SemaphoreSlim _pricingJobSemaphore = new(1, 1);
    private readonly SemaphoreSlim _workflowSemaphore = new(1, 1);
    private readonly object _activeWorkflowLock = new();
    private CancellationTokenSource? _activeWorkflowCts;
    private string? _activeWorkflowRunId;
    private DateTimeOffset _lastContextSync = DateTimeOffset.MinValue;
    private DateTimeOffset _lastEfficiencyReport = DateTimeOffset.MinValue;
    private int _consecutiveFailures;
    private bool _updateInProgress;
    private long? _decommissionPendingSince;
    private string? _lastUpdateChannel;
    private readonly DateTimeOffset _startTime = DateTimeOffset.UtcNow;
    private int _helperConsecutiveFailures;
    private bool _lastAuditChainValid = true;
    private int _lastRxCount;
    private DateOnly _lastPruneDate;
    private DateTimeOffset? _lastSyncAt;
    private bool _consentReceiptSent;

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
        _efficiencyCalc = new Intelligence.EfficiencyCalculator(stateDb);
        _fleetChannels = new Intelligence.FleetDataChannels(stateDb);
        _pricingJobExecutor = serviceProvider.GetService<IPricingJobExecutor>();
        _pricingJobCloudUploader = serviceProvider.GetService<PricingJobCloudUploader>();
        _ipcCommandClient = serviceProvider.GetService<IpcCommandClient>();
        _intentCursorClient = serviceProvider.GetService<IIntentCursorClient>();
        _discoveryClient = serviceProvider.GetService<SuavoAgent.Core.Discovery.DiscoveryClient>();
        _healthSignals = serviceProvider.GetService<IHealthSignals>();
        _healthCompositeCalculator = serviceProvider.GetService<HealthCompositeCalculator>();
        _cloudAuthRecovery = serviceProvider.GetService<CloudAuthRecoveryCoordinator>();
        _workflowExecutor = serviceProvider.GetService<WorkflowExecutor>();

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
        var lastAuditChainCheck = DateTimeOffset.UtcNow;
        // Codex 2026-04-26 audit posture: re-verify the chain periodically,
        // not just at startup. Tamper after-start would otherwise sit
        // undetected for the entire uptime window. 30 min is a reasonable
        // balance — rebuild cost on a typical day's audit log is sub-second
        // and detection latency stays well below any HIPAA breach-notice
        // clock (60 days for 500+ affected; we want to know in minutes).
        var auditChainCheckInterval = TimeSpan.FromMinutes(30);

        _logger.LogInformation("Heartbeat worker started. Interval: {Interval}s", _options.HeartbeatIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            _stateDb.PruneOldNonces(TimeSpan.FromMinutes(10));

            // Periodic audit chain re-verification (Codex 2026-04-26 finding).
            // Runs in the heartbeat loop instead of a separate worker so it
            // gets the same lifecycle + cancellation handling. A failure
            // flips _lastAuditChainValid which the heartbeat payload already
            // ships under audit.chainValid — cloud surface lights up
            // immediately on the next heartbeat.
            if (DateTimeOffset.UtcNow - lastAuditChainCheck > auditChainCheckInterval)
            {
                try
                {
                    var stillValid = _stateDb.VerifyAuditChain();
                    if (stillValid != _lastAuditChainValid)
                    {
                        if (!stillValid)
                        {
                            _logger.LogError(
                                "HIPAA ALERT: Audit chain integrity check FAILED post-startup. " +
                                "Tamper, corruption, or write race detected. " +
                                "Cloud heartbeat will surface audit.chainValid=false.");
                        }
                        else
                        {
                            _logger.LogInformation("Audit chain integrity check recovered to valid");
                        }
                        _lastAuditChainValid = stillValid;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Audit chain periodic verify threw — leaving previous state intact");
                }
                lastAuditChainCheck = DateTimeOffset.UtcNow;
            }

            // Daily pruning of observation data (30-day retention)
            var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
            if (today != _lastPruneDate)
            {
                _lastPruneDate = today;
                try
                {
                    var pruned = _stateDb.PruneBehavioralEventsByAge(TimeSpan.FromDays(30));
                    pruned += _stateDb.PruneAppSessionsByAge(TimeSpan.FromDays(30));
                    if (pruned > 0)
                        _logger.LogInformation("Pruned {Count} expired observation records", pruned);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Observation pruning failed");
                }

                // Purge expired delivery receipts (7-year default retention)
                try
                {
                    var receiptsPurged = Receipts.DeliveryReceiptGenerator.PurgeExpiredReceipts(
                        _options.ReceiptRetentionDays);
                    if (receiptsPurged > 0)
                        _logger.LogInformation("Purged {Count} expired delivery receipts", receiptsPurged);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Receipt purge failed");
                }
            }

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

                // Include efficiency report every 30 minutes for collective intelligence
                string? efficiencyReport = null;
                if (_efficiencyCalc != null && DateTimeOffset.UtcNow - _lastEfficiencyReport > TimeSpan.FromMinutes(30))
                {
                    try
                    {
                        var report = _efficiencyCalc.ComputeReport(_options.PharmacyId ?? "unknown");
                        var reportJson = System.Text.Json.JsonSerializer.Serialize(report);
                        var (isClean, _) = Intelligence.ComplianceBoundary.Validate(reportJson);
                        if (isClean)
                        {
                            efficiencyReport = reportJson;
                            _lastEfficiencyReport = DateTimeOffset.UtcNow;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Efficiency report generation failed — non-critical");
                    }
                }

                // Upload consent receipt on first heartbeat (once)
                string? consentReceipt = null;
                if (!_consentReceiptSent)
                {
                    try
                    {
                        var consentPath = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                            "SuavoAgent", "consent-receipt.json");
                        if (File.Exists(consentPath))
                        {
                            consentReceipt = File.ReadAllText(consentPath);
                            _consentReceiptSent = true;
                            _logger.LogInformation("Consent receipt will be uploaded to cloud");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Consent receipt read failed");
                    }
                }

                // Fleet signals — every heartbeat (lightweight)
                string? fleetSignals = null;
                if (_fleetChannels != null)
                {
                    try
                    {
                        var signals = _fleetChannels.ComputeSignals(_options.PharmacyId ?? "unknown");
                        var signalsJson = System.Text.Json.JsonSerializer.Serialize(signals);
                        var (isClean, _) = Intelligence.ComplianceBoundary.Validate(signalsJson);
                        if (isClean) fleetSignals = signalsJson;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Fleet signal computation failed");
                    }
                }

                // Wave 1B Track 1.4 — compute + locally append the
                // agent.health_composite event each tick. Failure is logged
                // but does NOT block the heartbeat critical path; the agent
                // retries on the next tick.
                var healthComposite = EmitHealthComposite();

                var pendingWbCount = _stateDb.GetPendingWritebacks().Count;
                var failedWbCount = _stateDb.GetFailedWritebackCount();
                var memoryMb = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);
                var writebackEngineEnabled = rxWorker?.WritebackEngine?.WritebackEnabled ?? false;
                var learningSessionId = string.IsNullOrWhiteSpace(_options.PharmacyId)
                    ? null
                    : _stateDb.GetActiveSessionId(_options.PharmacyId);
                var behavioralEventCount = learningSessionId is null
                    ? 0
                    : _stateDb.GetBehavioralEventCount(learningSessionId);
                var treeSnapshotCount = learningSessionId is null
                    ? 0
                    : _stateDb.GetBehavioralEventCount(learningSessionId, "treesnapshot");
                var interactionEventCount = learningSessionId is null
                    ? 0
                    : _stateDb.GetBehavioralEventCount(learningSessionId, "interaction");
                var learnedRoutineCount = learningSessionId is null
                    ? 0
                    : _stateDb.GetLearnedRoutineCount(learningSessionId);
                var workflowTemplateCount = _stateDb.GetWorkflowTemplateCount(_options.TemplateLearning.SkillId);

                // v3.12.1.1 — upload local auto-rule approval state so the
                // pharmacy portal UI (MKM #44) can render real rows. Cloud
                // upserts on (pharmacy_id, rule_id); retired/deleted local
                // rows are handled by the cloud-side sync freshness window,
                // not by sending a delete signal here.
                var autoRuleApprovals = _stateDb
                    .GetAllAutoRuleApprovals()
                    .Select(a => new
                    {
                        ruleId = a.RuleId,
                        templateId = a.TemplateId,
                        yamlSha256 = a.YamlSha256,
                        status = a.Status.ToString(),
                        shadowRuns = a.ShadowRuns,
                        shadowMatches = a.ShadowMatches,
                        shadowMismatches = a.ShadowMismatches,
                        approvedBy = a.ApprovedBy,
                        approvedAt = a.ApprovedAt,
                        rejectedReason = a.RejectedReason,
                    })
                    .ToArray();
                var helperAttached = ipcServer?.IsConnected ?? false;
                _helperConsecutiveFailures = helperAttached ? 0 : _helperConsecutiveFailures + 1;
                var helperPayload = BuildHelperPayload(ipcServer);
                var pioneerRxObservationStatus = helperAttached ? "observing" : "not_observing";
                var visionCapture = _serviceProvider
                    .GetService<VisionCaptureTelemetry>()?
                    .Snapshot();

                var payload = new
                {
                    agentId = _options.AgentId,
                    version = _options.Version,
                    pharmacyId = _options.PharmacyId,
                    updateChannel = _lastUpdateChannel ?? "stable",
                    machineFingerprint = _options.MachineFingerprint,
                    uptimeSeconds = (long)(DateTimeOffset.UtcNow - _startTime).TotalSeconds,
                    memoryMb = memoryMb,
                    memoryUsageMb = memoryMb,
                    runtimeHealth = RuntimeHealthEvidence.Collect(),
                    status = "online",
                    pioneerrxStatus = pioneerRxObservationStatus,
                    pioneerRxObservation = new
                    {
                        status = pioneerRxObservationStatus,
                        helperAttached,
                        sqlConnected,
                    },
                    // Top-level fields for cloud stats extraction
                    learningMode = _options.LearningMode,
                    sqlConnected = sqlConnected,
                    pendingWritebackCount = pendingWbCount,
                    failedWritebackCount = failedWbCount,
                    rxReadyCount = _lastRxCount,
                    receiptOnlyMode = _options.ReceiptOnlyMode,
                    writebackEngineEnabled = writebackEngineEnabled,
                    templateLearning = new
                    {
                        enabled = _options.TemplateLearning.Enabled,
                        mode = _options.TemplateLearning.Mode,
                        ruleGeneration = _options.TemplateLearning.RuleGeneration,
                        skillId = _options.TemplateLearning.SkillId,
                        processNameGlob = _options.TemplateLearning.ProcessNameGlob,
                        autoApproveOnFingerprintMatch = _options.TemplateLearning.AutoApproveOnFingerprintMatch,
                        sessionId = learningSessionId,
                        behavioralEventCount = behavioralEventCount,
                        treeSnapshotCount = treeSnapshotCount,
                        interactionEventCount = interactionEventCount,
                        learnedRoutineCount = learnedRoutineCount,
                        workflowTemplateCount = workflowTemplateCount,
                    },
                    autoExecution = new
                    {
                        enabled = _options.AutoExecution.Enabled,
                        requireConfirmation = _options.AutoExecution.RequireConfirmation,
                        writebackEnabled = _options.AutoExecution.WritebackEnabled,
                    },
                    vision = new
                    {
                        enabled = _options.Vision.Enabled,
                        tesseractEnabled = _options.Vision.Tesseract.Enabled,
                        periodicCaptureEnabled = _options.Vision.PeriodicCapture.Enabled,
                        periodicCaptureIntervalSeconds = _options.Vision.PeriodicCapture.IntervalSeconds,
                        capture = visionCapture?.ToPayload(),
                    },
                    watchdog = BuildWatchdogPayload(),
                    sql = new
                    {
                        connected = sqlConnected,
                        lastRxCount = _lastRxCount
                    },
                    helper = helperPayload,
                    writeback = new
                    {
                        pending = pendingWbCount,
                        failed = failedWbCount,
                        receiptOnlyMode = _options.ReceiptOnlyMode,
                        writebackEngineEnabled = writebackEngineEnabled,
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
                    intelligenceContext = intelligenceContext,
                    efficiencyReport = efficiencyReport,
                    fleetSignals = fleetSignals,
                    consentReceipt = consentReceipt,
                    // Wave 1B Track 1.4 — agent.health_composite payload.
                    // Null when the health module isn't wired or computation
                    // failed; cloud treats absent composite as "agent on a
                    // version older than 1B". Composite presence flips
                    // status from "heartbeating" to either "healthy" or
                    // "heartbeating-but-unhealthy" cloud-side.
                    healthComposite = healthComposite,
                    // v3.12.1.1 auto-rule approval mirror. Empty array when
                    // Learning:Template:Enabled is off or no templates have
                    // been extracted yet — safe to emit either way.
                    autoRuleApprovals = autoRuleApprovals,
                };

                var response = await _cloudClient.HeartbeatAsync(payload, stoppingToken);
                _lastSyncAt = DateTimeOffset.UtcNow;
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

                _commandVerifier?.PruneNonces(TimeSpan.FromMinutes(5));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                if (_cloudAuthRecovery is not null)
                {
                    try
                    {
                        await _cloudAuthRecovery.TryRecoverAfterAuthFailureAsync(ex, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception recoveryEx)
                    {
                        _logger.LogWarning(recoveryEx, "Cloud credential recovery handler failed");
                    }
                }
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

    /// <summary>
    /// Wave 1B Track 1.4 — compute the agent.health_composite payload and
    /// append a chained audit entry. Returns the composite payload (which
    /// the caller embeds in the heartbeat body so cloud sees it on the
    /// same tick), or null when the health module isn't wired or any step
    /// throws. Failure is non-blocking by design: the heartbeat critical
    /// path must keep running even if the composite computation breaks.
    /// </summary>
    /// <remarks>
    /// Audit-entry shape:
    ///   EventType = "agent.health_composite"
    ///   FromState = previous status (best-effort — empty on first tick)
    ///   ToState   = current status ("healthy" | "heartbeating-but-unhealthy")
    ///   Trigger   = "heartbeat_tick"
    ///   TaskId    = AgentId (so the entry is tenant-attributable)
    ///
    /// The HealthCompositePayload itself doesn't fit AuditEntry's columnar
    /// shape, but the components are derivable from <see cref="ToState"/>
    /// + cloud-side stitching with the heartbeat payload that ships in
    /// the same tick. Local audit is the forensic copy; cloud heartbeat
    /// is the live signal.
    /// </remarks>
    internal HealthCompositePayload? EmitHealthComposite()
    {
        if (_healthSignals is null || _healthCompositeCalculator is null)
            return null;

        try
        {
            var snapshot = _healthSignals.Snapshot();
            var composite = _healthCompositeCalculator.Compute(snapshot, DateTimeOffset.UtcNow);

            try
            {
                _stateDb.AppendChainedAuditEntry(new AuditEntry(
                    TaskId: _options.AgentId ?? "",
                    EventType: "agent.health_composite",
                    FromState: "",
                    ToState: composite.Status,
                    Trigger: "heartbeat_tick",
                    Actor: "system",
                    SourceComponent: "heartbeat_worker"));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "agent.health_composite audit append failed; cloud-side payload still ships. " +
                    "Agent will retry on next tick.");
            }

            return composite;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Composite emission failed; heartbeat continues. " +
                "Agent will retry on next tick.");
            return null;
        }
    }

    /// <summary>
    /// Trip A 2026-04-25 silent-IPC-failure metric. Use the atomic Snapshot()
    /// so the three telemetry fields ship together — Codex flagged the prior
    /// three-call read pattern as racy: count from Record() N could ship with
    /// reason from Record() N-1 if Record() landed between the count read and
    /// the reason read. Counter resets on Core restart — a steadily growing
    /// value between restarts is the signal of interest.
    /// </summary>
    private object BuildHelperPayload(IpcPipeServer? ipcServer)
    {
        var (rejectionCount, lastReason, lastAt) = IpcRejectionStats.Snapshot();
        return new
        {
            attached = ipcServer?.IsConnected ?? false,
            consecutiveFailures = _helperConsecutiveFailures,
            ipcRejectionCount = rejectionCount,
            lastIpcRejectReason = lastReason,
            lastIpcRejectAt = lastAt?.ToString("o"),
        };
    }

    private object BuildWatchdogPayload()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent",
            "watchdog-health.json");

        try
        {
            if (!File.Exists(path))
            {
                return new
                {
                    present = false,
                    reason = "no_watchdog_telemetry_file",
                };
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Watchdog telemetry read failed");
            return new
            {
                present = false,
                reason = "watchdog_telemetry_unreadable",
            };
        }
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

            var result = _commandVerifier.Verify(cmd);
            if (!result.IsValid)
            {
                _logger.LogWarning("Signed command rejected: {Reason}", result.Reason);
                return;
            }

            // Persistent nonce check (survives restarts). Record only AFTER
            // cryptographic verification, otherwise an attacker can burn a
            // future valid nonce by sending a forged envelope first.
            if (!_stateDb.TryRecordNonce(cmd.Nonce))
            {
                _logger.LogWarning("Command nonce already used: {Nonce}", cmd.Nonce);
                return;
            }

            _logger.LogInformation("Verified signed command: {Command} nonce:{Nonce}", cmd.Command, cmd.Nonce);

            switch (cmd.Command)
            {
                case "fetch_patient":
                    await HandleFetchPatientAsync(scEl, cmd, ct);
                    break;
                case "decommission":
                    await HandleDecommissionAsync(scEl, ct);
                    break;
                case "repair_agent":
                    await HandleRepairAgentAsync(scEl, ct);
                    break;
                case "collect_health_probe":
                    await HandleCollectHealthProbeAsync(scEl, cmd, ct);
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
                case "run_pricing_job":
                    _ = Task.Run(() => HandleRunPricingJobAsync(scEl, ct), ct);
                    break;
                case "find_and_run_pricing_job":
                    _ = Task.Run(() => HandleFindAndRunPricingJobAsync(scEl, ct), ct);
                    break;
                case "show_intent_cursor":
                    await HandleShowIntentCursorAsync(scEl, cmd, ct);
                    break;
                case "computer_use_observe":
                case "computer_use_propose":
                    await HandleComputerUseObserveProposeAsync(scEl, cmd, ct);
                    break;
                case "transition_auto_rule_approval":
                    HandleTransitionAutoRuleApproval(scEl);
                    break;
                case "run_workflow":
                    _ = Task.Run(() => HandleRunWorkflowAsync(scEl, cmd, ct), ct);
                    break;
                case "abort_workflow":
                    await HandleAbortWorkflowAsync(scEl, cmd, ct);
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

    private async Task HandleShowIntentCursorAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;
        var requesterId = dataEl.TryGetProperty("requesterId", out var rid) ? rid.GetString() : "operator";

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        if (_intentCursorClient is null)
        {
            _logger.LogWarning("show_intent_cursor: intent cursor client not registered");
            await AckAsync(false, null, "intent cursor client unavailable");
            return;
        }

        if (ContainsUnsafeIntentCursorField(dataEl))
        {
            _logger.LogWarning("show_intent_cursor: rejected unsafe payload shape");
            await AckAsync(false, null, "intent cursor payload may only include numeric cursor fields plus commandId/requesterId");
            return;
        }

        IntentCursorRequest? request;
        try
        {
            request = dataEl.Deserialize<IntentCursorRequest>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "show_intent_cursor: malformed payload");
            await AckAsync(false, null, "malformed intent cursor payload");
            return;
        }

        if (request is null)
        {
            await AckAsync(false, null, "missing intent cursor payload");
            return;
        }

        _stateDb.AppendChainedAuditEntry(new AuditEntry(
            TaskId: commandId ?? cmd.Nonce,
            EventType: "intent_cursor_command",
            FromState: "requested",
            ToState: "dispatched",
            Trigger: "signed_command",
            CommandId: cmd.Nonce,
            RequesterId: requesterId,
            Actor: "operator",
            SourceComponent: "heartbeat_worker",
            CaptureReason: "visual_only_cursor_overlay"));

        var result = await _intentCursorClient.ShowAsync(request, ct);
        if (!result.Success)
        {
            await AckAsync(false, null, result.ErrorCode ?? "intent cursor failed");
            return;
        }

        await AckAsync(true, result.Response, null);
    }

    private async Task HandleRunWorkflowAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        if (_workflowExecutor is null)
        {
            _logger.LogWarning("run_workflow received but WorkflowExecutor not registered (DI gap)");
            await AckAsync(false, null, "workflow_executor_unavailable");
            return;
        }

        if (!await _workflowSemaphore.WaitAsync(0, ct).ConfigureAwait(false))
        {
            _logger.LogWarning("run_workflow rejected: another workflow is already running");
            await AckAsync(false, null, "workflow_already_running");
            return;
        }

        try
        {
            WorkflowDefinitionDto? definition;
            try { definition = dataEl.Deserialize<WorkflowDefinitionDto>(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "run_workflow: malformed payload");
                await AckAsync(false, null, "malformed_workflow_payload");
                return;
            }

            if (definition is null
                || string.IsNullOrEmpty(definition.WorkflowRunId)
                || string.IsNullOrEmpty(definition.WorkflowName)
                || definition.Steps is null
                || definition.Steps.Count == 0)
            {
                await AckAsync(false, null, "invalid_workflow_definition");
                return;
            }

            _stateDb.AppendChainedAuditEntry(new AuditEntry(
                TaskId: definition.WorkflowRunId,
                EventType: "workflow_run_received",
                FromState: "queued",
                ToState: "starting",
                Trigger: "signed_command",
                CommandId: cmd.Nonce,
                RequesterId: "operator",
                Actor: "operator",
                SourceComponent: "heartbeat_worker",
                CaptureReason: $"workflow={definition.WorkflowName}@{definition.WorkflowVersion} steps={definition.Steps.Count}"));

            var charter = _serviceProvider.GetService<MissionCharter>() ?? BuildEphemeralCharter();
            var auditChain = _serviceProvider.GetService<SuavoAgent.Core.Audit.AuditChain>()
                ?? new SuavoAgent.Core.Audit.AuditChain();

            var pharmacyId = _options.PharmacyId ?? charter.PharmacyId;
            var actor = $"agent:{_options.AgentId ?? "?"}";

            using var workflowCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_activeWorkflowLock)
            {
                _activeWorkflowCts = workflowCts;
                _activeWorkflowRunId = definition.WorkflowRunId;
            }

            WorkflowExecutor.WorkflowExecutionResult execResult;
            try
            {
                execResult = await _workflowExecutor.ExecuteAsync(
                    definition,
                    _serviceProvider,
                    auditChain,
                    charter,
                    pharmacyId,
                    actor,
                    workflowCts.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_activeWorkflowLock)
                {
                    _activeWorkflowCts = null;
                    _activeWorkflowRunId = null;
                }
            }

            _logger.LogInformation(
                "run_workflow run={RunId} outcome={Outcome} steps={Done}/{Total} reason={Reason}",
                definition.WorkflowRunId,
                execResult.Outcome,
                execResult.StepsCompleted,
                execResult.TotalSteps,
                execResult.AbortReason);

            await AckAsync(
                ok: execResult.Outcome == WorkflowRunOutcome.Completed,
                result: new { run_id = definition.WorkflowRunId, outcome = execResult.Outcome.ToString(), steps_completed = execResult.StepsCompleted },
                err: execResult.AbortReason);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "run_workflow execution exception");
            await AckAsync(false, null, ex.Message);
        }
        finally
        {
            _workflowSemaphore.Release();
        }
    }

    private async Task HandleAbortWorkflowAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;
        var requestedRunId = dataEl.TryGetProperty("workflow_run_id", out var rid) ? rid.GetString() : null;
        var requestedReason = dataEl.TryGetProperty("reason", out var rr) ? rr.GetString() : null;

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        if (string.IsNullOrEmpty(requestedRunId))
        {
            await AckAsync(false, null, "missing workflow_run_id");
            return;
        }

        CancellationTokenSource? activeCts;
        string? activeRunId;
        lock (_activeWorkflowLock)
        {
            activeCts = _activeWorkflowCts;
            activeRunId = _activeWorkflowRunId;
        }

        if (!string.Equals(activeRunId, requestedRunId, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "abort_workflow received for run {Requested}, but active run is {Active} (no-op ack)",
                requestedRunId,
                activeRunId ?? "<none>");
            await AckAsync(true, new { aborted = false, reason = "no_active_run_with_id" }, null);
            return;
        }

        _stateDb.AppendChainedAuditEntry(new AuditEntry(
            TaskId: requestedRunId,
            EventType: "workflow_run_abort_requested",
            FromState: "in_progress",
            ToState: "aborting",
            Trigger: "signed_command",
            CommandId: cmd.Nonce,
            RequesterId: "operator",
            Actor: "operator",
            SourceComponent: "heartbeat_worker",
            CaptureReason: requestedReason ?? "dashboard_abort"));

        try { activeCts?.Cancel(); }
        catch (Exception ex) { _logger.LogWarning(ex, "abort_workflow: cancel threw"); }

        await AckAsync(true, new { aborted = true, run_id = requestedRunId, reason = requestedReason }, null);
    }

    private MissionCharter BuildEphemeralCharter() => new(
        CharterId: Guid.Empty,
        PharmacyId: _options.PharmacyId ?? "",
        Version: 0,
        EffectiveFrom: DateTimeOffset.UtcNow,
        Objectives: Array.Empty<MissionObjective>(),
        Constraints: Array.Empty<MissionConstraint>(),
        PriorityOrdering: new MissionPriorityOrdering(Array.Empty<string>()),
        Tolerance: new MissionToleranceThresholds(0, 0, 0.0),
        SignedByOperator: "agent_ephemeral",
        SignedAt: DateTimeOffset.UtcNow);

    private async Task HandleComputerUseObserveProposeAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;
        var requesterId = dataEl.TryGetProperty("requesterId", out var rid) ? rid.GetString() : "operator";

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        if (ContainsUnsafeComputerUseField(dataEl, cmd.Command))
        {
            _logger.LogWarning("{Command}: rejected unsafe observe/propose payload", cmd.Command);
            await AckAsync(false, null, "computer-use observe/propose payload must be synthetic and non-PHI");
            return;
        }

        var pack = dataEl.TryGetProperty("pack", out var packEl) ? packEl.GetString() : null;
        var proposal = dataEl.TryGetProperty("proposal", out var proposalEl) ? proposalEl.GetString() : null;

        _stateDb.AppendChainedAuditEntry(new AuditEntry(
            TaskId: commandId ?? cmd.Nonce,
            EventType: cmd.Command == "computer_use_observe"
                ? "computer_use_observe_command"
                : "computer_use_propose_command",
            FromState: "requested",
            ToState: "recorded",
            Trigger: "signed_command",
            CommandId: cmd.Nonce,
            RequesterId: requesterId,
            Actor: "operator",
            SourceComponent: "heartbeat_worker",
            CaptureReason: "synthetic_non_phi_observe_propose"));

        await AckAsync(true, new
        {
            mode = "synthetic",
            action = cmd.Command,
            pack,
            proposal,
            executed = false,
            mutated = false,
            screenshotsCaptured = false
        }, null);
    }

    private async Task HandleCollectHealthProbeAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;
        var requesterId = dataEl.TryGetProperty("requesterId", out var rid) ? rid.GetString() : "operator";

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        if (ContainsUnsafeHealthProbeField(dataEl))
        {
            _logger.LogWarning("collect_health_probe: rejected unsafe payload");
            await AckAsync(false, null, "health probe payload must be reason-only and non-PHI");
            return;
        }

        var reason = dataEl.TryGetProperty("reason", out var reasonEl)
            ? reasonEl.GetString() ?? "dashboard_diagnostics"
            : "dashboard_diagnostics";

        _stateDb.AppendChainedAuditEntry(new AuditEntry(
            TaskId: commandId ?? cmd.Nonce,
            EventType: "health_probe_command",
            FromState: "requested",
            ToState: "collected",
            Trigger: "signed_command",
            CommandId: cmd.Nonce,
            RequesterId: requesterId,
            Actor: "operator",
            SourceComponent: "heartbeat_worker",
            CaptureReason: "non_phi_health_probe"));

        await AckAsync(true, BuildHealthProbeResult(reason), null);
    }

    private object BuildHealthProbeResult(string reason)
    {
        var runtime = RuntimeHealthEvidence.Collect();
        var bootstrapPath = Path.Combine(RuntimeHealthEvidence.ProgramDataRoot, "bootstrap.ps1");
        var crashEvidenceCount = runtime.CrashLogs.Count(log => log.Exists && log.Bytes > 0);
        var configFailed =
            runtime.ConfigSync.Present &&
            (string.Equals(runtime.ConfigSync.Status, "failed", StringComparison.OrdinalIgnoreCase) ||
             runtime.ConfigSync.ConsecutiveFailures >= 3);
        var cloudAuthFailed =
            runtime.CloudAuth.Present &&
            !string.Equals(runtime.CloudAuth.Status, "ok", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(runtime.CloudAuth.Status, "success", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(runtime.CloudAuth.Status, "recovered", StringComparison.OrdinalIgnoreCase);
        var status = configFailed || cloudAuthFailed || crashEvidenceCount > 0 ? "needs_attention" : "healthy";

        return new
        {
            schema = "suavo.agent.health_probe.v1",
            status,
            reason,
            checkedAtUtc = DateTimeOffset.UtcNow.ToString("o"),
            screenshotsCaptured = false,
            mutated = false,
            agent = new
            {
                version = _options.Version,
                uptimeSeconds = (long)(DateTimeOffset.UtcNow - _startTime).TotalSeconds,
                processId = Environment.ProcessId,
            },
            install = new
            {
                bootstrapPresent = File.Exists(bootstrapPath),
                bootstrapSha256Prefix = SafeFileSha256Prefix(bootstrapPath),
            },
            configSync = runtime.ConfigSync,
            cloudAuth = runtime.CloudAuth,
            crashLogs = runtime.CrashLogs,
            audit = new
            {
                chainValid = _lastAuditChainValid,
                entryCount = _stateDb.GetAuditEntryCount(),
            },
            serviceProbe = CollectServiceProbe(),
        };
    }

    private static object CollectServiceProbe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new
            {
                platform = "non_windows",
                supported = false,
                services = Array.Empty<object>(),
            };
        }

        var services = new List<object>();
        foreach (var serviceName in new[] { "SuavoAgent.Core", "SuavoAgent.Broker", "SuavoAgent.Watchdog" })
        {
            try
            {
                using var controller = new System.ServiceProcess.ServiceController(serviceName);
                services.Add(new
                {
                    serviceName,
                    status = controller.Status.ToString(),
                    canStop = controller.CanStop,
                });
            }
            catch
            {
                services.Add(new
                {
                    serviceName,
                    status = "not_found",
                    canStop = false,
                });
            }
        }

        return new
        {
            platform = "windows",
            supported = true,
            services,
        };
    }

    private static string? SafeFileSha256Prefix(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var hash = SHA256.HashData(stream);
            return Convert.ToHexString(hash).ToLowerInvariant()[..16];
        }
        catch
        {
            return "unreadable";
        }
    }

    private static bool ContainsUnsafeHealthProbeField(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            var normalized = NormalizeIntentCursorFieldName(property.Name);
            if (normalized is not ("reason" or "commandid" or "requesterid") ||
                property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array ||
                IsBlockedComputerUseField(property.Name) ||
                HasUnsafeHealthProbeValue(normalized, property.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasUnsafeHealthProbeValue(string normalizedName, JsonElement value)
    {
        return normalizedName switch
        {
            "reason" => value.ValueKind != JsonValueKind.String ||
                value.GetString() is not (
                    "dashboard_diagnostics" or
                    "post_install_probe" or
                    "operator_requested" or
                    "before_repair" or
                    "after_repair" or
                    "watchdog_unhealthy"),
            "commandid" or "requesterid" =>
                value.ValueKind != JsonValueKind.String || value.GetString()?.Length > 128,
            _ => true,
        };
    }

    private static bool ContainsUnsafeComputerUseField(JsonElement element, string command)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!IsAllowedComputerUseField(property.Name, command) ||
                IsBlockedComputerUseField(property.Name) ||
                property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array ||
                HasUnsafeComputerUseValue(property.Name, property.Value, command))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAllowedComputerUseField(string name, string command)
    {
        var normalized = NormalizeIntentCursorFieldName(name);
        if (normalized is "pack" or "mode" or "commandid" or "requesterid")
        {
            return true;
        }

        return command == "computer_use_propose" && normalized == "proposal";
    }

    private static bool HasUnsafeComputerUseValue(string name, JsonElement value, string command)
    {
        var normalized = NormalizeIntentCursorFieldName(name);

        return normalized switch
        {
            "pack" => value.ValueKind != JsonValueKind.String ||
                value.GetString() is not ("workstation_health" or "pioneerrx_shadow" or "inbox_shadow"),
            "mode" => value.ValueKind != JsonValueKind.String ||
                value.GetString() != "synthetic",
            "proposal" => command != "computer_use_propose" ||
                value.ValueKind != JsonValueKind.String ||
                value.GetString() is not ("run_diagnostics" or "queue_repair" or "show_intent_cursor" or "open_delivery_inbox"),
            "commandid" or "requesterid" =>
                value.ValueKind != JsonValueKind.String || value.GetString()?.Length > 128,
            _ => true,
        };
    }

    private static bool IsBlockedComputerUseField(string name)
    {
        var normalized = NormalizeIntentCursorFieldName(name);

        return IsBlockedIntentCursorField(name) ||
            normalized is
                "screenshot" or
                "image" or
                "ocr" or
                "click" or
                "type" or
                "key" or
                "mouse" or
                "coordinates" or
                "address" or
                "phone";
    }

    private static bool ContainsUnsafeIntentCursorField(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!IsAllowedIntentCursorField(property.Name) ||
                IsBlockedIntentCursorField(property.Name) ||
                property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array ||
                HasUnsafeIntentCursorValue(property.Name, property.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAllowedIntentCursorField(string name)
    {
        var normalized = NormalizeIntentCursorFieldName(name);
        return normalized is
            "x" or
            "y" or
            "coordinatespace" or
            "durationms" or
            "diameterpx" or
            "opacity" or
            "tone" or
            "anchor" or
            "commandid" or
            "requesterid";
    }

    private static bool HasUnsafeIntentCursorValue(string name, JsonElement value)
    {
        var normalized = NormalizeIntentCursorFieldName(name);

        return normalized switch
        {
            "coordinatespace" => value.ValueKind != JsonValueKind.String ||
                !string.Equals(value.GetString(), IntentCursorCoordinateSpaces.Screen, StringComparison.Ordinal),
            "tone" => value.ValueKind != JsonValueKind.String ||
                value.GetString() is not (
                    IntentCursorTones.Agent or
                    IntentCursorTones.Attention or
                    IntentCursorTones.Success or
                    IntentCursorTones.Warning),
            "anchor" => value.ValueKind != JsonValueKind.String ||
                !string.Equals(value.GetString(), IntentCursorAnchors.PrimaryCenter, StringComparison.Ordinal),
            "x" or "y" or "durationms" or "diameterpx" or "opacity" =>
                value.ValueKind != JsonValueKind.Number,
            "commandid" or "requesterid" =>
                value.ValueKind != JsonValueKind.String || value.GetString()?.Length > 128,
            _ => true,
        };
    }

    private static bool IsBlockedIntentCursorField(string name)
    {
        var normalized = NormalizeIntentCursorFieldName(name);

        return normalized is
            "text" or
            "label" or
            "windowtitle" or
            "rx" or
            "rxnumber" or
            "rxid" or
            "prescription" or
            "prescriptionid" or
            "patient" or
            "patientid" or
            "patientname" or
            "patientfirstname" or
            "patientlastname" or
            "medication" or
            "ndc";
    }

    private static string NormalizeIntentCursorFieldName(string name) =>
        new(name
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

    private async Task HandleRepairAgentAsync(JsonElement scEl, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        _stateDb.AppendChainedAuditEntry(new AuditEntry(
            TaskId: _options.AgentId ?? "agent",
            EventType: "repair_command_received",
            FromState: "",
            ToState: "requested",
            Trigger: "repair_agent",
            CommandId: commandId,
            RequesterId: "operator",
            Actor: "operator",
            SourceComponent: "heartbeat_worker",
            CaptureReason: "signed_remote_repair"));

        var bootstrapPath = Path.Combine(RuntimeHealthEvidence.ProgramDataRoot, "bootstrap.ps1");
        if (!File.Exists(bootstrapPath))
        {
            _logger.LogWarning("repair_agent: bootstrap.ps1 missing at {Path}", bootstrapPath);
            await AckAsync(false, new { status = "missing_bootstrap" }, "bootstrap.ps1 missing");
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{bootstrapPath}\" --repair",
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                _logger.LogWarning("repair_agent: failed to start powershell repair process");
                await AckAsync(false, new { status = "start_failed" }, "failed to start repair process");
                return;
            }

            _logger.LogWarning("repair_agent: bootstrap --repair started, pid={Pid}", process.Id);
            await AckAsync(true, new { status = "repair_started", processId = process.Id }, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "repair_agent: failed to invoke bootstrap --repair");
            await AckAsync(false, new { status = "start_failed" }, "failed to invoke repair");
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
        var hashedRx = PhiScrubber.HmacHash(rxNumber, _options.HmacSalt ?? "[no-hmac-salt]");

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
            // Project to PatientDetailsPayload — driver-needed delivery fields
            // only, RxNumber dropped (cloud receives it as rxNumberHash via
            // the separate argument). Codex 2026-04-26 fixed the prior
            // 'object details' opaque shipping which silently leaked the
            // raw RxNumber inside the record alongside the hashed key.
            var payload = SuavoAgent.Contracts.Models.PatientDetailsPayload
                .FromRxPatientDetails(details);
            await _cloudClient.SendPatientDetailsAsync(rxNumber, payload, cmd.Nonce, ct);
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
    private async Task HandleDecommissionAsync(JsonElement scEl, CancellationToken ct)
    {
        try
        {
            var agentId = _options.AgentId ?? "";
            var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
            var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;

            async Task AckAsync(bool ok, object? result, string? err)
            {
                if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
                await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
            }

            // Phase 1: first decommission command — enter pending state
            if (_decommissionPendingSince == null)
            {
                _decommissionPendingSince = Stopwatch.GetTimestamp();
                _stateDb.AppendChainedAuditEntry(new AuditEntry(
                    agentId, "decommission", "", "DecommissionPending", "decommission_phase1"));

                // Generate random confirmation token for phase 2 (F8: non-deterministic, non-cloud-computable)
                var phase1ConfirmBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
                var phase1ConfirmToken = Convert.ToHexString(phase1ConfirmBytes).ToLowerInvariant();
                _stateDb.SetConfigValue("decommission_confirm_token", phase1ConfirmToken);
                _logger.LogInformation("Decommission phase 1: confirmation token generated and stored locally");

                _logger.LogWarning("DECOMMISSION phase 1 — awaiting confirmation (5+ min)");
                await AckAsync(true, new { phase = "pending_confirmation", minWaitSeconds = 300 }, null);
                return;
            }

            // Phase 2: second command — must be 5+ minutes after phase 1
            var elapsed = Stopwatch.GetElapsedTime(_decommissionPendingSince.Value);
            if (elapsed < TimeSpan.FromMinutes(5))
            {
                _logger.LogInformation("Decommission phase 2 too early ({Elapsed}) — waiting", elapsed);
                await AckAsync(false, new { phase = "pending_confirmation" }, "decommission confirmation window not elapsed");
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
                await AckAsync(false, null, "archive upload failed or ACK mismatch");
                return;
            }

            // Require confirmation token generated during phase 1 (random, locally stored)
            var confirmToken = dataEl.TryGetProperty("confirmationToken", out var ctok) ? ctok.GetString() : null;
            if (string.IsNullOrEmpty(confirmToken))
            {
                _logger.LogWarning("Decommission phase 2 rejected — missing confirmationToken");
                await AckAsync(false, new { phase = "archive_acknowledged", archiveId = ack.ArchiveId }, "missing confirmationToken");
                return;
            }
            var expectedToken = _stateDb.GetConfigValue("decommission_confirm_token");
            if (string.IsNullOrEmpty(expectedToken) || !confirmToken.Equals(expectedToken, StringComparison.Ordinal))
            {
                _logger.LogWarning("Decommission phase 2 rejected — invalid confirmationToken");
                await AckAsync(false, new { phase = "archive_acknowledged", archiveId = ack.ArchiveId }, "invalid confirmationToken");
                return;
            }
            _logger.LogInformation("Decommission confirmation token validated");

            _stateDb.AppendChainedAuditEntry(new AuditEntry(
                agentId, "decommission", "DecommissionPending", "Decommissioned", "decommission_phase2"));
            _logger.LogWarning("Audit archived (id={ArchiveId}) — removing agent", ack.ArchiveId);
            await AckAsync(true, new { phase = "decommissioned", archiveId = ack.ArchiveId }, null);

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

                // Delete services and wipe directories using C# directly — no shell delegation
                _logger.LogWarning("Decommission: stopping and deleting services");
                foreach (var svcName in new[] { "SuavoAgent.Core", "SuavoAgent.Broker" })
                {
                    try
                    {
                        using var sc = new System.ServiceProcess.ServiceController(svcName);
                        if (sc.Status != System.ServiceProcess.ServiceControllerStatus.Stopped)
                        {
                            sc.Stop();
                            sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped,
                                TimeSpan.FromSeconds(10));
                        }
                    }
                    catch (Exception scEx)
                    {
                        _logger.LogWarning(scEx, "Could not stop service {Svc}", svcName);
                    }

                    try
                    {
                        using var process = System.Diagnostics.Process.Start(
                            new System.Diagnostics.ProcessStartInfo("sc.exe")
                            {
                                ArgumentList = { "delete", svcName },
                                CreateNoWindow = true,
                                UseShellExecute = false
                            });
                        process?.WaitForExit(5000);
                    }
                    catch (Exception scEx)
                    {
                        _logger.LogWarning(scEx, "Could not delete service {Svc}", svcName);
                    }
                }

                _logger.LogWarning("Decommission: wiping data directory {DataDir}", dataDir);
                if (Directory.Exists(dataDir))
                {
                    // Secure-erase sensitive files before bulk delete
                    foreach (var sensitive in new[] { "state.db", "state.db.key", "pipe.nonce" })
                    {
                        var p = Path.Combine(dataDir, sensitive);
                        try { State.AgentStateDb.SecureDelete(p); } catch { }
                    }
                    try { Directory.Delete(dataDir, recursive: true); } catch (Exception ex) {
                        _logger.LogWarning(ex, "Could not delete data directory"); }
                }

                _logger.LogWarning("Decommission: wiping install directory {InstallDir}", installDir);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2000);
                    if (Directory.Exists(installDir))
                    {
                        // Secure-erase appsettings.json (contains DPAPI-sealed credentials) before bulk delete
                        try { State.AgentStateDb.SecureDelete(Path.Combine(installDir, "appsettings.json")); } catch { }
                        try { Directory.Delete(installDir, recursive: true); } catch { }
                    }
                });

                _logger.LogWarning("Decommission complete — agent terminating");
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

        var hashedRx = PhiScrubber.HmacHash(rxNumberStr, _options.HmacSalt ?? "[no-hmac-salt]");

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

        if (!_options.ReceiptOnlyMode)
        {
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
        }
        else
        {
            _logger.LogInformation("ReceiptOnlyMode: skipping writeback for Rx {RxHash}, receipt saved", hashedRx[..12]);
        }

        // Generate delivery receipt locally (audit failsafe)
        try
        {
            var receiptCmd = new DeliveryWritebackCommand(
                TaskId: taskId,
                RxNumber: rxNumberStr,
                FillNumber: fillNumber,
                ExternalSaleId: dataEl.TryGetProperty("externalSaleId", out var esi) ? esi.GetString() ?? "" : "",
                RecipientFirstName: dataEl.TryGetProperty("recipientFirstName", out var rfn) ? rfn.GetString() ?? "" : "",
                RecipientLastName: dataEl.TryGetProperty("recipientLastName", out var rln) ? rln.GetString() ?? "" : "",
                RecipientIdType: dataEl.TryGetProperty("recipientIdType", out var rit) ? rit.GetInt32() : 0,
                RecipientIdValue: dataEl.TryGetProperty("recipientIdValue", out var riv) ? riv.GetString() ?? "" : "",
                RecipientIdState: dataEl.TryGetProperty("recipientIdState", out var ris) ? ris.GetString() ?? "" : "",
                SignatureSvg: dataEl.TryGetProperty("signatureSvg", out var sig) ? sig.GetString() : null,
                Price: dataEl.TryGetProperty("price", out var pr) ? pr.GetDecimal() : 0,
                Tax: dataEl.TryGetProperty("tax", out var tx) ? tx.GetDecimal() : 0,
                CounselingStatus: dataEl.TryGetProperty("counselingStatus", out var cs2) ? cs2.GetInt32() : 0,
                DeliveredAt: deliveredAt ?? DateTimeOffset.UtcNow);

            var receiptGen = new DeliveryReceiptGenerator();
            var receiptPath = receiptGen.SaveReceipt(receiptCmd, _options.PharmacyId ?? "Unknown Pharmacy",
                driverName: dataEl.TryGetProperty("driverName", out var dn) ? dn.GetString() : null);
            _logger.LogInformation("Delivery receipt saved: {Path}", receiptPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate delivery receipt — writeback continues");
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

    /// <summary>
    /// v3.12.1.1 — apply a signed auto-rule-approval transition from the
    /// cloud operator UI. The command envelope shape:
    ///
    ///   data: {
    ///     ruleId: string,
    ///     toStatus: "Pending" | "Shadow" | "Approved" | "Rejected",
    ///     approvedBy?: string,   // operator user id when toStatus=Approved
    ///     approvedAt?: string,   // ISO-8601 when toStatus=Approved
    ///     reason?: string        // required when toStatus=Rejected
    ///   }
    ///
    /// The cloud enforces the state-machine gate (spec §4.3 evidence gate on
    /// Shadow→Approved). This handler trusts the inbound transition, flips
    /// the local SQLite row, and writes an audit entry. A missing ruleId —
    /// e.g. because the rule was retired locally after the command was
    /// enqueued — is a silent no-op (fail-soft, not fail-throw).
    /// </summary>
    private void HandleTransitionAutoRuleApproval(JsonElement scEl)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;

        string? ruleId = dataEl.TryGetProperty("ruleId", out var rEl) ? rEl.GetString() : null;
        string? toStatusStr = dataEl.TryGetProperty("toStatus", out var sEl) ? sEl.GetString() : null;
        string? approvedBy = dataEl.TryGetProperty("approvedBy", out var abEl) ? abEl.GetString() : null;
        string? approvedAt = dataEl.TryGetProperty("approvedAt", out var atEl) ? atEl.GetString() : null;
        string? reason = dataEl.TryGetProperty("reason", out var rsEl) ? rsEl.GetString() : null;

        if (string.IsNullOrEmpty(ruleId) || string.IsNullOrEmpty(toStatusStr))
        {
            _logger.LogWarning(
                "transition_auto_rule_approval: missing ruleId or toStatus; dropping");
            return;
        }

        if (!Enum.TryParse<AgentStateDb.AutoRuleStatus>(toStatusStr, ignoreCase: true, out var toStatus))
        {
            _logger.LogWarning(
                "transition_auto_rule_approval: invalid toStatus '{S}'; dropping", toStatusStr);
            return;
        }

        var existing = _stateDb.GetAutoRuleApproval(ruleId);
        var updated = _stateDb.SetAutoRuleApprovalStatus(
            ruleId, toStatus, approvedBy, approvedAt, reason);

        if (!updated)
        {
            _logger.LogInformation(
                "transition_auto_rule_approval: no row for rule {RuleId} — silent no-op",
                ruleId);
            return;
        }

        _stateDb.AppendChainedAuditEntry(new AuditEntry(
            TaskId: ruleId,
            EventType: "auto_rule_approval_transition",
            FromState: existing?.Status.ToString() ?? "unknown",
            ToState: toStatus.ToString(),
            Trigger: "cloud_command",
            CommandId: null,
            RequesterId: approvedBy ?? "operator"));

        _logger.LogInformation(
            "Auto-rule approval {RuleId}: {From} -> {To}",
            ruleId, existing?.Status.ToString() ?? "unknown", toStatus);
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

    private async Task HandleRunPricingJobAsync(JsonElement scEl, CancellationToken ct)
    {
        if (_pricingJobExecutor == null)
        {
            _logger.LogWarning("run_pricing_job: pricing executor not registered");
            return;
        }

        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var excelPath = dataEl.TryGetProperty("excelPath", out var ep) ? ep.GetString() : null;
        var pricingCandidateToken = dataEl.TryGetProperty("pricingCandidateToken", out var pct) ? pct.GetString() : null;
        var ndcColumn = dataEl.TryGetProperty("ndcColumn", out var nc) ? nc.GetString() ?? PricingJobDefaults.NdcColumn : PricingJobDefaults.NdcColumn;
        var supplierColumn = dataEl.TryGetProperty("supplierColumn", out var sc2) ? sc2.GetString() ?? PricingJobDefaults.SupplierColumn : PricingJobDefaults.SupplierColumn;
        var costColumn = dataEl.TryGetProperty("costColumn", out var cc) ? cc.GetString() ?? PricingJobDefaults.CostColumn : PricingJobDefaults.CostColumn;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        if (string.IsNullOrEmpty(excelPath) && string.IsNullOrEmpty(pricingCandidateToken))
        {
            _logger.LogWarning("run_pricing_job: missing workbook target");
            await AckAsync(false, null, "missing workbook target");
            return;
        }

        if (!string.IsNullOrEmpty(pricingCandidateToken))
        {
            excelPath = _stateDb.TryResolvePricingDiscoveryCandidate(pricingCandidateToken);
            if (string.IsNullOrEmpty(excelPath))
            {
                _logger.LogWarning("run_pricing_job: pricing candidate token was not found");
                await AckAsync(false, null, "pricing candidate expired - run discovery again");
                return;
            }
        }
        var workbookPath = excelPath!;

        // [C-1] Validate path safety: must be local absolute .xlsx, no UNC/traversal
        var ext = Path.GetExtension(workbookPath);
        if (!string.Equals(ext, ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("run_pricing_job: excelPath rejected — must be .xlsx");
            await AckAsync(false, null, "excelPath must be .xlsx");
            return;
        }
        if (workbookPath.StartsWith(@"\\") || !Path.IsPathRooted(workbookPath))
        {
            _logger.LogWarning("run_pricing_job: excelPath rejected — must be local absolute path");
            await AckAsync(false, null, "excelPath must be local absolute path");
            return;
        }
        var canonicalPath = Path.GetFullPath(workbookPath);
        if (!string.Equals(canonicalPath, workbookPath, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("run_pricing_job: excelPath rejected — canonicalization changed path");
            await AckAsync(false, null, "excelPath canonicalization mismatch");
            return;
        }

        // [M-3] Only one pricing job at a time — reject concurrent commands
        if (!await _pricingJobSemaphore.WaitAsync(TimeSpan.Zero, ct))
        {
            _logger.LogWarning("run_pricing_job: another job is already running, command ignored");
            await AckAsync(false, null, "another pricing job is already running");
            return;
        }

        try
        {
            var jobId = Guid.NewGuid().ToString("N");
            var spec = new PricingJobSpec(jobId, canonicalPath, ndcColumn, supplierColumn, costColumn);

            _logger.LogInformation("Pricing job {JobId} starting", jobId);

            var execution = await _pricingJobExecutor.RunAsync(spec, ct);
            var progress = execution.Progress;
            if (_pricingJobCloudUploader != null)
                await _pricingJobCloudUploader.UploadAsync(spec, execution, commandId, ct);

            _logger.LogInformation("Pricing job {JobId} finished: {Status} — {Completed}/{Total}",
                jobId, progress.Status, progress.CompletedItems, progress.TotalItems);

            await AckAsync(execution.Ok, new
            {
                jobId,
                mode = execution.Mode,
                totalItems = progress.TotalItems,
                completedItems = progress.CompletedItems,
                failedItems = progress.FailedItems,
                status = progress.Status.ToString(),
            }, execution.Ok ? null : execution.Error ?? "pricing job failed — see agent logs");
        }
        finally
        {
            _pricingJobSemaphore.Release();
        }
    }

    /// <summary>
    /// v3.13 discovery-mediated pricing job. Operator clicks "auto-find and
    /// run" in the portal; agent runs <see cref="SuavoAgent.Core.Discovery.FileLocatorService"/>
    /// via Helper IPC to locate the file, then:
    /// <list type="bullet">
    ///   <item><b>AutoUse</b> — runs the pricing job immediately on the
    ///     discovered path, ACKs success with progress.</item>
    ///   <item><b>RequireConfirm / Inconclusive</b> — ACKs with a
    ///     <c>needs_confirmation</c> payload carrying candidate metadata;
    ///     portal surfaces the picker and operator triggers a regular
    ///     <c>run_pricing_job</c> with the chosen path.</item>
    ///   <item><b>NotFound</b> — ACKs with <c>not_found</c>; portal prompts
    ///     operator to supply the path manually.</item>
    /// </list>
    /// </summary>
    private async Task HandleFindAndRunPricingJobAsync(JsonElement scEl, CancellationToken ct)
    {
        if (_discoveryClient == null || _ipcCommandClient == null || _pricingJobExecutor == null)
        {
            _logger.LogWarning("find_and_run_pricing_job: discovery/IPC/pricing executor not registered");
            return;
        }

        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var pack = dataEl.TryGetProperty("pack", out var pk) ? pk.GetString() ?? "pharmacy_rx" : "pharmacy_rx";
        var ndcColumn = dataEl.TryGetProperty("ndcColumn", out var nc) ? nc.GetString() ?? PricingJobDefaults.NdcColumn : PricingJobDefaults.NdcColumn;
        var supplierColumn = dataEl.TryGetProperty("supplierColumn", out var sc2) ? sc2.GetString() ?? PricingJobDefaults.SupplierColumn : PricingJobDefaults.SupplierColumn;
        var costColumn = dataEl.TryGetProperty("costColumn", out var cc) ? cc.GetString() ?? PricingJobDefaults.CostColumn : PricingJobDefaults.CostColumn;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        // Pack selection. v3.13 has only pharmacy_rx; future verticals plug in here.
        var spec = pack switch
        {
            "pharmacy_rx" => SuavoAgent.Core.Verticals.Pharmacy.PharmacyPresets.NdcPricingList(),
            _ => null,
        };
        if (spec is null)
        {
            _logger.LogWarning("find_and_run_pricing_job: unknown pack {Pack}", pack);
            await AckAsync(false, null, $"unknown pack: {pack}");
            return;
        }

        // Connect to Helper IPC.
        if (!_ipcCommandClient.IsConnected)
        {
            var connected = await _ipcCommandClient.ConnectAsync(TimeSpan.FromSeconds(10), ct);
            if (!connected)
            {
                _logger.LogError("find_and_run_pricing_job: cannot connect to Helper command pipe");
                await AckAsync(false, null, "Helper command pipe unreachable");
                return;
            }
        }

        // Run discovery.
        var discoveryJobId = Guid.NewGuid().ToString("N");
        var discoveryResult = await _discoveryClient.FindAsync(_ipcCommandClient, discoveryJobId, spec, ct);
        if (discoveryResult is null)
        {
            _logger.LogError("find_and_run_pricing_job: discovery returned null");
            await AckAsync(false, null, "discovery failed — see agent logs");
            return;
        }

        _logger.LogInformation(
            "find_and_run_pricing_job: discovery resolution={Resolution} best={File} confidence={Conf}",
            discoveryResult.Resolution,
            discoveryResult.Best?.Candidate.Candidate.FileName ?? "(none)",
            discoveryResult.Best?.Confidence.ToString("F2") ?? "-");

        // ---- Decision: auto-run, confirm, or ask operator ---------------------
        if (discoveryResult.Resolution == FileDiscoveryResolution.AutoUse && discoveryResult.Best is not null)
        {
            var chosenPath = discoveryResult.Best.Candidate.Candidate.AbsolutePath;

            // Same safety gates as run_pricing_job: .xlsx only, local absolute,
            // canonical path matches.
            if (!IsExcelPathSafe(chosenPath, out var canonical, out var unsafeReason))
            {
                _logger.LogWarning("find_and_run_pricing_job: rejected discovered path — {Reason}", unsafeReason);
                await AckAsync(false, new
                {
                    status = "path_rejected",
                    reason = unsafeReason,
                    discoveryResolution = discoveryResult.Resolution.ToString(),
                }, unsafeReason);
                return;
            }

            if (!await _pricingJobSemaphore.WaitAsync(TimeSpan.Zero, ct))
            {
                _logger.LogWarning("find_and_run_pricing_job: another pricing job is already running");
                await AckAsync(false, null, "another pricing job is already running");
                return;
            }

            try
            {
                var jobId = Guid.NewGuid().ToString("N");
                var jobSpec = new PricingJobSpec(jobId, canonical, ndcColumn, supplierColumn, costColumn);
                _logger.LogInformation("find_and_run_pricing_job: auto-running pricing job {JobId}", jobId);

                _stateDb.UpsertPricingJob(jobSpec, PricingJobStatus.Pending, 0, 0, 0);
                var execution = await _pricingJobExecutor.RunAsync(jobSpec, ct);
                var progress = execution.Progress;
                if (_pricingJobCloudUploader != null)
                    await _pricingJobCloudUploader.UploadAsync(jobSpec, execution, commandId, ct);

                await AckAsync(execution.Ok, new
                {
                    status = "auto_ran",
                    jobId,
                    mode = execution.Mode,
                    discoveredFileName = Path.GetFileName(canonical),
                    discoveredBucket = discoveryResult.Best.Candidate.Candidate.Bucket.ToString(),
                    discoveryConfidence = discoveryResult.Best.Confidence,
                    discoveryReason = discoveryResult.Best.Reason,
                    discoveryTier = discoveryResult.Best.Tier.ToString(),
                    totalItems = progress.TotalItems,
                    completedItems = progress.CompletedItems,
                    failedItems = progress.FailedItems,
                    pricingStatus = progress.Status.ToString(),
                }, execution.Ok ? null : execution.Error ?? "pricing job failed — see agent logs");
            }
            finally
            {
                _pricingJobSemaphore.Release();
            }
            return;
        }

        // Not confident enough to auto-run — surface candidates for operator pick.
        if (discoveryResult.Resolution == FileDiscoveryResolution.NotFound)
        {
            await AckAsync(true, new
            {
                status = "not_found",
                discoveryResolution = discoveryResult.Resolution.ToString(),
            }, null);
            return;
        }

        var candidatesPayload = BuildCandidatesPayload(discoveryResult);
        await AckAsync(true, new
        {
            status = "needs_confirmation",
            discoveryResolution = discoveryResult.Resolution.ToString(),
            candidates = candidatesPayload,
            suggestedColumns = new
            {
                ndcColumn,
                supplierColumn,
                costColumn,
            },
        }, null);
    }

    private static bool IsExcelPathSafe(string path, out string canonical, out string reason)
    {
        canonical = "";
        reason = "";
        var ext = Path.GetExtension(path);
        if (!string.Equals(ext, ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            reason = "path must be .xlsx";
            return false;
        }
        if (path.StartsWith(@"\\") || !Path.IsPathRooted(path))
        {
            reason = "path must be local absolute";
            return false;
        }
        canonical = Path.GetFullPath(path);
        if (!string.Equals(canonical, path, StringComparison.OrdinalIgnoreCase))
        {
            reason = "path canonicalization mismatch";
            return false;
        }
        return true;
    }

    private object[] BuildCandidatesPayload(FileDiscoveryResult result)
    {
        var list = new List<CandidateRanking>();
        if (result.Best is not null) list.Add(result.Best);
        list.AddRange(result.Alternatives);

        return list.Select(c =>
        {
            var candidate = c.Candidate.Candidate;
            var token = _stateDb.SavePricingDiscoveryCandidate(candidate.AbsolutePath, candidate.FileName);
            return new
            {
                pathToken = token,
                fileName = candidate.FileName,
                sizeBytes = candidate.SizeBytes,
                lastModifiedUtc = candidate.LastModifiedUtc,
                bucket = candidate.Bucket.ToString(),
                confidence = c.Confidence,
                reason = c.Reason,
                tier = c.Tier.ToString(),
                hasErrorFromSampler = c.Candidate.ErrorMessage is not null,
                samplerError = c.Candidate.ErrorMessage,
                columnHeaders = (c.Candidate.Shape as TabularShapeSample)?.ColumnHeaders,
                rowCount = (c.Candidate.Shape as TabularShapeSample)?.RowCount ?? 0,
                primaryKeyColumnIndex = (c.Candidate.Shape as TabularShapeSample)?.PrimaryKeyColumnIndex ?? -1,
            };
        }).ToArray();
    }
}
