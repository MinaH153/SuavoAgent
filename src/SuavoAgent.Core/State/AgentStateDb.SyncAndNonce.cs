using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Canary;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.Learning;

namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
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
        cmd.Parameters.AddWithValue("@expires", now.AddDays(7).ToString("o"));
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
        var plainCsb = new SqliteConnectionStringBuilder { DataSource = dbPath };
        var encCsb = new SqliteConnectionStringBuilder { DataSource = encPath, Password = password };
        using (var plain = new SqliteConnection(plainCsb.ConnectionString))
        {
            plain.Open();
            using var encConn = new SqliteConnection(encCsb.ConnectionString);
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

    internal static void SecureDelete(string path)
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
            fs.Flush();
        }
        File.Delete(path);
    }

    public bool TryRecordNonce(string nonce)
    {
        lock (_connLock)
        {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO command_nonces (nonce, received_at) VALUES (@nonce, @now)";
            cmd.Parameters.AddWithValue("@nonce", nonce);
            cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT = genuine PK duplicate
        {
            return false; // nonce already recorded → real replay
        }
        // Any OTHER failure (disk full, transient concurrent-connection error) must NOT be
        // misclassified as a replay — that would silently drop a cryptographically-valid command.
        // Let it surface; the caller logs it and the command is re-delivered on the next heartbeat.
        }
    }

    public void PruneOldNonces(TimeSpan maxAge)
    {
        lock (_connLock)
        {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM command_nonces WHERE received_at < @cutoff";
        cmd.Parameters.AddWithValue("@cutoff", DateTimeOffset.UtcNow.Subtract(maxAge).ToString("o"));
        cmd.ExecuteNonQuery();
        }
    }

}
