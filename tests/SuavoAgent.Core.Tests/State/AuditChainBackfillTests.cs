using Microsoft.Data.Sqlite;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.State;

/// <summary>
/// Regression tests for legacy AppendAuditEntry rows with NULL prev_hash
/// (Codex 2026-04-26 P1 finding). VerifyAuditChain previously returned
/// FALSE on such rows because storedHash (NULL) != expectedPrev (seed).
/// Fix: backfill NULL prev_hash on schema init so legacy rows chain to
/// the seed; funnel AppendAuditEntry through the chained path so no new
/// NULL rows are created.
/// </summary>
public class AuditChainBackfillTests : IDisposable
{
    private readonly string _dbPath;

    public AuditChainBackfillTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_audit_backfill_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void LegacyRowsWithNullPrevHash_BackfilledOnInit_ChainVerifies()
    {
        // Simulate a pre-chain-era install: insert rows directly with NULL prev_hash
        // bypassing AppendChainedAuditEntry. Then re-init the AgentStateDb and assert
        // the chain verifies (backfill happened on init).
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            // Bare-bones legacy schema — must match what an older agent would create.
            using var create = conn.CreateCommand();
            create.CommandText = """
                CREATE TABLE audit_entries (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    task_id TEXT NOT NULL,
                    from_state TEXT NOT NULL,
                    to_state TEXT NOT NULL,
                    trigger TEXT NOT NULL,
                    timestamp TEXT NOT NULL,
                    prev_hash TEXT
                );
                """;
            create.ExecuteNonQuery();

            for (int i = 0; i < 5; i++)
            {
                using var ins = conn.CreateCommand();
                ins.CommandText = """
                    INSERT INTO audit_entries (task_id, from_state, to_state, trigger, timestamp, prev_hash)
                    VALUES (@taskId, 'Queued', 'Claimed', 'Claim', @ts, NULL)
                    """;
                ins.Parameters.AddWithValue("@taskId", $"legacy-{i}");
                ins.Parameters.AddWithValue("@ts", $"2026-04-14T00:0{i}:00Z");
                ins.ExecuteNonQuery();
            }
        }

        using var db = new AgentStateDb(_dbPath);

