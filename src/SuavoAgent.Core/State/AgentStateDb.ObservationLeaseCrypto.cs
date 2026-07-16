using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private string GetObservationMasterSaltLocked(
        string sessionId,
        SqliteTransaction? transaction)
    {
        var proposed = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        if (string.Equals(sessionId, PreLearningObservationSession, StringComparison.Ordinal))
        {
            using (var insert = _conn.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT OR IGNORE INTO config_kv (key, value)
                    VALUES ('observation-hmac-salt', @salt)
                    """;
                insert.Parameters.AddWithValue("@salt", proposed);
                insert.ExecuteNonQuery();
            }

            using var readGlobal = _conn.CreateCommand();
            readGlobal.Transaction = transaction;
            readGlobal.CommandText = """
                SELECT value FROM config_kv WHERE key = 'observation-hmac-salt'
                """;
            return readGlobal.ExecuteScalar() as string
                ?? throw new InvalidDataException("observation_master_key_unavailable");
        }

        using (var update = _conn.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE learning_session
                SET hmac_salt = COALESCE(hmac_salt, @salt)
                WHERE id = @session
                """;
            update.Parameters.AddWithValue("@salt", proposed);
            update.Parameters.AddWithValue("@session", sessionId);
            if (update.ExecuteNonQuery() != 1)
                throw new InvalidDataException("observation_session_not_found");
        }

        using var read = _conn.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT hmac_salt FROM learning_session WHERE id = @session";
        read.Parameters.AddWithValue("@session", sessionId);
        return read.ExecuteScalar() as string
            ?? throw new InvalidDataException("observation_master_key_unavailable");
    }

    private long ReadNextObservationLeaseEpoch(SqliteTransaction transaction)
    {
        using var command = _conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(lease_epoch), 0) + 1 FROM observation_key_leases";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string DeriveObservationLeaseKey(
        string masterSalt,
        string leaseId,
        string sessionBinding,
        long epoch)
    {
        var key = Convert.FromBase64String(masterSalt);
        var context = Encoding.UTF8.GetBytes(
            $"observation-lease-v1\0{leaseId}\0{sessionBinding}\0{epoch}");
        try
        {
            return Convert.ToBase64String(HMACSHA256.HashData(key, context));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(context);
        }
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
