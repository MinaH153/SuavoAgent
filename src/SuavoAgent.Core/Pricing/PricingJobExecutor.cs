using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SuavoAgent.Adapters.PioneerRx;
using SuavoAgent.Adapters.PioneerRx.Pricing;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Adapters;
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
    bool SavingsUnitSafe = false)
{
    public static PricingLookupFactoryResult Success(
        ISupplierPriceLookup lookup,
        string mode,
        IAsyncDisposable? lease,
        IPharmacyBaselineVolumeProvider? provider = null,
        bool savingsUnitSafe = false) =>
        new(true, lookup, mode, null, lease, provider, savingsUnitSafe);

    public static PricingLookupFactoryResult Fail(string error, string mode = "sql") =>
        new(false, null, mode, error, null);
}

public interface IPricingLookupFactory
{
    Task<PricingLookupFactoryResult> TryCreateAsync(CancellationToken ct);
}

/// <summary>
/// Production pricing executor for Nadim-style batch jobs. It is intentionally
/// SQL-first and fail-closed: the default signed command must not drive the
/// pharmacist desktop through UIA just because SQL pricing is unavailable.
/// </summary>
public sealed class SqlFirstPricingJobExecutor : IPricingJobExecutor
{
    private readonly ExcelPricingReader _reader;
    private readonly ExcelPricingWriter _writer;
    private readonly AgentStateDb _db;
    private readonly IPricingLookupFactory _lookupFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SqlFirstPricingJobExecutor> _logger;
    private readonly AgentOptions _options;
    private readonly AgentActivity? _activity;

    public SqlFirstPricingJobExecutor(
        ExcelPricingReader reader,
        ExcelPricingWriter writer,
        AgentStateDb db,
        IPricingLookupFactory lookupFactory,
        ILoggerFactory loggerFactory,
        IOptions<AgentOptions> options,
        AgentActivity? activity = null)
    {
        _reader = reader;
        _writer = writer;
        _db = db;
        _lookupFactory = lookupFactory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SqlFirstPricingJobExecutor>();
        _options = options.Value;
        _activity = activity;
    }

    public async Task<PricingJobExecutionResult> RunAsync(PricingJobSpec spec, CancellationToken ct)
    {
        PricingLookupFactoryResult lookupResult;
        try
        {
            lookupResult = await _lookupFactory.TryCreateAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SQL pricing lookup factory failed");
            _db.UpsertPricingJob(spec, PricingJobStatus.Failed, 0, 0, 0);
            return Failed(spec, "sql pricing lookup unavailable");
        }

        if (!lookupResult.Ok || lookupResult.Lookup is null)
        {
            var error = string.IsNullOrWhiteSpace(lookupResult.Error)
                ? "sql pricing lookup unavailable"
                : lookupResult.Error!;
            _logger.LogWarning("SQL pricing job {JobId} rejected before run: {Reason}", spec.JobId, error);
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
            lookupResult.Provider,
            savingsEnabled
                ? new PricingSavingsOptions(
                    _options.PricingBaselineCostColumn,
                    _options.PricingQuantityColumn,
                    _options.PricingMaxPlausibleUnitCost,
                    _options.PricingMaxPlausibleQuantity,
                    _options.PricingSuspiciousSavingsFraction)
                : null,
            _activity);

        try
        {
            var progress = await runner.RunAsync(spec, ct);
            var ok = progress.Status == PricingJobStatus.Completed;
            return new PricingJobExecutionResult(
                progress,
                lookupResult.Mode,
                ok,
                ok ? null : "pricing job failed - see agent logs");
        }
        finally
        {
            // Always retire the live sub-step when the job ends (success, failure,
            // or cancel) so the cockpit doesn't show a stale step.
            _activity?.Clear();
        }
    }

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
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<PioneerRxSqlPricingLookupFactory> _logger;

    public PioneerRxSqlPricingLookupFactory(
        IOptions<AgentOptions> options,
        ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<PioneerRxSqlPricingLookupFactory>();
    }

