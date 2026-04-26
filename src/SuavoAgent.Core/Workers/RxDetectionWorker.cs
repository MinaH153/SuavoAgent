using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SuavoAgent.Adapters.PioneerRx.Canary;
using SuavoAgent.Adapters.PioneerRx.Sql;
using SuavoAgent.Contracts.Canary;
using SuavoAgent.Core.Canary;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using SuavoAgent.Contracts.Models;

namespace SuavoAgent.Core.Workers;

public sealed class RxDetectionWorker : BackgroundService
{
    private readonly ILogger<RxDetectionWorker> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly AgentOptions _options;
    private readonly SuavoCloudClient? _cloudClient;
    private readonly AgentStateDb _stateDb;
    private readonly bool _canaryEnabled;
    private readonly IServiceProvider _serviceProvider;
    private PioneerRxSqlEngine? _sqlEngine;
    private PioneerRxCanarySource? _canarySource;
    private PioneerRxWritebackEngine? _writebackEngine;
    private CanaryHoldState _holdState = CanaryHoldState.Clear;
    private bool _sqlConnected;

    public int DetectionIntervalSeconds { get; set; } = 300;
    public int LastDetectedCount { get; private set; }
    public DateTimeOffset? LastDetectionTime { get; private set; }
    public bool IsSqlConnected => _sqlConnected;
    public PioneerRxSqlEngine? SqlEngine => _sqlEngine;
    public PioneerRxWritebackEngine? WritebackEngine => _writebackEngine;

    public RxDetectionWorker(
        ILogger<RxDetectionWorker> logger,
        ILoggerFactory loggerFactory,
        IOptions<AgentOptions> options,
        AgentStateDb stateDb,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _options = options.Value;
        _stateDb = stateDb;
        _serviceProvider = serviceProvider;
        _cloudClient = serviceProvider.GetService<SuavoCloudClient>();
        _canaryEnabled = !_options.LearningMode;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
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
                _logger.LogDebug("SQL not connected, skipping detection cycle");
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
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

            // Enrich with patient delivery details (HIPAA: minimum necessary for delivery).
            //
            // HIPAA §164.312(b) + FDA 21 CFR Part 11 require an audit entry on
            // every PHI access. PullPatientForRxAsync reads patient name +
            // address + phone from the PMS — that is PHI. Audit BEFORE the
            // call so a crash mid-pull still leaves a record of the access
            // attempt; pre-access auditing is the HIPAA-defensible posture.
            // Codex flagged the prior "no audit on PHI pull" as a HIPAA gap
            // on the Vision/observation pipeline review.
            var hmacSalt = _options.HmacSalt ?? "[no-hmac-salt]";
            Dictionary<string, RxPatientDetails>? patientMap = null;
            try
            {
                patientMap = new Dictionary<string, RxPatientDetails>();
                foreach (var rx in readyRxs)
                {
                    var rxHash = PhiScrubber.HmacHash(rx.RxNumber, hmacSalt);
                    _stateDb.AppendChainedAuditEntry(new AuditEntry(
                        TaskId: rxHash,
                        EventType: "phi_access",
                        FromState: "",
                        ToState: "",
                        Trigger: "rx_detection_worker.enrich_for_delivery_sync",
                        RequesterId: "rx_detection_worker",
                        RxNumber: rxHash));

                    var pd = await _sqlEngine!.PullPatientForRxAsync(rx.RxNumber, ct);
                    if (pd != null) patientMap[rx.RxNumber] = pd;
                }
                _logger.LogDebug("Enriched {Count}/{Total} Rxs with patient details",
                    patientMap.Count, readyRxs.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Patient detail enrichment failed — syncing without");
                patientMap = null;
            }

            var json = SerializeRxBatch(readyRxs, hmacSalt, patientMap);
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
            _logger.LogInformation("Canary: no baseline — establishing from current schema");
            var templateBaseline = _canarySource.GetContractBaseline();

            // Run full preflight to capture live observed hashes
            var preflight = await _canarySource.VerifyPreflightAsync(templateBaseline, ct);
            if (!preflight.IsValid && preflight.Severity == CanarySeverity.Critical)
            {
                _logger.LogWarning("Canary: preflight CRITICAL during establishment — cannot establish baseline");
                return;
            }

            // Persist baseline built from the template (hashes match current live schema)
            _stateDb.UpsertCanaryBaseline(pharmacyId, templateBaseline);
            _stateDb.AppendChainedAuditEntry(new AuditEntry(
                pharmacyId, "canary", "", "established", "baseline_established"));

            // First batch syncs normally — no drift possible on establishment cycle
            var result = await _canarySource.DetectWithCanaryAsync(templateBaseline, ct);
            if (result.Rxs.Count > 0)
            {
                var json = SerializeRxBatch(result.Rxs, _options.HmacSalt ?? "[no-hmac-salt]");
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
            var json = SerializeRxBatch(detection.Rxs, _options.HmacSalt ?? "[no-hmac-salt]");
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

    private async Task TryConnectSqlAsync(CancellationToken ct)
    {
        var server = _options.SqlServer ?? "localhost";
        var database = _options.SqlDatabase ?? "PioneerPharmacySystem";

        _sqlEngine?.Dispose();
        _sqlEngine = new PioneerRxSqlEngine(
            server, database,
            _loggerFactory.CreateLogger<PioneerRxSqlEngine>(),
            _options.SqlUser, _options.SqlPassword);

        _sqlConnected = await _sqlEngine.TryConnectAsync(ct);

        if (_sqlConnected)
        {
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
    /// RxNumber is plaintext (operational ID). Patient fields are PHI
    /// but travel over HMAC-authenticated TLS to our own cloud.
    /// </summary>
    internal static string SerializeRxBatch(
        IReadOnlyList<RxMetadata> rxs,
        string hmacSalt = "",
        IReadOnlyDictionary<string, RxPatientDetails>? patientDetails = null)
    {
        var payload = new
        {
            snapshotType = "rx_delivery_queue",
            data = new
            {
                rxDeliveryQueue = rxs.Select(rx =>
                {
                    var pd = patientDetails != null && patientDetails.TryGetValue(rx.RxNumber, out var p) ? p : null;
                    var rxHash = !string.IsNullOrEmpty(hmacSalt)
                        ? PhiScrubber.HmacHash(rx.RxNumber, hmacSalt)
                        : PhiScrubber.HmacHash(rx.RxNumber, "[no-hmac-salt]");
                    return new
                    {
                        rxNumber = rxHash,
                        drugName = rx.DrugName,
                        ndc = rx.Ndc,
                        dateFilled = rx.DateFilled?.ToString("o"),
                        quantity = rx.Quantity,
                        statusGuid = rx.StatusGuid.ToString(),
                        detectedAt = rx.DetectedAt.ToString("o"),
                        patientFirstName = pd?.FirstName,
                        patientLastInitial = pd?.LastInitial,
                        patientPhone = pd?.Phone,
                        deliveryAddress1 = pd?.Address1,
                        deliveryAddress2 = pd?.Address2,
                        deliveryCity = pd?.City,
                        deliveryState = pd?.State,
                        deliveryZip = pd?.Zip
                    };
                }).ToArray(),
                totalDetected = rxs.Count,
                syncedAt = DateTimeOffset.UtcNow.ToString("o")
            },
            sqlConnected = true
        };
        return JsonSerializer.Serialize(payload);
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
