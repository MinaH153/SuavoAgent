using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SuavoAgent.Adapters.PioneerRx;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Discovery;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Models;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Contracts.Writeback;
using SuavoAgent.Core.ActionGrammarV1;
using SuavoAgent.Core.ActionGrammarV1.Workflows;
using SuavoAgent.Core.Autonomy;
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Diagnostics;
using SuavoAgent.Core.Health;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.Mission;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.Receipts;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Vision;

namespace SuavoAgent.Core.Workers;

public sealed partial class HeartbeatWorker : ResilientHostedService
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
    private readonly PricingTerminalAckOutbox? _pricingTerminalAckOutbox;
    private readonly SuavoAgent.Core.Autonomy.TaskAutonomyLedger? _taskAutonomy;
    private readonly IIpcCommandClient? _ipcCommandClient;
    private readonly IIntentCursorClient? _intentCursorClient;
    private readonly SuavoAgent.Core.Discovery.DiscoveryClient? _discoveryClient;
    private readonly ITopDispensedWorklistBuilder? _topDispensedWorklistBuilder;
    private readonly ITopDispensedWorklistProgressBuilder?
        _topDispensedWorklistProgressBuilder;
    private readonly IPricedWorkbookPublisher? _pricedWorkbookPublisher;
    // Wave 1B Track 1.4 — health composite. Both optional so the worker
    // still starts if Program.cs hasn't wired the health module yet (matches
    // the existing optional-deps pattern used for cloud client, pricing,
    // IPC command client, etc).
    private readonly IHealthSignals? _healthSignals;
    private readonly HealthCompositeCalculator? _healthCompositeCalculator;
    private readonly CloudAuthRecoveryCoordinator? _cloudAuthRecovery;
    private readonly WorkflowExecutor? _workflowExecutor;
    // Honeytoken immune reflex — read the Helper's gate-state compromise to emit the self-compromise
    // heartbeat signal. Optional (null if not wired); the read is best-effort and never blocks the heartbeat.
    private readonly SuavoAgent.Core.ActionGrammarV1.Verbs.Actuation.IActuationGateway? _actuationGateway;
    // On-device brain for the cockpit "talk to the agent" command. Optional (null/NullLocalInference
    // when reasoning is off) — the chat command then falls back to a cloud/templated reply.
    private readonly SuavoAgent.Core.Reasoning.ILocalInference? _localInference;
    // Actuation-readiness (strand detector). Tracker is written by ActuationReadinessWorker's
    // ping-only probe; the heartbeat just reads the latest snapshot — never probes inline.
    private readonly ActuationReadinessTracker? _actuationReadiness;
    private readonly HelperSelfHealCoordinator? _selfHealCoordinator;
    private readonly ApprovedPatientRetrievalCoordinator? _approvedPatientRetrieval;
    private readonly DeliveryWritebackCoordinator? _deliveryWriteback;
    private readonly SuavoAgent.Core.Reasoning.IActiveLearnedRuleRegistry? _activeLearnedRules;
    private readonly SuavoAgent.Core.Vision.VisionConfigurationCoordinator? _visionConfigurationCoordinator;
    private readonly SuavoAgent.Core.Vision.VisionConfigurationStatusProvider? _visionConfigurationStatus;
    private readonly SuavoAgent.Core.Vision.VisionConfigurationCommandOutbox? _visionConfigurationOutbox;
    private readonly Release1ConvergenceCoordinator? _release1Convergence;
    private readonly AutopilotRunCoordinator _autopilotRuns;
    private readonly SuavoAgent.Contracts.Security.ObservationActivationAuthority?
        _observationAuthority;
    private readonly string _autoRuleRunOwnerId = Guid.NewGuid().ToString("D");
    private readonly SemaphoreSlim _pricingJobSemaphore = new(1, 1);
    private readonly SemaphoreSlim _workflowSemaphore = new(1, 1);
    private readonly object _activeWorkflowLock = new();
    private CancellationTokenSource? _activeWorkflowCts;
    private string? _activeWorkflowRunId;

    // navigate_app — the general agentic loop. Single-threaded + cancellable, mirroring workflows.
    private readonly SemaphoreSlim _navigationSemaphore = new(1, 1);
    private readonly object _activeNavigationLock = new();
    private CancellationTokenSource? _activeNavigationCts;
    private string? _activeNavigationRunId;

    private DateTimeOffset _lastContextSync = DateTimeOffset.MinValue;
    private DateTimeOffset _lastEfficiencyReport = DateTimeOffset.MinValue;
    private int _consecutiveFailures;
    private bool _updateInProgress;
    private string? _lastUpdateChannel;
    private readonly DateTimeOffset _startTime = DateTimeOffset.UtcNow;
    private int _helperConsecutiveFailures;
    private bool _lastAuditChainValid = true;
    private int _lastRxCount;
    private DateOnly _lastPruneDate;
    private DateTimeOffset? _lastSyncAt;
    private bool _consentReceiptSent;

    protected override string WorkerName => "heartbeat";
    protected override bool RestartOnFault => _options.SelfHeal.WorkerSupervisorEnabled;

    protected override Task OnEscalateAsync()
    {
        // The cloud silent-agent alarm already catches a dead heartbeat; log loudly and stop.
        _logger.LogCritical("HeartbeatWorker exhausted supervised restarts — heartbeat halted");
        return Task.CompletedTask;
    }

    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        if (_cloudClient == null)
        {
            _logger.LogWarning("Heartbeat disabled — no cloud client configured");
            return;
        }

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

        await RecoverSignedAdmittedPricingCommandsAsync(stoppingToken)
            .ConfigureAwait(false);

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
                    _logger.LogSafeWarning(ex);
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
                    _logger.LogSafeDebug(ex);
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
                    _logger.LogSafeDebug(ex);
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
                // B2: distinguish a transient SQL blip from a sustained detection outage so the cockpit
                // can alarm even while the agent is otherwise online and heartbeating.
                var nowForRx = DateTimeOffset.UtcNow;
                var rxDetectionDegraded = rxWorker?.IsDetectionDegraded(nowForRx) ?? false;
                var sqlDarkSeconds = rxWorker?.SqlDarkSeconds(nowForRx) ?? 0;
                var consecutiveSqlFailures = rxWorker?.ConsecutiveSqlFailures ?? 0;
                var activeDetectionSource = rxWorker?.ActiveDetectionSource ?? "none";
                var learnedFallbackHealthy = rxWorker?.IsLearnedFallbackHealthy ?? false;
                var learnedAdapterStatus = _serviceProvider
                    .GetService<IActivePmsAdapterRegistry>()?
                    .Snapshot(nowForRx);

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
                        _logger.LogSafeDebug(ex);
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
                        _logger.LogSafeDebug(ex);
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
                        _logger.LogSafeDebug(ex);
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
                        _logger.LogSafeDebug(ex);
                    }
                }

                // Wave 1B Track 1.4 — compute + locally append the
                // agent.health_composite event each tick. Failure is logged
                // but does NOT block the heartbeat critical path; the agent
                // retries on the next tick.
                var healthComposite = EmitHealthComposite();

                // Honeytoken immune reflex — best-effort read of the Helper's compromise state. Null on any
                // failure/timeout (never blocks the heartbeat); apoptosis-level drives the cloud fleet-revoke.
                var compromiseSignal = await ReadCompromiseSignalAsync(stoppingToken);

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
                var behavioralDelivery = _stateDb.GetBehavioralDeliveryHealth(learningSessionId);
                var learnedRoutineCount = learningSessionId is null
                    ? 0
                    : _stateDb.GetLearnedRoutineCount(learningSessionId);
                var workflowTemplateCount = _stateDb.GetWorkflowTemplateCount(_options.TemplateLearning.SkillId);

                // FSD learning-screen telemetry — the phase + confidence signals that let the pharmacy
                // dashboard render the "what SuavoAgent has learned about this station" surface (observe→learn
                // made VISIBLE): phase (discovery→pattern→model→approved→active), when it entered that phase
                // (observed-since), and the supervised assist tallies (confirmed vs corrected = confidence to
                // assist). Best-effort: a learning-state read must NEVER break the heartbeat critical path.
                string? learningPhase = null, phaseChangedAtIso = null;
                int supervisedSuccessCount = 0, supervisedCorrectionCount = 0;
                if (learningSessionId is not null)
                {
                    try
                    {
                        if (_stateDb.GetLearningSession(learningSessionId) is { } ls)
                        {
                            learningPhase = ls.Phase;
                            supervisedSuccessCount = ls.SupervisedSuccessCount;
                            supervisedCorrectionCount = ls.SupervisedCorrectionCount;
                            phaseChangedAtIso = _stateDb.GetPhaseChangedAt(learningSessionId).ToString("o");
                        }
                    }
                    catch (Exception ex) { _logger.LogSafeDebug(ex); }
                }

                // v3.12.1.1 — upload local auto-rule approval state so the
                // pharmacy portal UI (MKM #44) can render real rows. Cloud
                // upserts on (pharmacy_id, rule_id); retired/deleted local
                // rows are handled by the cloud-side sync freshness window,
                // not by sending a delete signal here.
                var autoRuleApprovals = AutoRuleApprovalHeartbeatProjection.Project(
                    _stateDb.GetAllAutoRuleApprovals());
                var helperAttached = ipcServer?.IsConnected ?? false;
                _helperConsecutiveFailures = helperAttached ? 0 : _helperConsecutiveFailures + 1;
                var helperPayload = BuildHelperPayload(ipcServer);
                var pioneerObserver = _stateDb.GetLatestObserverStatus("pioneerrx");
                var observationReadiness = ObservationReadinessEvaluator.Evaluate(new(
                    nowForRx,
                    helperAttached,
                    behavioralDelivery,
                    pioneerObserver,
                    _stateDb.GetLatestObserverStatus("system_liveness"),
                    _stateDb.GetLatestObserverStatus("browser_domain"),
                    _stateDb.GetLatestObserverStatus("print"),
                    _stateDb.GetLatestObserverStatus("user_session"),
                    _stateDb.GetLatestObserverStatus("multi_app_uia")));
                var pioneerRxObserving = observationReadiness.PioneerRxReady;
                var pioneerRxObservationStatus = pioneerRxObserving ? "observing" : "not_observing";
                // Situation understanding: turn the raw signals (installed? db reachable? helper attached?)
                // into ONE state the agent can EXPLAIN, so a fresh install reports "PioneerRx is closed" or
                // "database unreachable" instead of failing silently by the absence of expected elements.
                var pms = PmsSituationClassifier.Classify(new PmsSignals(
                    PmsInstalled: PioneerRxInstallDetector.IsInstalled(_logger),
                    SqlConnected: sqlConnected,
                    HelperAttached: pioneerRxObserving,
                    // Explicitly configured = the operator pointed us at a SQL server (vs the implicit
                    // localhost fallback). Distinguishes "finish setup" from "configured DB is down".
                    SqlConfigured: !string.IsNullOrWhiteSpace(_options.SqlServer)));
                var visionCapture = _serviceProvider
                    .GetService<VisionCaptureTelemetry>()?
                    .Snapshot();
                var visionConfiguration = _visionConfigurationStatus?.Snapshot();

                var payload = new
                {
                    agentId = _options.AgentId,
                    version = _options.Version,
                    pharmacyId = _options.PharmacyId,
                    updateChannel = _lastUpdateChannel ?? "stable",
                    capabilities = BuildAgentCapabilities(),
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
                        observerStatus = pioneerObserver?.Status ?? "not_reported",
                        observerLastSeenAt = pioneerObserver?.ReceivedAtUtc.ToString("o"),
                        observerFresh = observationReadiness.PioneerRxReady,
                        liveObservationReady = observationReadiness.LiveObservationReady,
                        readinessCode = observationReadiness.Code,
                        readinessBlockers = observationReadiness.Blockers,
                        sqlConnected,
                        detectionDegraded = rxDetectionDegraded,
                        sqlDarkSeconds,
                        consecutiveSqlFailures,
                        activeDetectionSource,
                        learnedFallbackHealthy,
                    },
                    // PHI-free learned-adapter lifecycle telemetry: opaque session id,
                    // digest prefix, counters, and timestamps only. No query, table,
                    // status value, Rx number, or patient field leaves the workstation.
                    learnedAdapter = learnedAdapterStatus is null ? null : new
                    {
                        active = learnedAdapterStatus.HasActiveAdapter,
                        available = learnedAdapterStatus.IsAvailable(nowForRx),
                        sessionId = learnedAdapterStatus.SessionId,
                        templateDigestPrefix = learnedAdapterStatus.TemplateDigestPrefix,
                        consecutiveHealthFailures = learnedAdapterStatus.ConsecutiveHealthFailures,
                        retryAfter = learnedAdapterStatus.RetryAfter?.ToString("o"),
                        lastHealthyAt = learnedAdapterStatus.LastHealthyAt?.ToString("o"),
                    },
                    // The agent's own understanding of the PMS situation + a plain-language explanation
                    // the operator can act on (surfaced in the cockpit so a fresh install is never silent).
                    pmsSituation = new
                    {
                        situation = pms.Situation.ToString(),
                        code = pms.Code,
                        explanation = pms.Explanation,
                    },
                    // Top-level fields for cloud stats extraction
                    learningMode = _options.LearningMode,
                    sqlConnected = sqlConnected,
                    rxDetectionDegraded = rxDetectionDegraded,
                    sqlDarkSeconds = sqlDarkSeconds,
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
                        deliveryContractVersion = BehavioralEventBatch.CurrentContractVersion,
                        deliveryVerified = observationReadiness.ProtocolCompatible,
                        deliveryLossless = observationReadiness.Lossless,
                        liveObservationReady = observationReadiness.LiveObservationReady,
                        observationReadinessCode = observationReadiness.Code,
                        observationReadinessBlockers = observationReadiness.Blockers,
                        observationStreamCount = behavioralDelivery.StreamCount,
                        droppedEventCount = behavioralDelivery.DroppedEventCount,
                        sequenceGapCount = behavioralDelivery.SequenceGapCount,
                        rejectedEventCount = behavioralDelivery.RejectedEventCount,
                        duplicateBatchCount = behavioralDelivery.DuplicateBatchCount,
                        lastObservationBatchAt = behavioralDelivery.LastBatchUtc?.ToString("o"),
                        legacyUnverifiedSeen = behavioralDelivery.LegacyUnverifiedSeen,
                        lastLegacyBatchAt = behavioralDelivery.LastLegacyBatchUtc?.ToString("o"),
                        observationSpoolStatus = behavioralDelivery.ObservationSpoolStatus,
                        observationSpoolStatusAt = behavioralDelivery.LastObservationSpoolStatusUtc?.ToString("o"),
                        learnedRoutineCount = learnedRoutineCount,
                        workflowTemplateCount = workflowTemplateCount,
                        // FSD learning-screen fields (see telemetry computation above).
                        phase = learningPhase,
                        phaseChangedAt = phaseChangedAtIso,
                        supervisedSuccessCount = supervisedSuccessCount,
                        supervisedCorrectionCount = supervisedCorrectionCount,
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
                        configuration = visionConfiguration,
                        runtime = BuildVisionRuntimePayload(
                            _actuationReadiness?.Current?.VisionRuntime),
                        capture = visionCapture?.ToPayload(),
                    },
                    watchdog = BuildWatchdogPayload(),
                    sql = new
                    {
                        connected = sqlConnected,
                        lastRxCount = _lastRxCount
                    },
                    helper = helperPayload,
                    // Supervised-worker liveness (self-heal Chunk 3b) — see BuildWorkersPayload.
                    workers = BuildWorkersPayload(_serviceProvider.GetService<WorkerHealthRegistry>()),
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
                    // Local-brain (Tier-2 Qwen3) status so the cockpit can show readiness + only route
                    // chat to the brain when it's actually loaded (no slow round-trip to an off brain).
                    // modelId/isReady from ILocalInference; "off" when reasoning is disabled.
                    reasoning = new
                    {
                        modelId = _localInference?.ModelId ?? "off",
                        isReady = _localInference?.IsReady ?? false,
                        // Provisioning lifecycle + download percent — the dashboard's
                        // "Installing the brain… NN%" card. Enum serialized by NAME.
                        provisioningState = (_localInference?.ProvisioningState
                            ?? Reasoning.BrainProvisioningState.Off).ToString(),
                        provisioningPercent = _localInference?.ProvisioningPercent,
                    },
                    // Honeytoken immune reflex — PHI-free self-compromise signal (null unless tripped).
                    // apoptosis-level drives the cloud fleet-revoke; lower rungs are alarm + audit only.
                    compromise = compromiseSignal,
                    // v3.12.1.1 auto-rule approval mirror. Empty array when
                    // Learning:Template:Enabled is off or no templates have
                    // been extracted yet — safe to emit either way.
                    autoRuleApprovals = autoRuleApprovals,
                    // PHI-free PIC handshake. Only opaque tenant/device ids and
                    // exact policy digests cross the authenticated heartbeat.
                    pricingApprovalProposals = _stateDb
                        .GetPendingPricingApprovalProposals(
                            20,
                            DateTimeOffset.UtcNow),
                    // Phase C: the installer's post-install self-verify outcome (passed + summary),
                    // read from install-verify.json, so the cockpit can show install health remotely.
                    // Null until the installer has written it (older/legacy installs have no file).
                    installVerify = ReadInstallVerify(),
                };

                var response = await _cloudClient.HeartbeatAsync(payload, stoppingToken);
                var heartbeatObservedAt = DateTimeOffset.UtcNow;
                RenewPricingCloudAuthorityLease(response, heartbeatObservedAt);
                _lastSyncAt = heartbeatObservedAt;
                _consecutiveFailures = 0;
                _logger.LogDebug("Heartbeat OK");

                // Positive, target-version-bound install evidence. Setup does
                // not promote a staged credential or show Ready until this
                // proves cloud auth + the real interactive PMS path together.
                try
                {
                    var healthAtHeartbeat = _healthSignals?.Snapshot();
                    var actuationAtHeartbeat = _actuationReadiness?.Current;
                    RuntimeHealthEvidence.WriteCloudAuthHealth(
                        RuntimeHealthEvidence.CloudAuthHealthPath(),
                        status: "ok",
                        lastAttemptAt: _lastSyncAt.Value,
                        lastSuccessAt: _lastSyncAt,
                        consecutiveFailures: 0,
                        lastErrorKind: null,
                        recoveryAttempted: false,
                        recoveryOutcome: null,
                        restartRequested: false);
                    RuntimeHealthEvidence.WriteActivationReadiness(
                        RuntimeHealthEvidence.ActivationReadinessPath(),
                        _options.Version,
                        _options.AgentId,
                        _options.InstallProvisioningId,
                        _lastSyncAt.Value,
                        helperAttached,
                        healthAtHeartbeat?.IpcConnected ?? false,
                        actuationAtHeartbeat?.Ready ?? false,
                        sqlConnected,
                        healthAtHeartbeat?.SchemaCanaryGreen ?? false,
                        pms.Code,
                        deviceProof: null);
                }
                catch (Exception evidenceError)
                {
                    _logger.LogSafeDebug(evidenceError);
                }

                // A successful cloud round-trip is one REQUIRED update health gate, but never commits
                // by itself. It answers the SYSTEM coordinator's one-time challenge; Maintenance also
                // requires the signed cohort, service, and interactive Helper/IPC gates before commit.
                UpdateActivationHealthMilestoneWriter.TryWriteAfterSuccessfulHeartbeat(
                    _options.Version,
                    _options.AgentId,
                    _options.MachineFingerprint,
                    _logger);

                // Echo updateChannel from server for canary rollout tracking
                if (response.HasValue &&
                    response.Value.TryGetProperty("data", out var respData) &&
                    respData.TryGetProperty("updateChannel", out var channel))
                {
                    _lastUpdateChannel = channel.GetString();
                }

                // Process signed commands (fetch_patient, decommission, update)
                // All destructive actions require ECDSA-signed command envelope.
                if (response.HasValue)
                {
                    ProcessPricingApprovalProposalReceipts(response.Value);
                    await ProcessSignedCommandAsync(response.Value, stoppingToken);
                }

                // A command is served once by the control plane (status moves pending -> sent).
                // Retry the durable local operation independently until its authenticated callback
                // receipt lands and the idempotent command ACK succeeds.
                if (_approvedPatientRetrieval is not null)
                    await _approvedPatientRetrieval.RetryPendingAsync(stoppingToken);
                await RetryPendingDeliveryWritebacksAsync(stoppingToken).ConfigureAwait(false);
                if (_visionConfigurationOutbox is not null)
                    await _visionConfigurationOutbox.RetryPendingAsync(stoppingToken)
                        .ConfigureAwait(false);
                if (_release1Convergence is not null)
                    await _release1Convergence.RetryPendingAsync(stoppingToken)
                        .ConfigureAwait(false);

                _commandVerifier?.PruneNonces(TimeSpan.FromMinutes(5));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (ex is HttpRequestException httpException &&
                    IsTerminalInactiveAgentResponse(httpException))
                {
                    try
                    {
                        var cancellation = RevokePricingAuthorityAndCancelRuns(
                            _stateDb,
                            _autopilotRuns,
                            DateTimeOffset.UtcNow);
                        _logger.LogWarning(
                            "core.pricing.cloud_authority_revoked active_runs={Count} cancellation_failures={Failures}",
                            cancellation.SignalledRunCount,
                            cancellation.CancellationSignalFailureCount);
                    }
                    catch (Exception persistenceError)
                    {
                        _logger.LogCritical(
                            "core.pricing.cloud_authority_revocation_persist_failed exception_type={ExceptionType}",
                            persistenceError.GetType().Name);
                        throw;
                    }
                }
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
                        _logger.LogSafeWarning(recoveryEx);
                    }
                }
                _logger.LogSafeWarning(ex);
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
    /// <summary>
    /// Best-effort read of the Helper's honeytoken compromise state (via the existing actuation gate-state
    /// IPC) for the self-compromise heartbeat signal. Returns null on a missing gateway or any failure/
    /// timeout — the heartbeat must NEVER block on this. PHI-free by construction: only the opaque token id,
    /// the corroboration level, and the already-sanitized reason label cross the boundary.
    /// </summary>
}
