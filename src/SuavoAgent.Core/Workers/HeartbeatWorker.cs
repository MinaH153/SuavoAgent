using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SuavoAgent.Adapters.PioneerRx;
using SuavoAgent.Contracts.Discovery;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Models;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.ActionGrammarV1;
using SuavoAgent.Core.ActionGrammarV1.Workflows;
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

namespace SuavoAgent.Core.Workers;

public sealed class HeartbeatWorker : ResilientHostedService
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
    private readonly SuavoAgent.Core.Autonomy.TaskAutonomyLedger? _taskAutonomy;
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
    private long? _decommissionPendingSince;
    private string? _lastUpdateChannel;
    private readonly DateTimeOffset _startTime = DateTimeOffset.UtcNow;
    private int _helperConsecutiveFailures;
    private bool _lastAuditChainValid = true;
    private int _lastRxCount;
    private DateOnly _lastPruneDate;
    private DateTimeOffset? _lastSyncAt;
    private bool _consentReceiptSent;
    private bool _otaHealthConfirmed;

    public HeartbeatWorker(
        ILogger<HeartbeatWorker> logger,
        IOptions<AgentOptions> options,
        IServiceProvider serviceProvider,
        AgentStateDb stateDb,
        WorkerHealthRegistry? healthRegistry = null)
        : base(logger, healthRegistry)
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
        _taskAutonomy = serviceProvider.GetService<SuavoAgent.Core.Autonomy.TaskAutonomyLedger>();
        _ipcCommandClient = serviceProvider.GetService<IpcCommandClient>();
        _intentCursorClient = serviceProvider.GetService<IIntentCursorClient>();
        _discoveryClient = serviceProvider.GetService<SuavoAgent.Core.Discovery.DiscoveryClient>();
        _healthSignals = serviceProvider.GetService<IHealthSignals>();
        _healthCompositeCalculator = serviceProvider.GetService<HealthCompositeCalculator>();
        _cloudAuthRecovery = serviceProvider.GetService<CloudAuthRecoveryCoordinator>();
        _workflowExecutor = serviceProvider.GetService<WorkflowExecutor>();
        _actuationGateway = serviceProvider.GetService<SuavoAgent.Core.ActionGrammarV1.Verbs.Actuation.IActuationGateway>();
        _localInference = serviceProvider.GetService<SuavoAgent.Core.Reasoning.ILocalInference>();
        _actuationReadiness = serviceProvider.GetService<ActuationReadinessTracker>();
        _selfHealCoordinator = serviceProvider.GetService<HelperSelfHealCoordinator>();

        var agentId = _options.AgentId ?? "";
        var fingerprint = _options.MachineFingerprint ?? "";
        if (!string.IsNullOrEmpty(agentId))
        {
            _commandVerifier = new SignedCommandVerifier(
                new Dictionary<string, string> { ["suavo-cmd-v1"] = SelfUpdater.CommandPublicKeyDer },
                agentId, fingerprint);
        }
    }

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
                // B2: distinguish a transient SQL blip from a sustained detection outage so the cockpit
                // can alarm even while the agent is otherwise online and heartbeating.
                var nowForRx = DateTimeOffset.UtcNow;
                var rxDetectionDegraded = rxWorker?.IsDetectionDegraded(nowForRx) ?? false;
                var sqlDarkSeconds = rxWorker?.SqlDarkSeconds(nowForRx) ?? 0;
                var consecutiveSqlFailures = rxWorker?.ConsecutiveSqlFailures ?? 0;

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
                    catch (Exception ex) { _logger.LogDebug(ex, "learning-phase telemetry read failed (heartbeat unaffected)"); }
                }

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
                // Situation understanding: turn the raw signals (installed? db reachable? helper attached?)
                // into ONE state the agent can EXPLAIN, so a fresh install reports "PioneerRx is closed" or
                // "database unreachable" instead of failing silently by the absence of expected elements.
                var pms = PmsSituationClassifier.Classify(new PmsSignals(
                    PmsInstalled: PioneerRxInstallDetector.IsInstalled(_logger),
                    SqlConnected: sqlConnected,
                    HelperAttached: helperAttached,
                    // Explicitly configured = the operator pointed us at a SQL server (vs the implicit
                    // localhost fallback). Distinguishes "finish setup" from "configured DB is down".
                    SqlConfigured: !string.IsNullOrWhiteSpace(_options.SqlServer)));
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
                        detectionDegraded = rxDetectionDegraded,
                        sqlDarkSeconds,
                        consecutiveSqlFailures,
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
                    // Phase C: the installer's post-install self-verify outcome (passed + summary),
                    // read from install-verify.json, so the cockpit can show install health remotely.
                    // Null until the installer has written it (older/legacy installs have no file).
                    installVerify = ReadInstallVerify(),
                };

                var response = await _cloudClient.HeartbeatAsync(payload, stoppingToken);
                _lastSyncAt = DateTimeOffset.UtcNow;
                _consecutiveFailures = 0;
                _logger.LogDebug("Heartbeat OK");

                // OTA crash-loop guard: a SUCCESSFUL cloud heartbeat is the real "healthy" milestone
                // (Codex P1: must be AFTER the send succeeds, not while building the payload — else an
                // update that breaks cloud connectivity could commit locally and delete the rollback).
                // Commit any OTA on probation: clear the flag + reap the .old set. Once per process.
                if (!_otaHealthConfirmed)
                {
                    _otaHealthConfirmed = true;
                    var otaInstallDir = Path.GetDirectoryName(Environment.ProcessPath);
                    if (!string.IsNullOrEmpty(otaInstallDir))
                    {
                        try { SuavoAgent.Core.Cloud.SelfUpdater.ConfirmHealthyAndReap(otaInstallDir, _logger); }
                        catch (Exception ex) { _logger.LogDebug(ex, "OTA confirm-healthy non-fatal"); }
                    }

                    // Close the OTA telemetry loop: a clean self-update writes "applying" then EXITS,
                    // and on restart nothing flips it to success — so update-health.json (and the cloud
                    // rollout reading it) stays "applying" forever even though the new version is running
                    // and just heartbeated successfully (observed live on 3.24.0). Now that the first
                    // heartbeat on the new binary succeeded, finalize a stuck "applying" record that
                    // targets the version we are actually running.
                    try
                    {
                        var uh = RuntimeHealthEvidence.ReadUpdateHealth(RuntimeHealthEvidence.UpdateHealthPath());
                        if (RuntimeHealthEvidence.IsCompletedApplyingUpdate(uh, _options.Version))
                        {
                            WriteUpdateHealthEvidence(
                                "current",
                                _options.Version,
                                lastErrorKind: null,
                                consecutiveFailures: 0,
                                channel: _lastUpdateChannel ?? _options.UpdateChannel);
                            _logger.LogInformation(
                                "OTA telemetry finalized: update to v{Version} confirmed succeeded on first healthy heartbeat",
                                _options.Version);
                        }
                    }
                    catch (Exception ex) { _logger.LogDebug(ex, "OTA telemetry finalize non-fatal"); }
                }

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
    /// <summary>
    /// Best-effort read of the Helper's honeytoken compromise state (via the existing actuation gate-state
    /// IPC) for the self-compromise heartbeat signal. Returns null on a missing gateway or any failure/
    /// timeout — the heartbeat must NEVER block on this. PHI-free by construction: only the opaque token id,
    /// the corroboration level, and the already-sanitized reason label cross the boundary.
    /// </summary>
    private async Task<SuavoAgent.Contracts.Models.CompromiseSignalPayload?> ReadCompromiseSignalAsync(
        CancellationToken ct)
    {
        if (_actuationGateway is null) return null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var state = await _actuationGateway.GetStateAsync(timeout.Token).ConfigureAwait(false);
            if (!state.CompromiseDetected) return null;

            return new SuavoAgent.Contracts.Models.CompromiseSignalPayload(
                Detected: true,
                HoneytokenTripped: true,
                HoneytokenId: SuavoAgent.Contracts.Models.HoneytokenConstants.ComputeId(),
                CorroborationLevel: state.CompromiseLevel ?? "degrade",
                ReasonLabel: state.CompromiseReasonLabel ?? "_unknown",
                OccurredAtUtc: state.CompromiseAtUtc ?? DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Honeytoken compromise signal read failed (non-blocking)");
            return null;
        }
    }

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
            // Actuation-readiness — the strand detector. `attached` above reads the EVENT pipe
            // (Helper→Core) and is structurally blind to a stranded COMMAND pipe; this block is
            // the truthful "can the agent act right now" signal the cloud composite must use.
            // Null until the first probe completes (or on agents without the readiness worker).
            actuation = BuildActuationPayload(_actuationReadiness?.Current, _selfHealCoordinator?.Snapshot(DateTimeOffset.UtcNow)),
        };
    }

    /// <summary>
    /// Wire shape of <c>helper.actuation</c> in the heartbeat payload — the agent-side half of
    /// the actuation-readiness contract. PHI-free: booleans, session ids, pid, static codes,
    /// ISO timestamps. The cloud computes its health composite component from
    /// <c>ready</c> + freshness of <c>lastConclusiveCheckAt</c> (a stale verdict is UNKNOWN,
    /// not healthy) and renders <c>failureReason</c> in the cockpit when not ready.
    /// </summary>
    internal static object? BuildActuationPayload(ActuationReadinessSnapshot? s, SelfHealState? selfHeal)
    {
        if (s is null) return null;
        return new
        {
            ready = s.Ready,
            commandPipeResponsive = s.CommandPipeResponsive,
            isConsoleInteractive = s.IsConsoleInteractive,
            helperSessionId = s.HelperSessionId,
            activeConsoleSessionId = s.ActiveConsoleSessionId,
            helperPid = s.HelperPid,
            failureCode = s.FailureCode,
            failureReason = s.FailureReason,
            lastConclusiveCheckAt = s.LastConclusiveCheckAtUtc?.ToString("o"),
            lastProbeAttemptAt = s.LastProbeAttemptAtUtc.ToString("o"),
            lastCheckSkippedReason = s.SkippedReason,
            consecutiveStrandFailures = s.ConsecutiveStrandFailures,
            selfHeal = selfHeal is null ? null : new
            {
                lastAttemptAt = selfHeal.LastAttemptAtUtc?.ToString("o"),
                attemptsInWindow = selfHeal.AttemptsInWindow,
                exhausted = selfHeal.Exhausted,
            },
        };
    }

    /// <summary>
    /// Supervised-worker liveness for the heartbeat wire — restart-looping/escalated workers so
    /// the cloud's agent-health-watch can remediate at worker granularity (closed loop) rather
    /// than only detecting a fully-silent agent. Empty array when no registry / no faults (safe to
    /// emit either way). Names are static ("rx-detection", …); no PHI.
    /// </summary>
    internal static object[] BuildWorkersPayload(WorkerHealthRegistry? registry)
        => (registry?.Snapshot() ?? Array.Empty<WorkerHealth>())
            .Select(w => (object)new
            {
                name = w.Name,
                restartCount = w.RestartCount,
                escalated = w.Escalated,
                lastFaultUtc = w.LastFaultUtc.ToString("o"),
            })
            .ToArray();

    // Phase C: surface the installer's post-install self-verify outcome to the cockpit. Reads the
    // compact {passed, summary} from install-verify.json (written by SuavoAgent.Setup's PostInstallVerifier).
    // Read-only, fail-soft; returns null when the file is absent (legacy install) or unreadable.
    private object? ReadInstallVerify()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent",
            "install-verify.json");

        try
        {
            if (!File.Exists(path)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var passed = root.TryGetProperty("passed", out var p) && p.ValueKind == JsonValueKind.True;
            var summary = root.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString()
                : null;
            return new { passed, summary };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Install verification read failed");
            return null;
        }
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
                case "repair":
                case "repair_agent":
                    await HandleRepairAgentAsync(scEl, ct);
                    break;
                case "collect_health_probe":
                    await HandleCollectHealthProbeAsync(scEl, cmd, ct);
                    break;
                case "fetch_diagnostics":
                    await HandleFetchDiagnosticsAsync(scEl, cmd, ct);
                    break;
                case "export_pioneerrx_shadow_fixture":
                    await PioneerRxShadowFixtureCommand.HandleAsync(
                        scEl, cmd, _options, _serviceProvider, _stateDb, _cloudClient, _logger, ct);
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
                case "show_cursor":
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
                case "update_selector":
                    await HandleUpdateSelectorCommandAsync(scEl, cmd, ct);
                    break;
                case "navigate_app":
                    _ = Task.Run(() => HandleNavigateAppAsync(scEl, cmd, ct), ct);
                    break;
                case "navigate_pricing":
                    _ = Task.Run(() => HandleNavigatePricingAsync(scEl, cmd, ct), ct);
                    break;
                case "replay_template":
                    _ = Task.Run(() => HandleReplayTemplateAsync(scEl, cmd, ct), ct);
                    break;
                case "run_learned_template":
                    _ = Task.Run(() => HandleRunLearnedTemplateAsync(scEl, cmd, ct), ct);
                    break;
                case "explore_sandbox":
                    _ = Task.Run(() => HandleSandboxExploreAsync(scEl, cmd, ct), ct);
                    break;
                case "replay_skill":
                    _ = Task.Run(() => HandleReplaySkillAsync(scEl, cmd, ct), ct);
                    break;
                case "abort_navigation":
                    await HandleAbortNavigationAsync(scEl, cmd, ct);
                    break;
                case "force_restart":
                    await HandleForceRestartAsync(scEl, cmd, ct);
                    break;
                case "force_learning_phase":
                    await HandleForceLearningPhaseAsync(scEl, cmd, ct);
                    break;
                case "extend_app_allowlist":
                    await HandleExtendAppAllowlistAsync(scEl, cmd, ct);
                    break;
                case "discover_elements":
                    await HandleDiscoverElementsAsync(scEl, cmd, ct);
                    break;
                case "chat":
                    await HandleChatAsync(scEl, cmd, ct);
                    break;
                case "set_reasoning_config":
                    await HandleSetReasoningConfigAsync(scEl, cmd, ct);
                    break;
                case "set_vision_config":
                    await HandleSetVisionConfigAsync(scEl, cmd, ct);
                    break;
                case "restart_helper":
                    await HandleRestartHelperAsync(scEl, cmd, ct);
                    break;
                case "self_uninstall":
                    await HandleSelfUninstallAsync(scEl, ct);
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

    /// <summary>
    /// extend_app_allowlist — operator-pushed RUNTIME widening of the actuation sandbox app allowlist
    /// (canary / non-PHI boxes only; the cloud must never emit this to a PHI box). Merges
    /// { app_key: "process.exe" } into %PROGRAMDATA%\SuavoAgent\actuation.json "AllowedApps" (the
    /// complete set, atomic temp+rename), then re-applies in BOTH processes with NO restart: Core via
    /// LoadAndExtendFromConfig() (reads the file we just wrote — single merge path), Helper via the
    /// actuation.reload_allowlist IPC. Survives restart because both processes re-load the same file at
    /// startup. Each addition is pre-validated with the SAME rules ExtendAllowlist enforces so the ack
    /// can NAME each rejection instead of silently dropping it. Rides the signed ECDSA command pipeline.
    /// </summary>
    // Enumerate an allowlisted app's actionable UIA elements (controlType + automationId +
    // PHI-scrubbed name) and ack them as the command result — the agent's "look at the UI" capability,
    // so locators are grounded on real elements instead of guessed.
    private async Task HandleDiscoverElementsAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        try
        {
            if (_actuationGateway is null) { await AckAsync(false, null, "actuation_unavailable"); return; }
            var process = dataEl.TryGetProperty("process", out var pe) ? pe.GetString() : null;
            if (string.IsNullOrWhiteSpace(process)) { await AckAsync(false, null, "process required"); return; }
            var max = dataEl.TryGetProperty("max", out var me) && me.TryGetInt32(out var m) ? m : 60;

            var r = await _actuationGateway
                .DiscoverElementsAsync(new SuavoAgent.Contracts.Ipc.DiscoverElementsRequest(process, max), ct)
                .ConfigureAwait(false);
            if (!r.Ok) { await AckAsync(false, null, $"{r.RejectionCode}: {r.RejectionReason}"); return; }

            System.Text.Json.Nodes.JsonNode? elements = null;
            try { elements = System.Text.Json.Nodes.JsonNode.Parse(r.Payload ?? "[]"); } catch { /* leave null */ }
            var count = elements is System.Text.Json.Nodes.JsonArray arr ? arr.Count : 0;
            _logger.LogInformation("discover_elements process={Process} count={Count}", process, count);
            await AckAsync(true, new { process, count, elements }, null);
        }
        catch (Exception ex)
        {
            await AckAsync(false, null, ex.Message);
        }
    }

    // Per-box (canary-only) enable of Tier-2 reasoning. Writes %PROGRAMDATA%\SuavoAgent\reasoning.json
    // (shaped {"Agent":{"Reasoning":{...}}}) which is layered over appsettings + survives OTA. The
    // ILocalInference factory decides at startup, so this takes effect on the NEXT restart (then the
    // model + native libs auto-provision; a 2nd restart loads them). Off everywhere it isn't pushed.
    private async Task HandleSetReasoningConfigAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        try
        {
            string? Str(string k) => dataEl.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            int? Int(string k) => dataEl.TryGetProperty(k, out var v) && v.TryGetInt32(out var n) ? n : null;
            bool Bool(string k) => dataEl.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;

            var modelUrl = Str("modelUrl");
            var nativeLibsUrl = Str("nativeLibsUrl");
            // Validate URLs are https (no smuggling a local path / http source).
            foreach (var (name, url) in new[] { ("modelUrl", modelUrl), ("nativeLibsUrl", nativeLibsUrl) })
            {
                if (url is not null && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    await AckAsync(false, null, $"{name} must be https");
                    return;
                }
            }
            // Native code (the DLLs) MUST be pinned by sha256 — refuse to configure a native-libs
            // download with no integrity hash (it would be refused at extract time anyway; reject early).
            if (!string.IsNullOrWhiteSpace(nativeLibsUrl) && string.IsNullOrWhiteSpace(Str("nativeLibsSha256")))
            {
                await AckAsync(false, null, "nativeLibsSha256 is required when nativeLibsUrl is set (native code must be sha-pinned)");
                return;
            }

            // Build the Reasoning object; only include provided keys so unset ones fall back to defaults.
            var reasoning = new System.Text.Json.Nodes.JsonObject { ["Enabled"] = Bool("enabled") };
            void Put(string k, string? v) { if (!string.IsNullOrWhiteSpace(v)) reasoning[k] = v; }
            void PutInt(string k, int? v) { if (v is int n) reasoning[k] = n; }
            Put("ModelUrl", modelUrl);
            Put("ModelSha256", Str("modelSha256"));
            Put("ModelPath", Str("modelPath"));
            Put("ModelId", Str("modelId"));
            Put("NativeLibsUrl", nativeLibsUrl);
            Put("NativeLibsSha256", Str("nativeLibsSha256"));
            Put("NativeLibraryPath", Str("nativeLibraryPath"));
            PutInt("ContextSize", Int("contextSize"));
            PutInt("MaxOutputTokens", Int("maxOutputTokens"));
            PutInt("CpuThreads", Int("cpuThreads"));
            PutInt("IdleUnloadSeconds", Int("idleUnloadSeconds"));

            var root = new System.Text.Json.Nodes.JsonObject
            {
                ["Agent"] = new System.Text.Json.Nodes.JsonObject { ["Reasoning"] = reasoning },
            };

            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SuavoAgent");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "reasoning.json");
            var tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), ct);
            // Atomic replace — a crash/kill in the old delete-then-move window left reasoning.json
            // missing (the .tmp isn't read at boot), so startup silently bound default config.
            File.Move(tmp, path, overwrite: true);

            _logger.LogInformation("set_reasoning_config: wrote reasoning.json (Enabled={Enabled}) — takes effect on next restart", Bool("enabled"));
            await AckAsync(true, new { applied = true, enabled = Bool("enabled"), fields = reasoning.Select(kv => kv.Key).ToArray(), note = "restart required to activate" }, null);
        }
        catch (Exception ex)
        {
            await AckAsync(false, null, ex.Message);
        }
    }

    /// <summary>
    /// Writes vision.json + provisions the SHA-verified Tesseract bundle into the agent's (SYSTEM-
    /// writable) config dir, so the Helper can read the Pricing grid BY SIGHT. Runs in Core because the
    /// interactive user session cannot write the ACL-locked %ProgramData%\SuavoAgent folder. Native
    /// code, so the bundle is https + sha-pinned. Takes effect on the next restart.
    /// </summary>
    private async Task HandleSetVisionConfigAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        try
        {
            string? Str(string k) => dataEl.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            int? Int(string k) => dataEl.TryGetProperty(k, out var v) && v.TryGetInt32(out var n) ? n : null;
            bool Bool(string k) => dataEl.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;

            var enabled = Bool("enabled");
            var tesseractEnabled = Bool("tesseractEnabled");
            var bundleUrl = Str("bundleUrl");
            var bundleSha256 = Str("bundleSha256");

            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SuavoAgent");
            var nativeLibraryPath = Str("nativeLibraryPath") ?? Path.Combine(dir, "vision");
            var tessdataPath = Str("tessdataPath") ?? Path.Combine(nativeLibraryPath, "tessdata");
            var language = Str("language") ?? "eng";
            var minConfidence = Int("minConfidence") ?? 50;

            // Native code MUST be https + sha-pinned (same rule as the reasoning native libs).
            if (tesseractEnabled)
            {
                if (string.IsNullOrWhiteSpace(bundleUrl) || !bundleUrl!.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    await AckAsync(false, null, "bundleUrl (https) is required when tesseractEnabled=true");
                    return;
                }
                if (string.IsNullOrWhiteSpace(bundleSha256))
                {
                    await AckAsync(false, null, "bundleSha256 is required when tesseractEnabled=true (native code must be sha-pinned)");
                    return;
                }
            }

            // vision.json — VisionOptions shape (VisionBootstrap.LoadOptions deserializes this).
            var root = new System.Text.Json.Nodes.JsonObject
            {
                ["Enabled"] = enabled,
                ["Tesseract"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["Enabled"] = tesseractEnabled,
                    ["NativeLibraryPath"] = nativeLibraryPath,
                    ["TessdataPath"] = tessdataPath,
                    ["Language"] = language,
                    ["MinConfidence"] = minConfidence,
                },
            };

            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "vision.json");
            var tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), ct);
            File.Move(tmp, path, overwrite: true);
            _logger.LogInformation("set_vision_config: wrote vision.json (Enabled={Enabled}, OCR={Ocr})", enabled, tesseractEnabled);

            var provision = "skipped";
            if (tesseractEnabled)
            {
                var prov = new SuavoAgent.Core.Vision.TesseractBundleProvisioner(_logger);
                var r = await prov.ProvisionAsync(bundleUrl!, bundleSha256!, nativeLibraryPath, ct);
                provision = r.Message;
                if (!r.Ok)
                {
                    await AckAsync(false, null, $"vision.json written but Tesseract provisioning failed: {r.Message}");
                    return;
                }
            }

            // Grant the de-priv Helper (BUILTIN\Users) READ on vision.json + the vision dir. Core wrote
            // them into the SYSTEM-locked ProgramData folder, so without this the Helper (interactive
            // user) can't read vision.json and silently stays vision-off.
            var aclOk = SuavoAgent.Core.Vision.VisionAssetsAclGrant.Apply(
                path, nativeLibraryPath, m => _logger.LogInformation("set_vision_config acl: {Msg}", m));
            if (enabled && !aclOk)
                _logger.LogWarning("set_vision_config: Helper read-grant did not fully succeed — the Helper may not read vision.json");

            await AckAsync(true, new { applied = true, enabled, tesseractEnabled, nativeLibraryPath, tessdataPath, provision, aclGranted = aclOk, note = "restart required to activate" }, null);
        }
        catch (Exception ex)
        {
            await AckAsync(false, null, ex.Message);
        }
    }

    // Cockpit "talk to the agent" → the on-device Qwen3 brain. Reply is PHI-scrubbed Core-side
    // (defense-in-depth with the cloud ack scrub) before it leaves the box. When the brain isn't ready
    // (reasoning off / model still provisioning), acks ready=false so the cockpit falls back to a
    // cloud/templated reply — the local brain is a bonus, never a hard dependency.
    // 0/1 single-flight gate for chat inference. The local brain has ONE shared LLama context, which
    // is not safe to drive concurrently; this also caps how much CPU chat can ever take from PioneerRx.
    private int _chatInFlight;

    private Task HandleChatAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            try { await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "chat ack failed"); }
        }

        var prompt = dataEl.TryGetProperty("prompt", out var pe) ? pe.GetString() : null;
        if (string.IsNullOrWhiteSpace(prompt))
            return AckAsync(false, null, "prompt required");

        if (_localInference is null || !_localInference.IsReady)
            return AckAsync(true, new { reply = (string?)null, ready = false, model = _localInference?.ModelId ?? "none" }, null);

        // Already answering another prompt — ack a fast "not now" (cockpit shows its templated reply)
        // rather than serialize behind a slow local generation.
        if (Interlocked.CompareExchange(ref _chatInFlight, 1, 0) != 0)
            return AckAsync(true, new { reply = (string?)null, ready = true, model = _localInference.ModelId, busy = true }, null);

        // Run inference OFF the heartbeat loop. Chat must NEVER block heartbeats or operational
        // commands: a single slow/looping local generation used to stall the whole loop (one command
        // per heartbeat, awaited inline) until the cloud expired the command 5 min later. The nonce is
        // already consumed by the signed-command verifier, so detaching here is replay-safe. ChatAsync
        // owns its own hard wall-clock ceiling, so this task always completes and resets the gate.
        _ = Task.Run(async () =>
        {
            try
            {
                var reply = await _localInference.ChatAsync(prompt!, ct).ConfigureAwait(false);
                var scrubbed = SuavoAgent.Core.Learning.PhiScrubber.ScrubText(reply);
                await AckAsync(true, new { reply = scrubbed, ready = true, model = _localInference.ModelId }, null);
            }
            catch (Exception ex)
            {
                await AckAsync(false, null, ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _chatInFlight, 0);
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    private async Task HandleExtendAppAllowlistAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        try
        {
            if (!dataEl.TryGetProperty("apps", out var appsEl) || appsEl.ValueKind != JsonValueKind.Object)
            {
                await AckAsync(false, null, "apps object required: { \"app_key\": \"process.exe\" }");
                return;
            }

            var dir = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var path = System.IO.Path.Combine(dir, "SuavoAgent", "actuation.json");

            // Preserve existing config (Enabled / DryRun / prior AllowedApps) and merge the new apps.
            var root = System.IO.File.Exists(path)
                ? System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(path))?.AsObject()
                    ?? new System.Text.Json.Nodes.JsonObject()
                : new System.Text.Json.Nodes.JsonObject();
            var allowed = root["AllowedApps"] as System.Text.Json.Nodes.JsonObject
                ?? new System.Text.Json.Nodes.JsonObject();

            // Pre-validate each addition with the SAME rules ActuationAllowlistedSandboxApps.ExtendAllowlist
            // enforces, so the ack names what was rejected (the config loader silently drops bad entries).
            var rejected = new List<string>();
            foreach (var p in appsEl.EnumerateObject())
            {
                var key = p.Name?.Trim() ?? "";
                var proc = p.Value.ValueKind == JsonValueKind.String ? (p.Value.GetString() ?? "").Trim() : "";
                if (!System.Text.RegularExpressions.Regex.IsMatch(key, "^[a-zA-Z0-9_-]{1,64}$")) { rejected.Add($"{p.Name}:bad_key"); continue; }
                if (!proc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { rejected.Add($"{key}:not_exe"); continue; }
                if (proc.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0) { rejected.Add($"{key}:path_chars"); continue; }
                if (string.Equals(key, "notepad", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(key, "calculator", StringComparison.OrdinalIgnoreCase)) { rejected.Add($"{key}:reserved_default"); continue; }
                allowed[key] = proc;
            }
            root["AllowedApps"] = allowed;

            // Atomic write: temp + replace so a mid-write Watchdog restart never sees a torn file.
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            System.IO.File.WriteAllText(tmp,
                root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            System.IO.File.Move(tmp, path, overwrite: true);

            // Apply in Core in-process (reads the file we just wrote — single merge path, no drift).
            ActuationAllowlistedSandboxApps.LoadAndExtendFromConfig();

            // Apply in the Helper (separate process, separate static) — no restart.
            var helperOk = false;
            string? helperErr = null;
            if (_actuationGateway is not null)
            {
                var r = await _actuationGateway.ReloadAllowlistAsync(ct).ConfigureAwait(false);
                helperOk = r.Ok;
                helperErr = r.RejectionReason;
            }
            else helperErr = "actuation_gateway_unavailable";

            var effective = ActuationAllowlistedSandboxApps.ProcessNames.Keys.OrderBy(k => k).ToArray();
            _logger.LogInformation(
                "extend_app_allowlist: core_applied=true helper_applied={Helper} effective={Count} rejected={Rejected}",
                helperOk, effective.Length, rejected.Count);
            await AckAsync(helperOk, new
            {
                core_applied = true,
                helper_applied = helperOk,
                helper_error = helperErr,
                effective_app_keys = effective,
                rejected,
            }, helperOk ? null : $"helper_reload_failed: {helperErr}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "extend_app_allowlist failed");
            await AckAsync(false, null, ex.Message);
        }
    }

    // M2d — operator DIRECT selector correction (the fast path that bypasses the slow fleet
    // aggregate→approve cycle). The signed `update_selector` command carries a single SelectorPatch,
    // already ECDSA-verified by the signed-command pipeline. It maps through the SAME fail-closed
    // validator as fleet seeds and upserts with operator provenance — single-step, signed,
    // schema-validated, audited. A malformed/non-identifiable patch is rejected, never stored.
    private async Task HandleUpdateSelectorCommandAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        if (!dataEl.TryGetProperty("patch", out var patchEl) || patchEl.ValueKind != JsonValueKind.Object)
        {
            _logger.LogWarning("update_selector: missing patch payload");
            await AckAsync(false, null, "missing patch payload");
            return;
        }

        SeedSelectorPatch? wire;
        try
        {
            wire = JsonSerializer.Deserialize<SeedSelectorPatch>(patchEl.GetRawText());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "update_selector: patch deserialize failed");
            await AckAsync(false, null, "malformed patch");
            return;
        }

        var mapped = wire is null ? null : SelectorPatchMapper.TryMap(wire, $"operator:{cmd.Nonce}");
        if (mapped is null)
        {
            _logger.LogWarning("update_selector: patch rejected by fail-closed validator");
            await AckAsync(false, null, "patch rejected — malformed or non-identifiable");
            return;
        }

        try
        {
            // Atomic: the patch drives the live PMS, so applying it and recording the audit must
            // commit together or not at all. Wrapping both in one transaction means an audit-append
            // failure rolls back the upsert (the transaction disposes uncommitted), so we never leave
            // an applied-but-unaudited patch — and the ACK below truthfully reports the outcome.
            var now = DateTimeOffset.UtcNow.ToString("o");
            // Atomic: applying the patch (it drives the live PMS) and recording the chained audit entry
            // must commit together or not at all. AgentStateDb does both inside ONE transaction — the
            // handler must NOT open its own outer transaction around AppendChainedAuditEntry (which owns
            // a Serializable transaction for chain integrity); Microsoft.Data.Sqlite forbids nesting, and
            // the old outer-transaction wrapper threw `does not support nested transactions` on every
            // real apply (field-confirmed on Mina's box, 2026-06-03).
            _stateDb.UpsertSelectorPatchWithAudit(
                mapped,
                new AuditEntry(
                    TaskId: mapped.PatchId,
                    EventType: "selector_patch_applied",
                    FromState: "proposed",
                    ToState: "active",
                    Trigger: "update_selector",
                    CommandId: cmd.Nonce,
                    RequesterId: "operator",
                    Actor: "operator",
                    SourceComponent: "heartbeat_worker",
                    CaptureReason: $"step={mapped.StepId} skill={mapped.SkillId} via=operator"),
                now);
            _logger.LogInformation(
                "update_selector: applied operator patch {PatchId} for step {Step}", mapped.PatchId, mapped.StepId);
            await AckAsync(true, new { patchId = mapped.PatchId, step = mapped.StepId.ToString() }, null);
        }
        catch (Exception ex)
        {
            // Transaction rolled back on dispose — neither the patch nor the audit committed.
            _logger.LogWarning(ex, "update_selector: apply failed (rolled back)");
            await AckAsync(false, null, "apply failed — see agent logs");
        }
    }

    // Test-only seam, gated by Agent.TestHooks.Enabled (default false; signed config-override to flip).
    // Drives a SINGLE-STEP learning-phase transition on the active session so the M1 PhaseGate can be
    // exercised end-to-end on a real box. It NEVER bypasses a gate: UpdateLearningPhase enforces
    // IsValidPhaseTransition (one step forward only) + stamps phase_changed_at, and the PhaseGate still
    // evaluates and holds. Double-gated (ECDSA-signed command AND the flag), inert in the field.
    private async Task HandleForceLearningPhaseAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        if (!_options.TestHooks.Enabled)
        {
            _logger.LogWarning("force_learning_phase: rejected — Agent.TestHooks.Enabled is false");
            await AckAsync(false, null, "test_hooks_disabled");
            return;
        }

        var targetPhase = dataEl.TryGetProperty("targetPhase", out var tp) ? tp.GetString() : null;
        if (string.IsNullOrWhiteSpace(targetPhase))
        {
            _logger.LogWarning("force_learning_phase: missing targetPhase");
            await AckAsync(false, null, "missing targetPhase");
            return;
        }

        // Only the gate-exercising learning phases may be forced. 'approved'/'active' are
        // NEVER forceable: reaching them must go through the approve_pom digest-verified flow,
        // and forcing them here would bypass the POM approval gate (and trigger adapter
        // activation on an unapproved model). 'discovery' is the start state, not a forward target.
        if (targetPhase is not ("pattern" or "model"))
        {
            _logger.LogWarning("force_learning_phase: phase '{Phase}' is not forceable (only pattern/model)", targetPhase);
            await AckAsync(false, null, "phase_not_forceable");
            return;
        }

        var explicitSession = dataEl.TryGetProperty("sessionId", out var sid) ? sid.GetString() : null;
        var sessionId = !string.IsNullOrWhiteSpace(explicitSession)
            ? explicitSession
            : _stateDb.GetActiveSessionId(_options.PharmacyId ?? "");
        if (string.IsNullOrEmpty(sessionId))
        {
            _logger.LogWarning("force_learning_phase: no active learning session");
            await AckAsync(false, null, "no_active_learning_session");
            return;
        }

        var session = _stateDb.GetLearningSession(sessionId);
        if (session is null)
        {
            _logger.LogWarning("force_learning_phase: session {Session} not found", sessionId);
            await AckAsync(false, null, "session_not_found");
            return;
        }
        var fromPhase = session.Value.Phase;

        try
        {
            // Enforces IsValidPhaseTransition (single-step forward) + stamps phase_changed_at=now,
            // which makes the PhaseGate's 72h calendar floor fail on the fresh phase → gate holds.
            _stateDb.UpdateLearningPhase(sessionId, targetPhase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "force_learning_phase: rejected transition {From} -> {To}", fromPhase, targetPhase);
            await AckAsync(false, null, $"invalid_transition: {fromPhase} -> {targetPhase}");
            return;
        }

        _stateDb.AppendChainedAuditEntry(new AuditEntry(
            TaskId: sessionId,
            EventType: "test_force_phase",
            FromState: fromPhase,
            ToState: targetPhase,
            Trigger: "force_learning_phase",
            CommandId: cmd.Nonce,
            RequesterId: "operator",
            Actor: "operator",
            SourceComponent: "heartbeat_worker",
            CaptureReason: $"test hook forced {fromPhase} -> {targetPhase}"));

        _logger.LogInformation("force_learning_phase: session {Session} forced {From} -> {To}",
            sessionId, fromPhase, targetPhase);
        await AckAsync(true, new { sessionId, fromPhase, toPhase = targetPhase }, null);
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

    private async Task HandleNavigateAppAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        // ValueKind-guarded so a signed-but-malformed payload (e.g. numeric objective) acks cleanly
        // instead of throwing GetString() unobserved in this fire-and-forget task (Codex Q5).
        var objective = dataEl.TryGetProperty("objective", out var o) && o.ValueKind == JsonValueKind.String
            ? o.GetString() : null;
        var taskKey = dataEl.TryGetProperty("taskKey", out var tk) && tk.ValueKind == JsonValueKind.String
            ? tk.GetString() : null;
        if (string.IsNullOrWhiteSpace(objective) || string.IsNullOrWhiteSpace(taskKey))
        {
            await AckAsync(false, null, "malformed_navigate_payload");
            return;
        }

        var runId = dataEl.TryGetProperty("runId", out var rid) && rid.ValueKind == JsonValueKind.String
            && rid.GetString() is { Length: > 0 } r
            ? r : Guid.NewGuid().ToString("n");
        var maxSteps = dataEl.TryGetProperty("maxSteps", out var ms) && ms.TryGetInt32(out var msv) ? msv : 25;
        var deadlineSeconds = dataEl.TryGetProperty("deadlineSeconds", out var ds) && ds.TryGetInt32(out var dsv) ? dsv : 120;
        // Fail-safe default: dry-run unless the operator EXPLICITLY sends dryRun=false.
        var dryRun = !(dataEl.TryGetProperty("dryRun", out var dr) && dr.ValueKind == JsonValueKind.False);

        if (!await _navigationSemaphore.WaitAsync(0, ct).ConfigureAwait(false))
        {
            await AckAsync(false, null, "navigation_already_running");
            return;
        }

        try
        {
            _stateDb.AppendChainedAuditEntry(new AuditEntry(
                TaskId: runId,
                EventType: "navigate_run_received",
                FromState: "queued",
                ToState: "starting",
                Trigger: "signed_command",
                CommandId: cmd.Nonce,
                RequesterId: "operator",
                Actor: "operator",
                SourceComponent: "heartbeat_worker",
                CaptureReason: $"navigate dryRun={dryRun} maxSteps={maxSteps}"));

            var charter = _serviceProvider.GetService<MissionCharter>() ?? BuildEphemeralCharter();
            var audit = _serviceProvider.GetService<SuavoAgent.Core.Audit.AuditChain>()
                ?? new SuavoAgent.Core.Audit.AuditChain();
            var pharmacyId = _options.PharmacyId ?? charter.PharmacyId;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(deadlineSeconds);

            var safetyOptions = new SuavoAgent.Core.Agentic.Adapters.NavigateSafetyOptions(
                EnableTaskAutonomy: _options.EnableTaskAutonomy,
                LivePms: false, // v1: navigate is sandbox/dry-run gated; live-PMS navigate is a future, explicit path
                ExecutorMode: _options.PricingExecutor,
                AllowLiveActuation: false);

            var runner = SuavoAgent.Core.Agentic.NavigateLoopFactory.Create(
                _serviceProvider, safetyOptions, charter, audit, deadline);

            var loopOptions = new SuavoAgent.Core.Agentic.AgenticLoopOptions
            {
                MaxSteps = maxSteps,
                Deadline = TimeSpan.FromSeconds(deadlineSeconds),
                DryRun = dryRun,
                AllowedActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Click", "Type", "PressKey", "WaitForElement", "VerifyElement", "Log",
                },
            };

            var objectiveModel = new SuavoAgent.Core.Agentic.AgentObjective(objective!, taskKey!, pharmacyId);

            using var navCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_activeNavigationLock)
            {
                _activeNavigationCts = navCts;
                _activeNavigationRunId = runId;
            }

            SuavoAgent.Core.Agentic.AgenticLoopResult result;
            try
            {
                // MOAT Increment 2 — replay-first: a HEALTHY banked skill whose entry StateHash matches
                // the live screen replays deterministically (zero LLM) INSTEAD of the loop. Same composite
                // gate (preflight / M3 autonomy / never-blind-on-live-PMS) as the loop; any miss/drift
                // falls through to the loop below unchanged. navigate banks with app="" (see
                // HarvestVerifiedSkill call), so the lookup uses the same key.
                if (_options.ReplayFirst)
                {
                    var rf = await TryReplayFirstAsync(
                        objectiveModel, app: string.Empty, loopOptions, charter, audit, deadline,
                        safetyOptions, safetyOverride: null, targetProcess: null, runId, navCts.Token)
                        .ConfigureAwait(false);
                    if (rf is { ReplayCompleted: true, Replay: { } replayed })
                    {
                        await AckAsync(true, new
                        {
                            run_id = runId,
                            termination = SuavoAgent.Core.Agentic.TerminationReason.Done.ToString(),
                            steps = replayed.StepsCompleted,
                            escalated = false,
                            detail = "replay_first",
                            replay_first = true,
                        }, null);
                        return; // skip the loop — the skill just re-verified itself (no re-harvest needed)
                    }
                }

                result = await runner.RunAsync(objectiveModel, loopOptions, navCts.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_activeNavigationLock)
                {
                    _activeNavigationCts = null;
                    _activeNavigationRunId = null;
                }
            }

            _logger.LogInformation(
                "navigate_app run={RunId} termination={Term} steps={Steps} cloud={Cloud} escalated={Esc}",
                runId, result.Termination, result.StepCount, result.CloudCallsUsed, result.EscalationEmitted);

            // MOAT — feed this run's VERIFIED verdict trail into the Physarum edge-conductance memory:
            // a Met step thickens its (state→action) edge, an unverified/failed step evaporates it. This
            // is the discarded-history gap closed (Phase 1.2) + the substrate PhysarumActionPolicy will
            // explore over (Phase 2). Verified-only by construction (EdgeReinforcement). Exploration stays
            // shadow/dry-run confined upstream; this only RECORDS what the real verifier already decided,
            // so it is safe regardless of dryRun. Best-effort: a learning-layer fault must never fail the
            // navigate ack, but we log it rather than swallow (no silent failure).
            try
            {
                var conductance = _serviceProvider.GetService<SuavoAgent.Core.Agentic.IEdgeConductanceStore>()
                    ?? new SuavoAgent.Core.State.AgentStateDbEdgeConductanceStore(_stateDb);
                SuavoAgent.Core.Agentic.EdgeReinforcement.ApplyRun(
                    conductance, pharmacyId, taskKey!, result.FinalMemory.History,
                    SuavoAgent.Core.Agentic.ConductanceParams.Default);
            }
            catch (Exception reinforceEx)
            {
                _logger.LogWarning(reinforceEx,
                    "navigate_app run={RunId} edge-conductance reinforcement failed (run unaffected)", runId);
            }

            // Amortize ratchet: a successful verified navigate trajectory becomes a replayable skill.
            HarvestVerifiedSkill(objectiveModel, string.Empty, result, runId);

            await AckAsync(
                ok: result.Termination == SuavoAgent.Core.Agentic.TerminationReason.Done,
                result: new
                {
                    run_id = runId,
                    termination = result.Termination.ToString(),
                    steps = result.StepCount,
                    escalated = result.EscalationEmitted,
                    detail = result.Detail,
                    // Observe→assist bridge: a NON-EXECUTING learned-template offer for the presence layer to
                    // render (purple-cursor "want me to?"). Null unless the run terminated on a terminal Assist.
                    // steps_summary is STRUCTURAL ONLY (kinds + control-type counts) — no raw element text.
                    assisted_template = result.AssistedTemplate is { } at ? new
                    {
                        template_id = at.TemplateId,
                        confidence = at.Confidence,
                        observation_count = at.ObservationCount,
                        steps_summary = at.StepsSummary,
                    } : null,
                },
                err: result.Termination == SuavoAgent.Core.Agentic.TerminationReason.Done ? null : result.Termination.ToString());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "navigate_app execution exception");
            await AckAsync(false, null, ex.Message);
        }
        finally
        {
            _navigationSemaphore.Release();
        }
    }

    /// <summary>
    /// navigate_pricing — wire the reasoning NAVIGATOR to drive the pricing navigation for an NDC. Builds
    /// the pricing objective (<see cref="SuavoAgent.Core.Pricing.PricingNavObjective"/>) and runs it through
    /// the SAME navigate loop (perceive→reason→act, replay-first, edge-reinforcement, skill-banking) as
    /// navigate_app — so the on-device brain REASONS its way to the Supplier-Catalog grid on whatever
    /// PioneerRx version is on screen, instead of firing the hardcoded PricingWorkflow selectors. The
    /// money-critical grid READ stays deterministic (the separate pricing-lookup path owns the actual
    /// cost); the navigator only gets us to the grid. Delegates to <see cref="HandleNavigateAppAsync"/> so
    /// there is ONE navigate implementation — no duplicated loop / harvest / safety-gate logic. Same
    /// fail-safe posture (dry-run unless dryRun=false is sent explicitly).
    /// </summary>
    private async Task HandleNavigatePricingAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        // ValueKind-guard every read: JsonElement.GetString/TryGetInt32 THROW on a wrong-kind value (a
        // signed-but-malformed payload, e.g. commandId as a JSON number), and this handler runs in a
        // fire-and-forget Task.Run — an unobserved throw would drop the command with no ACK (cloud sees
        // only a timeout). Guarded reads instead ack malformed_navigate_pricing_payload.
        var commandId = dataEl.TryGetProperty("commandId", out var cid) && cid.ValueKind == JsonValueKind.String
            ? cid.GetString() : null;

        var ndc = dataEl.TryGetProperty("ndc", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString() : null;
        if (!SuavoAgent.Core.Pricing.PricingNavObjective.IsPlausibleNdc(ndc))
        {
            if (!string.IsNullOrEmpty(commandId) && _cloudClient != null)
                await _cloudClient.AckCommandAsync(commandId, false, null, "malformed_navigate_pricing_payload", ct)
                    .ConfigureAwait(false);
            return;
        }

        // Compose the navigate_app payload from the pricing objective; carry the optional nav knobs so the
        // operator can still tune steps/deadline/dryRun. Then delegate — one navigate implementation.
        var navData = new Dictionary<string, object?>
        {
            ["commandId"] = commandId,
            ["objective"] = SuavoAgent.Core.Pricing.PricingNavObjective.Build(ndc!),
            ["taskKey"] = SuavoAgent.Core.Pricing.PricingNavObjective.TaskKey,
        };
        if (dataEl.TryGetProperty("runId", out var ridEl) && ridEl.ValueKind == JsonValueKind.String)
            navData["runId"] = ridEl.GetString();
        if (dataEl.TryGetProperty("maxSteps", out var msEl) && msEl.ValueKind == JsonValueKind.Number && msEl.TryGetInt32(out var msv))
            navData["maxSteps"] = msv;
        if (dataEl.TryGetProperty("deadlineSeconds", out var dsEl) && dsEl.ValueKind == JsonValueKind.Number && dsEl.TryGetInt32(out var dsv))
            navData["deadlineSeconds"] = dsv;
        if (dataEl.TryGetProperty("dryRun", out var drEl) && drEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
            navData["dryRun"] = drEl.ValueKind == JsonValueKind.True;

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { data = navData }));
        await HandleNavigateAppAsync(doc.RootElement, cmd, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// MOAT — explore_sandbox: run the Physarum frontier explorer against an ALLOWLISTED sandbox app to
    /// learn UI navigation from real, verified screen changes. This is the ONLY place the permissive
    /// <see cref="SuavoAgent.Core.Agentic.Adapters.SandboxExploreSafetyGate"/> + ExploreMode policy are
    /// constructed (Codex Q3 invariant). Real execution (DryRun=false) is structurally confined to the
    /// sandbox allowlist; the live PMS is never allowlisted, and v1 actuation is click-only (Codex Q1).
    /// Verified verdicts reinforce conductance post-run (same as navigate); the store is read-only mid-run.
    /// </summary>
    private async Task HandleSandboxExploreAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        var objective = dataEl.TryGetProperty("objective", out var o) && o.ValueKind == JsonValueKind.String
            ? o.GetString() : null;
        var taskKey = dataEl.TryGetProperty("taskKey", out var tk) && tk.ValueKind == JsonValueKind.String
            ? tk.GetString() : null;
        var app = dataEl.TryGetProperty("app", out var ap) && ap.ValueKind == JsonValueKind.String
            ? ap.GetString() : null;
        if (string.IsNullOrWhiteSpace(objective) || string.IsNullOrWhiteSpace(taskKey) || string.IsNullOrWhiteSpace(app))
        {
            await AckAsync(false, null, "malformed_explore_payload");
            return;
        }

        // Fail-closed: the sandbox app MUST be in the actuation allowlist (the Helper re-checks too). The
        // live PMS is never in this set, so explore can never target it.
        var allowed = ActuationAllowlistedSandboxApps.ProcessNames.Keys
            .Concat(ActuationAllowlistedSandboxApps.ProcessNames.Values)
            .Any(a => string.Equals(a, app, StringComparison.OrdinalIgnoreCase));
        if (!allowed)
        {
            await AckAsync(false, null, $"app_not_allowlisted:{app}");
            return;
        }

        var runId = dataEl.TryGetProperty("runId", out var rid) && rid.ValueKind == JsonValueKind.String
            && rid.GetString() is { Length: > 0 } r
            ? r : Guid.NewGuid().ToString("n");
        var maxSteps = dataEl.TryGetProperty("maxSteps", out var ms) && ms.TryGetInt32(out var msv) ? msv : 25;
        var deadlineSeconds = dataEl.TryGetProperty("deadlineSeconds", out var ds) && ds.TryGetInt32(out var dsv) ? dsv : 120;

        // Shared with navigate/replay — actuation is one-at-a-time, which also serializes conductance writes.
        if (!await _navigationSemaphore.WaitAsync(0, ct).ConfigureAwait(false))
        {
            await AckAsync(false, null, "navigation_already_running");
            return;
        }

        try
        {
            _stateDb.AppendChainedAuditEntry(new AuditEntry(
                TaskId: runId,
                EventType: "explore_sandbox_received",
                FromState: "queued",
                ToState: "starting",
                Trigger: "signed_command",
                CommandId: cmd.Nonce,
                RequesterId: "operator",
                Actor: "operator",
                SourceComponent: "heartbeat_worker",
                CaptureReason: $"explore app={app} maxSteps={maxSteps}"));

            var charter = _serviceProvider.GetService<MissionCharter>() ?? BuildEphemeralCharter();
            var audit = _serviceProvider.GetService<SuavoAgent.Core.Audit.AuditChain>()
                ?? new SuavoAgent.Core.Audit.AuditChain();
            var pharmacyId = _options.PharmacyId ?? charter.PharmacyId;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(deadlineSeconds);

            var safetyOptions = new SuavoAgent.Core.Agentic.Adapters.NavigateSafetyOptions(
                EnableTaskAutonomy: false,   // explore bypasses graduation via the sandbox gate, not autonomy
                LivePms: false,              // explore NEVER targets a live PMS
                ExecutorMode: _options.PricingExecutor,
                AllowLiveActuation: false);

            // Codex Q3 invariant: the permissive gate is constructed HERE and passed as an explicit override
            // — never DI-registered. Core has no live-gate IPC, so the synthetic clean state mirrors
            // NavigateLoopFactory; the authoritative kill/pause enforcement is Helper-side at act time.
            var exploreGate = new SuavoAgent.Core.Agentic.Adapters.SandboxExploreSafetyGate(
                gateState: () => new SuavoAgent.Contracts.Ipc.ActuationGateState(
                    Enabled: true, DryRun: false, PausedUntilUtc: null, PauseReason: null, KillSwitchTrippedUtc: null));

            var policyOptions = new SuavoAgent.Core.Agentic.PhysarumPolicyOptions { ProcessName = app! };

            var runner = SuavoAgent.Core.Agentic.NavigateLoopFactory.Create(
                _serviceProvider, safetyOptions, charter, audit, deadline,
                policyOptions: policyOptions, safetyOverride: exploreGate);

            var loopOptions = new SuavoAgent.Core.Agentic.AgenticLoopOptions
            {
                MaxSteps = maxSteps,
                Deadline = TimeSpan.FromSeconds(deadlineSeconds),
                DryRun = false, // real execution — confined to the sandbox allowlist by the gate + Helper
                AllowedActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Click", "Type", "PressKey", "WaitForElement", "VerifyElement", "Log",
                },
            };

            var objectiveModel = new SuavoAgent.Core.Agentic.AgentObjective(objective!, taskKey!, pharmacyId);

            // MOAT pre-launch: explore_sandbox perceives via WINDOW-SCOPED capture of the launch-established
            // window, so the app MUST be running + foreground (and _activeTarget set in the Helper) before the
            // loop's first perceive — else capture returns sandbox_not_launched. CandidateEnumerator is
            // click-only, so the loop never launches on its own. Goes through the real Helper ActuationGate
            // (allowlist + Enabled), so on a disabled/locked box it fails fast with a clear reason, not a blind loop.
            if (_actuationGateway is null)
            {
                await AckAsync(false, null, "actuation_gateway_unavailable");
                return;
            }
            SuavoAgent.Contracts.Ipc.ActuationResult launchResult;
            try
            {
                launchResult = await _actuationGateway
                    .LaunchSandboxAppAsync(new SuavoAgent.Contracts.Ipc.LaunchSandboxAppRequest(app!, DryRun: false), ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception launchEx)
            {
                _logger.LogWarning(launchEx, "explore_sandbox: pre-launch exception app={App}", app);
                await AckAsync(false, null, $"launch_exception:{launchEx.GetType().Name}");
                return;
            }
            if (!launchResult.Ok)
            {
                _logger.LogWarning("explore_sandbox: pre-launch rejected app={App} code={Code}", app, launchResult.RejectionCode);
                await AckAsync(false, null, $"launch_rejected:{launchResult.RejectionCode ?? "unknown"}");
                return;
            }
            _logger.LogInformation(
                "explore_sandbox: pre-launched app={App} (durationMs={Ms}) — settling before perceive",
                app, launchResult.DurationMs);
            try { await Task.Delay(TimeSpan.FromMilliseconds(750), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }

            using var navCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_activeNavigationLock)
            {
                _activeNavigationCts = navCts;
                _activeNavigationRunId = runId;
            }

            SuavoAgent.Core.Agentic.AgenticLoopResult result;
            try
            {
                // MOAT Increment 2 — replay-first for explore: the app is pre-launched (above), so the
                // window-scoped precheck perceive sees the same entry state the banked skill starts at.
                // Uses the SAME explore gate instance + "sandbox" capture path the loop would use.
                if (_options.ReplayFirst)
                {
                    var rf = await TryReplayFirstAsync(
                        objectiveModel, app!, loopOptions, charter, audit, deadline,
                        safetyOptions, safetyOverride: exploreGate, targetProcess: "sandbox", runId, navCts.Token)
                        .ConfigureAwait(false);
                    if (rf is { ReplayCompleted: true, Replay: { } replayed })
                    {
                        await AckAsync(true, new
                        {
                            run_id = runId,
                            app,
                            termination = SuavoAgent.Core.Agentic.TerminationReason.Done.ToString(),
                            steps = replayed.StepsCompleted,
                            detail = "replay_first",
                            replay_first = true,
                        }, null);
                        return; // skip the loop — the skill just re-verified itself (no re-harvest needed)
                    }
                }

                result = await runner.RunAsync(objectiveModel, loopOptions, navCts.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_activeNavigationLock)
                {
                    _activeNavigationCts = null;
                    _activeNavigationRunId = null;
                }
            }

            _logger.LogInformation(
                "explore_sandbox run={RunId} app={App} termination={Term} steps={Steps}",
                runId, app, result.Termination, result.StepCount);

            // Verified verdict trail → conductance reinforcement (same as navigate). Best-effort, logged.
            try
            {
                var conductance = _serviceProvider.GetService<SuavoAgent.Core.Agentic.IEdgeConductanceStore>()
                    ?? new SuavoAgent.Core.State.AgentStateDbEdgeConductanceStore(_stateDb);
                SuavoAgent.Core.Agentic.EdgeReinforcement.ApplyRun(
                    conductance, pharmacyId, taskKey!, result.FinalMemory.History,
                    SuavoAgent.Core.Agentic.ConductanceParams.Default);
            }
            catch (Exception reinforceEx)
            {
                _logger.LogWarning(reinforceEx,
                    "explore_sandbox run={RunId} edge-conductance reinforcement failed (run unaffected)", runId);
            }

            // Amortize ratchet: a successful verified explore trajectory becomes a replayable skill.
            HarvestVerifiedSkill(objectiveModel, app!, result, runId);

            await AckAsync(
                ok: result.Termination == SuavoAgent.Core.Agentic.TerminationReason.Done,
                result: new
                {
                    run_id = runId,
                    app,
                    termination = result.Termination.ToString(),
                    steps = result.StepCount,
                    detail = result.Detail,
                },
                err: result.Termination == SuavoAgent.Core.Agentic.TerminationReason.Done ? null : result.Termination.ToString());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "explore_sandbox execution exception");
            await AckAsync(false, null, ex.Message);
        }
        finally
        {
            _navigationSemaphore.Release();
        }
    }

    /// <summary>
    /// MOAT — the amortize step (lever 3): after a run, bank a SUCCESSFUL execution-verified trajectory as a
    /// replayable <see cref="SuavoAgent.Core.Agentic.VerifiedSkill"/>. Re-verifying the same path thickens
    /// its success count rather than duplicating. Verified-only by construction (Done + every banked step
    /// Met). Off the hot path (post-run) + best-effort (logged, never fails the run). Returns silently when
    /// the run didn't succeed or had no verified steps.
    /// </summary>
    private void HarvestVerifiedSkill(
        SuavoAgent.Core.Agentic.AgentObjective objective, string app,
        SuavoAgent.Core.Agentic.AgenticLoopResult result, string runId)
    {
        try
        {
            var skill = SuavoAgent.Core.Agentic.VerifiedTrajectoryHarvester.Harvest(objective, app, result, out var refusalReason);
            if (skill is null)
            {
                // PHI-certification refusal (Phase-3B): the reason is an operational code, never a value
                // — safe to log. Silent-null (non-Done run / nothing verified) stays silent.
                if (refusalReason is not null)
                    _logger.LogInformation(
                        "verified-skill harvest refused run={RunId} reason={Reason}", runId, refusalReason);
                return;
            }
            var count = _stateDb.UpsertVerifiedSkill(
                skill.SkillId, skill.PharmacyId, skill.TaskKey, skill.App, skill.SerializeSteps(), skill.StepsHash);
            _logger.LogInformation(
                "verified-skill banked run={RunId} skill={SkillId} steps={Steps} successCount={Count}",
                runId, skill.SkillId[..12], skill.Steps.Count, count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "verified-skill harvest failed run={RunId} (run unaffected)", runId);
        }
    }

    /// <summary>
    /// MOAT Increment 2 — replay-first attempt for a navigate/explore objective. Looks up the
    /// most-confirmed banked skill for (pharmacy, taskKey, app) and hands it to
    /// <see cref="SuavoAgent.Core.Agentic.ReplayFirstRunner"/>, wired by
    /// <see cref="SuavoAgent.Core.Agentic.NavigateReplayFactory"/> to the SAME perceiver path + safety
    /// gate + actuator the loop for this command uses. Returns null when nothing is banked or the
    /// attempt machinery itself faults — the caller falls through to the agentic loop in every
    /// non-Completed case, so replay-first can never make a run LESS safe or LESS capable than the loop.
    /// Best-effort by construction: only cancellation propagates.
    /// </summary>
    private async Task<SuavoAgent.Core.Agentic.ReplayFirstResult?> TryReplayFirstAsync(
        SuavoAgent.Core.Agentic.AgentObjective objective,
        string app,
        SuavoAgent.Core.Agentic.AgenticLoopOptions loopOptions,
        MissionCharter charter,
        SuavoAgent.Core.Audit.AuditChain audit,
        DateTimeOffset deadline,
        SuavoAgent.Core.Agentic.Adapters.NavigateSafetyOptions safetyOptions,
        SuavoAgent.Core.Agentic.ISafetyGate? safetyOverride,
        string? targetProcess,
        string runId,
        CancellationToken ct)
    {
        try
        {
            var best = _stateDb.GetBestVerifiedSkillForTask(objective.PharmacyId, objective.TaskKey, app);
            if (best is null)
                return null; // nothing banked — the routine miss, silent
            var bv = best.Value;

            // Reconstruct + PIN the banked steps to the stored steps_hash (the identity Phase-3B
            // certified). A row that does not deserialize or does not hash back to its pin is
            // Unparseable-class — decay it (3 strikes retires) so a corrupt row cannot pin replay-first
            // off for this task forever, and fall through to the loop.
            IReadOnlyList<SuavoAgent.Core.Agentic.VerifiedStep>? steps = null;
            try { steps = SuavoAgent.Core.Agentic.VerifiedSkill.DeserializeSteps(bv.StepsJson); }
            catch (JsonException) { /* corrupt row — handled below */ }
            if (steps is null || steps.Count == 0
                || !string.Equals(
                    SuavoAgent.Core.Agentic.VerifiedSkill.ComputeStepsHash(steps), bv.StepsHash, StringComparison.Ordinal))
            {
                RecordReplayFirstOutcome(bv.SkillId, success: false, runId);
                _logger.LogWarning(
                    "replay-first run={RunId} skill={SkillId} banked steps unreadable or steps-hash mismatch — skipped + decayed",
                    runId, bv.SkillId[..12]);
                return null;
            }

            var skill = new SuavoAgent.Core.Agentic.VerifiedSkill(
                bv.SkillId, objective.PharmacyId, objective.TaskKey, app, steps, bv.StepsHash);

            var replayRunner = SuavoAgent.Core.Agentic.NavigateReplayFactory.Create(
                _serviceProvider, safetyOptions, charter, audit, deadline, targetProcess, safetyOverride);

            var result = await replayRunner.TryReplayAsync(
                skill, bv.SuccessCount, objective, loopOptions, _options.ReplayFirstAllowTypeSteps,
                (skillId, ok) => RecordReplayFirstOutcome(skillId, ok, runId), ct).ConfigureAwait(false);

            if (result.Replay is { } replay)
            {
                // A replay actually fired (entry fingerprint matched + every gate passed) — chain it into
                // the audit log with the outcome. Reasons/outcomes are operational codes, never values.
                _stateDb.AppendChainedAuditEntry(new AuditEntry(
                    TaskId: runId,
                    EventType: "replay_first_attempt",
                    FromState: "starting",
                    ToState: result.ReplayCompleted ? "replayed" : "fell_through",
                    Trigger: "replay_first",
                    RequesterId: "agent",
                    Actor: "agent",
                    SourceComponent: "heartbeat_worker",
                    CaptureReason: $"skill={bv.SkillId[..12]} outcome={replay.Outcome} steps={replay.StepsCompleted}"));
                _logger.LogInformation(
                    "replay-first run={RunId} skill={SkillId} outcome={Outcome} stepsCompleted={Steps}",
                    runId, bv.SkillId[..12], replay.Outcome, replay.StepsCompleted);
            }
            else
            {
                // Pre-replay skips are deliberately quiet (entry mismatch is the routine case) — debug-only.
                _logger.LogDebug(
                    "replay-first run={RunId} skill={SkillId} skipped reason={Reason}",
                    runId, bv.SkillId[..12], result.SkipReason);
            }

            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "replay-first run={RunId} attempt failed (falling through to agentic loop)", runId);
            return null;
        }
    }

    /// <summary>Slime-mold skill hygiene for replay-first, best-effort: a hygiene write must never
    /// change the run's control flow (same contract as the replay_skill handler's update).</summary>
    private void RecordReplayFirstOutcome(string skillId, bool success, string runId)
    {
        try
        {
            _stateDb.RecordSkillReplayOutcome(skillId, success);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "replay-first run={RunId} skill-hygiene update failed (run unaffected)", runId);
        }
    }

    /// <summary>
    /// MOAT — cash in the amortize ratchet: deterministically REPLAY a banked VerifiedSkill (zero reasoning,
    /// no LLM). Loads the skill by id, or the most-confirmed skill for (task, app), reconstructs it, and runs
    /// <see cref="SuavoAgent.Core.Agentic.VerifiedSkillReplayer"/> — which executes each step only while the
    /// live screen matches the verified path and STOPS fail-closed on any drift. Shares the navigation
    /// semaphore (one actuating run at a time); sandbox-confined like the run that learned it.
    /// </summary>
    private async Task HandleReplaySkillAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        var skillId = dataEl.TryGetProperty("skillId", out var sid) && sid.ValueKind == JsonValueKind.String ? sid.GetString() : null;
        var taskKey = dataEl.TryGetProperty("taskKey", out var tk) && tk.ValueKind == JsonValueKind.String ? tk.GetString() : null;
        var app = dataEl.TryGetProperty("app", out var ap) && ap.ValueKind == JsonValueKind.String ? ap.GetString() : null;

        var charterForId = _serviceProvider.GetService<MissionCharter>() ?? BuildEphemeralCharter();
        var pharmacyId = _options.PharmacyId ?? charterForId.PharmacyId;

        // Resolve the skill: explicit skillId wins; else the most-confirmed skill for (task, app).
        string resolvedSkillId, stepsJson, stepsHash, resolvedTask, resolvedApp;
        if (!string.IsNullOrWhiteSpace(skillId))
        {
            var row = _stateDb.GetVerifiedSkill(skillId);
            if (row is null) { await AckAsync(false, null, "skill_not_found"); return; }
            var rv = row.Value;
            if (!string.Equals(rv.PharmacyId, pharmacyId, StringComparison.Ordinal)) { await AckAsync(false, null, "skill_pharmacy_mismatch"); return; }
            resolvedSkillId = skillId!;
            resolvedTask = rv.TaskKey;
            resolvedApp = rv.App;
            stepsJson = rv.StepsJson;
            stepsHash = rv.StepsHash;
        }
        else if (!string.IsNullOrWhiteSpace(taskKey) && !string.IsNullOrWhiteSpace(app))
        {
            var best = _stateDb.GetBestVerifiedSkillForTask(pharmacyId, taskKey, app);
            if (best is null) { await AckAsync(false, null, "no_skill_for_task"); return; }
            var bv = best.Value;
            resolvedSkillId = bv.SkillId;
            stepsJson = bv.StepsJson;
            stepsHash = bv.StepsHash;
            resolvedTask = taskKey!;
            resolvedApp = app!;
        }
        else
        {
            await AckAsync(false, null, "malformed_replay_skill_payload");
            return;
        }

        var runId = dataEl.TryGetProperty("runId", out var rid) && rid.ValueKind == JsonValueKind.String
            && rid.GetString() is { Length: > 0 } r ? r : Guid.NewGuid().ToString("n");
        var deadlineSeconds = dataEl.TryGetProperty("deadlineSeconds", out var ds) && ds.TryGetInt32(out var dsv) ? dsv : 120;

        var steps = SuavoAgent.Core.Agentic.VerifiedSkill.DeserializeSteps(stepsJson);
        if (steps.Count == 0) { await AckAsync(false, null, "empty_skill"); return; }
        var skill = new SuavoAgent.Core.Agentic.VerifiedSkill(resolvedSkillId, pharmacyId, resolvedTask, resolvedApp, steps, stepsHash);

        if (!await _navigationSemaphore.WaitAsync(0, ct).ConfigureAwait(false))
        {
            await AckAsync(false, null, "navigation_already_running");
            return;
        }

        try
        {
            _stateDb.AppendChainedAuditEntry(new AuditEntry(
                TaskId: runId,
                EventType: "replay_skill_received",
                FromState: "queued",
                ToState: "starting",
                Trigger: "signed_command",
                CommandId: cmd.Nonce,
                RequesterId: "operator",
                Actor: "operator",
                SourceComponent: "heartbeat_worker",
                CaptureReason: $"replay skill={resolvedSkillId[..12]} task={resolvedTask} app={resolvedApp} steps={steps.Count}"));

            var charter = charterForId;
            var audit = _serviceProvider.GetService<SuavoAgent.Core.Audit.AuditChain>() ?? new SuavoAgent.Core.Audit.AuditChain();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(deadlineSeconds);

            var replayer = SuavoAgent.Core.Agentic.ReplaySkillFactory.Create(_serviceProvider, charter, audit, deadline);
            var loopOptions = new SuavoAgent.Core.Agentic.AgenticLoopOptions { DryRun = false, Deadline = TimeSpan.FromSeconds(deadlineSeconds) };
            var objective = new SuavoAgent.Core.Agentic.AgentObjective($"replay skill {resolvedSkillId[..12]}", resolvedTask, pharmacyId);

            using var navCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_activeNavigationLock) { _activeNavigationCts = navCts; _activeNavigationRunId = runId; }

            SuavoAgent.Core.Agentic.SkillReplayResult result;
            try
            {
                result = await replayer.ReplayAsync(skill, objective, loopOptions, navCts.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_activeNavigationLock) { _activeNavigationCts = null; _activeNavigationRunId = null; }
            }

            _logger.LogInformation(
                "replay_skill run={RunId} skill={SkillId} outcome={Outcome} stepsCompleted={Steps}",
                runId, resolvedSkillId[..12], result.Outcome, result.StepsCompleted);

            // Slime-mold skill hygiene: a Completed replay re-thickens; a drift / skill-fault outcome decays
            // and may retire the skill (so a stale path stops being auto-selected + exploration re-learns it).
            // Environmental denials (kill-switch, perceive-fail, cancel, no-steps) are NOT the skill's fault →
            // ignored. Best-effort: a hygiene write must never change the run's ack.
            try
            {
                var o = result.Outcome;
                if (o == SuavoAgent.Core.Agentic.SkillReplayOutcome.Completed)
                    _stateDb.RecordSkillReplayOutcome(resolvedSkillId, success: true);
                else if (o is SuavoAgent.Core.Agentic.SkillReplayOutcome.StateMismatch
                    or SuavoAgent.Core.Agentic.SkillReplayOutcome.PostconditionFailed
                    or SuavoAgent.Core.Agentic.SkillReplayOutcome.Unparseable
                    or SuavoAgent.Core.Agentic.SkillReplayOutcome.StepRejected)
                    _stateDb.RecordSkillReplayOutcome(resolvedSkillId, success: false);
            }
            catch (Exception hygieneEx)
            {
                _logger.LogWarning(hygieneEx, "replay_skill run={RunId} skill-hygiene update failed (run unaffected)", runId);
            }

            await AckAsync(
                ok: result.Outcome == SuavoAgent.Core.Agentic.SkillReplayOutcome.Completed,
                result: new { run_id = runId, skill_id = resolvedSkillId, outcome = result.Outcome.ToString(), steps_completed = result.StepsCompleted, detail = result.Detail },
                err: result.Outcome == SuavoAgent.Core.Agentic.SkillReplayOutcome.Completed ? null : result.Outcome.ToString());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "replay_skill execution exception");
            await AckAsync(false, null, ex.Message);
        }
        finally
        {
            _navigationSemaphore.Release();
        }
    }

    private async Task HandleReplayTemplateAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) && cid.ValueKind == JsonValueKind.String
            ? cid.GetString() : null;

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        var taskKey = dataEl.TryGetProperty("taskKey", out var tk) && tk.ValueKind == JsonValueKind.String ? tk.GetString() : null;
        if (string.IsNullOrWhiteSpace(taskKey))
        {
            await AckAsync(false, null, "missing_taskKey");
            return;
        }

        SuavoAgent.Contracts.Learning.WorkflowTemplate? template = null;
        try
        {
            if (dataEl.TryGetProperty("template", out var t) && t.ValueKind == JsonValueKind.Object)
                template = t.Deserialize<SuavoAgent.Contracts.Learning.WorkflowTemplate>();
        }
        catch (Exception) { template = null; }

        if (template is null || template.Steps is null || template.Steps.Count == 0)
        {
            await AckAsync(false, null, "malformed_template");
            return;
        }

        var runId = dataEl.TryGetProperty("runId", out var rid) && rid.ValueKind == JsonValueKind.String
            && rid.GetString() is { Length: > 0 } r ? r : Guid.NewGuid().ToString("n");
        var deadlineSeconds = dataEl.TryGetProperty("deadlineSeconds", out var ds) && ds.TryGetInt32(out var dsv) ? dsv : 120;
        var dryRun = !(dataEl.TryGetProperty("dryRun", out var dr) && dr.ValueKind == JsonValueKind.False);

        // Share the navigation semaphore: replay and navigate both actuate, so never concurrently.
        if (!await _navigationSemaphore.WaitAsync(0, ct).ConfigureAwait(false))
        {
            await AckAsync(false, null, "actuation_already_running");
            return;
        }

        try
        {
            _stateDb.AppendChainedAuditEntry(new AuditEntry(
                TaskId: runId,
                EventType: "replay_template_received",
                FromState: "queued",
                ToState: "starting",
                Trigger: "signed_command",
                CommandId: cmd.Nonce,
                RequesterId: "operator",
                Actor: "operator",
                SourceComponent: "heartbeat_worker",
                CaptureReason: $"replay template={template.TemplateId} dryRun={dryRun} steps={template.Steps.Count}"));

            var charter = _serviceProvider.GetService<MissionCharter>() ?? BuildEphemeralCharter();
            var audit = _serviceProvider.GetService<SuavoAgent.Core.Audit.AuditChain>()
                ?? new SuavoAgent.Core.Audit.AuditChain();
            var pharmacyId = _options.PharmacyId ?? charter.PharmacyId;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(deadlineSeconds);

            var safetyOptions = new SuavoAgent.Core.Agentic.Adapters.NavigateSafetyOptions(
                EnableTaskAutonomy: _options.EnableTaskAutonomy,
                LivePms: false,
                ExecutorMode: _options.PricingExecutor,
                AllowLiveActuation: false);

            var replayer = SuavoAgent.Core.Agentic.Replication.ReplayFactory.Create(
                _serviceProvider, safetyOptions, charter, audit, deadline);

            var plan = SuavoAgent.Core.Agentic.Replication.TemplatePlanCompiler.Compile(template);
            var baseContext = new SuavoAgent.Core.Agentic.ActuationContext(pharmacyId, taskKey, dryRun);
            var objective = new SuavoAgent.Core.Agentic.AgentObjective($"replay:{template.SkillId}", taskKey, pharmacyId);
            var options = new SuavoAgent.Core.Agentic.Replication.TemplateReplayOptions();

            using var navCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_activeNavigationLock)
            {
                _activeNavigationCts = navCts;
                _activeNavigationRunId = runId;
            }

            SuavoAgent.Core.Agentic.Replication.ReplayResult result;
            try
            {
                result = await replayer.ReplayAsync(plan, objective, baseContext, options, navCts.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_activeNavigationLock)
                {
                    _activeNavigationCts = null;
                    _activeNavigationRunId = null;
                }
            }

            _logger.LogInformation(
                "replay_template run={RunId} outcome={Outcome} steps={Steps} failedOrdinal={Ord}",
                runId, result.Outcome, result.StepsCompleted, result.FailedOrdinal);

            await AckAsync(
                ok: result.Outcome == SuavoAgent.Core.Agentic.Replication.ReplayOutcome.Completed,
                result: new
                {
                    run_id = runId,
                    outcome = result.Outcome.ToString(),
                    steps_completed = result.StepsCompleted,
                    failed_ordinal = result.FailedOrdinal,
                    detail = result.Detail,
                },
                err: result.Outcome == SuavoAgent.Core.Agentic.Replication.ReplayOutcome.Completed ? null : result.Outcome.ToString());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "replay_template execution exception");
            await AckAsync(false, null, ex.Message);
        }
        finally
        {
            _navigationSemaphore.Release();
        }
    }

    /// <summary>
    /// run_learned_template — FSD "autopilot engage": deterministically execute an operator-APPROVED learned
    /// <see cref="SuavoAgent.Contracts.Learning.WorkflowTemplate"/> by replaying its CLICK-family steps through
    /// the same fail-closed perceiver / safety / actuator seam via
    /// <see cref="SuavoAgent.Core.Agentic.Replication.GatedTemplateExecutor"/>. Distinct from replay_template
    /// (the internal harvest path) by four fail-closed gates: the template must exist + be active; its auto-rule
    /// approval must be <c>Approved</c> (never Pending / Shadow / Rejected / missing); any Type / PressKey /
    /// writeback step refuses the WHOLE template (v1 click-only); and every click still passes the per-action
    /// safety gate (a dry-run verdict STOPS — it never actuates). Shares the navigation semaphore + the
    /// abort_navigation cancellation seam. Ack is STRUCTURAL ONLY (outcome + counts + template-id prefix, no PHI).
    /// </summary>
    private async Task HandleRunLearnedTemplateAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) && cid.ValueKind == JsonValueKind.String
            ? cid.GetString() : null;

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        // 1. templateId is required (ValueKind-guarded like navigate_pricing).
        var templateId = dataEl.TryGetProperty("templateId", out var tid) && tid.ValueKind == JsonValueKind.String
            ? tid.GetString() : null;
        if (string.IsNullOrWhiteSpace(templateId))
        {
            await AckAsync(false, null, "malformed_run_learned_template");
            return;
        }

        // 2. Load the template locally (no egress); missing OR retired ⇒ not found (fail-closed).
        var row = _stateDb.GetWorkflowTemplate(templateId);
        if (row is null || row.RetiredAt is not null)
        {
            await AckAsync(false, null, "template_not_found");
            return;
        }

        // 3. APPROVAL GATE (fail-closed): the auto-rule for this template MUST be Approved. Rule id follows
        //    TemplateRuleGenerator's convention exactly: auto.<skillId>.<templateId[:12]> (clamped for short ids).
        //    Missing / Pending / Shadow / Rejected all refuse — an operator approval is the only key that turns.
        var idSuffix = templateId.Length >= 12 ? templateId[..12] : templateId;
        var ruleId = $"auto.{row.SkillId}.{idSuffix}";
        var approval = _stateDb.GetAutoRuleApproval(ruleId);
        if (approval is null || approval.Status != SuavoAgent.Core.State.AgentStateDb.AutoRuleStatus.Approved)
        {
            var status = approval?.Status.ToString() ?? "none";
            await AckAsync(false, null, $"template_not_approved:{status}");
            return;
        }

        var runId = dataEl.TryGetProperty("runId", out var rid) && rid.ValueKind == JsonValueKind.String
            && rid.GetString() is { Length: > 0 } r ? r : Guid.NewGuid().ToString("n");
        var deadlineSeconds = dataEl.TryGetProperty("deadlineSeconds", out var ds) && ds.TryGetInt32(out var dsv) ? dsv : 120;

        // 4. One actuating run at a time — share the navigation semaphore with navigate/replay.
        if (!await _navigationSemaphore.WaitAsync(0, ct).ConfigureAwait(false))
        {
            await AckAsync(false, null, "navigation_already_running");
            return;
        }

        try
        {
            SuavoAgent.Contracts.Learning.WorkflowTemplate template;
            try
            {
                template = SuavoAgent.Core.Learning.TemplateRuleGenerator.Rehydrate(row);
            }
            catch (Exception)
            {
                await AckAsync(false, null, "template_unreadable");
                return;
            }

            _stateDb.AppendChainedAuditEntry(new AuditEntry(
                TaskId: runId,
                EventType: "run_learned_template_received",
                FromState: "queued",
                ToState: "starting",
                Trigger: "signed_command",
                CommandId: cmd.Nonce,
                RequesterId: "operator",
                Actor: "operator",
                SourceComponent: "heartbeat_worker",
                CaptureReason: $"run approved template={idSuffix} skill={row.SkillId} steps={template.Steps.Count}"));

            var charter = _serviceProvider.GetService<MissionCharter>() ?? BuildEphemeralCharter();
            var audit = _serviceProvider.GetService<SuavoAgent.Core.Audit.AuditChain>()
                ?? new SuavoAgent.Core.Audit.AuditChain();
            var pharmacyId = _options.PharmacyId ?? charter.PharmacyId;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(deadlineSeconds);

            // Reuse the SAME gate policy as replay_template — no parallel gate, no bypass. The approval gate
            // above is the operator's autopilot-engage key; the per-action autonomy gate still runs per step.
            var safetyOptions = new SuavoAgent.Core.Agentic.Adapters.NavigateSafetyOptions(
                EnableTaskAutonomy: _options.EnableTaskAutonomy,
                LivePms: false,
                ExecutorMode: _options.PricingExecutor,
                AllowLiveActuation: false);

            var executor = SuavoAgent.Core.Agentic.Replication.ReplayFactory.CreateExecutor(
                _serviceProvider, safetyOptions, charter, audit, deadline);

            // Operator explicitly approved + commanded ⇒ DryRun=false. Each step still needs a hard Allow from
            // GateAction to actuate (AllowDryRun ⇒ STOP), so the approval never bypasses the autonomy gate.
            var baseContext = new SuavoAgent.Core.Agentic.ActuationContext(pharmacyId, row.SkillId, DryRun: false);
            var objective = new SuavoAgent.Core.Agentic.AgentObjective($"run:{idSuffix}", row.SkillId, pharmacyId);
            var options = new SuavoAgent.Core.Agentic.Replication.TemplateReplayOptions();

            using var navCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_activeNavigationLock)
            {
                _activeNavigationCts = navCts;
                _activeNavigationRunId = runId;
            }

            SuavoAgent.Core.Agentic.Replication.GatedReplayResult result;
            try
            {
                result = await executor.ExecuteAsync(template, objective, baseContext, options, navCts.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_activeNavigationLock)
                {
                    _activeNavigationCts = null;
                    _activeNavigationRunId = null;
                }
            }

            _logger.LogInformation(
                "run_learned_template run={RunId} template={Tid} outcome={Outcome} steps={Steps} failedOrdinal={Ord}",
                runId, idSuffix, result.Outcome, result.StepsCompleted, result.FailedOrdinal);

            await AckAsync(
                ok: result.Outcome == SuavoAgent.Core.Agentic.Replication.GatedReplayOutcome.Completed,
                result: new
                {
                    run_id = runId,
                    template = idSuffix,
                    outcome = result.Outcome.ToString(),
                    steps_completed = result.StepsCompleted,
                    failed_ordinal = result.FailedOrdinal,
                    detail = result.Detail,
                },
                err: result.Outcome == SuavoAgent.Core.Agentic.Replication.GatedReplayOutcome.Completed ? null : result.Outcome.ToString());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "run_learned_template execution exception");
            await AckAsync(false, null, ex.Message);
        }
        finally
        {
            _navigationSemaphore.Release();
        }
    }

    private async Task HandleAbortNavigationAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;
        var requestedRunId = dataEl.TryGetProperty("runId", out var rid) && rid.ValueKind == JsonValueKind.String
            ? rid.GetString() : null;
        var requestedReason = dataEl.TryGetProperty("reason", out var rr) && rr.ValueKind == JsonValueKind.String
            ? rr.GetString() : null;

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        CancellationTokenSource? activeCts;
        string? activeRunId;
        lock (_activeNavigationLock)
        {
            activeCts = _activeNavigationCts;
            activeRunId = _activeNavigationRunId;
        }

        // A specific runId that doesn't match the active run is a no-op ack; no runId ⇒ abort whatever's active.
        if (!string.IsNullOrEmpty(requestedRunId) && !string.Equals(activeRunId, requestedRunId, StringComparison.Ordinal))
        {
            await AckAsync(true, new { aborted = false, reason = "no_active_run_with_id" }, null);
            return;
        }

        try { activeCts?.Cancel(); }
        catch (Exception ex) { _logger.LogWarning(ex, "abort_navigation: cancel threw"); }

        await AckAsync(true, new { aborted = true, run_id = activeRunId, reason = requestedReason }, null);
    }

    private async Task HandleForceRestartAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) && cid.ValueKind == JsonValueKind.String
            ? cid.GetString() : null;
        var reason = dataEl.TryGetProperty("reason", out var rr) && rr.ValueKind == JsonValueKind.String
            ? rr.GetString() : null;

        _stateDb.AppendChainedAuditEntry(new AuditEntry(
            TaskId: cmd.Nonce,
            EventType: "force_restart_requested",
            FromState: "running",
            ToState: "restarting",
            Trigger: "signed_command",
            CommandId: cmd.Nonce,
            RequesterId: "operator",
            Actor: "operator",
            SourceComponent: "heartbeat_worker",
            CaptureReason: reason ?? "operator_force_restart"));

        // ACK before exiting so the cloud confirms the restart was accepted (it won't hear from us again
        // until the new process comes up). ECDSA + persistent-nonce verification already gated this command.
        if (!string.IsNullOrEmpty(commandId) && _cloudClient != null)
        {
            try { await _cloudClient.AckCommandAsync(commandId, true, new { restarting = true, reason }, null, ct); }
            catch (Exception ex) { _logger.LogDebug(ex, "force_restart ack failed (non-fatal — restarting anyway)"); }
        }

        _logger.LogWarning("force_restart received (reason={Reason}) — exiting for SCM/Watchdog restart", reason ?? "operator");

        // Brief grace to flush the ACK, then exit non-zero → Core stops → the Watchdog restarts it.
        try { await Task.Delay(TimeSpan.FromMilliseconds(500), ct); } catch (OperationCanceledException) { }
        Environment.Exit(1);
    }

    /// <summary>
    /// restart_helper — the remote recovery lever for a stranded Helper command pipe. Core
    /// (LocalService) cannot kill/relaunch a process in the interactive session, so it drops
    /// the <see cref="HelperRestartRequest"/> sentinel; the Broker (LocalSystem) consumes it
    /// within ~5s, kills every Helper, and relaunches a fresh one into the active console
    /// session. Distinct from force_restart (which restarts CORE — useless against a wedged
    /// Helper holding the single-instance pipe).
    ///
    /// Safety gates, in order:
    ///   1. REFUSE while any actuation is mid-flight — all actuating paths single-flight
    ///      through the pricing/workflow/navigation semaphores, so try-acquiring ALL THREE
    ///      with zero wait is an exact "nothing is actuating" test. The sentinel is written
    ///      WHILE holding them, so no job can begin between the check and the write.
    ///   2. Jobs that start after the write see the pending sentinel (checked after semaphore
    ///      acquisition in the pricing handlers) and refuse until the Broker consumes it.
    ///   3. The sentinel is freshness-bounded (2 min) and consumed exactly once Broker-side.
    /// Idempotent: a second restart_helper while one is pending overwrites the same sentinel
    /// and collapses into a single restart.
    /// </summary>
    private async Task HandleRestartHelperAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) && cid.ValueKind == JsonValueKind.String
            ? cid.GetString() : null;
        var reason = dataEl.TryGetProperty("reason", out var rr) && rr.ValueKind == JsonValueKind.String
            ? rr.GetString() : null;

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            try { await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "restart_helper ack failed"); }
        }

        // Gate 1: nothing may be actuating. Fixed acquisition order + zero wait + full rollback
        // on partial acquisition — cannot deadlock, cannot leak a permit.
        var held = new List<SemaphoreSlim>(3);
        try
        {
            foreach (var sem in new[] { _pricingJobSemaphore, _workflowSemaphore, _navigationSemaphore })
            {
                if (!await sem.WaitAsync(TimeSpan.Zero, ct))
                {
                    _logger.LogWarning(
                        "restart_helper REFUSED — an actuation (pricing/workflow/navigation) is mid-flight; " +
                        "yanking the Helper under a live run is never allowed");
                    await AckAsync(false, new { state = "refused_actuation_in_flight" },
                        "refused: an actuation is mid-flight — retry when the job finishes or abort it first");
                    return;
                }
                held.Add(sem);
            }

            var payload = new HelperRestartRequest.Payload(
                DateTimeOffset.UtcNow,
                string.IsNullOrWhiteSpace(reason) ? "operator_restart_helper" : reason!,
                "operator",
                commandId);
            HelperRestartRequest.Write(HelperRestartRequest.DefaultPath(), payload);

            _stateDb.AppendChainedAuditEntry(new AuditEntry(
                TaskId: cmd.Nonce,
                EventType: "helper_restart_requested",
                FromState: "running",
                ToState: "restart_pending",
                Trigger: "signed_command",
                CommandId: cmd.Nonce,
                RequesterId: "operator",
                Actor: "operator",
                SourceComponent: "heartbeat_worker",
                CaptureReason: payload.Reason));

            _logger.LogWarning(
                "restart_helper accepted (reason={Reason}) — sentinel written; Broker will kill + relaunch " +
                "the Helper into the active console session within ~5s", payload.Reason);

            await AckAsync(true, new
            {
                state = "restart_pending",
                requestedAtUtc = payload.RequestedAtUtc.ToString("o"),
                note = "Broker consumes the request within ~5s; watch helper.actuation.ready flip true on the next probe (≤60s)",
            }, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "restart_helper failed to write the restart sentinel");
            await AckAsync(false, null, $"restart_helper_failed:{ex.GetType().Name}");
        }
        finally
        {
            foreach (var sem in held) sem.Release();
        }
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

    /// <summary>
    /// <c>fetch_diagnostics</c> — gathers a PHI-safe snapshot of the box's config,
    /// SQL connectivity, helper/IPC health, and error-mesh counters and acks it to
    /// the cloud. Read-only: never drives the PMS, never mutates the box. This is
    /// how Claude debugs an agent remotely without touching the PHI workstation.
    /// Secrets (ApiKey/SqlPassword/HmacSalt/CloudCertPin) are never gathered;
    /// <see cref="DiagnosticsSnapshotBuilder"/> enforces the safe field set and the
    /// ack POST runs the snapshot through OutboundPhiGuard.
    /// </summary>
    private async Task HandleFetchDiagnosticsAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;
        var requesterId = dataEl.TryGetProperty("requesterId", out var rid) ? rid.GetString() : "operator";

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        // Build + audit + ack inside a try so a throw NEVER leaves the command stuck
        // at 'sent'. AckCommandAsync is best-effort cloud-side, but the cloud can only
        // resolve a command it hears about — if snapshot build (RuntimeHealthEvidence
        // .Collect / BuildWatchdogPayload / DiagnosticsSnapshotBuilder.Build) or the
        // audit append throws, no ack would be sent and the operator's cockpit would
        // spin forever. On failure, ack 'failed' with a SAFE, exception-TYPE-only reason
        // (never the message — it could carry a path or other infra detail).
        try
        {
            var rxWorker = _serviceProvider.GetService<RxDetectionWorker>();
            var pharmacies = _options.GetEffectivePharmacies();
            var firstPharmacy = pharmacies.Count > 0 ? pharmacies[0] : null;
            var sql = new DiagnosticsSnapshotBuilder.SqlDiagnostics(
                Configured: pharmacies.Count > 0,
                Connected: rxWorker?.IsSqlConnected ?? false,
                Server: firstPharmacy?.SqlServer ?? _options.SqlServer,
                Database: firstPharmacy?.SqlDatabase ?? _options.SqlDatabase,
                User: firstPharmacy?.SqlUser ?? _options.SqlUser);

            var wire = new DiagnosticsSnapshotBuilder.WireDiagnostics(
                SentryInitialized: global::SuavoAgent.Diagnostics.Wire.SentryInitialized,
                EventsEmittedTotal: global::SuavoAgent.Diagnostics.Wire.EventsEmittedTotal,
                WireHandlerFailedTotal: global::SuavoAgent.Diagnostics.Wire.WireHandlerFailedTotal,
                SentryEnqueuedTotal: global::SuavoAgent.Diagnostics.Wire.SentryEnqueuedTotal,
                SentryEnqueueFailedTotal: global::SuavoAgent.Diagnostics.Wire.SentryEnqueueFailedTotal,
                SentryBeforeSendFailedTotal: global::SuavoAgent.Diagnostics.Wire.SentryBeforeSendFailedTotal,
                RulesetVersion: global::SuavoAgent.Diagnostics.Wire.RulesetVersion);

            // Live Helper actuation-gate state — surfaces WHY actuation is gated (gate_disabled /
            // paused / kill-switch / honeytoken-compromise) remotely. Best-effort; never fail the snapshot.
            object? actuationGate = null;
            if (_actuationGateway != null)
            {
                try
                {
                    actuationGate = await _actuationGateway.GetStateAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception gateEx)
                {
                    actuationGate = new { error = gateEx.GetType().Name };
                    _logger.LogDebug(gateEx, "fetch_diagnostics: actuation gate state read failed");
                }
            }

            var snapshot = DiagnosticsSnapshotBuilder.Build(
                _options,
                sql,
                wire,
                helper: BuildHelperPayload(_serviceProvider.GetService<IpcPipeServer>()),
                watchdog: BuildWatchdogPayload(),
                runtimeHealth: RuntimeHealthEvidence.Collect(),
                commandPipeConnected: _ipcCommandClient?.IsConnected ?? false,
                uptimeSeconds: (long)(DateTimeOffset.UtcNow - _startTime).TotalSeconds,
                processId: Environment.ProcessId,
                collectedAtUtc: DateTimeOffset.UtcNow,
                actuationGate: actuationGate);

            _stateDb.AppendChainedAuditEntry(new AuditEntry(
                TaskId: commandId ?? cmd.Nonce,
                EventType: "fetch_diagnostics_command",
                FromState: "requested",
                ToState: "collected",
                Trigger: "signed_command",
                CommandId: cmd.Nonce,
                RequesterId: requesterId,
                Actor: "operator",
                SourceComponent: "heartbeat_worker",
                CaptureReason: "non_phi_diagnostics"));

            await AckAsync(true, snapshot, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "fetch_diagnostics snapshot build failed");
            // Exception TYPE only — safe to surface; the message may contain infra detail.
            await AckAsync(false, null, $"diagnostics_collection_failed:{ex.GetType().Name}");
        }
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
            // Glide target + easing (v3.18.x). toX/toY are numbers; toAnchor and
            // easing are closed enums (see HasUnsafeIntentCursorValue) so none can
            // smuggle PHI through a free-form string.
            "tox" or
            "toy" or
            "toanchor" or
            "easing" or
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
            "anchor" or "toanchor" => value.ValueKind != JsonValueKind.String ||
                !string.Equals(value.GetString(), IntentCursorAnchors.PrimaryCenter, StringComparison.Ordinal),
            "easing" => value.ValueKind != JsonValueKind.String ||
                value.GetString() is not (
                    IntentCursorEasings.Linear or
                    IntentCursorEasings.EaseInOutCubic),
            "x" or "y" or "tox" or "toy" or "durationms" or "diameterpx" or "opacity" =>
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

        // Bug 20 — reject unrecognized reasons up front instead of silently
        // re-mapping to "remote_command". Operator gets a clear NACK that
        // lists the allowed reasons; the closed-set persisted to the
        // request file stays HIPAA-clean. Watchdog-side redaction (see
        // WatchdogWorker) stays as defense-in-depth.
        var (rawReason, validation) = WatchdogRepairRequestWriter.InspectReason(dataEl);
        if (validation == WatchdogRepairRequestWriter.ReasonValidation.Rejected)
        {
            _logger.LogWarning(
                "repair_agent: rejecting unknown reason — must be one of [{Allowed}]",
                string.Join(", ", WatchdogRepairRequestWriter.AllowedReasons));
            await AckAsync(
                false,
                new
                {
                    status = "rejected",
                    rejected_reason = rawReason,
                    allowed_reasons = WatchdogRepairRequestWriter.AllowedReasons,
                },
                "unknown_repair_reason");
            return;
        }

        var reason = WatchdogRepairRequestWriter.ReadReason(dataEl);

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

        try
        {
            var requestPath = WatchdogRepairRequestWriter.Queue(
                _options.WatchdogRepairRequestPath,
                commandId,
                reason,
                _options.AgentId);
            _logger.LogWarning("repair_agent: queued LocalSystem Watchdog repair request at {Path}", requestPath);
            await AckAsync(true, new { status = "queued_for_watchdog" }, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "repair_agent: failed to queue Watchdog repair request");
            await AckAsync(false, new { status = "queue_failed" }, "failed to queue repair");
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
            var sent = await _cloudClient.SendPatientDetailsAsync(rxNumber, payload, cmd.Nonce, ct);
            if (sent)
                _logger.LogInformation("fetch_patient: sent details for Rx {RxHash}", hashedRx[..12]);
            else
                _logger.LogWarning("fetch_patient: details NOT sent for Rx {RxHash} — patient PHI egress is fail-closed (pending audited route)", hashedRx[..12]);
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
    /// Dashboard self-uninstall ("the agent removes itself"). Core (LocalService) cannot delete
    /// services or wipe admin-owned dirs, so it best-effort archives the audit chain to the cloud
    /// (HIPAA 164.530(j)), then drops a sentinel the Broker (LocalSystem) polls for — the Broker
    /// launches a detached SYSTEM cleaner that removes everything, watchdog-first. Only reachable via
    /// a signed envelope; the cloud gates it to the Pharmacist-in-Charge.
    /// </summary>
    private async Task HandleSelfUninstallAsync(JsonElement scEl, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            try { await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct); } catch { /* best effort */ }
        }

        try
        {
            var agentId = _options.AgentId ?? "";
            _logger.LogWarning("SELF-UNINSTALL requested — archiving audit (best-effort) then signalling the Broker");

            // Best-effort audit archive to the cloud; do NOT block the uninstall on it (the operator
            // explicitly requested removal through the PIC-gated dashboard).
            try
            {
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
                if (_cloudClient != null)
                    await _cloudClient.UploadAuditArchiveAsync(archivePayload, digest, ct);
            }
            catch (Exception aex) { _logger.LogWarning(aex, "Self-uninstall: audit archive failed (continuing)"); }

            try
            {
                _stateDb.AppendChainedAuditEntry(new AuditEntry(
                    agentId, "self_uninstall", "", "SelfUninstallRequested", "self_uninstall"));
            }
            catch { /* audit best-effort */ }

            // Drop the sentinel the Broker (LocalSystem) polls for. Core has Modify on the data dir.
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SuavoAgent");
            var sentinel = Path.Combine(dataDir, "uninstall.request");
            try
            {
                Directory.CreateDirectory(dataDir);
                await File.WriteAllTextAsync(sentinel, $"requested {DateTimeOffset.UtcNow:o}", ct);
            }
            catch (Exception sex)
            {
                _logger.LogError(sex, "Self-uninstall: could not write sentinel — uninstall NOT triggered");
                await AckAsync(false, null, "sentinel write failed");
                return;
            }

            _logger.LogWarning("Self-uninstall sentinel written — the Broker will remove the agent shortly");
            await AckAsync(true, new { phase = "sentinel_written" }, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Self-uninstall handling failed");
            await AckAsync(false, null, ex.Message);
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

        string? targetVersion = null;
        string? targetChannel = null;
        try
        {
            var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
            var manifestStr = dataEl.TryGetProperty("manifest", out var m) ? m.GetString() : null;
            var signatureHex = dataEl.TryGetProperty("manifestSignature", out var sig) ? sig.GetString() : null;
            targetChannel = dataEl.TryGetProperty("channel", out var ch) ? ch.GetString() : "stable";

            if (string.IsNullOrEmpty(manifestStr))
            {
                _logger.LogWarning("Signed update command missing manifest — rejecting");
                WriteUpdateHealthEvidence(
                    "failed",
                    targetVersion: null,
                    lastErrorKind: "missing_manifest",
                    consecutiveFailures: 1,
                    channel: targetChannel);
                return;
            }

            var manifest = UpdateManifest.Parse(manifestStr);
            if (manifest is null)
            {
                _logger.LogWarning("Signed update command has malformed manifest — rejecting");
                WriteUpdateHealthEvidence(
                    "failed",
                    targetVersion: null,
                    lastErrorKind: "malformed_manifest",
                    consecutiveFailures: 1,
                    channel: targetChannel);
                return;
            }
            targetVersion = manifest.Version;

            if (manifest.Version == _options.Version)
            {
                _logger.LogDebug("Already running v{Version} — skipping update", manifest.Version);
                WriteUpdateHealthEvidence(
                    "current",
                    targetVersion,
                    lastErrorKind: null,
                    consecutiveFailures: 0,
                    channel: targetChannel);
                return;
            }

            // Canary channel validation: only apply updates matching our assigned channel.
            // Cloud assigns channel (stable/canary/beta) via heartbeat response.
            var myChannel = _lastUpdateChannel ?? _options.UpdateChannel;
            if (!string.Equals(targetChannel, myChannel, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(targetChannel, "stable", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Update channel mismatch: target={Target}, mine={Mine} — skipping",
                    targetChannel, myChannel);
                WriteUpdateHealthEvidence(
                    "skipped_channel",
                    targetVersion,
                    lastErrorKind: null,
                    consecutiveFailures: 0,
                    channel: targetChannel);
                return;
            }

            _updateInProgress = true;
            _logger.LogInformation("Signed package update: v{Version} ({Count} binaries)",
                manifest.Version, 3);
            WriteUpdateHealthEvidence(
                "applying",
                targetVersion,
                lastErrorKind: null,
                consecutiveFailures: 0,
                channel: targetChannel);

            await SelfUpdater.TryApplyPackageUpdateAsync(manifest, signatureHex ?? "", _logger, ct);
            // If we get here, update failed (TryApplyPackageUpdateAsync exits on success)
            WriteUpdateHealthEvidence(
                "failed",
                targetVersion,
                lastErrorKind: "apply_returned_without_exit",
                consecutiveFailures: 1,
                channel: targetChannel);
            _updateInProgress = false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Signed update command failed");
            WriteUpdateHealthEvidence(
                "failed",
                targetVersion,
                lastErrorKind: ex.GetType().Name,
                consecutiveFailures: 1,
                channel: targetChannel);
            _updateInProgress = false;
        }
    }

    private void WriteUpdateHealthEvidence(
        string status,
        string? targetVersion,
        string? lastErrorKind,
        int consecutiveFailures,
        string? channel)
    {
        try
        {
            RuntimeHealthEvidence.WriteUpdateHealth(
                RuntimeHealthEvidence.UpdateHealthPath(),
                status,
                targetVersion,
                DateTimeOffset.UtcNow,
                status is "current" ? DateTimeOffset.UtcNow : null,
                consecutiveFailures,
                lastErrorKind,
                channel ?? _lastUpdateChannel ?? _options.UpdateChannel);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Update health evidence write failed");
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

    /// <summary>
    /// M3: feed the per-task autonomy ledger from a finished pricing run. Best-effort + fail-soft —
    /// a ledger error must never affect the run. A completed run earns a clean-streak tick; the
    /// ledger only ever raises CAPABILITY (unsupervised execution stays separately fail-closed).
    /// </summary>
    private void RecordPricingAutonomy(PricingJobExecutionResult execution)
    {
        if (_taskAutonomy is null) return;
        try
        {
            _taskAutonomy.RecordRun(
                taskKey: "pricing",
                pharmacyId: _options.PharmacyId ?? "",
                clean: execution.Ok,
                outcome: execution.Progress.Status.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TaskAutonomy: failed to record pricing run (non-fatal)");
        }
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

        // Pre-flight: a UIA job drives the LIVE PMS screen. Fail fast (don't touch the
        // screen) unless the Helper is reachable, answering, and in the interactive
        // session. SqlFirst reads SQL and never actuates, so it needs no live Helper.
        if (_options.PricingExecutor == PricingExecutorMode.UiaFirst)
        {
            var preflight = await HelperInteractivePreflight.CheckAsync(
                _ipcCommandClient, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), ct);
            if (!preflight.Ok)
            {
                _logger.LogError("run_pricing_job: Helper pre-flight failed — {Error}", preflight.Error);
                await AckAsync(false, null, preflight.Error);
                return;
            }
            _logger.LogInformation(
                "run_pricing_job: Helper interactive pre-flight OK (session {Session})", preflight.HelperSessionId);
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
            // A pending Helper restart means the Broker is about to KILL the Helper (≤5s).
            // Starting a UIA job now would have it yanked mid-typing. Checked AFTER acquiring
            // the semaphore (restart_helper writes the sentinel while holding it — no TOCTOU).
            if (HelperRestartRequest.IsPending(HelperRestartRequest.DefaultPath(), DateTimeOffset.UtcNow))
            {
                _logger.LogWarning("run_pricing_job: refused — a Helper restart is pending");
                await AckAsync(false, null, "helper restart in progress — retry in ~1 minute");
                return;
            }

            var jobId = Guid.NewGuid().ToString("N");
            var spec = new PricingJobSpec(jobId, canonicalPath, ndcColumn, supplierColumn, costColumn);

            _logger.LogInformation("Pricing job {JobId} starting", jobId);

            var execution = await _pricingJobExecutor.RunAsync(spec, ct);
            var progress = execution.Progress;
            if (_pricingJobCloudUploader != null)
                await _pricingJobCloudUploader.UploadAsync(spec, execution, commandId, ct);

            RecordPricingAutonomy(execution);

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
                reason = progress.HaltReason, // e.g. "helper_unreachable" on a halt → exact cockpit badge
            }, execution.Ok ? null : execution.Error ?? "pricing job failed — see agent logs");
        }
        catch (Exception ex)
        {
            // Fire-and-forget dispatch: without this the SqlFirst executor's unguarded SQLite/workbook
            // boundary (a `database is locked`/IO throw) would become an unobserved detached-task
            // exception with NO failure ack — the operator's portal would hang "running" forever.
            // Match the run_workflow handler: always ack failure.
            _logger.LogError(ex, "run_pricing_job: unexpected failure");
            await AckAsync(false, null, ex.Message);
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

        // Pre-flight: discovery runs FileLocatorService in the Helper's user session and the
        // pricing run drives the live PMS screen — both need the Helper alive, answering, and
        // in the interactive session. Fail fast before touching anything if it isn't.
        var preflight = await HelperInteractivePreflight.CheckAsync(
            _ipcCommandClient, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), ct);
        if (!preflight.Ok)
        {
            _logger.LogError("find_and_run_pricing_job: Helper pre-flight failed — {Error}", preflight.Error);
            await AckAsync(false, null, preflight.Error);
            return;
        }
        _logger.LogInformation(
            "find_and_run_pricing_job: Helper interactive pre-flight OK (session {Session})", preflight.HelperSessionId);

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
        var discovery = await _discoveryClient.FindAsync(_ipcCommandClient, discoveryJobId, spec, ct);
        if (!discovery.Succeeded)
        {
            var failure = discovery.Failure!;
            _logger.LogError(
                "find_and_run_pricing_job: discovery failed reason={Reason} ipcStatus={Status} code={Code}",
                failure.ReasonCode, failure.IpcStatus, failure.ErrorCode);
            await AckAsync(false, failure.ToAckResult(), failure.HumanMessage);
            return;
        }
        var discoveryResult = discovery.Result!;

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
                // Same gate as run_pricing_job: never start a UIA run the Broker is about to
                // kill the Helper out from under (sentinel checked under the semaphore).
                if (HelperRestartRequest.IsPending(HelperRestartRequest.DefaultPath(), DateTimeOffset.UtcNow))
                {
                    _logger.LogWarning("find_and_run_pricing_job: refused — a Helper restart is pending");
                    await AckAsync(false, null, "helper restart in progress — retry in ~1 minute");
                    return;
                }

                var jobId = Guid.NewGuid().ToString("N");
                var jobSpec = new PricingJobSpec(jobId, canonical, ndcColumn, supplierColumn, costColumn);
                _logger.LogInformation("find_and_run_pricing_job: auto-running pricing job {JobId}", jobId);

                _stateDb.UpsertPricingJob(jobSpec, PricingJobStatus.Pending, 0, 0, 0);
                var execution = await _pricingJobExecutor.RunAsync(jobSpec, ct);
                var progress = execution.Progress;
                if (_pricingJobCloudUploader != null)
                    await _pricingJobCloudUploader.UploadAsync(jobSpec, execution, commandId, ct);

                RecordPricingAutonomy(execution);

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
                    reason = progress.HaltReason, // e.g. "helper_unreachable" on a halt → exact cockpit badge
                }, execution.Ok ? null : execution.Error ?? "pricing job failed — see agent logs");
            }
            catch (Exception ex)
            {
                // Fire-and-forget: a throw here (SqlFirst executor's unguarded SQLite/workbook boundary)
                // would otherwise leave the row stuck Pending with no failure ack. Always ack.
                _logger.LogError(ex, "find_and_run_pricing_job: unexpected failure during auto-run");
                await AckAsync(false, null, ex.Message);
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