        Assert.Equal(5, db.GetAuditEntryCount());
        Assert.True(db.VerifyAuditChain(),
            "After backfill, legacy NULL prev_hash rows must verify against the seed-rooted chain.");
    }

    [Fact]
    public void BackfillThenAppendChained_ChainStillVerifies()
    {
        // Legacy rows + new chained appends must form one continuous, verifiable chain.
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var create = conn.CreateCommand();
            create.CommandText = """
                CREATE TABLE audit_entries (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    task_id TEXT NOT NULL,
                    from_state TEXT NOT NULL,
                    to_state TEXT NOT NULL,
                    trigger TEXT NOT NULL,
                    timestamp TEXT NOT NULL,
                    prev_hash TEXT
                );
                """;
            create.ExecuteNonQuery();

            for (int i = 0; i < 3; i++)
            {
                using var ins = conn.CreateCommand();
                ins.CommandText = """
                    INSERT INTO audit_entries (task_id, from_state, to_state, trigger, timestamp, prev_hash)
                    VALUES (@taskId, 'Queued', 'Claimed', 'Claim', @ts, NULL)
                    """;
                ins.Parameters.AddWithValue("@taskId", $"legacy-{i}");
                ins.Parameters.AddWithValue("@ts", $"2026-04-14T00:0{i}:00Z");
                ins.ExecuteNonQuery();
            }
        }

        using var db = new AgentStateDb(_dbPath);
        db.AppendChainedAuditEntry(new AuditEntry(
            "post-1", "writeback_transition", "Claimed", "InProgress", "StartUia"));
        db.AppendChainedAuditEntry(new AuditEntry(
            "post-2", "writeback_transition", "InProgress", "VerifyPending", "WriteComplete"));

        Assert.Equal(5, db.GetAuditEntryCount());
        Assert.True(db.VerifyAuditChain());
    }

    [Fact]
    public void AppendAuditEntry_FunneledThroughChained_ProducesValidChain()
    {
        // The legacy AppendAuditEntry signature must route through the chained
        // path so no new NULL prev_hash rows are written. Two appends must
        // yield a chain that VerifyAuditChain accepts.
        using var db = new AgentStateDb(_dbPath);

        db.AppendAuditEntry("task-1", WritebackState.Queued, WritebackState.Claimed,
            WritebackTrigger.Claim, prevHash: null);
        db.AppendAuditEntry("task-2", WritebackState.Claimed, WritebackState.InProgress,
            WritebackTrigger.StartUia, prevHash: "ignored-legacy-arg");

        Assert.Equal(2, db.GetAuditEntryCount());
        Assert.True(db.VerifyAuditChain(),
            "AppendAuditEntry must funnel through the chained path; the legacy " +
            "prevHash argument is ignored to prevent NULL/arbitrary prev_hash rows.");
    }

    [Fact]
    public void Backfill_RecordsMarkerInConfigKv()
    {
        // Independent reviewer BLOCKER: backfill silently heals a chain whose
        // legacy rows were never chained at all. Compliance auditors need to
        // know which rows were backfilled vs originally chained. Record a
        // marker in config_kv on backfill so audits can distinguish.
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var create = conn.CreateCommand();
            create.CommandText = """
                CREATE TABLE audit_entries (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    task_id TEXT NOT NULL,
                    from_state TEXT NOT NULL,
                    to_state TEXT NOT NULL,
                    trigger TEXT NOT NULL,
                    timestamp TEXT NOT NULL,
                    prev_hash TEXT
                );
                """;
            create.ExecuteNonQuery();
            for (int i = 0; i < 3; i++)
            {
                using var ins = conn.CreateCommand();
                ins.CommandText = """
                    INSERT INTO audit_entries (task_id, from_state, to_state, trigger, timestamp, prev_hash)
                    VALUES (@taskId, 'Queued', 'Claimed', 'Claim', @ts, NULL)
                    """;
                ins.Parameters.AddWithValue("@taskId", $"legacy-{i}");
                ins.Parameters.AddWithValue("@ts", $"2026-04-14T00:0{i}:00Z");
                ins.ExecuteNonQuery();
            }
        }

        using (var db = new AgentStateDb(_dbPath)) { /* triggers backfill */ }

        // Marker must exist after backfill.
        using var probeConn = new SqliteConnection($"Data Source={_dbPath}");
        probeConn.Open();
        using var probe = probeConn.CreateCommand();
        probe.CommandText = "SELECT value FROM config_kv WHERE key = 'audit_chain_legacy_backfill'";
        var marker = probe.ExecuteScalar() as string;

        Assert.NotNull(marker);
        Assert.Contains("3", marker); // count of backfilled rows is captured
    }

    [Fact]
    public void Backfill_NoMarkerWhenNoLegacyRows()
    {
        // Marker is only written when backfill actually heals legacy rows.
        // Fresh DB or all-chained DB → no marker, so auditors aren't misled
        // into thinking a heal happened.
        using (var db = new AgentStateDb(_dbPath))
        {
            db.AppendChainedAuditEntry(new AuditEntry(
                "task-1", "writeback_transition", "Queued", "Claimed", "Claim"));
        }

        using var probeConn = new SqliteConnection($"Data Source={_dbPath}");
        probeConn.Open();
        using var probe = probeConn.CreateCommand();
        probe.CommandText = "SELECT value FROM config_kv WHERE key = 'audit_chain_legacy_backfill'";
        var marker = probe.ExecuteScalar() as string;

        Assert.Null(marker);
    }

    [Fact]
    public void Backfill_PreservesAlreadyChainedRows()
    {
        // Mixed db: some legacy NULL rows + some pre-existing valid chained rows.
        // Backfill must only touch the NULL rows — must NOT overwrite valid prev_hash values.
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var create = conn.CreateCommand();
            create.CommandText = """
                CREATE TABLE audit_entries (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    task_id TEXT NOT NULL,
                    from_state TEXT NOT NULL,
                    to_state TEXT NOT NULL,
                    trigger TEXT NOT NULL,
                    timestamp TEXT NOT NULL,
                    prev_hash TEXT
                );
                """;
            create.ExecuteNonQuery();
            // Two legacy NULL rows
            using (var ins = conn.CreateCommand())
            {
                ins.CommandText = """
                    INSERT INTO audit_entries (task_id, from_state, to_state, trigger, timestamp, prev_hash)
                    VALUES ('legacy-1', 'Queued', 'Claimed', 'Claim', '2026-04-14T00:00:00Z', NULL),
                           ('legacy-2', 'Claimed', 'InProgress', 'StartUia', '2026-04-14T00:01:00Z', NULL);
                    """;
                ins.ExecuteNonQuery();
            }
        }

        // First open backfills the legacy rows.
        using (var db1 = new AgentStateDb(_dbPath))
        {
            Assert.True(db1.VerifyAuditChain());
            db1.AppendChainedAuditEntry(new AuditEntry(
                "chained-1", "writeback_transition", "InProgress", "VerifyPending", "WriteComplete"));
        }

        // Capture the now-stored prev_hash for the chained row.
        string? storedPrevHashAfterFirstOpen;
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var read = conn.CreateCommand();
            read.CommandText = "SELECT prev_hash FROM audit_entries WHERE task_id='chained-1'";
            storedPrevHashAfterFirstOpen = read.ExecuteScalar() as string;
        }

        // Re-open: backfill must be idempotent — already-chained rows must not be overwritten.
        using (var db2 = new AgentStateDb(_dbPath))
        {
            Assert.True(db2.VerifyAuditChain());
        }

        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var read = conn.CreateCommand();
            read.CommandText = "SELECT prev_hash FROM audit_entries WHERE task_id='chained-1'";
            var after = read.ExecuteScalar() as string;
            Assert.Equal(storedPrevHashAfterFirstOpen, after);
        }
    }
}
