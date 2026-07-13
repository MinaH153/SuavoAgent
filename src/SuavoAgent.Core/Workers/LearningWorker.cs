using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.Canary;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Workers;

/// <summary>
/// Orchestrates the 30-day learning phases. Manages observer lifecycle,
/// phase transitions, and mode promotions. Only runs when LearningMode = true.
/// </summary>
public sealed class LearningWorker : BackgroundService
{
    private readonly ILogger<LearningWorker> _logger;
    private readonly AgentOptions _options;
    private readonly AgentStateDb _db;
    private readonly IServiceProvider _sp;
    private readonly SeedClient? _seedClient;
    private readonly SeedApplicator _applicator;
    private readonly List<ILearningObserver> _observers = new();
    private string? _sessionId;
    private bool _inferenceRan;
    private ActionCorrelator? _actionCorrelator;
    private BehavioralEventReceiver? _behavioralReceiver;
    private bool _pomUploaded;
    private int _uploadRetryCount;
    private DateTimeOffset _nextUploadRetryAt;
    private string? _pendingPomJson;
    private string? _pendingPomDigest;
    private bool _adapterActivated;
    private DateTimeOffset _lastPruneAt = DateTimeOffset.MinValue;
    private string? _lastSeedDigest;
    private string? _activeSeedDigest;
    private DateTimeOffset _phaseStartedAt;
    private string? _previousPhase;

