using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using System.Text.Json;
using SuavoAgent.Adapters.PioneerRx;
using SuavoAgent.Adapters.PioneerRx.Pricing;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.ActionGrammarV1.Verbs.Actuation;
using SuavoAgent.Core.Adapters;
using SuavoAgent.Core.Autonomy;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Pricing;

public sealed record PricingJobExecutionResult(
    PricingJobProgress Progress,
    string Mode,
    bool Ok,
    string? Error);

public interface IPricingJobExecutor
{
    Task<PricingJobExecutionResult> RunAsync(PricingJobSpec spec, CancellationToken ct);
}

public interface IRecoverablePricingJobExecutor
{
    PricingJobSpec? GetRecoverableSpec(PricingJobSpec proposed, string? commandId);
    PricingJobSpec? GetRecoverableSpecForCommand(string commandId);
}

public sealed record PricingLookupFactoryResult(
    bool Ok,
    ISupplierPriceLookup? Lookup,
    string Mode,
    string? Error,
    IAsyncDisposable? Lease,
    IPharmacyBaselineVolumeProvider? Provider = null,
    // True only when the sourced costPerUnit is a genuine PER-UNIT value (the catalog has a
    // dedicated per-unit cost column). When false the sourced cost is a pack cost, so subtracting a
    // per-unit baseline would be unit-unsafe — savings enrichment must be suppressed (Codex blocker).
    bool SavingsUnitSafe = false,
    PricingObservationContract? ObservationContract = null,
    PricingCostBasisAuthority? Authority = null)
{
    public static PricingLookupFactoryResult Success(
        ISupplierPriceLookup lookup,
        string mode,
        IAsyncDisposable? lease,
        IPharmacyBaselineVolumeProvider? provider = null,
        bool savingsUnitSafe = false,
        PricingObservationContract? observationContract = null,
        PricingCostBasisAuthority? authority = null) =>
        new(true, lookup, mode, null, lease, provider, savingsUnitSafe, observationContract, authority);

    public static PricingLookupFactoryResult Fail(string error, string mode = "sql") =>
        new(false, null, mode, error, null);
}

public interface IPricingLookupFactory
{
    Task<PricingLookupFactoryResult> TryCreateAsync(CancellationToken ct);

    Task<PricingLookupFactoryResult> TryCreateAsync(
        string? expectedApprovalId,
        string? expectedGrantDigest,
        CancellationToken ct) => TryCreateAsync(ct);
}

/// <summary>
/// Production pricing executor for Nadim-style batch jobs. It is intentionally
/// SQL-first and fail-closed: the default signed command must not drive the
/// pharmacist desktop through UIA just because SQL pricing is unavailable.
/// </summary>
public sealed class SqlFirstPricingJobExecutor : IPricingJobExecutor, IRecoverablePricingJobExecutor
{
    private readonly ExcelPricingReader _reader;
    private readonly ExcelPricingWriter _writer;
    private readonly AgentStateDb _db;
    private readonly IPricingLookupFactory _lookupFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SqlFirstPricingJobExecutor> _logger;
    private readonly AgentOptions _options;
    private readonly IReadOnlyDictionary<string, string> _trustedApprovalKeys;

    public SqlFirstPricingJobExecutor(
        ExcelPricingReader reader,
        ExcelPricingWriter writer,
        AgentStateDb db,
        IPricingLookupFactory lookupFactory,
        ILoggerFactory loggerFactory,
        IOptions<AgentOptions> options)
        : this(
            reader,
            writer,
            db,
            lookupFactory,
            loggerFactory,
            options,
            RemoteCommandTrust.CreateProductionKeyRegistry())
    {
    }

    internal SqlFirstPricingJobExecutor(
        ExcelPricingReader reader,
        ExcelPricingWriter writer,
        AgentStateDb db,
        IPricingLookupFactory lookupFactory,
        ILoggerFactory loggerFactory,
        IOptions<AgentOptions> options,
        IReadOnlyDictionary<string, string> trustedApprovalKeys)
    {
        _reader = reader;
        _writer = writer;
        _db = db;
        _lookupFactory = lookupFactory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SqlFirstPricingJobExecutor>();
        _options = options.Value;
        _trustedApprovalKeys = trustedApprovalKeys ??
            throw new ArgumentNullException(nameof(trustedApprovalKeys));
    }

