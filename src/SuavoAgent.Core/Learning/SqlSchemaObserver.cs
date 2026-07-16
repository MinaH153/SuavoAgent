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
    private readonly bool _trustServerCertificate;
    private readonly string? _serverCertificateSha256;
    private readonly ILogger _logger;
    private volatile bool _running;
    private int _eventsCollected;
    private DateTimeOffset _lastActivity;
    private bool _hasDmvAccess;

    public string Name => "sql";
    public ObserverPhase ActivePhases => ObserverPhase.Discovery | ObserverPhase.Pattern | ObserverPhase.Model;

    public SqlSchemaObserver(
        AgentStateDb db,
        string pharmacySalt,
        ILogger<SqlSchemaObserver> logger,
        bool trustServerCertificate = false,
        string? serverCertificateSha256 = null)
    {
        _db = db;
        _pharmacySalt = pharmacySalt;
        _logger = logger;
        _trustServerCertificate = trustServerCertificate;
        _serverCertificateSha256 = serverCertificateSha256;
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
        var sourceIdentity = await SqlSourceIdentityVerifier.ComputeAsync(
            conn,
            _pharmacySalt,
            _trustServerCertificate,
            _serverCertificateSha256,
            ct);
        _db.BeginDiscoveredSchemaSnapshot(
            sessionId,
            sourceIdentity.Digest,
            sourceIdentity.DatabaseName);

        // Full column catalog via INFORMATION_SCHEMA
        const string schemaQuery = """
            SELECT TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME, DATA_TYPE,
                   CHARACTER_MAXIMUM_LENGTH, IS_NULLABLE
            FROM INFORMATION_SCHEMA.COLUMNS
            ORDER BY TABLE_SCHEMA, TABLE_NAME, ORDINAL_POSITION
            """;

        try
        {
            await using (var cmd = new SqlCommand(schemaQuery, conn))
            {
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

                    _db.InsertDiscoveredSchema(sessionId, sourceIdentity.Digest, conn.Database,
                        schema, table, column, dataType, maxLen, nullable,
                        isPk: false, isFk: false,
                        fkTargetTable: null, fkTargetColumn: null, inferredPurpose: purpose);

                    _eventsCollected++;
                }
            }

            // Only enabled, trusted, single-column constraints and single-column
            // unique keys are eligible. The snapshot is marked complete only
            // after both catalog passes finish; a crash/failure leaves it
            // permanently ineligible for adapter generation.
            await DiscoverUniqueColumnsAsync(sessionId, conn, ct);
            await DiscoverForeignKeysAsync(sessionId, conn, ct);
            _db.CompleteDiscoveredSchemaSnapshot(sessionId);
        }
        catch
        {
            _db.InvalidateDiscoveredSchemaSnapshot(sessionId);
            throw;
        }

        _db.AppendLearningAudit(sessionId, "sql", "discover",
            $"{conn.Database}:{_eventsCollected} columns", phiScrubbed: false);
        _lastActivity = DateTimeOffset.UtcNow;

        _logger.LogInformation(
            "core.learning.schema_columns_cataloged count={Count}",
            _eventsCollected);
    }

    private async Task DiscoverForeignKeysAsync(
        string sessionId,
        SqlConnection conn,
        CancellationToken ct)
    {
        const string foreignKeyQuery = """
            WITH fk_shape AS (
                SELECT constraint_object_id, COUNT(*) AS component_count
                FROM sys.foreign_key_columns
                GROUP BY constraint_object_id
            )
            SELECT
                OBJECT_SCHEMA_NAME(fkc.parent_object_id),
                OBJECT_NAME(fkc.parent_object_id),
                COL_NAME(fkc.parent_object_id, fkc.parent_column_id),
                OBJECT_SCHEMA_NAME(fkc.referenced_object_id),
                OBJECT_NAME(fkc.referenced_object_id),
                COL_NAME(fkc.referenced_object_id, fkc.referenced_column_id)
            FROM sys.foreign_key_columns AS fkc
            INNER JOIN sys.foreign_keys AS fk
                ON fk.object_id = fkc.constraint_object_id
            INNER JOIN fk_shape AS shape
                ON shape.constraint_object_id = fkc.constraint_object_id
            WHERE fk.is_disabled = 0
              AND fk.is_not_trusted = 0
              AND shape.component_count = 1
              AND EXISTS (
                SELECT 1
                FROM sys.indexes AS unique_index
                INNER JOIN sys.index_columns AS unique_column
                    ON unique_column.object_id = unique_index.object_id
                   AND unique_column.index_id = unique_index.index_id
                   AND unique_column.key_ordinal > 0
                WHERE unique_index.object_id = fkc.referenced_object_id
                  AND unique_index.is_unique = 1
                  AND unique_index.is_disabled = 0
                  AND unique_index.is_hypothetical = 0
                  AND unique_index.has_filter = 0
                  AND unique_column.column_id = fkc.referenced_column_id
                  AND 1 = (
                    SELECT COUNT(*)
                    FROM sys.index_columns AS key_component
                    WHERE key_component.object_id = unique_index.object_id
                      AND key_component.index_id = unique_index.index_id
                      AND key_component.key_ordinal > 0
                  )
              )
            ORDER BY fkc.parent_object_id, fkc.constraint_column_id
            """;
        await using var command = new SqlCommand(foreignKeyQuery, conn)
        {
            CommandTimeout = 15,
        };
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (Enumerable.Range(0, 6).Any(reader.IsDBNull))
                continue;
            _db.BindDiscoveredForeignKey(
                sessionId,
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5));
        }
    }

    private async Task DiscoverUniqueColumnsAsync(
        string sessionId,
        SqlConnection conn,
        CancellationToken ct)
    {
        const string query = """
            SELECT
                OBJECT_SCHEMA_NAME(unique_index.object_id),
                OBJECT_NAME(unique_index.object_id),
                COL_NAME(unique_column.object_id, unique_column.column_id)
            FROM sys.indexes AS unique_index
            INNER JOIN sys.index_columns AS unique_column
                ON unique_column.object_id = unique_index.object_id
               AND unique_column.index_id = unique_index.index_id
               AND unique_column.key_ordinal > 0
            WHERE unique_index.is_unique = 1
              AND unique_index.is_disabled = 0
              AND unique_index.is_hypothetical = 0
              AND unique_index.has_filter = 0
              AND 1 = (
                SELECT COUNT(*)
                FROM sys.index_columns AS key_component
                WHERE key_component.object_id = unique_index.object_id
                  AND key_component.index_id = unique_index.index_id
                  AND key_component.key_ordinal > 0
              )
            ORDER BY unique_index.object_id, unique_index.index_id
            """;
        await using var command = new SqlCommand(query, conn)
        {
            CommandTimeout = 15,
        };
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (Enumerable.Range(0, 3).Any(reader.IsDBNull)) continue;
            _db.InsertDiscoveredUniqueColumn(
                sessionId,
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2));
        }
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
        _logger.LogInformation("core.learning.sql_schema_observer_started");
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
