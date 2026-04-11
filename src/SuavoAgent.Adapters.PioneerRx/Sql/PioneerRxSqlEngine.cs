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

        // Raw connection string — bypasses SqlConnectionStringBuilder which has
        // cross-compilation issues with Encrypt/TrustServerCertificate property setters.
        // PioneerRx SQL Server uses self-signed certs, so Encrypt=false is required.
        var parts = new List<string>
        {
            $"Data Source={server}",
            $"Initial Catalog={database}",
            "Connect Timeout=10",
            "Command Timeout=30",
            "Encrypt=true",
            "TrustServerCertificate=true",
            "Max Pool Size=1",
            "Min Pool Size=0"
        };

        if (!string.IsNullOrEmpty(sqlUser) && !string.IsNullOrEmpty(sqlPassword))
        {
            parts.Add($"User ID={sqlUser}");
            parts.Add($"Password={sqlPassword}");
            _logger.LogInformation("SQL using SQL Auth as {User}", sqlUser);
        }
        else
        {
            parts.Add("Integrated Security=true");
            _logger.LogInformation("SQL using Windows Auth");
        }

        _connectionString = string.Join(";", parts);
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

    private bool _itemJoinFailed;
    private bool _patientJoinFailed;
    private int _queryCount;
    private const int TierRecoveryInterval = 10; // retry full query every 10 cycles

    public async Task<IReadOnlyList<RxReadyForDelivery>> ReadReadyPrescriptionsAsync(CancellationToken ct)
    {
        if (_connection is null || _connection.State != System.Data.ConnectionState.Open)
            return Array.Empty<RxReadyForDelivery>();

        var statusGuids = _deliveryReadyGuids ?? FallbackGuids();

        // Auto-recover: periodically retry higher tiers in case failure was transient
        _queryCount++;
        if (_queryCount % TierRecoveryInterval == 0 && (_itemJoinFailed || _patientJoinFailed))
        {
            _logger.LogInformation("Tier recovery: retrying full query after {Count} cycles", _queryCount);
            _itemJoinFailed = false;
            _patientJoinFailed = false;
        }

        // Tier 1: Full query — Item + Patient (drug names + delivery address)
        if (!_itemJoinFailed && !_patientJoinFailed)
        {
            try
            {
                return await ExecuteDeliveryQueryAsync(
                    BuildFullDeliveryQuery(statusGuids.Count), statusGuids, ct);
            }
            catch (SqlException ex) when (ex.Number == 208)
            {
                _logger.LogWarning("Full query failed (invalid table) — trying without Patient join");
                _patientJoinFailed = true;
            }
        }

        // Tier 2: Item only (drug names, no patient)
        if (!_itemJoinFailed)
        {
            try
            {
                return await ExecuteDeliveryQueryAsync(
                    BuildDeliveryQuery(statusGuids.Count), statusGuids, ct);
            }
            catch (SqlException ex) when (ex.Number == 208)
            {
                _logger.LogWarning("Inventory.Item not found — falling back to base query");
                _itemJoinFailed = true;
            }
        }

        // Tier 3: Base query (Rx numbers only)
        return await ExecuteDeliveryQueryAsync(
            BuildDeliveryQueryBase(statusGuids.Count), statusGuids, ct);
    }

    private async Task<IReadOnlyList<RxReadyForDelivery>> ExecuteDeliveryQueryAsync(
        string query, IReadOnlyList<Guid> statusGuids, CancellationToken ct)
    {
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
    /// <summary>
    /// Full query: Rx + Item (drug name/NDC) + Patient (delivery address).
    /// Minimum necessary PHI: first name, last name initial, address, phone.
    /// </summary>
    public static string BuildFullDeliveryQuery(int statusCount)
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
    i.ItemName,
    i.NDC,
    i.DeaSchedule,
    per.FirstName,
    LEFT(per.LastName, 1) AS LastInitial,
    per.Phone1,
    per.Address1,
    per.Address2,
    per.City,
    per.State,
    per.Zip
FROM Prescription.RxTransaction rt
JOIN Prescription.Rx r ON rt.RxID = r.RxID
LEFT JOIN Inventory.Item i ON rt.DispensedItemID = i.ItemID
LEFT JOIN Person.Patient pat ON r.PatientID = pat.PatientID
LEFT JOIN Person.Person per ON pat.PersonID = per.PersonID
WHERE rt.RxTransactionStatusTypeID IN ({statusParams})
ORDER BY rt.DateFilled DESC";
    }

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
    i.ItemName,
    i.NDC,
    i.DeaSchedule