    public async Task<PricingJobExecutionResult> RunAsync(PricingJobSpec spec, CancellationToken ct)
    {
        PricingLookupFactoryResult lookupResult;
        try
        {
            lookupResult = await _lookupFactory.TryCreateAsync(
                spec.ApprovalId,
                spec.GrantDigest,
                ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogSafeWarning(ex);
            _db.UpsertPricingJob(spec, PricingJobStatus.Failed, 0, 0, 0);
            return Failed(spec, "sql pricing lookup unavailable");
        }

        if (!lookupResult.Ok || lookupResult.Lookup is null ||
            lookupResult.ObservationContract is null || lookupResult.Authority is null)
        {
            var error = string.IsNullOrWhiteSpace(lookupResult.Error)
                ? "sql pricing lookup unavailable"
                : lookupResult.Error!;
            _logger.LogWarning("core.sql_pricing.preflight_rejected");
            _db.UpsertPricingJob(spec, PricingJobStatus.Failed, 0, 0, 0);
            return Failed(spec, error, lookupResult.Mode);
        }

        await using var lease = lookupResult.Lease;
        // Excel baseline/quantity columns are also unit-gated: the workbook baseline is per-unit, so
        // only trust it when the sourced cost is per-unit too (SavingsUnitSafe).
        var savingsEnabled = _options.EnablePricingSavingsEnrichment && lookupResult.SavingsUnitSafe;
        var runner = new SqlPricingJobRunner(
            _reader,
            _writer,
            _db,
            lookupResult.Lookup,
            _loggerFactory.CreateLogger<SqlPricingJobRunner>(),
            lookupResult.ObservationContract,
            lookupResult.Authority,
            lookupResult.Provider,
            savingsEnabled
                ? new PricingSavingsOptions(
                    _options.PricingBaselineCostColumn,
                    _options.PricingQuantityColumn,
                    _options.PricingMaxPlausibleUnitCost,
                    _options.PricingMaxPlausibleQuantity,
                    _options.PricingSuspiciousSavingsFraction)
                : null,
            trustedApprovalKeys: _trustedApprovalKeys);

        var progress = await runner.RunAsync(spec, ct);
        var ok = progress.Status == PricingJobStatus.Completed;
        return new PricingJobExecutionResult(
            progress,
            lookupResult.Mode,
            ok,
            ok
                ? null
                : progress.HaltReason ?? "pricing job failed - see agent logs");
    }

    public PricingJobSpec? GetRecoverableSpec(PricingJobSpec proposed, string? commandId) =>
        _db.GetRecoverablePricingJob(
            "sql",
            _options.PharmacyId ?? "",
            _options.AgentId ?? "",
            _options.MachineFingerprint ?? "",
            DateTimeOffset.UtcNow,
            commandId,
            proposed.ExcelPath,
            _trustedApprovalKeys);

    public PricingJobSpec? GetRecoverableSpecForCommand(string commandId) =>
        _db.GetRecoverablePricingJob(
            "sql",
            _options.PharmacyId ?? "",
            _options.AgentId ?? "",
            _options.MachineFingerprint ?? "",
            DateTimeOffset.UtcNow,
            commandId,
            trustedApprovalKeys: _trustedApprovalKeys);

    private static PricingJobExecutionResult Failed(
        PricingJobSpec spec,
        string error,
        string mode = "sql") =>
        new(
            new PricingJobProgress(spec.JobId, 0, 0, 0, PricingJobStatus.Failed),
            mode,
            false,
            error);
}

/// <summary>
/// Builds the live PioneerRx SQL lookup used by <see cref="SqlFirstPricingJobExecutor"/>.
/// The open connection is leased to the runner for one job and disposed when the job completes.
/// </summary>
public sealed class PioneerRxSqlPricingLookupFactory : IPricingLookupFactory
{
    private readonly AgentOptions _options;
    private readonly AgentStateDb _db;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<PioneerRxSqlPricingLookupFactory> _logger;
    private readonly IReadOnlyDictionary<string, string> _trustedApprovalKeys;

    public PioneerRxSqlPricingLookupFactory(
        IOptions<AgentOptions> options,
        AgentStateDb db,
        ILoggerFactory loggerFactory)
        : this(
            options,
            db,
            loggerFactory,
            RemoteCommandTrust.CreateProductionKeyRegistry())
    {
    }

