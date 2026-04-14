using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Canary;
using SuavoAgent.Contracts.Models;

namespace SuavoAgent.Adapters.PioneerRx.Sql;

public sealed class PioneerRxSqlEngine : IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<PioneerRxSqlEngine> _logger;
    private SqlConnection? _connection;
    private IReadOnlyList<Guid>? _deliveryReadyGuids;
    private Dictionary<string, Guid>? _allStatusGuids;

    public IReadOnlyDictionary<string, Guid>? GetAllDiscoveredGuids() => _allStatusGuids;

    public bool IsConnected => _connection?.State == System.Data.ConnectionState.Open;

    /// Returns the physical connection identity for canary connection guard.
    public Guid? ConnectionId => _connection?.ClientConnectionId;

    public PioneerRxSqlEngine(string server, string database, ILogger<PioneerRxSqlEngine> logger,
        string? sqlUser = null, string? sqlPassword = null, bool trustServerCertificate = true)
    {
        _logger = logger;

        var csb = new SqlConnectionStringBuilder();
        csb.DataSource = server;
        csb.InitialCatalog = database;
        csb.ApplicationName = "SuavoAgent";
        csb.ConnectTimeout = 30;
        csb.MaxPoolSize = 1;
        csb.MinPoolSize = 0;
        // Use indexer for Encrypt/TrustServerCertificate (cross-compile safe)
        csb["Encrypt"] = "true";
        csb["TrustServerCertificate"] = trustServerCertificate.ToString();
        if (trustServerCertificate)
            _logger.LogWarning("SQL TrustServerCertificate=true — MITM risk on untrusted networks (HIPAA 164.312(e)(1))");

        if (!string.IsNullOrEmpty(sqlUser) && !string.IsNullOrEmpty(sqlPassword))
        {
            csb.UserID = sqlUser;
            csb.Password = sqlPassword;
            _logger.LogInformation("SQL using SQL Auth as {User}", sqlUser);
        }
        else
        {
            csb.IntegratedSecurity = true;
            _logger.LogInformation("SQL using Windows Auth");
        }

        _connectionString = csb.ConnectionString;
    }

    public async Task<bool> TryConnectAsync(CancellationToken ct)
    {
        try
        {
            _connection = new SqlConnection(_connectionString);
            await _connection.OpenAsync(ct);
            _logger.LogInformation("SQL connected to {DataSource}", _connection.DataSource);

            _deliveryReadyGuids = await DiscoverStatusGuidsAsync(ct);
            await DiscoverAllStatusGuidsAsync(ct);
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
    /// Fails explicitly if discovery fails — no fallback to hardcoded GUIDs.
    /// Called once on connect — never re-queries.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> DiscoverStatusGuidsAsync(CancellationToken ct)
    {
        if (_connection is null)
        {
            _logger.LogError("Cannot discover status GUIDs — no SQL connection");
            return Array.Empty<Guid>();
        }

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

            // Exact match returned nothing — try pattern-based discovery
            _logger.LogInformation("Exact status name match returned 0 results — trying pattern-based discovery");
            var patternQuery = "SELECT RxTransactionStatusTypeID, Description FROM Prescription.RxTransactionStatusType";
            await using var patCmd = new SqlCommand(patternQuery, _connection);
            patCmd.CommandTimeout = 10;
            await using var patReader = await patCmd.ExecuteReaderAsync(ct);
            while (await patReader.ReadAsync(ct))
            {
                var desc = patReader.GetString(patReader.GetOrdinal("Description"));
                if (PioneerRxConstants.MatchesDeliveryReadyPattern(desc))
                {
                    var guid = patReader.GetGuid(patReader.GetOrdinal("RxTransactionStatusTypeID"));
                    guids.Add(guid);
                    _logger.LogInformation("Pattern-matched status: {Description} = {Guid}", desc, guid);
                }
            }
            if (guids.Count > 0)
            {
                _logger.LogInformation("Pattern-discovered {Count} delivery-ready status GUIDs", guids.Count);
                return guids;
            }

            _logger.LogError("No delivery-ready status GUIDs found in lookup table — detection will be disabled until GUIDs are discoverable");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Status GUID discovery failed — detection disabled until SQL is reachable and status table is queryable");
        }

        return Array.Empty<Guid>();
    }

    /// <summary>
    /// Discovers ALL 5 delivery-related status GUIDs (including writeback targets).
    /// Called once on connect, stored in _allStatusGuids for writeback engine use.
    /// </summary>
    private async Task DiscoverAllStatusGuidsAsync(CancellationToken ct)
    {
        if (_connection is null) return;

        try
        {
            // Discover ALL delivery-related GUIDs (including writeback targets)
            _allStatusGuids = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

            var allStatusParams = string.Join(", ",
                Enumerable.Range(0, PioneerRxConstants.AllDeliveryStatusNames.Count)
                    .Select(i => $"@all{i}"));

            var allQuery = $@"SELECT Description, RxTransactionStatusTypeID
FROM Prescription.RxTransactionStatusType
WHERE Description IN ({allStatusParams})
ORDER BY Description";

            await using var allCmd = new SqlCommand(allQuery, _connection);
            allCmd.CommandTimeout = 10;
            for (int i = 0; i < PioneerRxConstants.AllDeliveryStatusNames.Count; i++)
                allCmd.Parameters.AddWithValue($"@all{i}", PioneerRxConstants.AllDeliveryStatusNames[i]);

            await using var allReader = await allCmd.ExecuteReaderAsync(ct);
            while (await allReader.ReadAsync(ct))
            {
                var desc = allReader.GetString(0);
                var guid = allReader.GetGuid(1);
                _allStatusGuids[desc] = guid;
                _logger.LogDebug("Discovered status GUID: {Description} = {Guid}", desc, guid);
            }

            _logger.LogInformation("Discovered {Count}/5 delivery status GUIDs", _allStatusGuids.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "All-status GUID discovery failed");
        }

        // Satisfy the compiler — unreachable but required by the original method signature split
        return;
    }

    private bool _itemJoinFailed;
    private bool _patientJoinFailed;
    private int _queryCount;
    private const int TierRecoveryInterval = 10; // retry full query every 10 cycles

    public async Task<IReadOnlyList<RxReadyForDelivery>> ReadReadyPrescriptionsAsync(CancellationToken ct)
    {
        if (_connection is null || _connection.State != System.Data.ConnectionState.Open)
            return Array.Empty<RxReadyForDelivery>();

        if (_deliveryReadyGuids is null || _deliveryReadyGuids.Count == 0)
        {
            _logger.LogDebug("No delivery-ready GUIDs discovered — skipping detection cycle");
            return Array.Empty<RxReadyForDelivery>();
        }
        var statusGuids = _deliveryReadyGuids;

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
    public static string BuildFullDeliveryQuery(int statusCount, int batchSize = 100)
    {
        var statusParams = string.Join(", ",
            Enumerable.Range(0, statusCount).Select(i => $"@status{i}"));

        return $@"SELECT TOP {batchSize}
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

    public static string BuildDeliveryQuery(int statusCount, int batchSize = 100)
    {
        var statusParams = string.Join(", ",
            Enumerable.Range(0, statusCount).Select(i => $"@status{i}"));

        return $@"SELECT TOP {batchSize}
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
    public static string BuildDeliveryQueryBase(int statusCount, int batchSize = 100)
    {
        var statusParams = string.Join(", ",
            Enumerable.Range(0, statusCount).Select(i => $"@status{i}"));

        return $@"SELECT TOP {batchSize}
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
    /// PHI-free metadata query — no Person JOIN, no patient data.
    /// Used for detection polling (HIPAA 164.502(b) minimum necessary).
    /// </summary>
    public static string BuildMetadataQuery(IReadOnlyList<string> statusNames, int batchSize = 100)
    {
        var statusParams = string.Join(", ", statusNames.Select((_, i) => $"@status{i}"));
        return $"""
            SELECT TOP {batchSize}
                r.RxNumber,
                rt.DateFilled,
                rt.DispensedQuantity,
                i.ItemName AS TradeName,
                i.NDC,
                rt.RxTransactionStatusTypeID AS StatusGuid
            FROM Prescription.RxTransaction rt
            JOIN Prescription.Rx r ON rt.RxID = r.RxID
            LEFT JOIN Inventory.Item i ON rt.DispensedItemID = i.ItemID
            LEFT JOIN Prescription.RxTransactionStatusType st ON rt.RxTransactionStatusTypeID = st.RxTransactionStatusTypeID
            WHERE st.Description IN ({statusParams})
              AND rt.DateFilled >= @cutoff
            ORDER BY rt.DateFilled DESC
            """;
    }

    /// <summary>
    /// PHI-free detection query — returns only operational metadata.
    /// HIPAA 164.502(b) minimum necessary: no Person JOIN, no patient data.
    /// </summary>
    public async Task<IReadOnlyList<RxMetadata>> ReadReadyMetadataAsync(CancellationToken ct)
    {
        if (_connection is null || _connection.State != System.Data.ConnectionState.Open)
            return Array.Empty<RxMetadata>();

        var statusNames = PioneerRxConstants.DeliveryReadyStatusNames;
        var query = BuildMetadataQuery(statusNames);

        await using var cmd = new SqlCommand(query, _connection);
        cmd.CommandTimeout = 30;
        for (int i = 0; i < statusNames.Count; i++)
            cmd.Parameters.AddWithValue($"@status{i}", statusNames[i]);
        cmd.Parameters.AddWithValue("@cutoff", DateTime.UtcNow.AddDays(-30));

        var results = new List<RxMetadata>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new RxMetadata(
                RxNumber: GetIntOrDefault(reader, "RxNumber").ToString(),
                DrugName: GetStringOrDefault(reader, "TradeName"),
                Ndc: GetStringOrDefault(reader, "NDC"),
                DateFilled: reader.IsDBNull(reader.GetOrdinal("DateFilled"))
                    ? null : reader.GetDateTime(reader.GetOrdinal("DateFilled")),
                Quantity: GetDecimalOrDefault(reader, "DispensedQuantity"),
                StatusGuid: GetGuidOrDefault(reader, "StatusGuid"),
                DetectedAt: DateTimeOffset.UtcNow));
        }

        _logger.LogDebug("Read {Count} ready metadata (PHI-free) from SQL", results.Count);
        return results;
    }

    /// <summary>
    /// Targeted patient fetch — PHI returned only for a single approved Rx.
    /// Called on-demand after delivery approval (HIPAA minimum necessary).
    /// </summary>
    public static string BuildPatientQuery()
    {
        return """
            SELECT TOP 1
                per.FirstName,
                LEFT(per.LastName, 1) AS LastInitial,
                per.Phone1 AS Phone,
                per.Address1,
                per.Address2,
                per.City,
                per.State,
                per.Zip
            FROM Prescription.RxTransaction rt
            JOIN Prescription.Rx r ON rt.RxID = r.RxID
            LEFT JOIN Person.Patient pat ON r.PatientID = pat.PatientID
            JOIN Person.Person per ON pat.PersonID = per.PersonID
            WHERE r.RxNumber = @rxNumber
            """;
    }

    public async Task<RxPatientDetails?> PullPatientForRxAsync(string rxNumber, CancellationToken ct)
    {
        if (_connection is null || _connection.State != System.Data.ConnectionState.Open)
            return null;

        await using var cmd = new SqlCommand(BuildPatientQuery(), _connection);
        cmd.CommandTimeout = 15;
        cmd.Parameters.AddWithValue("@rxNumber", rxNumber);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new RxPatientDetails(
            RxNumber: rxNumber,
            FirstName: GetStringOrDefault(reader, "FirstName"),
            LastInitial: GetStringOrDefault(reader, "LastInitial"),
            Phone: GetStringOrDefault(reader, "Phone"),
            Address1: GetStringOrDefault(reader, "Address1"),
            Address2: GetStringOrDefault(reader, "Address2"),
            City: GetStringOrDefault(reader, "City"),
            State: GetStringOrDefault(reader, "State"),
            Zip: GetStringOrDefault(reader, "Zip"));
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
   OR (TABLE_SCHEMA = 'Inventory' AND TABLE_NAME IN ('Item', 'ItemMaster'))
   OR (TABLE_SCHEMA = 'Person' AND TABLE_NAME IN ('Person', 'Address', 'Phone'))
   OR (TABLE_NAME LIKE '%Medication%' OR TABLE_NAME LIKE '%Drug%')
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

    public async Task<IReadOnlyList<ObservedObject>> QueryContractMetadataAsync(
        IReadOnlyList<(string Schema, string Table, string Column, bool IsRequired)> contractedObjects,
        CancellationToken ct)
    {
        if (_connection is null || _connection.State != System.Data.ConnectionState.Open)
            return Array.Empty<ObservedObject>();

        var conditions = new List<string>();
        for (int i = 0; i < contractedObjects.Count; i++)
            conditions.Add($"(s.name = @schema{i} AND o.name = @table{i} AND c.name = @col{i})");

        var query = $"""
            SELECT s.name AS schema_name, o.name AS table_name, c.name AS column_name,
                   t.name AS type_name, c.max_length, c.is_nullable
            FROM sys.columns c
            JOIN sys.objects o ON c.object_id = o.object_id
            JOIN sys.schemas s ON o.schema_id = s.schema_id
            JOIN sys.types t ON c.user_type_id = t.user_type_id
            WHERE ({string.Join(" OR ", conditions)})
            ORDER BY s.name, o.name, c.name
            """;

        await using var cmd = new SqlCommand(query, _connection);
        cmd.CommandTimeout = 10;
        for (int i = 0; i < contractedObjects.Count; i++)
        {
            cmd.Parameters.AddWithValue($"@schema{i}", contractedObjects[i].Schema);
            cmd.Parameters.AddWithValue($"@table{i}", contractedObjects[i].Table);
            cmd.Parameters.AddWithValue($"@col{i}", contractedObjects[i].Column);
        }

        var results = new List<ObservedObject>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var schemaName = reader.GetString(0);
            var tableName = reader.GetString(1);
            var columnName = reader.GetString(2);
            var typeName = reader.GetString(3);
            var maxLen = reader.IsDBNull(4) ? (int?)null : (int)reader.GetInt16(4);
            var nullable = reader.GetBoolean(5);

            var isRequired = contractedObjects.Any(co =>
                co.Schema.Equals(schemaName, StringComparison.OrdinalIgnoreCase) &&
                co.Table.Equals(tableName, StringComparison.OrdinalIgnoreCase) &&
                co.Column.Equals(columnName, StringComparison.OrdinalIgnoreCase) &&
                co.IsRequired);

            results.Add(new ObservedObject(schemaName, tableName, columnName,
                typeName, maxLen, nullable, isRequired));
        }

        return results;
    }

    public async Task<IReadOnlyList<ObservedStatus>> QueryStatusMapAsync(
        IReadOnlyList<string> statusDescriptions, CancellationToken ct)
    {
        if (_connection is null || _connection.State != System.Data.ConnectionState.Open)
            return Array.Empty<ObservedStatus>();

        var statusParams = string.Join(", ",
            Enumerable.Range(0, statusDescriptions.Count).Select(i => $"@s{i}"));

        var query = $"""
            SELECT Description, RxTransactionStatusTypeID
            FROM Prescription.RxTransactionStatusType
            WHERE Description IN ({statusParams})
            ORDER BY Description, RxTransactionStatusTypeID
            """;

        await using var cmd = new SqlCommand(query, _connection);
        cmd.CommandTimeout = 10;
        for (int i = 0; i < statusDescriptions.Count; i++)
            cmd.Parameters.AddWithValue($"@s{i}", statusDescriptions[i]);

        var results = new List<ObservedStatus>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ObservedStatus(
                reader.GetString(0),
                reader.GetGuid(1).ToString()));
        }

        return results;
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

    // No fallback GUIDs — discovery must succeed or detection stays disabled

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
