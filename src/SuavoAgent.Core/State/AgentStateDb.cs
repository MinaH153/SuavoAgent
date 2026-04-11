using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace SuavoAgent.Core.State;

public sealed class AgentStateDb : IDisposable
{
    private readonly SqliteConnection _conn;

    public AgentStateDb(string dbPath, string? password = null)
    {
        SQLitePCL.Batteries_V2.Init();

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };
        if (!string.IsNullOrEmpty(password))
            builder.Password = password;

        var connStr = builder.ToString();
        _conn = new SqliteConnection(connStr);
        _conn.Open();
        InitSchema();
    }

    private void InitSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS writeback_states (
                task_id TEXT PRIMARY KEY,
                state TEXT NOT NULL,
                rx_number TEXT NOT NULL,
                retry_count INTEGER NOT NULL DEFAULT 0,
                error TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS audit_entries (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                task_id TEXT NOT NULL,
                from_state TEXT NOT NULL,
                to_state TEXT NOT NULL,
                trigger TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                prev_hash TEXT
            );
            CREATE TABLE IF NOT EXISTS unsynced_batches (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                payload TEXT NOT NULL,
                created_at TEXT NOT NULL,
                retry_count INTEGER DEFAULT 0,
                status TEXT DEFAULT 'pending',
                expires_at TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void UpsertWritebackState(string taskId, string rxNumber, WritebackState state, int retryCount, string? error)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO writeback_states (task_id, state, rx_number, retry_count, error, created_at, updated_at)
            VALUES (@taskId, @state, @rxNumber, @retryCount, @error, @now, @now)
            ON CONFLICT(task_id) DO UPDATE SET
                state = @state,
                retry_count = @retryCount,
                error = @error,
                updated_at = @now
            """;
        cmd.Parameters.AddWithValue("@taskId", taskId);
        cmd.Parameters.AddWithValue("@state", state.ToString());
        cmd.Parameters.AddWithValue("@rxNumber", rxNumber);
        cmd.Parameters.AddWithValue("@retryCount", retryCount);
        cmd.Parameters.AddWithValue("@error", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(string TaskId, WritebackState State, string RxNumber, int RetryCount)> GetPendingWritebacks()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT task_id, state, rx_number, retry_count FROM writeback_states
            WHERE state NOT IN ('Done', 'ManualReview')
            ORDER BY created_at ASC
            """;

        var results = new List<(string, WritebackState, string, int)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var stateStr = reader.GetString(1);
            if (Enum.TryParse<WritebackState>(stateStr, out var state))
            {
                results.Add((reader.GetString(0), state, reader.GetString(2), reader.GetInt32(3)));
            }
        }
        return results;
    }

    public void AppendAuditEntry(string taskId, WritebackState from, WritebackState to, WritebackTrigger trigger, string? prevHash)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO audit_entries (task_id, from_state, to_state, trigger, timestamp, prev_hash)
            VALUES (@taskId, @from, @to, @trigger, @timestamp, @prevHash)
            """;
        cmd.Parameters.AddWithValue("@taskId", taskId);
        cmd.Parameters.AddWithValue("@from", from.ToString());
        cmd.Parameters.AddWithValue("@to", to.ToString());
        cmd.Parameters.AddWithValue("@trigger", trigger.ToString());
        cmd.Parameters.AddWithValue("@timestamp", DateTimeOffset.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@prevHash", (object?)prevHash ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public int GetAuditEntryCount()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM audit_entries";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void InsertUnsyncedBatch(string payload)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO unsynced_batches (payload, created_at, retry_count, status, expires_at)
            VALUES (@payload, @now, 0, 'pending', @expires)
            """;
        var now = DateTimeOffset.UtcNow;
        cmd.Parameters.AddWithValue("@payload", payload);
        cmd.Parameters.AddWithValue("@now", now.ToString("o"));
        cmd.Parameters.AddWithValue("@expires", now.AddDays(30).ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(long Id, string Payload, int RetryCount, string Status)> GetPendingBatches()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, payload, retry_count, status FROM unsynced_batches
            WHERE status = 'pending' ORDER BY created_at ASC
            """;
        var results = new List<(long, string, int, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3)));
        return results;
    }

    public void DeleteBatch(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM unsynced_batches WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public void IncrementBatchRetry(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE unsynced_batches
            SET retry_count = retry_count + 1,
                status = CASE WHEN retry_count + 1 >= 10 THEN 'dead_letter' ELSE 'pending' END
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public int GetDeadLetterCount()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM unsynced_batches WHERE status = 'dead_letter'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void BackdateExpiresAt(long id, DateTimeOffset expires)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE unsynced_batches SET expires_at = @expires WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@expires", expires.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public void PurgeExpiredDeadLetters()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM unsynced_batches
            WHERE status = 'dead_letter' AND expires_at < @now
            """;
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public static void MigrateToEncrypted(string dbPath, string password, ILogger logger)
    {
        var bakPath = dbPath + ".bak";

        // Clean up leftover .bak from crashed migration
        if (File.Exists(bakPath))
        {
            SecureDelete(bakPath);
            logger.LogInformation("Cleaned up unencrypted backup from previous migration");
        }

        // Test if DB is already encrypted
        try
        {
            using var testDb = new AgentStateDb(dbPath, password);
            testDb.Dispose();
            return; // Already encrypted
        }
        catch { /* Not encrypted — proceed with migration */ }

        // Test if DB is unencrypted
        try
        {
            using var plainDb = new AgentStateDb(dbPath);
            plainDb.Dispose();
        }
        catch
        {
            logger.LogWarning("state.db is neither encrypted nor plain — recreating");
            SecureDelete(dbPath);
            return;
        }

        logger.LogInformation("Migrating state.db to encrypted storage...");

        var encPath = dbPath + ".enc";
        using (var plain = new SqliteConnection($"Data Source={dbPath}"))
        {
            plain.Open();
            using var encConn = new SqliteConnection($"Data Source={encPath};Password={password}");
            encConn.Open();
            plain.BackupDatabase(encConn);
        }

        File.Move(dbPath, bakPath);
        File.Move(encPath, dbPath);

        // Verify encrypted DB works
        using (var verify = new AgentStateDb(dbPath, password))
        {
            verify.GetAuditEntryCount();
        }

        SecureDelete(bakPath);
        logger.LogInformation("Migration complete — state.db is now encrypted");
    }

    private static void SecureDelete(string path)
    {
        if (!File.Exists(path)) return;
        var length = new FileInfo(path).Length;
        using (var fs = File.OpenWrite(path))
        {
            var zeros = new byte[4096];
            var remaining = length;
            while (remaining > 0)
            {
                var chunk = (int)Math.Min(remaining, zeros.Length);
                fs.Write(zeros, 0, chunk);
                remaining -= chunk;
            }
        }
        File.Delete(path);
    }

    public void Dispose()
    {
        _conn.Dispose();
    }
}