    internal PioneerRxSqlPricingLookupFactory(
        IOptions<AgentOptions> options,
        AgentStateDb db,
        ILoggerFactory loggerFactory,
        IReadOnlyDictionary<string, string> trustedApprovalKeys)
    {
        _options = options.Value;
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<PioneerRxSqlPricingLookupFactory>();
        _trustedApprovalKeys = trustedApprovalKeys ??
            throw new ArgumentNullException(nameof(trustedApprovalKeys));
    }

    public async Task<PricingLookupFactoryResult> TryCreateAsync(CancellationToken ct)
        => await TryCreateAsync(null, null, ct).ConfigureAwait(false);

    public async Task<PricingLookupFactoryResult> TryCreateAsync(
        string? expectedApprovalId,
        string? expectedGrantDigest,
        CancellationToken ct)
    {
        var pharmacy = SelectPharmacy();
        if (pharmacy is null || string.IsNullOrWhiteSpace(pharmacy.SqlServer))
            return PricingLookupFactoryResult.Fail("SQL pricing unavailable: no SQL server configured");

        var connection = new SqlConnection(BuildConnectionString(pharmacy));
        try
        {
            await connection.OpenAsync(ct);

            var discovery = new PricingSchemaDiscovery(
                _loggerFactory.CreateLogger<PricingSchemaDiscovery>());
            var outcome = await discovery.DiscoverAsync(connection, ct);
            if (!outcome.Ok || outcome.Schema is null)
            {
                await connection.DisposeAsync();
                return PricingLookupFactoryResult.Fail(
                    $"SQL pricing schema unavailable: {outcome.Reason ?? "schema discovery failed"}");
            }

            // Feature A promises a per-unit comparison. Never admit a catalog
            // that exposes only pack cost: downstream code and the workbook
            // header must not relabel a pack amount as CostPerUnit.
            if (string.IsNullOrWhiteSpace(outcome.Schema.CostPerUnitColumn))
            {
                await connection.DisposeAsync();
                _logger.LogWarning(
                    "core.sql_pricing.cost_basis_unresolved");
                return PricingLookupFactoryResult.Fail(
                    "SQL pricing unavailable: catalog has no dedicated per-unit cost column");
            }

            var observationContract = PricingObservationPolicy.CreateSql(outcome.Schema);
            var authority = PricingApprovalAuthorityResolver.ResolveOrStageProposal(
                _db,
                pharmacy.PharmacyId,
                _options.AgentId ?? "",
                _options.MachineFingerprint ?? "",
                observationContract,
                DateTimeOffset.UtcNow,
                _trustedApprovalKeys,
                expectedApprovalId,
                expectedGrantDigest,
                out var authorityCode);
            if (authority is null)
            {
                await connection.DisposeAsync();
                _logger.LogWarning(
                    "core.sql_pricing.cost_basis_authority_blocked code={Code}",
                    authorityCode);
                return PricingLookupFactoryResult.Fail(authorityCode);
            }

            var lookup = new SqlSupplierPriceLookup(
                outcome.Schema,
                _ => Task.FromResult(connection),
                _loggerFactory.CreateLogger<SqlSupplierPriceLookup>());

            // M1 savings enrichment remains OFF by default. The admission gate
            // above proves that any admitted sourced cost is genuinely per-unit.
            const bool savingsUnitSafe = true;
            var savingsEnabled = _options.EnablePricingSavingsEnrichment;

            IPharmacyBaselineVolumeProvider? provider = savingsEnabled
                ? new SqlDispensedVolumeProvider(
                    outcome.Schema,
                    _ => Task.FromResult(connection),
                    _options.PricingSavingsWindowDays,
                    _options.PricingDispensedStatusNames,
                    _loggerFactory.CreateLogger<SqlDispensedVolumeProvider>())
                : null;

            return PricingLookupFactoryResult.Success(
                lookup,
                "sql",
                new SqlConnectionLease(connection),
                provider,
                savingsUnitSafe,
                observationContract,
                authority);
        }
        catch (OperationCanceledException)
        {
            await connection.DisposeAsync();
            throw;
        }
        catch (Exception ex)
        {
            await connection.DisposeAsync();
            _logger.LogSafeWarning(ex);
            return PricingLookupFactoryResult.Fail("SQL pricing unavailable - see agent logs");
        }
    }

