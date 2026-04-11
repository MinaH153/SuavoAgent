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
public sealed class LearnedPmsAdapter : ILocalPmsAdapter
{
    private readonly string _connectionString;
    private readonly ILogger _logger;
    private SqlConnection? _conn;

    public string PmsName { get; }
    public string DetectionQuery { get; }
    public string RxNumberColumn { get; }
    public string StatusColumn { get; }
    public IReadOnlyList<string> DeliveryReadyStatuses { get; }

    public LearnedPmsAdapter(
        string pmsName,
        string connectionString,
        string detectionQuery,
        string rxNumberColumn,
        string statusColumn,
        IReadOnlyList<string> deliveryReadyStatuses,
        ILogger logger)
    {
        PmsName = pmsName;
        _connectionString = connectionString;
        DetectionQuery = detectionQuery;
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

    public async Task<IReadOnlyList<RxReadyForDelivery>> PullReadyAsync(string? cursor, CancellationToken ct)
    {
        var results = new List<RxReadyForDelivery>();
        try
        {
            if (_conn is null || _conn.State != System.Data.ConnectionState.Open)
            {
                _conn = new SqlConnection(_connectionString);
                await _conn.OpenAsync(ct);
            }

            await using var cmd = new SqlCommand(DetectionQuery, _conn);
            cmd.CommandTimeout = 30;
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Learned adapter detection failed");
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
}
