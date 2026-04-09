using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Models;

namespace SuavoAgent.Adapters.PioneerRx.Sql;

public sealed class PioneerRxSqlEngine : IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<PioneerRxSqlEngine> _logger;
    private SqlConnection? _connection;

    public bool IsConnected => _connection?.State == System.Data.ConnectionState.Open;

    public PioneerRxSqlEngine(string server, string database, ILogger<PioneerRxSqlEngine> logger)
    {
        _logger = logger;
        _connectionString = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            IntegratedSecurity = true,
            ConnectTimeout = 10,
            CommandTimeout = 30,
            Encrypt = SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = true
        }.ConnectionString;
    }

    public async Task<bool> TryConnectAsync(CancellationToken ct)
    {
        try
        {
            _connection = new SqlConnection(_connectionString);
            await _connection.OpenAsync(ct);
            _logger.LogInformation("SQL connected to {DataSource}", _connection.DataSource);
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

    public async Task<IReadOnlyList<RxReadyForDelivery>> ReadReadyPrescriptionsAsync(
        IReadOnlyList<string> availableColumns, CancellationToken ct)
    {
        if (_connection is null || _connection.State != System.Data.ConnectionState.Open)
            return Array.Empty<RxReadyForDelivery>();

        var query = BuildReadyRxQuery(availableColumns, PioneerRxConstants.ReadyStatusValues);
        var parameters = BuildStatusParameters(PioneerRxConstants.ReadyStatusValues);

        await using var cmd = new SqlCommand(query, _connection);
        cmd.CommandTimeout = 30;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);

        var results = new List<RxReadyForDelivery>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            results.Add(new RxReadyForDelivery(
                RxNumber: GetStringOrDefault(reader, "RxNumber"),
                FillNumber: GetIntOrDefault(reader, "FillNumber"),
                DrugName: GetStringOrDefault(reader, "DrugName"),
                Ndc: GetStringOrDefault(reader, "NDC"),
                Quantity: GetDecimalOrDefault(reader, "Quantity"),
                DaysSupply: GetIntOrDefault(reader, "DaysSupply"),
                StatusText: GetStringOrDefault(reader, "Status"),
                IsControlled: GetBoolOrDefault(reader, "IsControlled"),
                DrugSchedule: GetNullableInt(reader, "DrugSchedule"),
                PatientIdRequired: GetBoolOrDefault(reader, "PatientIDRequired"),
                CounselingRequired: GetBoolOrDefault(reader, "CounselingRequired"),
                DetectedAt: DateTimeOffset.UtcNow,
                Source: DetectionSource.Sql));
        }

        _logger.LogDebug("Read {Count} ready prescriptions from SQL", results.Count);
        return results;
    }

    public static string BuildReadyRxQuery(
        IEnumerable<string> availableColumns,
        IReadOnlyList<string> statusValues)
    {
        var safeCols = availableColumns
            .Where(c => !IsPhiColumn(c))
            .ToList();

        if (safeCols.Count == 0)
            safeCols.Add("RxNumber");

        var statusParams = string.Join(", ",
            Enumerable.Range(0, statusValues.Count).Select(i => $"@status{i}"));

        return $"SELECT TOP 50 {string.Join(", ", safeCols)} " +
               $"FROM dbo.Prescription " +
               $"WHERE Status IN ({statusParams}) " +
               $"AND CAST(DateCreated AS DATE) >= DATEADD(day, -7, CAST(GETDATE() AS DATE)) " +
               $"ORDER BY DateCreated DESC";
    }

    public static bool IsPhiColumn(string columnName) =>
        PioneerRxConstants.PhiColumnBlocklist.Contains(columnName);

    private static IReadOnlyList<(string Name, string Value)> BuildStatusParameters(
        IReadOnlyList<string> statusValues) =>
        statusValues.Select((v, i) => ($"@status{i}", v)).ToList();

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

    private static bool GetBoolOrDefault(SqlDataReader reader, string column)
    {
        try { var ord = reader.GetOrdinal(column); return !reader.IsDBNull(ord) && reader.GetBoolean(ord); }
        catch { return false; }
    }

    private static int? GetNullableInt(SqlDataReader reader, string column)
    {
        try { var ord = reader.GetOrdinal(column); return reader.IsDBNull(ord) ? null : reader.GetInt32(ord); }
        catch { return null; }
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }
}
