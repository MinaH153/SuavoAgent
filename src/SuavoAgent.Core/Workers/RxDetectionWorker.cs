using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SuavoAgent.Adapters.PioneerRx;
using SuavoAgent.Adapters.PioneerRx.Canary;
using SuavoAgent.Adapters.PioneerRx.Sql;
using SuavoAgent.Contracts.Canary;
using SuavoAgent.Core.Canary;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using SuavoAgent.Contracts.Models;
using SuavoAgent.Contracts.Adapters;
using SuavoAgent.Core.Adapters;

namespace SuavoAgent.Core.Workers;

public sealed class RxDetectionWorker : ResilientHostedService
{
    private static readonly JsonSerializerOptions SyncPayloadJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly ILogger<RxDetectionWorker> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly AgentOptions _options;
    private readonly SuavoCloudClient? _cloudClient;
    private readonly AgentStateDb _stateDb;
    private readonly bool _canaryEnabled;
    private readonly IServiceProvider _serviceProvider;
    private readonly AdapterConfig _adapterConfig;
    private PioneerRxSqlEngine? _sqlEngine;
    private PioneerRxCanarySource? _canarySource;
    private PioneerRxWritebackEngine? _writebackEngine;
    private CanaryHoldState _holdState = CanaryHoldState.Clear;
    private SchemaCanaryExportGate? _lastSchemaCanaryExportGate;
    private bool _sqlConnected;
    private bool _loggedNoPmsOnce;
    internal bool LoggedNoPmsOnce => _loggedNoPmsOnce; // test hook

    // SQL reconnect backoff: replaces the old fixed 60s retry so a down PMS isn't probed every
    // minute. Grows 60s→180s cap (was 600s); reset on a successful connect (TryConnectSqlAsync).
    // Capped at 180s so detection recovers within ~3min of a real outage clearing, and so it lines
    // up with the dark-window escalation threshold below.
    private readonly ExponentialBackoff _sqlBackoff =
        new(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(180));

    // B2 — sustained-outage visibility. A down PMS makes RunCycleAsync skip GRACEFULLY (no throw),
    // so the worker supervisor's OnEscalateAsync never fires; without this the heartbeat shows only
    // a quiet sqlConnected=false and a pharmacy can go dark on delivery-ready detection with no
    // operator alarm. Track when SQL first went down + consecutive real connect failures, expose an
    // explicit `degraded` signal for the heartbeat, and log CRITICAL exactly once past the threshold.
    internal static readonly TimeSpan SqlDarkEscalationThreshold = TimeSpan.FromSeconds(180);
    private int _consecutiveSqlFailures;
    private DateTimeOffset? _sqlDownSince;
    private bool _degradedLogged;

    public int DetectionIntervalSeconds { get; set; } = 300;
    public int LastDetectedCount { get; private set; }
    public DateTimeOffset? LastDetectionTime { get; private set; }
    public bool IsSqlConnected => _sqlConnected;
    public int ConsecutiveSqlFailures => _consecutiveSqlFailures;
    public DateTimeOffset? SqlDownSince => _sqlDownSince;

    /// <summary>True when SQL has been down past the escalation threshold — a real detection outage,
    /// not a transient blip or a no-PMS dev box. Surfaced to the heartbeat as `rxDetectionDegraded`.</summary>
    public bool IsDetectionDegraded(DateTimeOffset now) =>
        _sqlDownSince is { } since && now - since >= SqlDarkEscalationThreshold;

    /// <summary>Seconds SQL has been continuously down (0 when connected), for heartbeat telemetry.</summary>
    public int SqlDarkSeconds(DateTimeOffset now) =>
        _sqlDownSince is { } since ? (int)Math.Max(0, (now - since).TotalSeconds) : 0;
    public PioneerRxSqlEngine? SqlEngine => _sqlEngine;
    public PioneerRxWritebackEngine? WritebackEngine => _writebackEngine;
    internal SchemaCanaryExportGate SnapshotSchemaCanaryExportGate() =>
        _canarySource is null
            ? SchemaCanaryExportGate.NotRecorded("schema_canary_unavailable")
            : _lastSchemaCanaryExportGate ?? SchemaCanaryExportGate.NotRecorded("schema_canary_not_recorded");

