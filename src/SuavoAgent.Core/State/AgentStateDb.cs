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

public sealed partial class AgentStateDb : IDisposable
{
    private readonly SqliteConnection _conn;

    // ONE connection-wide lock. SqliteConnection is not thread-safe across commands, so EVERY access
    // to _conn must serialize on the same monitor. Formerly three per-feature locks (audit / edge /
    // verified-skill) that did NOT serialize against the UNLOCKED pricing/nonce/task-autonomy methods
    // — a pricing run (HeartbeatWorker Task.Run) could race a concurrent navigate/explore write on the
    // same connection (Codex 2026-06-08 Q2 debt). The three names below now alias this one lock; the
    // hot-path read/write methods take it too. Monitor is re-entrant per-thread, so nested locked
    // calls are safe. Cost is a little more contention; SQLite writes are sub-ms.
    private readonly object _connLock = new();
    private readonly object _auditWriteLock;
    private readonly object _edgeConductanceLock;
    private readonly object _verifiedSkillLock;

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

        _auditWriteLock = _edgeConductanceLock = _verifiedSkillLock = _connLock;

        var connStr = builder.ToString();
        _conn = new SqliteConnection(connStr);
        _conn.Open();
        InitSchema();
        RecoverTerminalPricingDeliveries();
        _auditChainSeed = GetOrCreateGlobalSalt("audit-chain-seed");
        // Codex 2026-04-26 P1.2 — pre-chain-era rows (legacy AppendAuditEntry
        // with NULL prev_hash) used to false-fail VerifyAuditChain. Backfill
        // them once on startup so the chain is consistent before any
        // verification or new append runs.
        BackfillNullPrevHashRowsIfAny();
    }
}
