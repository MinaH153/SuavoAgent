using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Adapters;
using SuavoAgent.Contracts.Health;
using SuavoAgent.Contracts.Models;

namespace SuavoAgent.Core.Learning;

/// <summary>
/// An ILocalPmsAdapter generated from an approved POM.
/// Queries the Rx table using learned column names and delivery-ready status values.
/// Read-only -- writebacks deferred to Plan 4 (needs writeback column discovery).
/// </summary>
public sealed class LearnedPmsAdapter : ILocalPmsAdapter, IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _connLock = new(1, 1);
    private SqlConnection? _conn;

    public string PmsName { get; }
    public string DetectionQuery { get; }
    public IReadOnlyDictionary<string, string> StatusParameters { get; }
    public string RxNumberColumn { get; }
    public string StatusColumn { get; }
    public IReadOnlyList<string> DeliveryReadyStatuses { get; }

    public LearnedPmsAdapter(
        string pmsName,
        string connectionString,
        string detectionQuery,
        IReadOnlyDictionary<string, string> statusParameters,
        string rxNumberColumn,
        string statusColumn,
        IReadOnlyList<string> deliveryReadyStatuses,
        ILogger logger)
    {
        PmsName = pmsName;
        _connectionString = connectionString;
        DetectionQuery = detectionQuery;
        StatusParameters = statusParameters;
        RxNumberColumn = rxNumberColumn;
        StatusColumn = statusColumn;
        DeliveryReadyStatuses = deliveryReadyStatuses;
        _logger = logger;
    }

    public Task<CapabilityManifest> DiscoverCapabilitiesAsync(CancellationToken ct)
    {
        return Task.FromResult(new CapabilityManifest(
            CanReadSql: true,
            CanReadApi: false,
            CanWritebackApi: false,
            CanWritebackUia: false,
            CanReceiveEvents: false,
            PmsVersion: null,
            SqlServerEndpoint: null,
            ApiEndpoint: null,
            DiscoveredScreens: Array.Empty<string>()));
    }

    // Transient SQL error classes: connection, timeout, transport
    private static readonly HashSet<int> TransientErrorNumbers = new()
    {
        -2, 20, 64, 233, 10053, 10054, 10060, 40143, 40197, 40501, 40613, 49918, 49919, 49920,
    };

    private static bool IsTransient(SqlException ex) =>
        ex.Errors.Cast<SqlError>().Any(e => TransientErrorNumbers.Contains(e.Number));

    public async Task<IReadOnlyList<RxReadyForDelivery>> PullReadyAsync(string? cursor, CancellationToken ct)
    {
        var results = new List<RxReadyForDelivery>();
        try
        {
            await _connLock.WaitAsync(ct);
            try
            {
                if (_conn is null || _conn.State != System.Data.ConnectionState.Open)
                {
                    _conn?.Dispose();
                    _conn = new SqlConnection(_connectionString);
                    await _conn.OpenAsync(ct);
                }
            }
            finally { _connLock.Release(); }

            await using var cmd = new SqlCommand(DetectionQuery, _conn);
            cmd.CommandTimeout = 30;
            foreach (var (name, value) in StatusParameters)
                cmd.Parameters.AddWithValue(name, value);
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                var rxNum = reader[RxNumberColumn]?.ToString() ?? "";
                results.Add(new RxReadyForDelivery(
                    RxNumber: rxNum,
                    FillNumber: 0,
                    DrugName: "",
                    Ndc: "",
                    Quantity: 0,
                    DaysSupply: 0,
                    StatusText: reader[StatusColumn]?.ToString() ?? "",
                    IsControlled: false,
                    DrugSchedule: null,
                    PatientIdRequired: false,
                    CounselingRequired: false,
                    DetectedAt: DateTimeOffset.UtcNow,
                    Source: DetectionSource.Sql));
            }

            _logger.LogInformation("Learned adapter detected {Count} delivery-ready Rxs", results.Count);
        }
        catch (SqlException ex) when (IsTransient(ex))
        {
            _logger.LogWarning(ex, "Transient SQL error in learned adapter — will retry next cycle");
            _conn?.Dispose();
            _conn = null;
            throw; // Bubble up so caller knows this cycle failed
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Non-transient learned adapter detection error");
        }

        return results;
    }

    public Task<WritebackReceipt> SubmitWritebackAsync(DeliveryWritebackCommand cmd, CancellationToken ct)
    {
        // Writeback not supported by learned adapters yet
        return Task.FromResult(new WritebackReceipt(
            Success: false,
            TransactionId: null,
            Error: "Writeback not supported by learned adapter",
            Method: WritebackMethod.Manual,
            Verified: false,
            CompletedAt: DateTimeOffset.UtcNow));
    }

    public Task<bool> VerifyWritebackAsync(WritebackReceipt receipt, CancellationToken ct)
    {
        return Task.FromResult(false);
    }

    public Task<AdapterHealthReport> CheckHealthAsync(CancellationToken ct)
    {
        return Task.FromResult(new AdapterHealthReport(
            AdapterName: PmsName,
            IsHealthy: _conn?.State == System.Data.ConnectionState.Open,
            SqlStatus: _conn?.State == System.Data.ConnectionState.Open ? "connected" : "disconnected",
            UiaStatus: null,
            ApiStatus: null,
            CheckedAt: DateTimeOffset.UtcNow,
            Details: null));
    }

    public void Dispose()
    {
        _conn?.Dispose();
        _conn = null;
        _connLock.Dispose();
    }
}
