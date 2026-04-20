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
            _logger.LogInformation("Resuming existing learning session {Id} for pharmacy {Pharmacy}",
                _sessionId, pharmacyId);
        }
        else
        {
            _sessionId = $"learn-{_options.AgentId}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            _db.CreateLearningSession(_sessionId, pharmacyId);
            _logger.LogInformation("Created learning session {Id} for pharmacy {Pharmacy}",
                _sessionId, pharmacyId);
        }

        // W6: Wire session ID to WritebackProcessor so inline feedback records writeback outcomes
        var writebackProcessor = _sp.GetService<WritebackProcessor>();
        writebackProcessor?.SetSessionId(_sessionId);

        // Use secret per-session salt for PHI hashing (not AgentId, which is sent in heartbeats)
        var pharmacySalt = _db.GetOrCreateHmacSalt(_sessionId);

        // Initialize observers
        var processObs = new ProcessObserver(_db, pharmacySalt,
            _sp.GetRequiredService<ILogger<ProcessObserver>>());
        var sqlObs = new SqlSchemaObserver(_db, pharmacySalt,
            _sp.GetRequiredService<ILogger<SqlSchemaObserver>>());
        var dmvObs = new DmvQueryObserver(_db,
            () => new SqlConnection(BuildConnectionString()),
            _sp.GetRequiredService<ILogger<DmvQueryObserver>>());

        _observers.Add(processObs);
        _observers.Add(sqlObs);
        _observers.Add(dmvObs);

        // Behavioral correlation and learning instances
        _actionCorrelator = new ActionCorrelator(_db, _sessionId,
            clockCalibrated: false);
        _behavioralReceiver = new BehavioralEventReceiver(_db, _sessionId,
            onInteraction: (treeHash, elementId, controlType, timestamp) =>
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

        foreach (var obs in _observers)
        {
            if (obs.ActivePhases.HasFlag(currentPhase))
            {
                _ = obs.StartAsync(_sessionId, stoppingToken);
                _logger.LogInformation("Started observer: {Name}", obs.Name);
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
                    _logger.LogInformation("Stopped observer {Name} (not active in {Phase})", obs.Name, session.Phase);
                }

                // Start observers active in the new phase that weren't in the old phase
                foreach (var obs in _observers.Where(o => o.ActivePhases.HasFlag(newPhase)
                    && !o.ActivePhases.HasFlag(oldPhase)))
                {
                    _ = obs.StartAsync(_sessionId, stoppingToken);
                    _logger.LogInformation("Started observer {Name} for {Phase}", obs.Name, session.Phase);
                }

                // W-9: Update currentPhase so observer health checks use the correct phase
                currentPhase = newPhase;

                if (session.Phase == "pattern")
                    await PullSeedsAsync("pattern", stoppingToken);
                else if (session.Phase == "model")
                    await PullSeedsAsync("model", stoppingToken);

                _previousPhase = session.Phase;
            }

            // PhaseGate evaluation — check if seed-accelerated phase can advance
            if (_activeSeedDigest is not null && session.Phase is "pattern" or "model")
            {
                var canaryClean = !IsCanaryInHold();
                var unseededCount = _db.GetUnseededCorrelationCount(_sessionId);
                var gate = new PhaseGate(_db, _sessionId, session.Phase, _activeSeedDigest,
                    _phaseStartedAt, canaryClean, unseededCount);
                var eval = gate.Evaluate();

                if (eval.AbortAcceleration)
                {
                    _logger.LogWarning("Seed acceleration aborted — reverting to time-based phase duration");
                    _activeSeedDigest = null;
                }
                else if (eval.Ready)
                {
                    _logger.LogInformation("PhaseGate passed — advancing from {Phase}", session.Phase);
                    try
                    {
                        if (_seedClient is not null)
                            await _seedClient.ConfirmAsync(new SeedConfirmRequest(
                                _activeSeedDigest,
                                DateTimeOffset.UtcNow.ToString("o"),
                                0, 0), stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Seed confirm failed");
                    }
                    // Phase advance is still operator-triggered via signed command;
                    // PhaseGate readiness is logged for dashboard visibility
                    _db.AppendLearningAudit(_sessionId, "seed", "phase_gate_ready",
                        $"phase:{session.Phase},digest:{_activeSeedDigest[..12]}", phiScrubbed: false);
                }
            }

            // Check observer health — hard stop if any fails
            foreach (var obs in _observers)
            {
                var health = obs.CheckHealth();
                if (obs.ActivePhases.HasFlag(currentPhase) && !health.IsRunning)
                {
                    _logger.LogWarning("Observer {Name} stopped unexpectedly — flagging anomaly",
                        health.ObserverName);
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

            // Run RoutineDetector in pattern phase (periodic, not one-shot)
            if (session.Phase == "pattern")
            {
                try
                {
                    var routineDetector = new RoutineDetector(_db, _sessionId);
                    routineDetector.DetectAndPersist();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "RoutineDetector (pattern phase) failed");
                }

                // v3.12 — autonomous workflow template extraction + rule emission.
                // Flag-gated (Learning:Template:Enabled). All emitted rules ship
                // with autonomousOk=false so operator approval is still required
                // before any auto-rule fires in production.
                if (_options.TemplateLearning.Enabled)
                {
                    TryExtractAndEmitTemplates();
                }
            }

            // Daily behavioral event prune
            if (DateTimeOffset.UtcNow - _lastPruneAt >= TimeSpan.FromDays(1))
            {
                try
                {
                    _db.PruneBehavioralEvents(_sessionId, olderThanDays: 7);
                    _lastPruneAt = DateTimeOffset.UtcNow;
                    _logger.LogInformation("Pruned behavioral events older than 7 days for session {Session}", _sessionId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Behavioral event prune failed");
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
                    _logger.LogWarning(ex, "FeedbackProcessor batch tick failed");
                }
            }

            // Auto-trigger Pattern Engine when entering Model phase
            if (session.Phase == "model" && !_inferenceRan)
            {
                _logger.LogInformation("Model phase — running schema discovery + pattern engine");

                // CRITICAL-4a: Run SqlSchemaObserver.DiscoverSchemaAsync with a real SqlConnection
                var schemaObs = _observers.OfType<SqlSchemaObserver>().FirstOrDefault();
                if (schemaObs != null)
                {
                    try
                    {
                        await using var schemaConn = new SqlConnection(BuildConnectionString());
                        await schemaConn.OpenAsync(stoppingToken);
                        await schemaObs.DiscoverSchemaAsync(_sessionId, schemaConn, stoppingToken);
                        _logger.LogInformation("Schema discovery completed via SqlSchemaObserver");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Schema discovery failed — inference will use existing data");
                    }
                }

                // Run routine detection for behavioral learning
                try
                {
                    var routineDetector = new RoutineDetector(_db, _sessionId);
                    routineDetector.DetectAndPersist();
                    _logger.LogInformation("RoutineDetector completed for session {Session}", _sessionId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "RoutineDetector failed — continuing without behavioral routines");
                }

                // Run Rx queue inference
                var inference = new RxQueueInferenceEngine(_db);
                inference.InferAndPersist(_sessionId);
                _inferenceRan = true;

                var candidates = _db.GetRxQueueCandidates(_sessionId);
                _db.AppendLearningAudit(_sessionId, "pattern", "rx_inference",
                    $"candidates:{candidates.Count}", phiScrubbed: false);

                // CRITICAL-4b: Run StatusOrderingEngine for the top candidate's status column
                var topCandidate = candidates.FirstOrDefault();
                if (topCandidate.PrimaryTable != null && topCandidate.StatusColumn != null)
                {
                    try
                    {
                        await using var statusConn = new SqlConnection(BuildConnectionString());
                        await statusConn.OpenAsync(stoppingToken);
                        var statusValues = await QueryDistinctStatusValuesAsync(
                            statusConn, topCandidate.PrimaryTable, topCandidate.StatusColumn, stoppingToken);

                        if (statusValues.Count > 0)
                        {
                            var statusEngine = new StatusOrderingEngine(_db);
                            statusEngine.InferAndPersist(_sessionId, topCandidate.PrimaryTable,
                                topCandidate.StatusColumn, statusValues);
                            _logger.LogInformation("Status ordering: {Count} values inferred for {Table}.{Col}",
                                statusValues.Count, topCandidate.PrimaryTable, topCandidate.StatusColumn);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Status ordering failed for {Table}.{Col}",
                            topCandidate.PrimaryTable, topCandidate.StatusColumn);
                    }
                }

                // Prepare POM for upload
                var droppedCount = _behavioralReceiver?.TotalDroppedEvents ?? 0;
                _pendingPomJson = PomExporter.Export(_db, _sessionId,
                    droppedEventCount: droppedCount);
                _pendingPomDigest = PomExporter.ComputeDigest(
                    _options.PharmacyId ?? "", _sessionId, _pendingPomJson);

                _db.AppendLearningAudit(_sessionId, "worker", "pom_exported",
                    $"digest:{_pendingPomDigest[..12]}", phiScrubbed: false);
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
                        _logger.LogInformation("POM uploaded (id={PomId}, digest={Digest}, attempt={Attempt})",
                            pomId, _pendingPomDigest[..12], _uploadRetryCount + 1);

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
                        _logger.LogWarning("POM upload failed (attempt {Attempt}) — retrying after {Backoff}",
                            _uploadRetryCount, UploadBackoff[backoffIdx]);
                    }
                }
            }

            // Activate learned adapter when phase transitions to approved
            if (session.Phase == "approved" && !_adapterActivated)
            {
                // CRITICAL-5: Recompute digest and verify against stored approved_model_digest
                var storedDigest = session.ApprovedModelDigest;
                var pomSnapshot = _db.GetPomSnapshot(_sessionId);
                if (string.IsNullOrEmpty(storedDigest) || string.IsNullOrEmpty(pomSnapshot))
                {
                    _logger.LogWarning("Activation blocked — missing approval digest or POM snapshot for session {Id}", _sessionId);
                }
                else
                {
                    var recomputedDigest = PomExporter.ComputeDigest(
                        _options.PharmacyId ?? "", _sessionId, pomSnapshot);

                    if (!string.Equals(recomputedDigest, storedDigest, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("Activation REFUSED — digest mismatch: stored={Stored} recomputed={Recomputed}. " +
                            "POM may have been tampered. Requires re-approval.",
                            storedDigest[..12], recomputedDigest[..12]);
                    }
                    else
                    {
                        var generator = new AdapterGenerator(_db);
                        var adapter = generator.Generate(_sessionId,
                            connectionString: BuildConnectionString(),
                            logger: _sp.GetRequiredService<ILogger<LearnedPmsAdapter>>());

                        if (adapter != null)
                        {
                            _logger.LogInformation("Learned adapter activated: {Pms}, query targets {Table}",
                                adapter.PmsName, adapter.DetectionQuery.Split('\n')[^1].Trim());
                            _db.UpdateLearningPhase(_sessionId, "active");
                            _adapterActivated = true;

                            _db.AppendLearningAudit(_sessionId, "worker", "adapter_activated",
                                adapter.PmsName, phiScrubbed: false);
                        }
                        else
                        {
                            _logger.LogWarning("Adapter generation failed — no viable Rx queue candidate");
                        }
                    }
                }
            }

            // Phase auto-advance is manual for now — operator triggers via signed command
            // Future: auto-advance based on LearningSession.GetNextPhase()
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
    /// v3.12 — runs <see cref="WorkflowTemplateExtractor"/> then
    /// <see cref="TemplateRuleGenerator"/>. Never throws; a failure is logged
    /// and the phase loop continues. Flag-gated on
    /// <c>TemplateLearning.Enabled</c>.
    /// </summary>
    private void TryExtractAndEmitTemplates()
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
                thresholds);
            var extracted = extractor.ExtractAndPersist();

            var rulesRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SuavoAgent", "rules", "auto");
            var generator = new TemplateRuleGenerator(_db, rulesRoot,
                _sp.GetRequiredService<ILogger<TemplateRuleGenerator>>());
            var emitted = generator.EmitPendingRules();

            if (extracted.Count > 0 || emitted > 0)
            {
                _logger.LogInformation(
                    "TemplateLearning: extracted {Extracted} template(s), emitted {Emitted} rule file(s)",
                    extracted.Count, emitted);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TemplateLearning tick failed — continuing");
        }
    }

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

            if (phase == "pattern")
            {
                var result = _applicator.ApplyPatternSeeds(_sessionId!, seedResp);
                if (!result.AlreadyApplied)
                {
                    _activeSeedDigest = seedResp.SeedDigest;
                    _lastSeedDigest = seedResp.SeedDigest;
                    _actionCorrelator?.RegisterSeededShapes(
                        _applicator.GetSeededShapeHashes(seedResp.SeedDigest));
                    _actionCorrelator?.SetActiveSeedDigest(seedResp.SeedDigest);
                    _logger.LogInformation("Applied {Count} pattern seeds from digest {Digest}",
                        result.ItemsApplied, seedResp.SeedDigest);
                }
            }
            else // model
            {
                var result = _applicator.ApplyModelSeeds(_sessionId!, seedResp);
                if (!result.AlreadyApplied)
                {
                    _activeSeedDigest = seedResp.SeedDigest;
                    _lastSeedDigest = seedResp.SeedDigest;
                    _logger.LogInformation(
                        "Applied {Applied} model seeds, skipped {Skipped} from digest {Digest}",
                        result.CorrelationsApplied, result.CorrelationsSkipped, seedResp.SeedDigest);
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
                    _logger.LogInformation(
                        "Applied {Applied} template(s), skipped {Skipped} from digest {Digest}",
                        tplResult.TemplatesApplied, tplResult.TemplatesSkipped, seedResp.SeedDigest);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Seed pull failed at {Phase} entry — continuing without seeds", phase);
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
            $"SELECT DISTINCT {safeColumn} FROM {safeTable} WHERE {safeColumn} IS NOT NULL", conn);
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

    private string BuildConnectionString()
    {
        var csb = new SqlConnectionStringBuilder();
        if (!string.IsNullOrEmpty(_options.SqlServer)) csb.DataSource = _options.SqlServer;
        if (!string.IsNullOrEmpty(_options.SqlDatabase)) csb.InitialCatalog = _options.SqlDatabase;
        csb.ApplicationName = "SuavoAgent";
        csb.MaxPoolSize = 1;
        csb["Encrypt"] = "true";
        csb["TrustServerCertificate"] = _options.SqlTrustServerCertificate.ToString();
        if (!string.IsNullOrEmpty(_options.SqlUser))
        {
            csb.UserID = _options.SqlUser;
            csb.Password = _options.SqlPassword;
        }
        else
        {
            csb.IntegratedSecurity = true;
        }
        return csb.ConnectionString;
    }
}