    public async Task<PricingLookupFactoryResult> TryCreateAsync(CancellationToken ct)
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

            var lookup = new SqlSupplierPriceLookup(
                outcome.Schema,
                _ => Task.FromResult(connection),
                _loggerFactory.CreateLogger<SqlSupplierPriceLookup>());

            // M1 savings enrichment — OFF by default (fail-closed): a savings dollar figure must be
            // verified against the live box before it is trusted. Additionally unit-gated: the
            // sourced costPerUnit is only a true PER-UNIT value when the catalog has a dedicated
            // per-unit column; otherwise it is a pack cost and (per-unit baseline − pack sourced)
            // would be garbage, so enrichment is suppressed (Codex blocker — never emit a
            // unit-unsafe number).
            var savingsUnitSafe = outcome.Schema.CostPerUnitColumn is not null;
            var savingsEnabled = _options.EnablePricingSavingsEnrichment && savingsUnitSafe;
            if (_options.EnablePricingSavingsEnrichment && !savingsUnitSafe)
                _logger.LogWarning(
                    "Pricing savings enrichment suppressed: catalog has no per-unit cost column, so sourced cost is per-pack (unit-unsafe).");

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
                savingsUnitSafe);
        }
        catch (OperationCanceledException)
        {
            await connection.DisposeAsync();
            throw;
        }
        catch (Exception ex)
        {
            await connection.DisposeAsync();
            _logger.LogWarning(ex, "SQL pricing lookup unavailable");
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
        csb["Encrypt"] = "true";
        csb["TrustServerCertificate"] = _options.SqlTrustServerCertificate.ToString();

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
public sealed class UiaFirstPricingJobExecutor : IPricingJobExecutor
{
    private readonly PricingJobRunner _runner;
    private readonly IIpcCommandClient _commandClient;
    private readonly AgentStateDb _db;
    private readonly ILogger<UiaFirstPricingJobExecutor> _logger;

    public UiaFirstPricingJobExecutor(
        PricingJobRunner runner,
        IIpcCommandClient commandClient,
        AgentStateDb db,
        ILogger<UiaFirstPricingJobExecutor> logger)
    {
        _runner = runner;
        _commandClient = commandClient;
        _db = db;
        _logger = logger;
    }

    public async Task<PricingJobExecutionResult> RunAsync(PricingJobSpec spec, CancellationToken ct)
    {
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
                "UiaFirstPricingJobExecutor: blind-run gate BLOCKED job {JobId} — {Error}",
                spec.JobId, preflight.Error);
            _db.UpsertPricingJob(spec, PricingJobStatus.Failed, 0, 0, 0);
            return new PricingJobExecutionResult(
                new PricingJobProgress(spec.JobId, 0, 0, 0, PricingJobStatus.Failed),
                Mode: "uia",
                Ok: false,
                Error: preflight.Error
                    ?? "Helper interactive pre-flight failed — refusing to drive the live screen");
        }

        _logger.LogInformation(
            "UiaFirstPricingJobExecutor: starting job {JobId} (UIA-via-IPC path; Excel={Path}; Helper session {Session})",
            spec.JobId, spec.ExcelPath, preflight.HelperSessionId);

        try
        {
            var progress = await _runner.RunAsync(spec, _commandClient, ct);
            var ok = progress.Status == PricingJobStatus.Completed;
            return new PricingJobExecutionResult(
                progress,
                Mode: "uia",
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
            _logger.LogError(ex, "UiaFirstPricingJobExecutor: job {JobId} threw at orchestration boundary", spec.JobId);
            _db.UpsertPricingJob(spec, PricingJobStatus.Failed, 0, 0, 0);
            return new PricingJobExecutionResult(
                new PricingJobProgress(spec.JobId, 0, 0, 0, PricingJobStatus.Failed),
                Mode: "uia",
                Ok: false,
                Error: "pricing job failed - see agent logs");
        }
    }
}