    private PharmacyConfig? SelectPharmacy()
    {
        var pharmacies = _options.GetEffectivePharmacies()
            .Where(p => p.Enabled)
            .ToList();
        if (pharmacies.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(_options.PharmacyId))
        {
            var match = pharmacies.FirstOrDefault(p =>
                string.Equals(p.PharmacyId, _options.PharmacyId, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }

        return pharmacies[0];
    }

    private string BuildConnectionString(PharmacyConfig pharmacy)
    {
        var csb = new SqlConnectionStringBuilder
        {
            DataSource = pharmacy.SqlServer,
            InitialCatalog = AdapterCatalog.Resolve(pharmacy.SqlDatabase, PioneerRxAdapterConfig.Create()),
            ApplicationName = "SuavoAgent.Pricing",
            ConnectTimeout = 30,
            MaxPoolSize = 1,
            MinPoolSize = 0,
        };
        SqlConnectionSecurity.Apply(csb, _options);

        if (!string.IsNullOrWhiteSpace(pharmacy.SqlUser))
        {
            csb.UserID = pharmacy.SqlUser;
            csb.Password = pharmacy.SqlPassword;
        }
        else
        {
            csb.IntegratedSecurity = true;
        }

        return csb.ConnectionString;
    }

    private sealed class SqlConnectionLease : IAsyncDisposable
    {
        private readonly SqlConnection _connection;

        public SqlConnectionLease(SqlConnection connection)
        {
            _connection = connection;
        }

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }
}

/// <summary>
/// UIA-first pricing executor for pharmacies that have not authorized direct SQL access
/// against the PioneerRx backend (default pilot posture; Nadim's Better Life Pharmacy is
/// tenant zero). Drives PioneerRx exclusively through the documented operator workflow:
/// Item -&gt; Rx Item -&gt; Quick Search -&gt; Pricing tab -&gt; read supplier grid.
///
/// Like <see cref="SqlFirstPricingJobExecutor"/> this is fail-closed: the executor does not
/// silently fall back to SQL if the Helper IPC channel is unreachable. The runner's per-NDC
/// failure handling (no response from Helper -&gt; SupplierPriceResult.Found=false with an
/// explicit error) plus the brain evaluator's streak-halt rules surface a dead Helper as a
/// halted job, not a partial success.
///
/// Throttle is set at <see cref="PricingJobRunner"/> construction from
/// <see cref="AgentOptions.PricingThrottleMs"/>; recommended 1500 ms for UIA to stay below
/// any anti-automation heuristic the vendor may apply.
/// </summary>
public sealed class UiaFirstPricingJobExecutor : IPricingJobExecutor, IRecoverablePricingJobExecutor
{
    private readonly PricingJobRunner _runner;
    private readonly IIpcCommandClient _commandClient;
    private readonly AgentStateDb _db;
    private readonly IActuationGateway _actuationGateway;
    private readonly ILogger<UiaFirstPricingJobExecutor> _logger;
    private readonly AgentOptions _options;
    private readonly IReadOnlyDictionary<string, string> _trustedApprovalKeys;
    private readonly IPioneerRxAutonomyIdentityProvider? _pmsIdentityProvider;

    public UiaFirstPricingJobExecutor(
        PricingJobRunner runner,
        IIpcCommandClient commandClient,
        AgentStateDb db,
        IActuationGateway actuationGateway,
        ILogger<UiaFirstPricingJobExecutor> logger,
        IOptions<AgentOptions> options)
        : this(
            runner,
            commandClient,
            db,
            actuationGateway,
            logger,
            options,
            pmsIdentityProvider: null,
            RemoteCommandTrust.CreateProductionKeyRegistry())
    {
    }

    internal UiaFirstPricingJobExecutor(
        PricingJobRunner runner,
        IIpcCommandClient commandClient,
        AgentStateDb db,
        IActuationGateway actuationGateway,
        ILogger<UiaFirstPricingJobExecutor> logger,
        IOptions<AgentOptions> options,
        IPioneerRxAutonomyIdentityProvider? pmsIdentityProvider,
        IReadOnlyDictionary<string, string> trustedApprovalKeys)
    {
        _runner = runner;
        _commandClient = commandClient;
        _db = db;
        _actuationGateway = actuationGateway ?? throw new ArgumentNullException(nameof(actuationGateway));
        _logger = logger;
        _options = options.Value;
        _pmsIdentityProvider = pmsIdentityProvider;
        _trustedApprovalKeys = trustedApprovalKeys ??
            throw new ArgumentNullException(nameof(trustedApprovalKeys));
    }

    public async Task<PricingJobExecutionResult> RunAsync(PricingJobSpec spec, CancellationToken ct)
    {
        var modality = _options.PricingExecutor == PricingExecutorMode.VisionFirst
            ? "vision"
            : "uia";

        // Authoritative live-actuation preflight. Pricing has no simulation path:
        // dry-run therefore blocks rather than pretending the PMS was navigated.
        ActuationGateState? gateState = null;
        try
        {
            using var gateCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            gateCts.CancelAfter(TimeSpan.FromSeconds(5));
            gateState = await _actuationGateway.GetStateAsync(gateCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout is an unavailable safety state and therefore a rejection.
        }
        catch (Exception ex)
        {
            _logger.LogSafeWarning(ex);
        }

        var gateReject = PricingActuationPreflight.RejectionCode(gateState, DateTimeOffset.UtcNow);
        if (gateReject is not null)
        {
            _logger.LogError(
                "core.uia_pricing.actuation_gate_blocked");
            _db.UpsertPricingJob(spec, PricingJobStatus.Failed, 0, 0, 0);
            return new PricingJobExecutionResult(
                new PricingJobProgress(spec.JobId, 0, 0, 0, PricingJobStatus.Failed),
                Mode: modality,
                Ok: false,
                Error: PricingSafetyErrors.ActuationGateClosed(gateReject));
        }

        // BLIND-RUN GATE (executor invariant, fail-closed). Reaching this method means we are
        // about to drive the LIVE PMS screen via UIA. The Helper must be reachable, answering,
        // and in the interactive console session (SI=1, re-derived in Core from raw session ids
        // — never a self-reported bool). This gate lives in the EXECUTOR, not only at heartbeat
        // dispatch, so EVERY caller (heartbeat, idle-autopilot, chatbox, mission loop) is gated
        // — a future caller cannot reach _runner.RunAsync without proving the screen is visible.
        // See feedback-helper-must-run-in-interactive-session + never-blind-run-on-live-PMS.
        var preflight = await HelperInteractivePreflight.CheckAsync(
            _commandClient, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), ct);
        if (!preflight.Ok)
        {
            _logger.LogError(
                "core.uia_pricing.interactive_preflight_blocked");
            _db.UpsertPricingJob(spec, PricingJobStatus.Failed, 0, 0, 0);
            return new PricingJobExecutionResult(
                new PricingJobProgress(spec.JobId, 0, 0, 0, PricingJobStatus.Failed),
                Mode: modality,
                Ok: false,
                Error: preflight.Error
                    ?? "Helper interactive pre-flight failed — refusing to drive the live screen");
        }

        var livePmsIdentity = _pmsIdentityProvider?.Current(DateTimeOffset.UtcNow);
        if (livePmsIdentity is null)
        {
            _logger.LogError("core.uia_pricing.pms_identity_unavailable");
            _db.UpsertPricingJob(spec, PricingJobStatus.Failed, 0, 0, 0);
            return new PricingJobExecutionResult(
                new PricingJobProgress(spec.JobId, 0, 0, 0, PricingJobStatus.Failed),
                Mode: modality,
                Ok: false,
                Error: "pricing_live_pms_identity_unavailable");
        }

        var screenContext = await CaptureScreenContextAsync(ct).ConfigureAwait(false);
        if (screenContext is null)
        {
            _logger.LogError("core.uia_pricing.screen_identity_unavailable");
            _db.UpsertPricingJob(spec, PricingJobStatus.Failed, 0, 0, 0);
            return new PricingJobExecutionResult(
                new PricingJobProgress(spec.JobId, 0, 0, 0, PricingJobStatus.Failed),
                Mode: modality,
                Ok: false,
                Error: "pricing_live_screen_identity_unavailable");
        }

        var pmsFingerprint = PricingObservationPolicy.Digest(
            "pioneerrx_live_process_identity_v1",
            livePmsIdentity.FileVersion,
            livePmsIdentity.ExecutableSha256,
            livePmsIdentity.SignerCertificateSha256,
            livePmsIdentity.ApprovalReceiptDigest,
            livePmsIdentity.AuthorityDigest,
            livePmsIdentity.ApprovalCounter.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        var activePatches = _db.GetActiveSelectorPatches().ToArray();
        PricingObservationContract observationContract;
        try
        {
            observationContract = PricingObservationPolicy.CreateUia(
                modality,
                pmsFingerprint,
                screenContext.ScreenSignatureV1,
                activePatches);
        }
        catch (ArgumentException)
        {
            _db.UpsertPricingJob(spec, PricingJobStatus.Failed, 0, 0, 0);
            return new PricingJobExecutionResult(
                new PricingJobProgress(spec.JobId, 0, 0, 0, PricingJobStatus.Failed),
                Mode: modality,
                Ok: false,
                Error: "pricing_live_screen_identity_invalid");
        }

        var authority = PricingApprovalAuthorityResolver.ResolveOrStageProposal(
            _db,
            _options.PharmacyId ?? "",
            _options.AgentId ?? "",
            _options.MachineFingerprint ?? "",
            observationContract,
            DateTimeOffset.UtcNow,
            _trustedApprovalKeys,
            spec.ApprovalId,
            spec.GrantDigest,
            out var authorityCode);
        if (authority is null)
        {
            _logger.LogError(
                "core.uia_pricing.cost_basis_authority_blocked code={Code}",
                authorityCode);
            _db.UpsertPricingJob(spec, PricingJobStatus.Failed, 0, 0, 0);
            return new PricingJobExecutionResult(
                new PricingJobProgress(spec.JobId, 0, 0, 0, PricingJobStatus.Failed),
                Mode: modality,
                Ok: false,
                Error: authorityCode);
        }

        _logger.LogInformation(
            "core.uia_pricing.run_started");

        try
        {
            var progress = await _runner.RunAsync(
                spec,
                _commandClient,
                observationContract,
                authority,
                activePatches,
                pmsFingerprint,
                screenContext.ScreenSignatureV1,
                ct);
            var ok = progress.Status == PricingJobStatus.Completed;
            return new PricingJobExecutionResult(
                progress,
                Mode: modality,
                Ok: ok,
                Error: ok ? null : $"pricing job ended with status {progress.Status} - see agent logs");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Catch-all so a malformed Excel or transient IPC error surfaces as a clean
            // failure result, mirroring SqlFirstPricingJobExecutor's behavior. The runner
            // itself already swallows per-row exceptions; this catches only the orchestration
            // boundary (Excel read, DB write).
            _logger.LogSafeError(ex);
            _db.UpsertPricingJob(spec, PricingJobStatus.Failed, 0, 0, 0);
            return new PricingJobExecutionResult(
                new PricingJobProgress(spec.JobId, 0, 0, 0, PricingJobStatus.Failed),
                Mode: modality,
                Ok: false,
                Error: "pricing job failed - see agent logs");
        }
    }

    public PricingJobSpec? GetRecoverableSpec(PricingJobSpec proposed, string? commandId) =>
        _db.GetRecoverablePricingJob(
            _options.PricingExecutor == PricingExecutorMode.VisionFirst ? "vision" : "uia",
            _options.PharmacyId ?? "",
            _options.AgentId ?? "",
            _options.MachineFingerprint ?? "",
            DateTimeOffset.UtcNow,
            commandId,
            proposed.ExcelPath,
            _trustedApprovalKeys);

    public PricingJobSpec? GetRecoverableSpecForCommand(string commandId) =>
        _db.GetRecoverablePricingJob(
            _options.PricingExecutor == PricingExecutorMode.VisionFirst ? "vision" : "uia",
            _options.PharmacyId ?? "",
            _options.AgentId ?? "",
            _options.MachineFingerprint ?? "",
            DateTimeOffset.UtcNow,
            commandId,
            trustedApprovalKeys: _trustedApprovalKeys);

    private async Task<PricingScreenObservationContext?> CaptureScreenContextAsync(
        CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        IpcResponse? response;
        try
        {
            response = await _commandClient.SendAsync(
                new IpcRequest(
                    id,
                    IpcCommands.PricingObservationContext,
                    1,
                    Data: null),
                TimeSpan.FromSeconds(10),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogSafeWarning(ex);
            return null;
        }

        if (response is null || response.Id != id ||
            response.Command != IpcCommands.PricingObservationContext ||
            response.Status != IpcStatus.Ok || response.Error is not null ||
            response.Data is null)
            return null;
        try
        {
            var parsed = response.Data.Value.Deserialize<PricingScreenObservationContext>();
            return parsed is { ProcessId: > 0, ScreenSignatureV1.Length: 64 } &&
                   parsed.ScreenSignatureV1.All(ch =>
                       ch is >= '0' and <= '9' or >= 'a' and <= 'f')
                ? parsed
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal static class PricingActuationPreflight
{
    public static string? RejectionCode(ActuationGateState? state, DateTimeOffset now)
        => LiveActuationGatePolicy.RejectionCode(state, now);
}