    private static readonly TimeSpan[] UploadBackoff =
    {
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15), // cap
    };

    public LearningWorker(
        ILogger<LearningWorker> logger,
        IOptions<AgentOptions> options,
        AgentStateDb db,
        IServiceProvider sp,
        SeedApplicator applicator,
        SeedClient? seedClient = null)
    {
        _logger = logger;
        _options = options.Value;
        _db = db;
        _sp = sp;
        _applicator = applicator;
        _seedClient = seedClient;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.LearningMode)
        {
            _logger.LogInformation("Learning mode disabled — LearningWorker idle");
            return;
        }

        var pharmacyId = _options.PharmacyId ?? "unknown";

        // CRITICAL-7: Resume existing non-terminal session instead of creating date-derived ID
        _sessionId = _db.GetActiveSessionId(pharmacyId);
        if (_sessionId != null)
        {
            _logger.LogInformation("core.learning.session_resumed");
        }
        else
        {
            _sessionId = $"learn-{_options.AgentId}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            _db.CreateLearningSession(_sessionId, pharmacyId);
            _logger.LogInformation("core.learning.session_created");
        }

        // Use secret per-session salt for PHI hashing (not AgentId, which is sent in heartbeats)
        var pharmacySalt = _db.GetOrCreateHmacSalt(_sessionId);

        // Initialize observers
        var processObs = new ProcessObserver(_db, pharmacySalt,
            _sp.GetRequiredService<ILogger<ProcessObserver>>());
        var sqlObs = new SqlSchemaObserver(_db, pharmacySalt,
            _sp.GetRequiredService<ILogger<SqlSchemaObserver>>(),
            _options.SqlTrustServerCertificate,
            _options.SqlServerCertificateSha256);
        var dmvObs = new DmvQueryObserver(_db,
            () => new SqlConnection(BuildConnectionString(_options)),
            _sp.GetRequiredService<ILogger<DmvQueryObserver>>());

        _observers.Add(processObs);
        _observers.Add(sqlObs);
        _observers.Add(dmvObs);

        // Behavioral correlation and learning instances
        _actionCorrelator = new ActionCorrelator(_db, _sessionId,
            clockCalibrated: false);
        // Codex 2026-04-27 (Trip A root cause + redesign) — the singleton
        // BehavioralEventReceiver lazy-resolves the session id per batch
        // via AgentStateDb.GetActiveSessionId, so events automatically pick
        // up _sessionId now that this LearningWorker has registered it. We
        // only need to wire the post-persist interaction callback so
        // ActionCorrelator sees UI events that arrive over IPC.
        _behavioralReceiver = _sp.GetRequiredService<BehavioralEventReceiver>();
        _behavioralReceiver.SetInteractionCallback(
            (treeHash, elementId, controlType, timestamp) =>
                _actionCorrelator.RecordUiEvent(treeHash, elementId, controlType, timestamp));

        // Bridge DMV observations → ActionCorrelator for UI↔SQL correlation
        dmvObs.Correlator = _actionCorrelator;

        // Wire DMV clock calibration state to correlator
        dmvObs.ClockCalibratedChanged += calibrated => _actionCorrelator.SetClockCalibrated(calibrated);

        _db.AppendLearningAudit(_sessionId, "worker", "start",
            $"observers:{_observers.Count}", phiScrubbed: false);

        // Start observers for current phase
        var session = _db.GetLearningSession(_sessionId)!.Value;
        var currentPhase = LearningSession.PhaseToObserverPhase(session.Phase);
        _previousPhase = session.Phase;
        _phaseStartedAt = _db.GetPhaseChangedAt(_sessionId);
        if (session.Phase is "pattern" or "model")
        {
            var restoredSeed = _db.GetLatestAppliedSeed(_sessionId, session.Phase);
            if (restoredSeed is not null)
            {
                _activeSeedDigest = restoredSeed.SeedDigest;
                _lastSeedDigest = restoredSeed.SeedDigest;
                if (session.Phase == "pattern")
                {
                    _actionCorrelator?.RegisterSeededShapes(
                        _applicator.GetSeededShapeHashes(restoredSeed.SeedDigest));
                    _actionCorrelator?.SetActiveSeedDigest(restoredSeed.SeedDigest);
                }
                _logger.LogInformation("core.learning.seed_binding_restored");
            }
        }

        foreach (var obs in _observers)
        {
            if (obs.ActivePhases.HasFlag(currentPhase))
            {
                _ = obs.StartAsync(_sessionId, stoppingToken);
                _logger.LogInformation("core.learning.observer_started");
            }
        }

        // Phase management loop
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

            session = _db.GetLearningSession(_sessionId)!.Value;

            // Detect phase transitions and pull seeds at entry points
            if (session.Phase != _previousPhase)
            {
                _phaseStartedAt = _db.GetPhaseChangedAt(_sessionId);
                _activeSeedDigest = null; // reset for new phase

                var oldPhase = LearningSession.PhaseToObserverPhase(_previousPhase ?? "discovery");
                var newPhase = LearningSession.PhaseToObserverPhase(session.Phase);

                // Stop observers not active in the new phase
                foreach (var obs in _observers.Where(o => o.ActivePhases.HasFlag(oldPhase)
                    && !o.ActivePhases.HasFlag(newPhase)))
                {
                    await obs.StopAsync();
                    _logger.LogInformation("core.learning.observer_stopped_for_phase");
                }

                // Start observers active in the new phase that weren't in the old phase
                foreach (var obs in _observers.Where(o => o.ActivePhases.HasFlag(newPhase)
                    && !o.ActivePhases.HasFlag(oldPhase)))
                {
                    _ = obs.StartAsync(_sessionId, stoppingToken);
                    _logger.LogInformation("core.learning.observer_started_for_phase");
                }

                // W-9: Update currentPhase so observer health checks use the correct phase
                currentPhase = newPhase;

                if (session.Phase == "pattern")
                    await PullSeedsAsync("pattern", stoppingToken);
                else if (session.Phase == "model")
                    await PullSeedsAsync("model", stoppingToken);

                _previousPhase = session.Phase;
            }

            var patternPhaseGateReady = false;

            // PhaseGate evaluation — seeded pattern learning may advance after
            // independent local confirmation. Model is still evaluated for
            // visibility, but can never auto-transition to human approval.
            if (_activeSeedDigest is not null && session.Phase is "pattern" or "model")
            {
                var canaryClean = !IsCanaryInHold();
                var unseededCount = _db.GetUnseededCorrelationCount(_sessionId);
                var gate = new PhaseGate(_db, _sessionId, session.Phase, _activeSeedDigest,
                    _phaseStartedAt, canaryClean, unseededCount);
                var eval = gate.Evaluate();

                // Every eval is logged (not just advance) so a HOLD is directly observable — which
                // gate failed and why. Previously the gate was silent unless ready/abort.
                _logger.LogInformation(
                    "core.learning.phase_gate_evaluated ready={Ready} count={Count}",
                    eval.Ready,
                    eval.Gates.Count);

                if (eval.AbortAcceleration)
                {
                    _logger.LogWarning("Seed acceleration aborted — reverting to time-based phase duration");
                    _activeSeedDigest = null;
                }
                else if (eval.Ready)
                {
                    var seedReceipt = _db.GetSeedApplicationReceipt(_activeSeedDigest);
                    var confirmed = seedReceipt?.Accepted == true;
                    if (!confirmed && seedReceipt is not null && _seedClient is not null)
                    {
                        try
                        {
                            confirmed = await _seedClient.ConfirmAsync(
                                seedReceipt.Signed, stoppingToken);
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogSafeWarning(ex);
                        }
                    }
                    if (confirmed && seedReceipt is { Accepted: false })
                        _db.MarkSeedApplicationReceiptAccepted(
                            seedReceipt.Signed.Receipt.CommandId);
                    patternPhaseGateReady = session.Phase == "pattern" && confirmed;
                    _logger.LogInformation(
                        "core.learning.phase_gate_passed verified={Verified} ready={Ready}",
                        confirmed,
                        patternPhaseGateReady);
                    if (!confirmed)
                        _logger.LogWarning(
                            "Seed application receipt is not cloud-confirmed; phase advancement remains blocked");
                    _db.AppendLearningAudit(_sessionId, "seed", "phase_gate_ready",
                        $"phase:{session.Phase},digest:{_activeSeedDigest[..12]}", phiScrubbed: false);
                }
            }

            var progression = LearningPhaseProgression.Evaluate(
                session.Phase,
                _phaseStartedAt,
                DateTimeOffset.UtcNow,
                patternPhaseGateReady);
            if (progression is not null)
            {
                _db.UpdateLearningPhase(_sessionId, progression.NextPhase);
                _db.AppendLearningAudit(
                    _sessionId,
                    "worker",
                    "phase_auto_advanced",
                    $"from:{session.Phase},to:{progression.NextPhase},reason:{progression.Reason}",
                    phiScrubbed: false);
                _logger.LogInformation("core.learning.phase_advanced");
                continue;
            }

            // Check observer health — hard stop if any fails
            foreach (var obs in _observers)
            {
                var health = obs.CheckHealth();
                if (obs.ActivePhases.HasFlag(currentPhase) && !health.IsRunning)
                {
                    _logger.LogWarning("core.learning.observer_stopped_unexpectedly");
                    _db.AppendLearningAudit(_sessionId, "worker", "observer_health_fail",
                        health.ObserverName, phiScrubbed: false);

                    // If in autonomous mode, hard stop
                    if (session.Mode == "autonomous")
                    {
                        _logger.LogWarning("HARD STOP: observer failure in autonomous mode — downgrading to supervised");
                        _db.UpdateLearningMode(_sessionId, "supervised");
                    }
                }
            }

            // Routine/template extraction starts during discovery, but discovery
            // is capture-only: no rule file, approval row, assist, or actuation.
            if (session.Phase is "discovery" or "pattern")
            {
                try
                {
                    var routineDetector = new RoutineDetector(_db, _sessionId);
                    routineDetector.DetectAndPersist();
                }
                catch (Exception ex)
                {
                    _logger.LogSafeWarning(ex);
                }

                // v3.12 — autonomous workflow template extraction + rule emission.
                // Flag-gated (Learning:Template:Enabled). All emitted rules ship
                // with autonomousOk=false so operator approval is still required
                // before any auto-rule fires in production.
                if (_options.TemplateLearning.Enabled)
                {
                    TryExtractAndEmitTemplates(captureOnly: session.Phase == "discovery");
                }
            }

            // Daily behavioral event prune
            if (DateTimeOffset.UtcNow - _lastPruneAt >= TimeSpan.FromDays(1))
            {
                try
                {
                    _db.PruneBehavioralEvents(_sessionId, olderThanDays: 7);
                    _lastPruneAt = DateTimeOffset.UtcNow;
                    _logger.LogInformation("core.learning.expired_events_pruned");
                }
                catch (Exception ex)
                {
                    _logger.LogSafeWarning(ex);
                }
            }

            // Feedback processing (batch) — decay, operator directives, stale escalation
            if (session.Phase is "pattern" or "model" or "approved" or "active")
            {
                try
                {
                    var feedbackProcessor = new FeedbackProcessor(_db, _sessionId);
                    feedbackProcessor.ProcessPendingFeedback();

                    // W7: Run correlation window recalibration during active phase
                    if (session.Phase == "active")
                    {
                        feedbackProcessor.ProcessRecalibration();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogSafeWarning(ex);
                }
            }

            // Auto-trigger Pattern Engine when entering Model phase
            if (session.Phase == "model" && !_inferenceRan)
            {
                _logger.LogInformation("Model phase — running schema discovery + pattern engine");

                try
                {
                    // Every query-shaping observation comes from one explicitly
                    // identity-verified physical SQL connection. Transparent driver
                    // reconnect is disabled in BuildConnectionString.
                    var schemaObs = _observers.OfType<SqlSchemaObserver>().FirstOrDefault()
                        ?? throw new InvalidOperationException("SQL schema observer is unavailable.");
                    await using var schemaConn = new SqlConnection(BuildConnectionString(_options));
                    await schemaConn.OpenAsync(stoppingToken);
                    await schemaObs.DiscoverSchemaAsync(_sessionId, schemaConn, stoppingToken);
                    _logger.LogInformation("Schema discovery completed via SqlSchemaObserver");

                    // Behavioral routines do not authorize SQL reads, so their
                    // failure may remain non-blocking for this schema contract.
                    try
                    {
                        var routineDetector = new RoutineDetector(_db, _sessionId);
                        routineDetector.DetectAndPersist();
                        _logger.LogInformation("core.learning.routine_detection_completed");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogSafeWarning(ex);
                    }

                    var inference = new RxQueueInferenceEngine(_db);
                    inference.InferAndPersist(_sessionId);
                    var candidates = _db.GetRxQueueCandidates(_sessionId);
                    _db.AppendLearningAudit(_sessionId, "pattern", "rx_inference",
                        $"candidates:{candidates.Count}", phiScrubbed: false);

                    var topCandidate = candidates.FirstOrDefault();
                    if (topCandidate.PrimaryTable is null || topCandidate.StatusColumn is null)
                        throw new InvalidDataException("No complete Rx queue candidate was inferred.");

                    var statusValues = await QueryDistinctStatusValuesAsync(
                        schemaConn,
                        topCandidate.PrimaryTable,
                        topCandidate.StatusColumn,
                        stoppingToken);
                    if (statusValues.Count == 0)
                        throw new InvalidDataException("No status values were observed for the inferred Rx queue.");

                    var statusEngine = new StatusOrderingEngine(_db);
                    statusEngine.InferAndPersist(
                        _sessionId,
                        topCandidate.PrimaryTable,
                        topCandidate.StatusColumn,
                        statusValues);
                    _db.CompleteLearnedTemplateEvidence(_sessionId);
                    if (new AdapterGenerator(_db).Describe(_sessionId) is null)
                        throw new InvalidDataException("Learned SQL template contract is incomplete.");

                    _logger.LogInformation(
                        "core.learning.status_ordering_inferred count={Count}",
                        statusValues.Count);

                    var droppedCount = _behavioralReceiver?.TotalDroppedEvents ?? 0;
                    _pendingPomJson = PomExporter.Export(
                        _db,
                        _sessionId,
                        droppedEventCount: droppedCount);
                    _pendingPomDigest = PomExporter.ComputeDigest(
                        _options.PharmacyId ?? "",
                        _sessionId,
                        _pendingPomJson);
                    _inferenceRan = true;
                    _db.AppendLearningAudit(_sessionId, "worker", "pom_exported",
                        $"digest:{_pendingPomDigest[..12]}", phiScrubbed: false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _db.InvalidateLearnedTemplateEvidence(_sessionId);
                    _inferenceRan = false;
                    _pendingPomJson = null;
                    _pendingPomDigest = null;
                    _logger.LogSafeWarning(ex);
                }
            }

            // Upload POM (with retry + backoff on subsequent iterations)
            if (session.Phase == "model" && _inferenceRan && !_pomUploaded
                && _pendingPomJson != null && _pendingPomDigest != null)
            {
                if (DateTimeOffset.UtcNow < _nextUploadRetryAt)
                    continue; // Backoff not elapsed yet

                var cloudClient = _sp.GetService<SuavoCloudClient>();
                if (cloudClient != null)
                {
                    var pomId = await cloudClient.UploadPomAsync(_pendingPomJson, _pendingPomDigest, stoppingToken);
                    if (pomId != null)
                    {
                        _pomUploaded = true;
                        _logger.LogInformation(
                            "core.learning.pom_uploaded attempt={Attempt}",
                            _uploadRetryCount + 1);

                        // CRITICAL-6: Freeze POM — stop observers so no mutations after upload
                        foreach (var obs in _observers)
                            await obs.StopAsync();
                        _logger.LogInformation("Observers stopped — POM frozen for review");

                        // Store frozen snapshot for approval verification
                        _db.StorePomSnapshot(_sessionId, _pendingPomJson);
                    }
                    else
                    {
                        _uploadRetryCount++;
                        var backoffIdx = Math.Min(_uploadRetryCount - 1, UploadBackoff.Length - 1);
                        _nextUploadRetryAt = DateTimeOffset.UtcNow + UploadBackoff[backoffIdx];
                        _logger.LogWarning(
                            "core.learning.pom_upload_failed attempt={Attempt}",
                            _uploadRetryCount);
                    }
                }
            }

            // Restore or activate only through the digest-bound registry. The
            // registry independently verifies the local human approval receipt,
            // frozen POM digest, session, and adapter-template digest.
            if (session.Phase is "approved" or "active" && !_adapterActivated)
            {
                var registry = _sp.GetService<IActivePmsAdapterRegistry>();
                if (registry is null)
                {
                    _logger.LogWarning("Learned adapter activation blocked — registry unavailable");
                }
                else
                {
                    var result = registry.ActivateApproved(_sessionId);
                    _adapterActivated = result.IsActive;
                }
            }
        }

        // Cleanup
        foreach (var obs in _observers)
        {
            await obs.StopAsync();
            obs.Dispose();
        }

        _logger.LogInformation("LearningWorker stopped");
    }

    /// <summary>
    /// Runs <see cref="WorkflowTemplateExtractor"/> and, only outside capture
    /// mode with RuleGeneration explicitly enabled, <see cref="TemplateRuleGenerator"/>.
    /// Never throws; a failure is logged and the phase loop continues.
    /// </summary>
    private void TryExtractAndEmitTemplates(bool captureOnly)
    {
        try
        {
            var opts = _options.TemplateLearning;
            var thresholds = new WorkflowTemplateThresholds
            {
                MinRoutineConfidence = opts.MinRoutineConfidence,
                MinStepCount = opts.MinStepCount,
                MaxExpectedVisiblePerScreen = opts.MaxExpectedVisiblePerScreen,
                MatchRatio = opts.MatchRatio,
                LowConfidenceRetirementAfter = opts.LowConfidenceRetirementAfter,
            };
            var extractor = new WorkflowTemplateExtractor(
                _db, _sessionId!, opts.SkillId, opts.ProcessNameGlob,
                () => BuildLocalPmsVersionFingerprint(),
                thresholds,
                captureOnly: captureOnly);
            var extracted = extractor.ExtractAndPersist();

            var emitted = 0;
            if (ShouldEmitTemplateRules(opts, captureOnly))
            {
                var rulesRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "SuavoAgent", "rules", "auto");
                var generator = new TemplateRuleGenerator(_db, rulesRoot,
                    _sp.GetRequiredService<ILogger<TemplateRuleGenerator>>());
                emitted = generator.EmitPendingRules();
            }
            else if (extracted.Count > 0)
            {
                _logger.LogInformation("core.learning.template_capture_only");
            }

            if (extracted.Count > 0 || emitted > 0)
            {
                _logger.LogInformation(
                    "TemplateLearning: extracted {Extracted} template(s), emitted {Emitted} rule file(s)",
                    extracted.Count, emitted);
            }
        }
        catch (Exception ex)
        {
            _logger.LogSafeWarning(ex);
        }
    }

    internal static bool ShouldEmitTemplateRules(
        TemplateLearningOptions options,
        bool captureOnly = false) =>
        options.Enabled
        && !captureOnly
        && options.RuleGeneration
        && !string.Equals(options.Mode, "capture", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds a best-effort <see cref="PmsVersionFingerprint"/> for the
    /// current installation. Falls back to sentinel values when the schema
    /// canary baseline is not yet established — templates with such
    /// fingerprints will fail cross-installation matching at seed-apply time,
    /// which is the correct conservative behaviour.
    /// </summary>
    private PmsVersionFingerprint BuildLocalPmsVersionFingerprint()
    {
        var contractFingerprint = _db.GetLatestContractFingerprint(
            _options.PharmacyId ?? "unknown") ?? "unestablished";
        return new PmsVersionFingerprint(
            PmsType: "PioneerRx",
            SchemaHash: contractFingerprint,
            UiaDialectHash: "unestablished",
            ProductVersionString: null);
    }

    /// <summary>
    /// Pulls seeds from the cloud at phase entry and applies them locally.
    /// Falls back gracefully — seed failure never blocks learning.
    /// </summary>
    private async Task PullSeedsAsync(string phase, CancellationToken ct)
    {
        if (_seedClient is null) return;

        try
        {
            var treeHashes = phase == "model"
                ? _db.GetDistinctTreeHashes(_sessionId!)
                : (IReadOnlyList<string>)Array.Empty<string>();

            var contractFingerprint = _db.GetLatestContractFingerprint(
                _options.PharmacyId ?? "unknown") ?? "";
            // Discover PMS type from observed processes instead of hardcoding
            var pmsType = "Unknown";
            var processes = _db.GetObservedProcesses(_sessionId!);
            foreach (var (procName, _, _, _, isPms) in processes)
            {
                if (isPms && ProcessObserver.KnownPmsSignatures.TryGetValue(procName, out var name))
                {
                    pmsType = name;
                    break;
                }
            }
            var pmsVersionHash = "";

            var seedReq = new SeedRequest(
                pmsType,
                phase,
                contractFingerprint,
                pmsVersionHash,
                treeHashes,
                _lastSeedDigest);

            var seedResp = await _seedClient.PullAsync(seedReq, ct);
            if (seedResp is null) return;

            var deviceSigner = _sp.GetService<IDeviceAuthoritySigner>()
                ?? throw new InvalidOperationException(
                    "Device authority signer unavailable; fleet seed application is blocked.");
            if (!string.Equals(
                    seedResp.DeviceKeyId,
                    deviceSigner.KeyId,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Fleet seed was issued to a different device authority key.");

            var correlationsApplied = 0;
            var correlationsSkipped = 0;

            if (phase == "pattern")
            {
                var result = _applicator.ApplyPatternSeeds(_sessionId!, seedResp);
                correlationsApplied += result.ItemsApplied;
                _activeSeedDigest = seedResp.SeedDigest;
                _lastSeedDigest = seedResp.SeedDigest;
                _actionCorrelator?.RegisterSeededShapes(
                    _applicator.GetSeededShapeHashes(seedResp.SeedDigest));
                _actionCorrelator?.SetActiveSeedDigest(seedResp.SeedDigest);
                if (!result.AlreadyApplied)
                {
                    _logger.LogInformation(
                        "core.learning.pattern_seeds_applied count={Count}",
                        result.ItemsApplied);
                }
            }
            else // model
            {
                var result = _applicator.ApplyModelSeeds(
                    _sessionId!, seedResp, _options.FleetLearning.Enabled);
                correlationsApplied += result.CorrelationsApplied;
                correlationsSkipped += result.CorrelationsSkipped;
                _activeSeedDigest = seedResp.SeedDigest;
                _lastSeedDigest = seedResp.SeedDigest;
                if (!result.AlreadyApplied)
                {
                    _logger.LogInformation(
                        "core.learning.model_seeds_applied count={Count} skipped={Skipped}",
                        result.CorrelationsApplied,
                        result.CorrelationsSkipped);
                }
            }

            // Spec-D §6 — cross-pharmacy workflow template transfer. Orthogonal
            // to the pattern/model phase split: templates ride alongside either
            // payload, and ApplyWorkflowTemplates has its own per-template
            // idempotency via seed_items. Runs after the phase-specific apply
            // so query shapes referenced by templates are already resolved.
            if (seedResp.WorkflowTemplates is { Count: > 0 })
            {
                var tplResult = _applicator.ApplyWorkflowTemplates(
                    _sessionId!, seedResp, BuildLocalPmsVersionFingerprint());
                if (tplResult.TemplatesApplied > 0 || tplResult.TemplatesSkipped > 0)
                {
                    correlationsApplied += tplResult.TemplatesApplied;
                    correlationsSkipped += tplResult.TemplatesSkipped;
                    _logger.LogInformation(
                        "core.learning.templates_applied count={Count} skipped={Skipped}",
                        tplResult.TemplatesApplied,
                        tplResult.TemplatesSkipped);
                }
            }

            // M2c — learned selector corrections close the learn-from-mistake loop. Same verified
            // envelope; own seed_items idempotency; malformed/non-identifiable patches are skipped.
            if (seedResp.SelectorPatches is { Count: > 0 })
            {
                var patchResult = _applicator.ApplySelectorPatches(seedResp);
                if (patchResult.PatchesApplied > 0 || patchResult.PatchesSkipped > 0)
                {
                    correlationsApplied += patchResult.PatchesApplied;
                    correlationsSkipped += patchResult.PatchesSkipped;
                    _logger.LogInformation(
                        "core.learning.selector_patches_applied count={Count} skipped={Skipped}",
                        patchResult.PatchesApplied,
                        patchResult.PatchesSkipped);
                }
            }

            _ = _db.GetOrCreateSeedApplicationReceipt(
                seedResp,
                _options,
                _sessionId!,
                correlationsApplied,
                correlationsSkipped,
                deviceSigner);
        }
        catch (Exception ex)
        {
            _logger.LogSafeWarning(ex);
        }
    }

    /// <summary>
    /// Checks whether the schema canary is in hold state for this pharmacy.
    /// Returns true if hold is active (i.e. canary is NOT clean).
    /// </summary>
    private bool IsCanaryInHold()
    {
        var pharmacyId = _options.PharmacyId ?? "unknown";
        var hold = _db.GetCanaryHold(pharmacyId, "pioneerrx");
        return hold is not null;
    }

    /// <summary>
    /// Queries distinct status values from the PMS database for a given table and column.
    /// Uses bracket-escaped identifiers and validated table names to prevent SQL injection.
    /// </summary>
    private static async Task<IReadOnlyList<(string Value, string DisplayName)>> QueryDistinctStatusValuesAsync(
        SqlConnection conn, string table, string statusColumn, CancellationToken ct)
    {
        // Validate table name: must be schema.table with only word characters
        if (!System.Text.RegularExpressions.Regex.IsMatch(table, @"^[\w]+\.[\w]+$"))
            return Array.Empty<(string, string)>();

        var parts = table.Split('.');
        var safeTable = $"[{parts[0].Replace("]", "]]")}].[{parts[1].Replace("]", "]]")}]";
        var safeColumn = $"[{statusColumn.Replace("]", "]]")}]";

        await using var cmd = new SqlCommand(
            $"SELECT DISTINCT TOP (256) {safeColumn} FROM {safeTable} " +
            $"WHERE {safeColumn} IS NOT NULL ORDER BY {safeColumn}", conn);
        cmd.CommandTimeout = 15;
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var results = new List<(string, string)>();
        while (await reader.ReadAsync(ct))
        {
            var val = reader[0]?.ToString() ?? "";
            if (!string.IsNullOrWhiteSpace(val))
                results.Add((val, val));
        }
        return results;
    }

    internal static string BuildConnectionString(AgentOptions options)
    {
        var csb = new SqlConnectionStringBuilder();
        if (!string.IsNullOrEmpty(options.SqlServer)) csb.DataSource = options.SqlServer;
        if (!string.IsNullOrEmpty(options.SqlDatabase)) csb.InitialCatalog = options.SqlDatabase;
        csb.ApplicationName = "SuavoAgent";
        csb.MaxPoolSize = 1;
        csb.ConnectRetryCount = 0;
        SqlConnectionSecurity.Apply(csb, options);
        if (!string.IsNullOrEmpty(options.SqlUser))
        {
            csb.UserID = options.SqlUser;
            csb.Password = options.SqlPassword;
        }
        else
        {
            csb.IntegratedSecurity = true;
        }
        return csb.ConnectionString;
    }
}
