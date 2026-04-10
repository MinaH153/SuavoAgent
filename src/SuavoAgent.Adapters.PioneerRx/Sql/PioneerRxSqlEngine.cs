using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Models;

namespace SuavoAgent.Adapters.PioneerRx.Sql;

public sealed class PioneerRxSqlEngine : IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<PioneerRxSqlEngine> _logger;
    private SqlConnection? _connection;
    private IReadOnlyList<Guid>? _deliveryReadyGuids;

    public bool IsConnected => _connection?.State == System.Data.ConnectionState.Open;

    public PioneerRxSqlEngine(string server, string database, ILogger<PioneerRxSqlEngine> logger,
        string? sqlUser = null, string? sqlPassword = null)
    {
        _logger = logger;
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            ConnectTimeout = 10,
            CommandTimeout = 30,
            Encrypt = SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = true,
            MaxPoolSize = 1, // CRITICAL: one connection only — never disrupt pharmacy
            MinPoolSize = 0
        };

        if (!string.IsNullOrEmpty(sqlUser) && !string.IsNullOrEmpty(sqlPassword))
        {
            builder.IntegratedSecurity = false;
            builder.UserID = sqlUser;
            builder.Password = sqlPassword;
            _logger.LogInformation("SQL using SQL Auth as {User}", sqlUser);
        }
        else
        {
            builder.IntegratedSecurity = true;
            _logger.LogInformation("SQL using Windows Auth");
        }

        _connectionString = builder.ConnectionString;
    }

    public async Task<bool> TryConnectAsync(CancellationToken ct)
    {
        try
        {
            _connection = new SqlConnection(_connectionString);
            await _connection.OpenAsync(ct);
            _logger.LogInformation("SQL connected to {DataSource}", _connection.DataSource);

            _deliveryReadyGuids = await DiscoverStatusGuidsAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SQL connection failed");
            _connection?.Dispose();
            _connection = null;
            return false;
        }
    }

    /// <summary>
    /// Discovers delivery-ready status GUIDs from the lookup table.
    /// Falls back to Care Pharmacy known GUIDs if discovery fails.
    /// Called once on connect — never re-queries.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> DiscoverStatusGuidsAsync(CancellationToken ct)
    {
        if (_connection is null)
            return FallbackGuids();

        try
        {
            var statusParams = string.Join(", ",
                Enumerable.Range(0, PioneerRxConstants.DeliveryReadyStatusNames.Count)
                    .Select(i => $"@s{i}"));

            var query = $@"SELECT RxTransactionStatusTypeID, Description
FROM Prescription.RxTransactionStatusType
WHERE Description IN ({statusParams})";

            await using var cmd = new SqlCommand(query, _connection);
            cmd.CommandTimeout = 10;
            for (int i = 0; i < PioneerRxConstants.DeliveryReadyStatusNames.Count; i++)
                cmd.Parameters.AddWithValue($"@s{i}", PioneerRxConstants.DeliveryReadyStatusNames[i]);

            var guids = new List<Guid>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var guid = reader.GetGuid(reader.GetOrdinal("RxTransactionStatusTypeID"));
                var desc = reader.GetString(reader.GetOrdinal("Description"));
                guids.Add(guid);
                _logger.LogInformation("Discovered status: {Description} = {Guid}", desc, guid);
            }

            if (guids.Count > 0)
            {
                _logger.LogInformation("Discovered {Count} delivery-ready status GUIDs from lookup table", guids.Count);
                return guids;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Status GUID discovery failed, using fallback GUIDs");
        }

        return FallbackGuids();
    }

    public async Task<IReadOnlyList<RxReadyForDelivery>> ReadReadyPrescriptionsAsync(CancellationToken ct)
    {
        if (_connection is null || _connection.State != System.Data.ConnectionState.Open)
            return Array.Empty<RxReadyForDelivery>();

        var statusGuids = _deliveryReadyGuids ?? FallbackGuids();
        var query = BuildDeliveryQuery(statusGuids.Count);

        await using var cmd = new SqlCommand(query, _connection);
        cmd.CommandTimeout = 30;
        for (int i = 0; i < statusGuids.Count; i++)
            cmd.Parameters.AddWithValue($"@status{i}", statusGuids[i]);

        var results = new List<RxReadyForDelivery>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
            results.Add(MapRxFromReader(reader));

        _logger.LogDebug("Read {Count} ready prescriptions from SQL", results.Count);
        return results;
    }

    /// <summary>
    /// Builds the delivery-ready Rx query using the real PioneerRx schema.
    /// Prescription.RxTransaction (workflow) JOIN Prescription.Rx (master Rx).
    /// Status is GUID — parameterized, not string.
    /// No PHI columns (no Patient join).
    /// </summary>
    public static string BuildDeliveryQuery(int statusCount)
    {
        var statusParams = string.Join(", ",
            Enumerable.Range(0, statusCount).Select(i => $"@status{i}"));

        return $@"SELECT TOP 50
    rt.RxTransactionID,
    rt.DateFilled,
    rt.RefillNumber,
    rt.DispensedQuantity,
    rt.DaysSupply,
    rt.RxTransactionStatusTypeID,
    rt.PromiseTime,
    r.RxNumber,
    r.MedicationDescription,
    r.PrescribedNDC,
    r.DispensedNDC
FROM Prescription.RxTransaction rt
JOIN Prescription.Rx r ON rt.RxID = r.RxID
WHERE rt.RxTransactionStatusTypeID IN ({statusParams})
    AND rt.DateFilled >= DATEADD(day, -7, CAST(GETDATE() AS DATE))
ORDER BY rt.DateFilled DESC";
    }

    public static bool IsPhiColumn(string columnName) =>
        PioneerRxConstants.PhiColumnBlocklist.Contains(columnName);

    private static RxReadyForDelivery MapRxFromReader(SqlDataReader reader)
    {
        var dispensedNdc = GetStringOrDefault(reader, "DispensedNDC");
        var prescribedNdc = GetStringOrDefault(reader, "PrescribedNDC");

        return new RxReadyForDelivery(
            RxNumber: GetIntOrDefault(reader, "RxNumber").ToString(),
            FillNumber: GetIntOrDefault(reader, "RefillNumber"),
            DrugName: GetStringOrDefault(reader, "MedicationDescription"),
            Ndc: dispensedNdc.Length > 0 ? dispensedNdc : prescribedNdc,
            Quantity: GetDecimalOrDefault(reader, "DispensedQuantity"),
            DaysSupply: GetIntOrDefault(reader, "DaysSupply"),
            StatusText: GetGuidOrDefault(reader, "RxTransactionStatusTypeID").ToString(),
            IsControlled: false, // TODO: join Drug table for DEA schedule
            DrugSchedule: null,
            PatientIdRequired: false,
            CounselingRequired: false,
            DetectedAt: DateTimeOffset.UtcNow,
            Source: DetectionSource.Sql);
    }

    private static IReadOnlyList<Guid> FallbackGuids() =>
        PioneerRxConstants.FallbackStatusGuids.Values.ToList();

    private static string GetStringOrDefault(SqlDataReader reader, string column)
    {
        try { var ord = reader.GetOrdinal(column); return reader.IsDBNull(ord) ? "" : reader.GetString(ord); }
        catch { return ""; }
    }

    private static int GetIntOrDefault(SqlDataReader reader, string column)
    {
        try { var ord = reader.GetOrdinal(column); return reader.IsDBNull(ord) ? 0 : reader.GetInt32(ord); }
        catch { return 0; }
    }

    private static decimal GetDecimalOrDefault(SqlDataReader reader, string column)
    {
        try { var ord = reader.GetOrdinal(column); return reader.IsDBNull(ord) ? 0m : reader.GetDecimal(ord); }
        catch { return 0m; }
    }

    private static Guid GetGuidOrDefault(SqlDataReader reader, string column)
    {
        try { var ord = reader.GetOrdinal(column); return reader.IsDBNull(ord) ? Guid.Empty : reader.GetGuid(ord); }
        catch { return Guid.Empty; }
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }
}