FROM Prescription.RxTransaction rt
JOIN Prescription.Rx r ON rt.RxID = r.RxID
LEFT JOIN Inventory.Item i ON rt.DispensedItemID = i.ItemID
WHERE rt.RxTransactionStatusTypeID IN ({statusParams})
ORDER BY rt.DateFilled DESC";
    }

    /// <summary>
    /// Base query without Inventory.Item join — fallback when table doesn't exist.
    /// </summary>
    public static string BuildDeliveryQueryBase(int statusCount)
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
    r.RxNumber
FROM Prescription.RxTransaction rt
JOIN Prescription.Rx r ON rt.RxID = r.RxID
WHERE rt.RxTransactionStatusTypeID IN ({statusParams})
ORDER BY rt.DateFilled DESC";
    }

    /// <summary>
    /// Discovers table schemas for Prescription.Rx and related tables.
    /// Runs once on connect — used to find medication/drug name columns.
    /// </summary>
    public async Task<Dictionary<string, List<string>>> DiscoverSchemaAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, List<string>>();
        if (_connection is null) return result;

        try
        {
            const string query = @"
SELECT TABLE_SCHEMA + '.' + TABLE_NAME AS full_name, COLUMN_NAME
FROM INFORMATION_SCHEMA.COLUMNS
WHERE (TABLE_SCHEMA = 'Prescription' AND TABLE_NAME IN ('Rx', 'RxTransaction'))
   OR (TABLE_NAME LIKE '%Medication%' OR TABLE_NAME LIKE '%Drug%' OR TABLE_NAME LIKE '%Item%')
ORDER BY full_name, ORDINAL_POSITION";

            await using var cmd = new SqlCommand(query, _connection);
            cmd.CommandTimeout = 10;
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                var table = reader.GetString(0);
                var column = reader.GetString(1);
                if (!result.ContainsKey(table))
                    result[table] = new List<string>();
                result[table].Add(column);
            }

            _logger.LogInformation("Schema discovery: found {TableCount} tables", result.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Schema discovery failed");
        }

        return result;
    }

    public static bool IsPhiColumn(string columnName) =>
        PioneerRxConstants.PhiColumnBlocklist.Contains(columnName);

    private static RxReadyForDelivery MapRxFromReader(SqlDataReader reader)
    {
        var deaSchedule = GetNullableInt(reader, "DeaSchedule");
        var isControlled = deaSchedule is >= 2 and <= 5;

        return new RxReadyForDelivery(
            RxNumber: GetIntOrDefault(reader, "RxNumber").ToString(),
            FillNumber: GetIntOrDefault(reader, "RefillNumber"),
            DrugName: GetStringOrDefault(reader, "ItemName"),
            Ndc: GetStringOrDefault(reader, "NDC"),
            Quantity: GetDecimalOrDefault(reader, "DispensedQuantity"),
            DaysSupply: GetIntOrDefault(reader, "DaysSupply"),
            StatusText: GetGuidOrDefault(reader, "RxTransactionStatusTypeID").ToString(),
            IsControlled: isControlled,
            DrugSchedule: deaSchedule,
            PatientIdRequired: isControlled && deaSchedule <= 3,
            CounselingRequired: false,
            DetectedAt: DateTimeOffset.UtcNow,
            Source: DetectionSource.Sql,
            PatientFirstName: GetStringOrDefault(reader, "FirstName"),
            PatientLastInitial: GetStringOrDefault(reader, "LastInitial"),
            PatientPhone: GetStringOrDefault(reader, "Phone1"),
            DeliveryAddress1: GetStringOrDefault(reader, "Address1"),
            DeliveryAddress2: GetStringOrDefault(reader, "Address2"),
            DeliveryCity: GetStringOrDefault(reader, "City"),
            DeliveryState: GetStringOrDefault(reader, "State"),
            DeliveryZip: GetStringOrDefault(reader, "Zip"));
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

    private static int? GetNullableInt(SqlDataReader reader, string column)
    {
        try { var ord = reader.GetOrdinal(column); return reader.IsDBNull(ord) ? null : reader.GetInt32(ord); }
        catch { return null; }
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
