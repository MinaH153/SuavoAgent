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
    /// <summary>
    /// Runs <paramref name="ddl"/> inside a transaction iff the migration version
    /// has not already been applied. Fail-closed: any error aborts the transaction
    /// AND throws, so startup fails instead of silently half-migrating a HIPAA
    /// surface. DDL with multiple statements is OK — SQLite executes them in
    /// order within the transaction.
    /// </summary>
    private void ApplyMigrationIfNeeded(int version, string description, string ddl)
    {
        using var checkCmd = _conn.CreateCommand();
        checkCmd.CommandText = "SELECT 1 FROM schema_migrations WHERE version = @v LIMIT 1";
        checkCmd.Parameters.AddWithValue("@v", version);
        if (checkCmd.ExecuteScalar() is not null) return;

        using var txn = _conn.BeginTransaction();
        try
        {
            using (var ddlCmd = _conn.CreateCommand())
            {
                ddlCmd.Transaction = txn;
                ddlCmd.CommandText = ddl;
                ddlCmd.ExecuteNonQuery();
            }
            using (var markCmd = _conn.CreateCommand())
            {
                markCmd.Transaction = txn;
                markCmd.CommandText =
                    "INSERT INTO schema_migrations (version, applied_at, description) VALUES (@v, @at, @d)";
                markCmd.Parameters.AddWithValue("@v", version);
                markCmd.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("o"));
                markCmd.Parameters.AddWithValue("@d", description);
                markCmd.ExecuteNonQuery();
            }
            txn.Commit();
        }
        catch
        {
            txn.Rollback();
            throw;
        }
    }

    private void Execute(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
