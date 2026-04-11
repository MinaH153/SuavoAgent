// src/SuavoAgent.Core/Learning/SqlSchemaObserver.cs
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Learning;

/// <summary>
/// Discovers SQL Server schemas via INFORMATION_SCHEMA and DMVs.
/// DMV access (VIEW SERVER STATE) is optional — falls back to metadata-only.
/// All query text is processed through the fail-closed SqlTokenizer.
/// </summary>
public sealed class SqlSchemaObserver : ILearningObserver
{
    private readonly AgentStateDb _db;
    private readonly string _pharmacySalt;
    private readonly ILogger _logger;
    private volatile bool _running;
    private int _eventsCollected;
    private DateTimeOffset _lastActivity;
    private bool _hasDmvAccess;

    public string Name => "sql";
    public ObserverPhase ActivePhases => ObserverPhase.Discovery | ObserverPhase.Pattern | ObserverPhase.Model;

    public SqlSchemaObserver(AgentStateDb db, string pharmacySalt, ILogger<SqlSchemaObserver> logger)
    {
        _db = db;
        _pharmacySalt = pharmacySalt;
        _logger = logger;
    }

    public static string InferColumnPurpose(string columnName)
    {
        var lower = columnName.ToLowerInvariant();
        if (lower.EndsWith("id") || lower.EndsWith("_id")) return "identifier";
        if (lower.Contains("date") || lower.Contains("_at") || lower.Contains("time")
            || lower.Contains("created") || lower.Contains("updated")
            || lower.EndsWith("on")) return "temporal";
        if (lower.Contains("npi") || lower.Contains("dea") || lower.Contains("ndc")) return "regulatory";
        if (lower.Contains("price") || lower.Contains("amount") || lower.Contains("cost")
            || lower.Contains("quantity") || lower.Contains("total")) return "amount";
        if (lower.Contains("name") || lower.Contains("first") || lower.Contains("last")) return "name";
        if (lower.Contains("status") || lower.Contains("state") || lower.Contains("type")) return "status";
        return "unknown";
    }

    public static bool IsLikelyForeignKey(string columnName)
    {
        var lower = columnName.ToLowerInvariant();
        return (lower.EndsWith("id") || lower.EndsWith("_id")) && lower.Length > 2;
    }

    public async Task DiscoverSchemaAsync(string sessionId, SqlConnection conn, CancellationToken ct)
    {
        var serverHash = PhiScrubber.HmacHash(conn.DataSource, _pharmacySalt);

        // Full column catalog via INFORMATION_SCHEMA
        const string schemaQuery = """
            SELECT TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME, DATA_TYPE,
                   CHARACTER_MAXIMUM_LENGTH, IS_NULLABLE
            FROM INFORMATION_SCHEMA.COLUMNS
            ORDER BY TABLE_SCHEMA, TABLE_NAME, ORDINAL_POSITION
            """;

        await using var cmd = new SqlCommand(schemaQuery, conn);
        cmd.CommandTimeout = 30;
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var schema = reader.GetString(0);
            var table = reader.GetString(1);
            var column = reader.GetString(2);
            var dataType = reader.GetString(3);
            var maxLen = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4);
            var nullable = reader.GetString(5) == "YES";
            var purpose = InferColumnPurpose(column);

            _db.InsertDiscoveredSchema(sessionId, serverHash, conn.Database,
                schema, table, column, dataType, maxLen, nullable,
                isPk: false, isFk: IsLikelyForeignKey(column),
                fkTargetTable: null, fkTargetColumn: null, inferredPurpose: purpose);

            _eventsCollected++;
        }

        _db.AppendLearningAudit(sessionId, "sql", "discover",
            $"{conn.Database}:{_eventsCollected} columns", phiScrubbed: false);
        _lastActivity = DateTimeOffset.UtcNow;

        _logger.LogInformation("Schema discovery: {Count} columns cataloged from {Db}",
            _eventsCollected, conn.Database);
    }

    public async Task CheckDmvAccessAsync(SqlConnection conn, CancellationToken ct)
    {
        try
        {
            await using var cmd = new SqlCommand(
                "SELECT TOP 1 1 FROM sys.dm_exec_query_stats", conn);
            cmd.CommandTimeout = 5;
            await cmd.ExecuteScalarAsync(ct);
            _hasDmvAccess = true;
            _logger.LogInformation("DMV access confirmed (VIEW SERVER STATE available)");
        }
        catch
        {
            _hasDmvAccess = false;
            _logger.LogInformation("DMV access unavailable — metadata-only discovery");
        }
    }

    public bool HasDmvAccess => _hasDmvAccess;

    public async Task StartAsync(string sessionId, CancellationToken ct)
    {
        _running = true;
        _logger.LogInformation("SqlSchemaObserver started for session {Session}", sessionId);
        // Actual discovery triggered by LearningWorker with a SqlConnection
        await Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _running = false;
        return Task.CompletedTask;
    }

    public ObserverHealth CheckHealth() => new(
        Name, _running, _eventsCollected, 0, _lastActivity);

    public void Dispose() { _running = false; }
}
