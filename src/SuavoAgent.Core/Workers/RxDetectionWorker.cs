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
using SuavoAgent.Contracts.Security;
using SuavoAgent.Core.Adapters;

namespace SuavoAgent.Core.Workers;

public sealed partial class RxDetectionWorker : ResilientHostedService
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
    private readonly IRxCorrelationStore? _rxCorrelationStore;
    private readonly IActivePmsAdapterRegistry? _learnedAdapterRegistry;
    private readonly ObservationActivationAuthority? _observationAuthority;
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
    private bool _learnedFallbackHealthy;
    private string _activeDetectionSource = "none";

    public int DetectionIntervalSeconds { get; set; } = 300;
    public int LastDetectedCount { get; private set; }
    public DateTimeOffset? LastDetectionTime { get; private set; }
    public bool IsSqlConnected => _sqlConnected;
    public int ConsecutiveSqlFailures => _consecutiveSqlFailures;
    public DateTimeOffset? SqlDownSince => _sqlDownSince;
    public bool IsLearnedFallbackHealthy => _learnedFallbackHealthy;
    public string ActiveDetectionSource => _activeDetectionSource;

    /// <summary>True when SQL has been down past the escalation threshold — a real detection outage,
    /// not a transient blip or a no-PMS dev box. Surfaced to the heartbeat as `rxDetectionDegraded`.</summary>
    public bool IsDetectionDegraded(DateTimeOffset now) =>
        !_learnedFallbackHealthy &&
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
        _rxCorrelationStore = serviceProvider.GetService<IRxCorrelationStore>();
        _learnedAdapterRegistry = serviceProvider.GetService<IActivePmsAdapterRegistry>();
        _observationAuthority = serviceProvider.GetService<ObservationActivationAuthority>();
        if (_observationAuthority is not null)
            _observationAuthority.AuthorityLost += OnObservationAuthorityLost;
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
        _learnedFallbackHealthy = false;
        _logger.LogCritical(
            "RxDetectionWorker exhausted supervised restarts — detection halted, awaiting repair");
        return Task.CompletedTask;
    }

    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Rx detection worker started (canary={Canary})", _canaryEnabled);

        _stateDb.PurgeExpiredDeadLetters();

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
                _logger.LogSafeWarning(ex);
                _sqlConnected = false;
            }

            var delay = _observationAuthority is not null &&
                        !_observationAuthority.ObservationEnabled
                ? TimeSpan.FromSeconds(1)
                : TimeSpan.FromSeconds(DetectionIntervalSeconds);
            await Task.Delay(delay, stoppingToken);
        }

        _sqlEngine?.Dispose();
        _logger.LogInformation("Rx detection worker stopped");
    }

    internal async Task RunCycleAsync(CancellationToken ct)
    {
        using var activation = _observationAuthority?.TryAcquireExecutionLease(ct);
        if (_observationAuthority is not null && activation is null)
        {
            SuspendObservation();
            return;
        }
        if (activation is not null) ct = activation.Token;

        if (!_sqlConnected)
        {
            await TryConnectSqlAsync(ct);
            if (!_sqlConnected)
            {
                if (await TryRunLearnedFallbackAsync("builtin_connection_unavailable", ct)) return;
                await DelayUnavailableDetectionAsync(ct);
                return;
            }
        }

        try
        {
            var builtInAvailable = _canarySource != null
                ? await RunCanaryDetectionAsync(ct)
                : await RunLegacyDetectionAsync(ct);
            if (builtInAvailable)
            {
                _learnedFallbackHealthy = false;
                SetDetectionSource("builtin", "builtin_available");
                return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            MarkSqlConnectFailed(DateTimeOffset.UtcNow);
            _logger.LogWarning(
                "Built-in Rx detection unavailable; evaluating approved learned fallback (errorType={ErrorType})",
                ex.GetType().Name);
        }

        if (await TryRunLearnedFallbackAsync("builtin_contract_unavailable", ct)) return;
        await DelayUnavailableDetectionAsync(ct);
    }

    private async Task<bool> RunLegacyDetectionAsync(CancellationToken ct)
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

            var hmacSalt = RequireHmacSalt();
            PersistRxCorrelations(readyRxs, hmacSalt);

            var json = SerializeRxBatch(
                readyRxs,
                hmacSalt,
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
        return true;
    }

    private async Task<bool> RunCanaryDetectionAsync(CancellationToken ct)
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
                _logger.LogSafeWarning(ex);
                return false;
            }

            var preflight = await _canarySource.VerifyPreflightAsync(establishedBaseline, ct);
            RecordSchemaCanaryGate(preflight);
            if (!preflight.IsValid)
            {
                _logger.LogWarning(
                    "Canary: observed baseline verification failed during establishment ({Severity})",
                    preflight.Severity);
                return false;
            }

            _stateDb.UpsertCanaryBaseline(pharmacyId, establishedBaseline);
            _stateDb.AppendChainedAuditEntry(new AuditEntry(
                pharmacyId, "canary", "", "established", "baseline_established"));

            // First batch syncs normally — no drift possible on establishment cycle
            var result = await _canarySource.DetectWithCanaryAsync(establishedBaseline, ct);
            if (result.Rxs.Count > 0)
            {
                var hmacSalt = RequireHmacSalt();
                PersistRxCorrelations(result.Rxs, hmacSalt);
                RecordSchemaCanaryGate(result.PostflightVerification);
                var json = SerializeRxBatch(
                    result.Rxs,
                    hmacSalt,
                    schemaVerification: result.PostflightVerification,
                    pharmacyId: _options.PharmacyId,
                    agentInstallId: _options.AgentId,
                    includeLegacyDeliveryQueue: _options.EnableLegacyPhiDeliveryQueueSync);
                if (!await TrySyncPayloadToCloudAsync(json, ct))
                    _stateDb.InsertUnsyncedBatch(json);
            }

            LastDetectedCount = result.Rxs.Count;
            LastDetectionTime = DateTimeOffset.UtcNow;
            return true;
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
            return false;
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
            var hmacSalt = RequireHmacSalt();
            PersistRxCorrelations(detection.Rxs, hmacSalt);
            var json = SerializeRxBatch(
                detection.Rxs,
                hmacSalt,
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
        return true;
    }

    /// <summary>
    /// Serializes the pre-approval, hash-only candidate batch. Patient identity,
    /// phone, address, city, state, ZIP, delivery hashes, missing-field flags,
    /// and patient-field provenance are deliberately absent. The optional
    /// patientDetails argument is retained only for binary/source compatibility
    /// with old fixture callers and is never read.
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
        bool includeLegacyDeliveryQueue = false,
        string sourcePms = "PioneerRx",
        string schemaSignature = "pioneerrx.sql.metadata.v1",
        string evidenceSourceKind = RxCorrelationSourceKinds.PioneerRxBuiltIn,
        string? evidenceSourceBinding = null)
    {
        var serializedAt = serializedAtUtc ?? DateTimeOffset.UtcNow;
        var scanWindowId = $"rxscan-{serializedAt.ToUnixTimeMilliseconds()}";
        var candidates = rxs.Select(rx =>
        {
            var rxHash = HashRxNumber(rx.RxNumber, hmacSalt);
            var warnings = BuildCandidateWarnings(schemaVerification);
            var isControlled = rx.DrugSchedule is >= 2 and <= 5;
            const DetectionSource source = DetectionSource.Sql;
            var localEvidenceId = BuildLocalEvidenceId(
                rxHash,
                rx,
                evidenceSourceKind,
                evidenceSourceBinding);
            var fieldConfidence = BuildCandidateFieldConfidence(rx);
            var fieldProvenance = BuildCandidateProvenance(rx, source, schemaSignature, localEvidenceId);
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
                Provenance: new RxOrderCandidateProvenance(
                    PharmacyId: pharmacyId,
                    AgentInstallId: agentInstallId,
                    EvidenceId: localEvidenceId,
                    Pms: sourcePms,
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

        // includeLegacyDeliveryQueue is intentionally inert. That historical shape linked an Rx
        // hash to raw drug/fill metadata and could be re-enabled by configuration. Keeping the
        // parameter preserves old fixture callers while making the wire contract one-way: only
        // hash-only rxOrderCandidates can leave the workstation before pharmacist approval.
        _ = includeLegacyDeliveryQueue;

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

    internal static string BuildLocalEvidenceId(
        string rxHash,
        RxMetadata rx,
        string sourceKind = RxCorrelationSourceKinds.PioneerRxBuiltIn,
        string? sourceBinding = null)
    {
        var shortHash = rxHash.Length >= 16 ? rxHash[..16] : rxHash;
        // DetectedAt is stamped on every poll, so using it directly created a new evidence row every
        // five minutes and could hide an already-approved transition behind a newest duplicate. A
        // bounded day bucket plus fill number is stable within the observation day. The cloud ingest
        // is append-only (ON CONFLICT DO NOTHING), so a permanent fill-date key would freeze
        // captured_at_utc and become unapprovably stale after 24 hours. Rotating once per UTC day
        // preserves approval freshness without five-minute duplicate growth.
        var basisDate = rx.DetectedAt.UtcDateTime.Date;
        var utcBasis = new DateTimeOffset(
            DateTime.SpecifyKind(basisDate, DateTimeKind.Utc),
            TimeSpan.Zero);
        var fillOffsetSeconds = Math.Clamp(rx.FillNumber, 0, 999);
        long evidenceNumber;
        if (sourceKind == RxCorrelationSourceKinds.LearnedApproved)
        {
            if (sourceBinding is not { Length: 64 } ||
                !sourceBinding.All(Uri.IsHexDigit))
                throw new InvalidOperationException("Learned evidence requires an exact template binding.");
            var sourceDigest = PhiScrubber.HmacHash(
                $"{rxHash}|{basisDate:yyyyMMdd}|{fillOffsetSeconds}",
                sourceBinding);
            evidenceNumber = 1_000_000_000_000L +
                             (long)(Convert.ToUInt64(sourceDigest[..12], 16) % 9_000_000_000_000UL);
        }
        else if (sourceKind != RxCorrelationSourceKinds.PioneerRxBuiltIn || sourceBinding is not null)
        {
            throw new InvalidOperationException("Rx evidence source binding is invalid.");
        }
        else
        {
            evidenceNumber = utcBasis.ToUnixTimeSeconds() + fillOffsetSeconds;
        }
        return $"rxh-{shortHash}-{evidenceNumber}";
    }

    private static List<RxOrderCandidateWarning> BuildCandidateWarnings(
        ContractVerification? schemaVerification = null)
    {
        var warnings = new List<RxOrderCandidateWarning>();
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

    private static Dictionary<string, double> BuildCandidateFieldConfidence(RxMetadata rx)
    {
        return new Dictionary<string, double>
        {
            ["rxHash"] = 1.0d,
            ["medication.nameHash"] = string.IsNullOrWhiteSpace(rx.DrugName) ? 0.4d : 1.0d,
            ["medication.ndc"] = string.IsNullOrWhiteSpace(rx.Ndc) ? 0.4d : 1.0d,
            ["medication.quantity"] = rx.Quantity > 0 ? 1.0d : 0.4d,
            ["medication.daysSupply"] = rx.DaysSupply > 0 ? 1.0d : 0.4d,
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

        return provenance;
    }

    private async Task<bool> TrySyncPayloadToCloudAsync(string json, CancellationToken ct)
    {
        if (_cloudClient is null) return true; // no cloud = nothing to sync

        try
        {
            var payload = JsonSerializer.Deserialize<JsonElement>(json);
            var localBinding = _learnedAdapterRegistry?.CurrentBinding()
                ?? throw new InvalidOperationException(
                    "No device-authorized learned source is active; built-in sync remains fail-closed until signed runtime provenance is enrolled.");
            var cloudBinding = _stateDb.GetCloudLearnedSourceBinding(localBinding)
                ?? throw new InvalidOperationException(
                    "The active learned source has no cloud activation receipt.");
            var signer = _serviceProvider.GetService<IDeviceAuthoritySigner>()
                ?? throw new InvalidOperationException("Device authority signer is unavailable.");
            var batchDigest = DeviceAuthorityCanonical.HashUnsignedSync(payload);
            var persisted = _stateDb.GetOrCreateRxDeviceReceipt(
                batchDigest,
                cloudBinding,
                _options,
                signer);
            var envelope = new
            {
                snapshotType = payload.GetProperty("snapshotType").Clone(),
                data = payload.GetProperty("data").Clone(),
                sqlConnected = payload.TryGetProperty("sqlConnected", out var sql) && sql.GetBoolean(),
                uiaConnected = payload.TryGetProperty("uiaConnected", out var uia) && uia.GetBoolean(),
                sourceReceipt = JsonSerializer.SerializeToElement(
                    persisted.Signed.Receipt,
                    SyncPayloadJsonOptions),
                sourceKeyId = persisted.Signed.KeyId,
                sourceSignature = persisted.Signed.Signature,
            };
            if (!await _cloudClient.SyncRxDeviceBoundAsync(
                    envelope,
                    persisted.Signed,
                    ct).ConfigureAwait(false))
                throw new InvalidOperationException(
                    "Cloud returned no exact device-source acceptance receipt.");
            _stateDb.MarkRxDeviceReceiptAccepted(batchDigest);
            _logger.LogInformation("Synced batch to cloud");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogSafeError(ex);
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
