using System.Security.Cryptography;
using System.Text;
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

        // Migrate: add columns for chained audit entries
        TryAlter("ALTER TABLE audit_entries ADD COLUMN event_type TEXT DEFAULT 'writeback_transition'");
        TryAlter("ALTER TABLE audit_entries ADD COLUMN command_id TEXT");
        TryAlter("ALTER TABLE audit_entries ADD COLUMN requester_id TEXT");
        TryAlter("ALTER TABLE audit_entries ADD COLUMN rx_number TEXT");
    }

    private void TryAlter(string sql)
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        catch { /* Column already exists */ }
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

    private static readonly string AuditChainSeed =
        Convert.ToBase64String(SHA256.HashData(
            Encoding.UTF8.GetBytes("SuavoAgent-audit-chain-v1")));

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

    public string? GetLastAuditHash()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT prev_hash FROM audit_entries ORDER BY id DESC LIMIT 1";
        var result = cmd.ExecuteScalar();
        return result is DBNull or null ? null : (string)result;
    }

    public static string ComputeAuditHash(string prevHash, string taskId, string eventType,
        string fromState, string toState, string trigger, string timestamp)
    {
        var payload = $"{prevHash}|{taskId}|{eventType}|{fromState}|{toState}|{trigger}|{timestamp}";
        return Convert.ToBase64String(
            SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    public string AppendChainedAuditEntry(AuditEntry entry) =>
        AppendChainedAuditEntry(entry, DateTimeOffset.UtcNow.ToString("o"));

    internal string AppendChainedAuditEntry(AuditEntry entry, string timestamp)
    {
        var prevHash = GetLastAuditHash() ?? AuditChainSeed;
        var newHash = ComputeAuditHash(prevHash, entry.TaskId, entry.EventType,
            entry.FromState, entry.ToState, entry.Trigger, timestamp);

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO audit_entries (task_id, from_state, to_state, trigger, timestamp, prev_hash,
                                       event_type, command_id, requester_id, rx_number)
            VALUES (@taskId, @from, @to, @trigger, @timestamp, @prevHash,
                    @eventType, @commandId, @requesterId, @rxNumber)
            """;
        cmd.Parameters.AddWithValue("@taskId", entry.TaskId);
        cmd.Parameters.AddWithValue("@from", entry.FromState);
        cmd.Parameters.AddWithValue("@to", entry.ToState);
        cmd.Parameters.AddWithValue("@trigger", entry.Trigger);
        cmd.Parameters.AddWithValue("@timestamp", timestamp);
        cmd.Parameters.AddWithValue("@prevHash", newHash);
        cmd.Parameters.AddWithValue("@eventType", entry.EventType);
        cmd.Parameters.AddWithValue("@commandId", (object?)entry.CommandId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@requesterId", (object?)entry.RequesterId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rxNumber", (object?)entry.RxNumber ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        return newHash;
    }

    public bool VerifyAuditChain()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT task_id, event_type, from_state, to_state, trigger, timestamp, prev_hash
            FROM audit_entries ORDER BY id ASC
            """;
        using var reader = cmd.ExecuteReader();
        var expectedPrev = AuditChainSeed;
        while (reader.Read())
        {
            var taskId = reader.GetString(0);
            var eventType = reader.IsDBNull(1) ? "writeback_transition" : reader.GetString(1);
            var from = reader.GetString(2);
            var to = reader.GetString(3);
            var trigger = reader.GetString(4);
            var timestamp = reader.GetString(5);
            var storedHash = reader.IsDBNull(6) ? null : reader.GetString(6);

            var computed = ComputeAuditHash(expectedPrev, taskId, eventType, from, to, trigger, timestamp);
            if (storedHash != computed) return false;
            expectedPrev = computed;
        }
        return true;
    }

    internal void TamperAuditEntryForTest(int id, string fromState, string toState)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE audit_entries SET from_state = @from, to_state = @to WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@from", fromState);
        cmd.Parameters.AddWithValue("@to", toState);
        cmd.ExecuteNonQuery();
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