    public RxDetectionWorker(
        ILogger<RxDetectionWorker> logger,
        ILoggerFactory loggerFactory,
        IOptions<AgentOptions> options,
        AgentStateDb stateDb,
        IServiceProvider serviceProvider,
        WorkerHealthRegistry? healthRegistry = null)
        : base(logger, healthRegistry)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _options = options.Value;
        _stateDb = stateDb;
        _serviceProvider = serviceProvider;
        _cloudClient = serviceProvider.GetService<SuavoCloudClient>();
        _adapterConfig = serviceProvider.GetService<IAdapterRegistry>()?.Default ?? PioneerRxAdapterConfig.Create();
        _canaryEnabled = !_options.LearningMode;
    }

    protected override string WorkerName => "rx-detection";
    protected override bool RestartOnFault => _options.SelfHeal.WorkerSupervisorEnabled;

    protected override Task OnEscalateAsync()
    {
        // Exhausted in-process restarts: mark disconnected so the heartbeat reports degraded and
        // log CRITICAL — the cloud silent-agent / health-watch surfaces it for repair.
        _sqlConnected = false;
        _logger.LogCritical(
            "RxDetectionWorker exhausted supervised restarts — detection halted, awaiting repair");
        return Task.CompletedTask;
    }

    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Rx detection worker started (canary={Canary})", _canaryEnabled);

        _stateDb.PurgeExpiredDeadLetters();

        await TryConnectSqlAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Rx detection cycle failed");
                _sqlConnected = false;
            }

            await Task.Delay(TimeSpan.FromSeconds(DetectionIntervalSeconds), stoppingToken);
        }

        _sqlEngine?.Dispose();
        _logger.LogInformation("Rx detection worker stopped");
    }

    internal async Task RunCycleAsync(CancellationToken ct)
    {
        if (!_sqlConnected)
        {
            await TryConnectSqlAsync(ct);
            if (!_sqlConnected)
            {
                var now = DateTimeOffset.UtcNow;
                if (IsDetectionDegraded(now) && !_degradedLogged)
                {
                    _degradedLogged = true;
                    _logger.LogCritical(
                        "Rx detection DARK for {Seconds}s ({Failures} consecutive SQL failures) — pharmacy " +
                        "not receiving delivery-ready detection; heartbeat now reports rxDetectionDegraded=true",
                        SqlDarkSeconds(now), _consecutiveSqlFailures);
                }
                var backoff = _sqlBackoff.NextDelay();
                _logger.LogDebug("SQL not connected, skipping detection cycle (retry in {Delay}s)", backoff.TotalSeconds);
                await Task.Delay(backoff, ct);
                return;
            }
        }

        if (_canarySource != null)
            await RunCanaryDetectionAsync(ct);
        else
            await RunLegacyDetectionAsync(ct);
    }

    private async Task RunLegacyDetectionAsync(CancellationToken ct)
    {
        // Retry persisted unsynced batches first
        await RetryPendingBatchesAsync(ct);

        // PHI-free detection: metadata only, no Person JOIN (HIPAA 164.502(b))
        var readyRxs = await _sqlEngine!.ReadReadyMetadataAsync(ct);
        LastDetectedCount = readyRxs.Count;
        LastDetectionTime = DateTimeOffset.UtcNow;

        if (readyRxs.Count > 0)
        {
            _logger.LogInformation("Detected {Count} ready prescriptions", readyRxs.Count);

            var hmacSalt = _options.HmacSalt ?? "[no-hmac-salt]";
            var patientMap = await EnrichPatientDetailsAsync(readyRxs, hmacSalt, ct);

            var json = SerializeRxBatch(
                readyRxs,
                hmacSalt,
                patientMap,
                pharmacyId: _options.PharmacyId,
                agentInstallId: _options.AgentId,
                includeLegacyDeliveryQueue: _options.EnableLegacyPhiDeliveryQueueSync);
            if (!await TrySyncPayloadToCloudAsync(json, ct))
                _stateDb.InsertUnsyncedBatch(json);
        }
        else
        {
            _logger.LogDebug("No ready prescriptions found");
        }
    }

    private async Task RunCanaryDetectionAsync(CancellationToken ct)
    {
        var pharmacyId = _options.PharmacyId ?? "unknown";
        var adapterType = _canarySource!.AdapterType;

        // ── Load or establish baseline (errata E1) ──
        var baseline = _stateDb.GetCanaryBaseline(pharmacyId, adapterType);
        if (baseline is null)
        {
            _logger.LogInformation("Canary: no baseline — establishing from observed schema");

            ContractBaseline establishedBaseline;
            try
            {
                establishedBaseline = await _canarySource.EstablishBaselineAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Canary: baseline establishment failed — cannot verify PioneerRx schema");
                return;
            }

            var preflight = await _canarySource.VerifyPreflightAsync(establishedBaseline, ct);
            RecordSchemaCanaryGate(preflight);
            if (!preflight.IsValid)
            {
                _logger.LogWarning(
                    "Canary: observed baseline verification failed during establishment ({Severity})",
                    preflight.Severity);
                return;
            }

            _stateDb.UpsertCanaryBaseline(pharmacyId, establishedBaseline);
            _stateDb.AppendChainedAuditEntry(new AuditEntry(
                pharmacyId, "canary", "", "established", "baseline_established"));

            // First batch syncs normally — no drift possible on establishment cycle
            var result = await _canarySource.DetectWithCanaryAsync(establishedBaseline, ct);
            if (result.Rxs.Count > 0)
            {
                var hmacSalt = _options.HmacSalt ?? "[no-hmac-salt]";
                var patientMap = await EnrichPatientDetailsAsync(result.Rxs, hmacSalt, ct);
                RecordSchemaCanaryGate(result.PostflightVerification);
                var json = SerializeRxBatch(
                    result.Rxs,
                    hmacSalt,
                    patientMap,
                    schemaVerification: result.PostflightVerification,
                    pharmacyId: _options.PharmacyId,
                    agentInstallId: _options.AgentId,
                    includeLegacyDeliveryQueue: _options.EnableLegacyPhiDeliveryQueueSync);
                if (!await TrySyncPayloadToCloudAsync(json, ct))
                    _stateDb.InsertUnsyncedBatch(json);
            }

            LastDetectedCount = result.Rxs.Count;
            LastDetectionTime = DateTimeOffset.UtcNow;
            return;
        }

        // ── Retry pending batches (only when not in hold) ──
        // ── Restore hold state from DB (survives restarts) ──
        var holdRecord = _stateDb.GetCanaryHold(pharmacyId, adapterType);
        if (holdRecord != null)
        {
            _holdState = new CanaryHoldState(true, CanarySeverity.Critical,
                holdRecord.Value.BlockedCycles, 0, null);
        }

        // ── Detect with canary ──
        var detection = await _canarySource.DetectWithCanaryAsync(baseline, ct);
        var verification = detection.PostflightVerification;
        RecordSchemaCanaryGate(verification);

        // ── Escalation state machine ──
        _holdState = SchemaCanaryEscalation.Transition(_holdState, verification.Severity);

        if (_holdState.IsInHold)
        {
            _stateDb.UpsertCanaryHold(pharmacyId, adapterType,
                _holdState.EffectiveSeverity.ToString().ToLowerInvariant(),
                baseline.ContractFingerprint);
            _stateDb.IncrementCanaryHoldCycles(pharmacyId, adapterType);
            _stateDb.InsertCanaryIncident(pharmacyId, adapterType,
                verification.Severity.ToString().ToLowerInvariant(),
                JsonSerializer.Serialize(verification.DriftedComponents),
                baseline.ContractFingerprint,
                verification.ObservedHash ?? "",
                verification.Details,
                detection.Rxs.Count);

            _logger.LogWarning("CANARY: drift — batch dropped, hold active ({Cycles} blocked)",
                _holdState.BlockedCycles);
            LastDetectedCount = 0;
            LastDetectionTime = DateTimeOffset.UtcNow;
            return;
        }

        // ── Clean — clear any prior hold ──
        if (verification.Severity == CanarySeverity.None && holdRecord != null)
        {
            _stateDb.ClearCanaryHold(pharmacyId, adapterType);
            _holdState = CanaryHoldState.Clear;
            _logger.LogInformation("Canary: hold cleared — schema verified clean");
        }

        // ── Retry pending batches before syncing new ──
        await RetryPendingBatchesAsync(ct);

        // ── Sync batch normally ──
        if (detection.Rxs.Count > 0)
        {
            _logger.LogInformation("Canary: {Count} ready prescriptions — schema verified clean", detection.Rxs.Count);
            var hmacSalt = _options.HmacSalt ?? "[no-hmac-salt]";
            var patientMap = await EnrichPatientDetailsAsync(detection.Rxs, hmacSalt, ct);
            var json = SerializeRxBatch(
                detection.Rxs,
                hmacSalt,
                patientMap,
                schemaVerification: detection.PostflightVerification,
                pharmacyId: _options.PharmacyId,
                agentInstallId: _options.AgentId,
                includeLegacyDeliveryQueue: _options.EnableLegacyPhiDeliveryQueueSync);
            if (!await TrySyncPayloadToCloudAsync(json, ct))
                _stateDb.InsertUnsyncedBatch(json);
        }
        else
        {
            _logger.LogDebug("Canary: no ready prescriptions found");
        }

        LastDetectedCount = detection.Rxs.Count;
        LastDetectionTime = DateTimeOffset.UtcNow;
    }

    private async Task RetryPendingBatchesAsync(CancellationToken ct)
    {
        var pendingBatches = _stateDb.GetPendingBatches();
        if (pendingBatches.Count > 0)
        {
            _logger.LogInformation("Retrying {Count} persisted unsynced batches", pendingBatches.Count);
            foreach (var batch in pendingBatches)
            {
                if (await TrySyncPayloadToCloudAsync(batch.Payload, ct))
                    _stateDb.DeleteBatch(batch.Id);
                else
                    _stateDb.IncrementBatchRetry(batch.Id);
            }
        }
    }

    private async Task<IReadOnlyDictionary<string, RxPatientDetails>> EnrichPatientDetailsAsync(
        IReadOnlyList<RxMetadata> readyRxs,
        string hmacSalt,
        CancellationToken ct)
    {
        var patientMap = new Dictionary<string, RxPatientDetails>();
        if (_sqlEngine is null || readyRxs.Count == 0)
            return patientMap;

        var failures = 0;
        foreach (var rx in readyRxs)
        {
            var rxHash = PhiScrubber.HmacHash(rx.RxNumber, hmacSalt);

            // HIPAA §164.312(b): audit before PHI access so a crash mid-read
            // still leaves local evidence that patient fields were requested.
            _stateDb.AppendChainedAuditEntry(new AuditEntry(
                TaskId: rxHash,
                EventType: "phi_access",
                FromState: "",
                ToState: "",
                Trigger: "rx_detection_worker.enrich_for_delivery_sync",
                RequesterId: "rx_detection_worker",
                RxNumber: rxHash));

            try
            {
                var patientDetails = await _sqlEngine.PullPatientForRxAsync(rx.RxNumber, ct);
                if (patientDetails != null)
                    patientMap[rx.RxNumber] = patientDetails;
            }
            catch (Exception ex)
            {
                failures++;
                _logger.LogWarning(ex, "Patient detail enrichment failed for one Rx; continuing without patient fields");
            }
        }

        if (failures > 0)
        {
            _logger.LogWarning(
                "Patient detail enrichment completed with {Failures}/{Total} failures",
                failures,
                readyRxs.Count);
        }
        else
        {
            _logger.LogDebug(
                "Enriched {Count}/{Total} Rxs with patient details",
                patientMap.Count,
                readyRxs.Count);
        }

        return patientMap;
    }

    // B2 state transitions — internal so tests can drive the degraded state machine without a live PMS.
    internal void MarkSqlConnected()
    {
        _sqlConnected = true;
        _consecutiveSqlFailures = 0;
        _sqlDownSince = null;
        _degradedLogged = false;
        _sqlBackoff.Reset();
    }

    internal void MarkSqlConnectFailed(DateTimeOffset now)
    {
        _sqlConnected = false;
        _consecutiveSqlFailures++;
        _sqlDownSince ??= now; // first failure of this outage stamps when it went dark
    }

    // No PMS on this host (dev box / sandbox): not connected, but NOT an outage — clear any dark state
    // so a machine without PioneerRx never reports `degraded`.
    private void MarkSqlNotApplicable()
    {
        _sqlConnected = false;
        _consecutiveSqlFailures = 0;
        _sqlDownSince = null;
        _degradedLogged = false;
    }

    private async Task TryConnectSqlAsync(CancellationToken ct)
    {
        // No-PMS short-circuit: skip the 30s SqlConnection.OpenAsync timeout
        // (+ warning-log noise that counts toward error_event_count_24h) on
        // sandboxes and dev workstations where PioneerRx isn't installed at
        // all. Fail-open inside the detector handles the registry-permissions
        // edge case. We log the skip once per worker lifetime so log volume
        // stays bounded.
        if (!PioneerRxInstallDetector.IsInstalled(_logger))
        {
            MarkSqlNotApplicable();
            if (!_loggedNoPmsOnce)
            {
                _logger.LogInformation(
                    "PioneerRx not installed on this host — skipping SQL detection (no-PMS mode)");
                _loggedNoPmsOnce = true;
            }
            return;
        }

        var server = _options.SqlServer ?? "localhost";
        var database = AdapterCatalog.Resolve(_options.SqlDatabase, _adapterConfig);

        _sqlEngine?.Dispose();
        // The canary source is bound to the engine instance. Disposing+replacing the engine on a
        // reconnect (now more frequent under the worker supervisor) would otherwise leave the
        // `_canarySource == null` guard below holding a source bound to the disposed engine — so
        // clear it here to force a rebuild against the new engine.
        _canarySource = null;
        _sqlEngine = new PioneerRxSqlEngine(
            server, database,
            _loggerFactory.CreateLogger<PioneerRxSqlEngine>(),
            _options.SqlUser, _options.SqlPassword, _options.SqlTrustServerCertificate);

        var connected = await _sqlEngine.TryConnectAsync(ct);

        if (connected)
        {
            MarkSqlConnected();
            _logger.LogInformation("SQL connected to {Server}/{Db}", server, database);
            await SyncSchemaDiscoveryAsync(ct);

            // Create canary source after successful SQL connection
            if (_canaryEnabled && _canarySource == null)
            {
                _canarySource = new PioneerRxCanarySource(_sqlEngine,
                    _loggerFactory.CreateLogger<PioneerRxCanarySource>());
                _logger.LogInformation("Canary detection source initialized for PioneerRx");
            }

            // Create writeback engine with separate connection pool
            if (_sqlConnected && _sqlEngine != null)
            {
                var allGuids = _sqlEngine.GetAllDiscoveredGuids();
                if (allGuids != null && allGuids.Count >= 5)
                {
                    var writebackCsb = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder();
                    if (!string.IsNullOrEmpty(_options.SqlServer)) writebackCsb.DataSource = _options.SqlServer;
                    if (!string.IsNullOrEmpty(_options.SqlDatabase)) writebackCsb.InitialCatalog = _options.SqlDatabase;
                    writebackCsb.ApplicationName = "SuavoWriteback";
                    writebackCsb.MaxPoolSize = 1;
                    writebackCsb["Encrypt"] = "true";
                    writebackCsb["TrustServerCertificate"] = _options.SqlTrustServerCertificate.ToString();
                    if (!string.IsNullOrEmpty(_options.SqlUser))
                    {
                        writebackCsb.UserID = _options.SqlUser;
                        writebackCsb.Password = _options.SqlPassword;
                    }
                    else
                    {
                        writebackCsb.IntegratedSecurity = true;
                    }

                    _writebackEngine = new PioneerRxWritebackEngine(
                        writebackCsb.ConnectionString,
                        allGuids,
                        _loggerFactory.CreateLogger<PioneerRxWritebackEngine>());

                    await _writebackEngine.DetectTriggersAsync(ct);
                    _logger.LogInformation("Writeback engine created (enabled={Enabled})", _writebackEngine.WritebackEnabled);

                    // Attach to WritebackProcessor if available
                    var processor = _serviceProvider.GetService<WritebackProcessor>();
                    processor?.SetWritebackEngine(_writebackEngine);
                }
                else
                {
                    _logger.LogWarning("Writeback engine NOT created — insufficient status GUIDs ({Count}/5)",
                        allGuids?.Count ?? 0);
                }
            }
        }
        else
        {
            MarkSqlConnectFailed(DateTimeOffset.UtcNow);
            _logger.LogWarning("SQL connection failed for {Server}/{Db}", server, database);
            _canarySource = null;
        }
    }

    private async Task SyncSchemaDiscoveryAsync(CancellationToken ct)
    {
        if (_cloudClient is null || _sqlEngine is null) return;

        try
        {
            var schema = await _sqlEngine.DiscoverSchemaAsync(ct);
            if (schema.Count == 0) return;

            var payload = new
            {
                snapshotType = "schema_discovery",
                data = new
                {
                    tables = schema.ToDictionary(
                        kv => kv.Key,
                        kv => (object)kv.Value),
                    discoveredAt = DateTimeOffset.UtcNow.ToString("o")
                },
                sqlConnected = true
            };

            await _cloudClient.SyncRxAsync(payload, ct);
            _logger.LogInformation("Schema discovery synced: {Count} tables", schema.Count);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Schema discovery sync failed — non-critical");
        }
    }

    /// <summary>
    /// Serializes Rx batch with optional patient delivery details.
    /// Plain Rx numbers never leave the agent. The canonical
    /// rxOrderCandidates payload uses a per-pharmacy HMAC key and carries
    /// field provenance so the cloud can distinguish PHI ingestion from
    /// telemetry/model-prompt data.
    /// </summary>
    internal static string SerializeRxBatch(
        IReadOnlyList<RxMetadata> rxs,
        string hmacSalt = "",
        IReadOnlyDictionary<string, RxPatientDetails>? patientDetails = null,
        ContractVerification? schemaVerification = null,
        string? pharmacyId = null,
        string? agentInstallId = null,
        string? pmsVersion = null,
        string hashKeyVersion = "local-hmac-v1",
        DateTimeOffset? serializedAtUtc = null,
        bool includeLegacyDeliveryQueue = false)
    {
        var serializedAt = serializedAtUtc ?? DateTimeOffset.UtcNow;
        var scanWindowId = $"rxscan-{serializedAt.ToUnixTimeMilliseconds()}";
        var candidates = rxs.Select(rx =>
        {
            var pd = patientDetails != null && patientDetails.TryGetValue(rx.RxNumber, out var p) ? p : null;
            var rxHash = HashRxNumber(rx.RxNumber, hmacSalt);
            var warnings = BuildCandidateWarnings(pd, schemaVerification);
            var isControlled = rx.DrugSchedule is >= 2 and <= 5;
            const DetectionSource source = DetectionSource.Sql;
            const string schemaSignature = "pioneerrx.sql.metadata.v1";
            var localEvidenceId = BuildLocalEvidenceId(rxHash, rx.DetectedAt);
            var patientDelivery = BuildPatientDelivery(pd, hmacSalt);
            var fieldConfidence = BuildCandidateFieldConfidence(rx, pd);
            var fieldProvenance = BuildCandidateProvenance(rx, pd, source, schemaSignature, localEvidenceId);
            var confidence = ComputeCandidateConfidence(fieldConfidence, fieldProvenance, schemaVerification);

            return new RxOrderCandidate(
                RxHash: rxHash,
                Medication: new RxOrderMedication(
                    NameHash: HashPhi(rx.DrugName, hmacSalt),
                    Ndc: rx.Ndc,
                    Strength: null,
                    Form: null,
                    Quantity: rx.Quantity,
                    DaysSupply: rx.DaysSupply,
                    Refills: rx.FillNumber,
                    IsControlled: isControlled,
                    DrugSchedule: rx.DrugSchedule,
                    PatientIdRequired: isControlled && rx.DrugSchedule <= 3,
                    CounselingRequired: false,
                    Priority: rx.Priority,
                    TemperatureRequirement: rx.TemperatureRequirement),
                PatientDelivery: patientDelivery,
                Provenance: new RxOrderCandidateProvenance(
                    PharmacyId: pharmacyId,
                    AgentInstallId: agentInstallId,
                    EvidenceId: localEvidenceId,
                    Pms: "PioneerRx",
                    PmsVersion: pmsVersion,
                    ExtractionMethod: source,
                    CapturedAtUtc: rx.DetectedAt,
                    ScanWindowId: scanWindowId,
                    SchemaSignature: schemaSignature,
                    WindowSignature: null,
                    HashKeyVersion: hashKeyVersion),
                Confidence: confidence,
                FieldConfidence: fieldConfidence,
                FieldProvenance: fieldProvenance,
                Warnings: warnings,
                SchemaVersion: 1);
        }).ToArray();

        var data = new Dictionary<string, object?>
        {
            ["rxOrderCandidates"] = candidates,
            ["totalDetected"] = rxs.Count,
            ["syncedAt"] = serializedAt.ToString("o")
        };

        if (includeLegacyDeliveryQueue)
        {
            // Track 3 invariant (Codex CRITICAL #15, closed 2026-05-12):
            // the legacy rxDeliveryQueue ships ONLY operational metadata.
            // PHI fields (patient name/phone/address) are intentionally
            // absent — they were silently dropped cloud-side by
            // sanitizeSnapshotData anyway, and HIPAA minimum-necessary
            // forbids putting them on the wire in the first place.
            // Patient delivery details flow exclusively through the typed,
            // signed-command path SuavoCloudClient.SendPatientDetailsAsync.
            data["rxDeliveryQueue"] = rxs.Select(rx => new
            {
                rxNumber = HashRxNumber(rx.RxNumber, hmacSalt),
                drugName = rx.DrugName,
                ndc = rx.Ndc,
                dateFilled = rx.DateFilled?.ToString("o"),
                quantity = rx.Quantity,
                statusGuid = rx.StatusGuid.ToString(),
                detectedAt = rx.DetectedAt.ToString("o"),
            }).ToArray();
        }

        var payload = new
        {
            snapshotType = "rx_delivery_queue",
            data,
            sqlConnected = true
        };

        return JsonSerializer.Serialize(payload, SyncPayloadJsonOptions);
    }

    private static string HashRxNumber(string rxNumber, string hmacSalt) =>
        !string.IsNullOrEmpty(hmacSalt)
            ? PhiScrubber.HmacHash(rxNumber, hmacSalt)
            : PhiScrubber.HmacHash(rxNumber, "[no-hmac-salt]");

    private static string? HashPhi(string? value, string hmacSalt)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = string.Join(
            " ",
            value.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return PhiScrubber.HmacHash(
            normalized,
            !string.IsNullOrEmpty(hmacSalt) ? hmacSalt : "[no-hmac-salt]");
    }

    private static RxOrderPatientDelivery BuildPatientDelivery(RxPatientDetails? pd, string hmacSalt)
    {
        var flags = BuildMissingAddressFlags(pd);
        var zipDigits = new string((pd?.Zip ?? "").Where(char.IsDigit).Take(9).ToArray());
        var zip5 = zipDigits.Length >= 5 ? zipDigits[..5] : null;
        var zip4Present = zipDigits.Length >= 9;
        var nameBasis = string.Join(" ", new[] { pd?.FirstName, pd?.LastInitial }
            .Where(v => !string.IsNullOrWhiteSpace(v)));

        return new RxOrderPatientDelivery(
            NameHash: HashPhi(nameBasis, hmacSalt),
            AddressLine1Hash: HashPhi(pd?.Address1, hmacSalt),
            AddressLine2Hash: HashPhi(pd?.Address2, hmacSalt),
            City: string.IsNullOrWhiteSpace(pd?.City) ? null : pd!.City!.Trim(),
            State: string.IsNullOrWhiteSpace(pd?.State) ? null : pd!.State!.Trim().ToUpperInvariant(),
            Zip5: zip5,
            Zip4Present: zip4Present,
            PhoneHash: HashPhi(pd?.Phone, hmacSalt),
            MissingAddressFlags: flags);
    }

    private static string BuildLocalEvidenceId(string rxHash, DateTimeOffset detectedAt)
    {
        var shortHash = rxHash.Length >= 16 ? rxHash[..16] : rxHash;
        return $"rxh-{shortHash}-{detectedAt.ToUnixTimeSeconds()}";
    }

    private static List<RxOrderCandidateWarning> BuildCandidateWarnings(
        RxPatientDetails? pd,
        ContractVerification? schemaVerification = null)
    {
        var warnings = new List<RxOrderCandidateWarning>();
        if (string.IsNullOrWhiteSpace(pd?.FirstName) || string.IsNullOrWhiteSpace(pd?.LastInitial))
            warnings.Add(RxOrderCandidateWarning.MissingPatientIdentity);
        if (string.IsNullOrWhiteSpace(pd?.Address1) ||
            string.IsNullOrWhiteSpace(pd?.City) ||
            string.IsNullOrWhiteSpace(pd?.State) ||
            string.IsNullOrWhiteSpace(pd?.Zip))
        {
            warnings.Add(RxOrderCandidateWarning.MissingDeliveryAddress);
        }
        if (BuildMissingAddressFlags(pd).Contains(RxMissingAddressFlag.MissingZip5))
        {
            warnings.Add(RxOrderCandidateWarning.MissingZip5);
        }
        if (schemaVerification?.Severity == CanarySeverity.Warning)
        {
            warnings.Add(RxOrderCandidateWarning.SchemaCanaryDrift);
            foreach (var component in schemaVerification.DriftedComponents)
            {
                var normalized = new string(component
                    .Where(ch => char.IsLetterOrDigit(ch) || ch == '_')
                    .Select(char.ToLowerInvariant)
                    .ToArray());
                if (normalized == "object")
                    warnings.Add(RxOrderCandidateWarning.SchemaCanaryObject);
                else if (normalized == "column")
                    warnings.Add(RxOrderCandidateWarning.SchemaCanaryColumn);
                else if (normalized == "index")
                    warnings.Add(RxOrderCandidateWarning.SchemaCanaryIndex);
                else if (normalized == "type")
                    warnings.Add(RxOrderCandidateWarning.SchemaCanaryType);
            }
        }
        return warnings;
    }

    private void RecordSchemaCanaryGate(ContractVerification verification)
    {
        _lastSchemaCanaryExportGate = new SchemaCanaryExportGate(
            Status: verification.Severity == CanarySeverity.None && verification.IsValid ? "pass" : "fail",
            Severity: verification.Severity.ToString().ToLowerInvariant(),
            Recorded: true,
            Passing: verification.Severity == CanarySeverity.None && verification.IsValid,
            BaselineHash: verification.BaselineHash,
            ObservedHash: verification.ObservedHash,
            DriftedComponents: verification.DriftedComponents,
            Details: verification.Details,
            RecordedAtUtc: DateTimeOffset.UtcNow.ToString("o"));
    }

    private static List<RxMissingAddressFlag> BuildMissingAddressFlags(RxPatientDetails? pd)
    {
        var flags = new List<RxMissingAddressFlag>();
        if (string.IsNullOrWhiteSpace(pd?.FirstName) || string.IsNullOrWhiteSpace(pd?.LastInitial))
            flags.Add(RxMissingAddressFlag.MissingName);
        if (string.IsNullOrWhiteSpace(pd?.Phone))
            flags.Add(RxMissingAddressFlag.MissingPhone);
        if (string.IsNullOrWhiteSpace(pd?.Address1))
            flags.Add(RxMissingAddressFlag.MissingAddressLine1);
        if (string.IsNullOrWhiteSpace(pd?.City))
            flags.Add(RxMissingAddressFlag.MissingCity);
        if (string.IsNullOrWhiteSpace(pd?.State))
            flags.Add(RxMissingAddressFlag.MissingState);

        var zipDigits = new string((pd?.Zip ?? "").Where(char.IsDigit).Take(9).ToArray());
        if (zipDigits.Length < 5)
            flags.Add(RxMissingAddressFlag.MissingZip5);

        return flags;
    }

    private static Dictionary<string, double> BuildCandidateFieldConfidence(RxMetadata rx, RxPatientDetails? pd)
    {
        return new Dictionary<string, double>
        {
            ["rxHash"] = 1.0d,
            ["medication.nameHash"] = string.IsNullOrWhiteSpace(rx.DrugName) ? 0.4d : 1.0d,
            ["medication.ndc"] = string.IsNullOrWhiteSpace(rx.Ndc) ? 0.4d : 1.0d,
            ["medication.quantity"] = rx.Quantity > 0 ? 1.0d : 0.4d,
            ["medication.daysSupply"] = rx.DaysSupply > 0 ? 1.0d : 0.4d,
            ["patientDelivery.nameHash"] =
                string.IsNullOrWhiteSpace(pd?.FirstName) || string.IsNullOrWhiteSpace(pd?.LastInitial) ? 0.4d : 1.0d,
            ["patientDelivery.addressLine1Hash"] = string.IsNullOrWhiteSpace(pd?.Address1) ? 0.4d : 1.0d,
            ["patientDelivery.city"] = string.IsNullOrWhiteSpace(pd?.City) ? 0.4d : 1.0d,
            ["patientDelivery.state"] = string.IsNullOrWhiteSpace(pd?.State) ? 0.4d : 1.0d,
            ["patientDelivery.zip5"] = BuildMissingAddressFlags(pd).Contains(RxMissingAddressFlag.MissingZip5) ? 0.4d : 1.0d,
            ["patientDelivery.phoneHash"] = string.IsNullOrWhiteSpace(pd?.Phone) ? 0.4d : 1.0d,
        };
    }

    private static double ComputeCandidateConfidence(
        IReadOnlyDictionary<string, double> fieldConfidence,
        IReadOnlyDictionary<string, RxFieldProvenance> fieldProvenance,
        ContractVerification? schemaVerification)
    {
        var sourceCompleteness = fieldConfidence.Count == 0
            ? 0d
            : fieldConfidence.Values.Select(value => Math.Clamp(value, 0d, 1d)).Average();
        var provenanceQuality = fieldConfidence.Count == 0
            ? 0d
            : fieldConfidence.Keys
                .Select(key => fieldProvenance.TryGetValue(key, out var provenance)
                    ? Math.Clamp(provenance.Confidence, 0d, 1d)
                    : 0d)
                .Average();
        var provenanceCoverage = fieldConfidence.Count == 0
            ? 0d
            : fieldConfidence.Keys
                .Select(key => fieldProvenance.ContainsKey(key) ? 1d : 0d)
                .Average();
        var schemaScore = schemaVerification?.Severity switch
        {
            null => 1.0d,
            CanarySeverity.None when schemaVerification.IsValid => 1.0d,
            CanarySeverity.Warning => 0.72d,
            CanarySeverity.Critical => 0.0d,
            _ => 0.5d,
        };

        var confidence =
            (sourceCompleteness * 0.55d) +
            (provenanceQuality * 0.20d) +
            (provenanceCoverage * 0.10d) +
            (schemaScore * 0.15d);
        return Math.Clamp(confidence, 0d, 1d);
    }

    private static Dictionary<string, RxFieldProvenance> BuildCandidateProvenance(
        RxMetadata rx,
        RxPatientDetails? pd,
        DetectionSource source,
        string schemaSignature,
        string localEvidenceId)
    {
        RxFieldProvenance SqlOperational(double confidence = 1.0d) => new(
            Source: source,
            SourceDetail: "sql_metadata",
            Confidence: confidence,
            Classification: "operational",
            EvidenceId: localEvidenceId,
            Signature: schemaSignature);

        RxFieldProvenance SqlPhi(double confidence = 1.0d) => new(
            Source: source,
            SourceDetail: "sql_patient_detail",
            Confidence: confidence,
            Classification: "phi-direct",
            EvidenceId: localEvidenceId,
            Signature: schemaSignature);

        var provenance = new Dictionary<string, RxFieldProvenance>
        {
            ["rxHash"] = new(
                Source: source,
                SourceDetail: "sql_metadata",
                Confidence: 1.0d,
                Classification: "phi-direct-hmac",
                EvidenceId: localEvidenceId,
                Signature: schemaSignature),
            ["medication.nameHash"] = new(
                Source: source,
                SourceDetail: "sql_metadata",
                Confidence: string.IsNullOrWhiteSpace(rx.DrugName) ? 0.4d : 1.0d,
                Classification: "phi-direct-hmac",
                EvidenceId: localEvidenceId,
                Signature: schemaSignature),
            ["medication.ndc"] = SqlOperational(string.IsNullOrWhiteSpace(rx.Ndc) ? 0.4d : 1.0d),
            ["medication.quantity"] = SqlOperational(rx.Quantity > 0 ? 1.0d : 0.4d),
            ["medication.refills"] = SqlOperational(),
            ["medication.daysSupply"] = SqlOperational(rx.DaysSupply > 0 ? 1.0d : 0.4d),
            ["medication.drugSchedule"] = SqlOperational(rx.DrugSchedule.HasValue ? 1.0d : 0.4d),
        };

        if (pd != null)
        {
            provenance["patientDelivery.nameHash"] =
                SqlPhi(string.IsNullOrWhiteSpace(pd.FirstName) || string.IsNullOrWhiteSpace(pd.LastInitial) ? 0.4d : 1.0d);
            provenance["patientDelivery.phoneHash"] = SqlPhi(string.IsNullOrWhiteSpace(pd.Phone) ? 0.4d : 1.0d);
            provenance["patientDelivery.addressLine1Hash"] = SqlPhi(string.IsNullOrWhiteSpace(pd.Address1) ? 0.4d : 1.0d);
            provenance["patientDelivery.addressLine2Hash"] = SqlPhi(string.IsNullOrWhiteSpace(pd.Address2) ? 0.7d : 1.0d);
            provenance["patientDelivery.city"] = SqlPhi(string.IsNullOrWhiteSpace(pd.City) ? 0.4d : 1.0d);
            provenance["patientDelivery.state"] = SqlPhi(string.IsNullOrWhiteSpace(pd.State) ? 0.4d : 1.0d);
            provenance["patientDelivery.zip5"] = SqlPhi(BuildMissingAddressFlags(pd).Contains(RxMissingAddressFlag.MissingZip5) ? 0.4d : 1.0d);
        }

        return provenance;
    }

    private async Task<bool> TrySyncPayloadToCloudAsync(string json, CancellationToken ct)
    {
        if (_cloudClient is null) return true; // no cloud = nothing to sync

        try
        {
            var payload = JsonSerializer.Deserialize<JsonElement>(json);
            await _cloudClient.SyncRxAsync(payload, ct);
            _logger.LogInformation("Synced batch to cloud");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cloud sync FAILED — will retry next cycle");
            return false;
        }
    }
}

internal sealed record SchemaCanaryExportGate(
    string Status,
    string Severity,
    bool Recorded,
    bool Passing,
    string? BaselineHash,
    string? ObservedHash,
    IReadOnlyList<string> DriftedComponents,
    string? Details,
    string? RecordedAtUtc)
{
    public static SchemaCanaryExportGate NotRecorded(string status) => new(
        Status: status,
        Severity: "unknown",
        Recorded: false,
        Passing: false,
        BaselineHash: null,
        ObservedHash: null,
        DriftedComponents: Array.Empty<string>(),
        Details: null,
        RecordedAtUtc: null);
}
