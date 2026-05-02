using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SuavoAgent.Adapters.PioneerRx.Pricing;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Config;
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
    IAsyncDisposable? Lease)
{
    public static PricingLookupFactoryResult Success(
        ISupplierPriceLookup lookup,
        string mode,
        IAsyncDisposable? lease) =>
        new(true, lookup, mode, null, lease);

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

    public SqlFirstPricingJobExecutor(
        ExcelPricingReader reader,
        ExcelPricingWriter writer,
        AgentStateDb db,
        IPricingLookupFactory lookupFactory,
        ILoggerFactory loggerFactory)
    {
        _reader = reader;
        _writer = writer;
        _db = db;
        _lookupFactory = lookupFactory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SqlFirstPricingJobExecutor>();
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
        var runner = new SqlPricingJobRunner(
            _reader,
            _writer,
            _db,
            lookupResult.Lookup,
            _loggerFactory.CreateLogger<SqlPricingJobRunner>());

        var progress = await runner.RunAsync(spec, ct);
        var ok = progress.Status == PricingJobStatus.Completed;
        return new PricingJobExecutionResult(
            progress,
            lookupResult.Mode,
            ok,
            ok ? null : "pricing job failed - see agent logs");
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

            return PricingLookupFactoryResult.Success(
                lookup,
                "sql",
                new SqlConnectionLease(connection));
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
            InitialCatalog = string.IsNullOrWhiteSpace(pharmacy.SqlDatabase)
                ? "PioneerPharmacySystem"
                : pharmacy.SqlDatabase,
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
