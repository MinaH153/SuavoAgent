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
    public void SetConfigValue(string key, string value)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO config_kv (key, value) VALUES (@k, @v)";
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@v", value);
        cmd.ExecuteNonQuery();
    }

    public string? GetConfigValue(string key)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM config_kv WHERE key = @k";
        cmd.Parameters.AddWithValue("@k", key);
        return cmd.ExecuteScalar() as string;
    }

    private string GetOrCreateGlobalSalt(string key)
    {
        var newSalt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var insertCmd = _conn.CreateCommand();
        insertCmd.CommandText = "INSERT OR IGNORE INTO config_kv (key, value) VALUES (@k, @v)";
        insertCmd.Parameters.AddWithValue("@k", key);
        insertCmd.Parameters.AddWithValue("@v", newSalt);
        insertCmd.ExecuteNonQuery();

        using var readCmd = _conn.CreateCommand();
        readCmd.CommandText = "SELECT value FROM config_kv WHERE key = @k";
        readCmd.Parameters.AddWithValue("@k", key);
        return (string)readCmd.ExecuteScalar()!;
    }

    /// <summary>
    /// Installation-scoped fallback used only when no learning session is
    /// active. It keeps system observation HMACs keyed and non-portable while
    /// allowing the command/actuation Helper to run with LearningMode=false.
    /// </summary>
    public string GetOrCreateObservationHmacSalt()
    {
        lock (_connLock)
            return GetOrCreateGlobalSalt("observation-hmac-salt");
    }

    private void TryAlter(string sql)
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            // SQLITE_ERROR (1) includes "duplicate column name" — expected during migration
        }
    }

    public void UpsertWritebackState(string taskId, string rxNumber, WritebackState state, int retryCount, string? error)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO writeback_states (task_id, state, rx_number, rx_number_enc, retry_count, error, created_at, updated_at)
            VALUES (@taskId, @state, @rxNumberHash, @rxNumberEnc, @retryCount, @error, @now, @now)
            ON CONFLICT(task_id) DO UPDATE SET
                state = @state,
                retry_count = @retryCount,
                error = @error,
                updated_at = @now
            """;
        cmd.Parameters.AddWithValue("@taskId", taskId);
        cmd.Parameters.AddWithValue("@state", state.ToString());
        cmd.Parameters.AddWithValue("@rxNumberHash", HmacRxNumber(rxNumber));
        cmd.Parameters.AddWithValue("@rxNumberEnc", (object?)EncryptRxNumber(rxNumber) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@retryCount", retryCount);
        cmd.Parameters.AddWithValue("@error", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(string TaskId, WritebackState State, string RxNumber, int RetryCount)> GetPendingWritebacks()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT task_id, state, rx_number, retry_count, rx_number_enc FROM writeback_states
            WHERE state NOT IN ('Done', 'ManualReview')
            ORDER BY created_at ASC
            """;

        var results = new List<(string, WritebackState, string, int)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var stateStr = reader.GetString(1);
            if (!Enum.TryParse<WritebackState>(stateStr, out var state)) continue;
            var enc = reader.IsDBNull(4) ? null : reader.GetString(4);
            // Prefer decrypted enc value; fall back to rx_number (plaintext for old rows pre-migration)
            var actualRx = DecryptRxNumber(enc) ?? reader.GetString(2);
            results.Add((reader.GetString(0), state, actualRx, reader.GetInt32(3)));
        }
        return results;
    }

    public int GetFailedWritebackCount()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM writeback_states WHERE state = 'ManualReview'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void UpdateNextRetryAt(string taskId, DateTimeOffset nextRetry)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE writeback_states SET next_retry_at = @nextRetry WHERE task_id = @taskId";
        cmd.Parameters.AddWithValue("@taskId", taskId);
        cmd.Parameters.AddWithValue("@nextRetry", nextRetry.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(string TaskId, WritebackState State, string RxNumber, int RetryCount, DateTimeOffset? NextRetryAt)>
        GetDueWritebacks()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT task_id, state, rx_number, retry_count, next_retry_at, rx_number_enc FROM writeback_states
            WHERE state NOT IN ('Done', 'ManualReview')
              AND (next_retry_at IS NULL OR next_retry_at <= @now)
            ORDER BY created_at ASC
            """;
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));

        var results = new List<(string, WritebackState, string, int, DateTimeOffset?)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var stateStr = reader.GetString(1);
            if (!Enum.TryParse<WritebackState>(stateStr, out var state)) continue;
            DateTimeOffset? nextRetry = reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4));
            var enc = reader.IsDBNull(5) ? null : reader.GetString(5);
            var actualRx = DecryptRxNumber(enc) ?? reader.GetString(2);
            results.Add((reader.GetString(0), state, actualRx, reader.GetInt32(3), nextRetry));
        }
        return results;
    }

    // Per-installation audit chain seed — loaded from hmac_salts table after schema init.
    // Using a per-install secret prevents an attacker who knows the codebase from pre-computing
    // the expected genesis hash of a forged chain.
    private string _auditChainSeed = "";

    /// <summary>
    /// Codex 2026-04-26 P1.2 — legacy signature retained for source compatibility
    /// but every audit write now funnels through <see cref="AppendChainedAuditEntry(AuditEntry)"/>.
    /// The <paramref name="prevHash"/> argument is intentionally ignored: the
    /// chained path computes the correct prev_hash from the actual chain tail
    /// under <see cref="_auditWriteLock"/>, which prevents legacy callers from
    /// inserting NULL or arbitrary prev_hash rows that would later cause
    /// <see cref="VerifyAuditChain"/> to false-fail during compliance checks.
    /// </summary>
    public void AppendAuditEntry(string taskId, WritebackState from, WritebackState to, WritebackTrigger trigger, string? prevHash)
    {
        _ = AppendChainedAuditEntry(new AuditEntry(
            TaskId: taskId,
            EventType: "writeback_transition",
            FromState: from.ToString(),
            ToState: to.ToString(),
            Trigger: trigger.ToString()));
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
        cmd.CommandText = """
            SELECT prev_hash, task_id, event_type, from_state, to_state, trigger, timestamp
            FROM audit_entries ORDER BY id DESC LIMIT 1
            """;
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var prevHash = reader.IsDBNull(0) ? _auditChainSeed : reader.GetString(0);
        var taskId = reader.GetString(1);
        var eventType = reader.IsDBNull(2) ? "writeback_transition" : reader.GetString(2);
        var from = reader.GetString(3);
        var to = reader.GetString(4);
        var trigger = reader.GetString(5);
        var timestamp = reader.GetString(6);
        return ComputeAuditHash(prevHash, taskId, eventType, from, to, trigger, timestamp);
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
        // Codex 2026-04-26 P1.1 — read-prev-hash + compute-new-hash + INSERT must be
        // atomic. Without serialization, two concurrent writers can both read the
        // same prev_hash, both insert with that prev_hash, and produce a chain
        // where row N's stored prev_hash != row N-1's computed hash.
        // VerifyAuditChain would then false-fail on a chain that is internally
        // self-consistent for one writer's view but globally broken.
        //
        // The C# lock protects both the audit-chain logical invariant AND the
        // shared SqliteConnection (which is not safe for concurrent commands).
        // Within the lock we additionally wrap the read+insert in an explicit
        // SQLite transaction so a crash mid-append cannot leave a partially
        // applied row.
        lock (_auditWriteLock)
        {
            // Codex 2026-04-27 review: use IsolationLevel.Serializable so
            // Microsoft.Data.Sqlite issues BEGIN IMMEDIATE — that acquires the
            // SQLite write lock NOW, blocking any other connection (separate
            // process or another AgentStateDb instance on the same DB file)
            // from advancing past its own GetLastAuditHash read until we
            // commit. Combined with the in-process lock above, this closes
            // the race even when multiple writers share the DB across
            // process boundaries (PRAGMA busy_timeout=5000 covers contention).
            using var tx = _conn.BeginTransaction(System.Data.IsolationLevel.Serializable);
            var newHash = AppendAuditEntryLocked(entry, timestamp, tx);
            tx.Commit();
            return newHash;
        }
    }

    /// <summary>
    /// Inserts one chained audit row on an ALREADY-OPEN transaction, assuming the caller holds
    /// <c>_auditWriteLock</c>. Does NOT open or commit the transaction — so it can be composed with
    /// another write into a SINGLE atomic commit (e.g. <see cref="UpsertSelectorPatchWithAudit"/>)
    /// without nesting transactions, which Microsoft.Data.Sqlite forbids. Returns the new chain hash.
    /// </summary>
    private string AppendAuditEntryLocked(AuditEntry entry, string timestamp, Microsoft.Data.Sqlite.SqliteTransaction tx)
    {
        var prevHash = GetLastAuditHashLocked() ?? _auditChainSeed;
        var newHash = ComputeAuditHash(prevHash, entry.TaskId, entry.EventType,
            entry.FromState, entry.ToState, entry.Trigger, timestamp);

        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        // Codex 2026-04-26: forensic metadata columns (actor / source_component / capture_reason /
        // window_title_hash / element_count / scrubber_version / storage_id) are written alongside the
        // chained columns but do NOT contribute to the prev_hash chain — that keeps existing rows
        // verifiable while still recording capture intent for audit dossier reconstruction.
        cmd.CommandText = """
            INSERT INTO audit_entries (task_id, from_state, to_state, trigger, timestamp, prev_hash,
                                       event_type, command_id, requester_id, rx_number,
                                       actor, source_component, capture_reason,
                                       window_title_hash, element_count, scrubber_version, storage_id)
            VALUES (@taskId, @from, @to, @trigger, @timestamp, @prevHash,
                    @eventType, @commandId, @requesterId, @rxNumber,
                    @actor, @sourceComponent, @captureReason,
                    @windowTitleHash, @elementCount, @scrubberVersion, @storageId)
            """;
        cmd.Parameters.AddWithValue("@taskId", entry.TaskId);
        cmd.Parameters.AddWithValue("@from", entry.FromState);
        cmd.Parameters.AddWithValue("@to", entry.ToState);
        cmd.Parameters.AddWithValue("@trigger", entry.Trigger);
        cmd.Parameters.AddWithValue("@timestamp", timestamp);
        cmd.Parameters.AddWithValue("@prevHash", prevHash);
        cmd.Parameters.AddWithValue("@eventType", entry.EventType);
        cmd.Parameters.AddWithValue("@commandId", (object?)entry.CommandId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@requesterId", (object?)entry.RequesterId ?? DBNull.Value);
        // Store HMAC hash of rx_number — never store raw PHI in audit log
        var rxHash = entry.RxNumber != null ? HmacRxNumber(entry.RxNumber) : null;
        cmd.Parameters.AddWithValue("@rxNumber", (object?)rxHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@actor", (object?)entry.Actor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sourceComponent", (object?)entry.SourceComponent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@captureReason", (object?)entry.CaptureReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@windowTitleHash", (object?)entry.WindowTitleHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@elementCount", (object?)entry.ElementCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@scrubberVersion", (object?)entry.ScrubberVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@storageId", (object?)entry.StorageId ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        return newHash;
    }

    // Same query as GetLastAuditHash but assumes the caller already holds
    // _auditWriteLock — kept private to avoid recursive locking.
    private string? GetLastAuditHashLocked()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT prev_hash, task_id, event_type, from_state, to_state, trigger, timestamp
            FROM audit_entries ORDER BY id DESC LIMIT 1
            """;
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var prevHash = reader.IsDBNull(0) ? _auditChainSeed : reader.GetString(0);
        var taskId = reader.GetString(1);
        var eventType = reader.IsDBNull(2) ? "writeback_transition" : reader.GetString(2);
        var from = reader.GetString(3);
        var to = reader.GetString(4);
        var trigger = reader.GetString(5);
        var timestamp = reader.GetString(6);
        return ComputeAuditHash(prevHash, taskId, eventType, from, to, trigger, timestamp);
    }

    /// <summary>
    /// Codex 2026-04-26 P1.2 — pre-chain-era rows (legacy AppendAuditEntry
    /// callers, agent installs from before chained audits shipped) have
    /// prev_hash IS NULL. <see cref="VerifyAuditChain"/> requires every row's
    /// stored prev_hash to equal the running chain tail, so legacy rows
    /// would false-fail. This method walks the chain in id order, fills in
    /// any NULL prev_hash with the chain's expected value at that row's
    /// position, and leaves valid rows untouched. Idempotent: a second run
    /// finds no NULL rows and exits.
    /// </summary>
    private void BackfillNullPrevHashRowsIfAny()
    {
        lock (_auditWriteLock)
        {
            // Cheap pre-check: skip the walk if no NULL rows exist.
            using (var probe = _conn.CreateCommand())
            {
                probe.CommandText = "SELECT COUNT(*) FROM audit_entries WHERE prev_hash IS NULL";
                if (Convert.ToInt64(probe.ExecuteScalar()) == 0) return;
            }

            // Use BEGIN IMMEDIATE so the scan + update is one serialized writer
            // transaction across processes. The C# lock above prevents
            // re-entry within this AgentStateDb instance.
            using var tx = _conn.BeginTransaction(System.Data.IsolationLevel.Serializable);

            var rows = new List<(long Id, string TaskId, string EventType,
                string FromState, string ToState, string Trigger, string Timestamp, bool IsNullPrev)>();
            using (var read = _conn.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = """
                    SELECT id, task_id, event_type, from_state, to_state, trigger, timestamp, prev_hash
                    FROM audit_entries ORDER BY id ASC
                    """;
                using var reader = read.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add((
                        reader.GetInt64(0),
                        reader.GetString(1),
                        reader.IsDBNull(2) ? "writeback_transition" : reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        reader.GetString(6),
                        reader.IsDBNull(7)));
                }
            }

            var expectedPrev = _auditChainSeed;
            int backfilled = 0;
            foreach (var row in rows)
            {
                if (row.IsNullPrev)
                {
                    using var upd = _conn.CreateCommand();
                    upd.Transaction = tx;
                    upd.CommandText = "UPDATE audit_entries SET prev_hash = @ph WHERE id = @id";
                    upd.Parameters.AddWithValue("@ph", expectedPrev);
                    upd.Parameters.AddWithValue("@id", row.Id);
                    upd.ExecuteNonQuery();
                    backfilled++;
                }
                expectedPrev = ComputeAuditHash(expectedPrev, row.TaskId, row.EventType,
                    row.FromState, row.ToState, row.Trigger, row.Timestamp);
            }

            // Codex 2026-04-27 review (independent BLOCKER): record a
            // forensic marker so HIPAA auditors can distinguish post-marker
            // (originally-chained) rows from pre-marker (legacy backfilled)
            // rows. The marker captures backfilled count + UTC timestamp +
            // the highest id at backfill time so any chain re-validation
            // can treat rows with id <= watermark as best-effort historical
            // and rows after as live-chain authoritative.
            if (backfilled > 0)
            {
                var watermark = rows.Count > 0 ? rows[rows.Count - 1].Id : 0L;
                var markerValue = $"{{\"backfilled\":{backfilled}," +
                    $"\"watermark_id\":{watermark}," +
                    $"\"backfilled_at\":\"{DateTimeOffset.UtcNow:o}\"}}";
                using var marker = _conn.CreateCommand();
                marker.Transaction = tx;
                marker.CommandText = """
                    INSERT INTO config_kv (key, value) VALUES (@k, @v)
                    ON CONFLICT(key) DO UPDATE SET value = excluded.value
                    """;
                marker.Parameters.AddWithValue("@k", "audit_chain_legacy_backfill");
                marker.Parameters.AddWithValue("@v", markerValue);
                marker.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public bool VerifyAuditChain()
    {
        // Codex 2026-04-27 review: take _auditWriteLock so VerifyAuditChain
        // observes a consistent chain snapshot — without the lock, a
        // concurrent writer mid-INSERT can cause the verify reader to see
        // a row that exists but whose prev_hash references a tail that the
        // verifier already passed (false-fail). Materialize all rows under
        // lock before doing the per-row hash chain walk so the lock-hold
        // is bounded by I/O time, not hash compute time.
        var rows = new List<(string TaskId, string EventType, string FromState,
            string ToState, string Trigger, string Timestamp, string? StoredHash)>();
        lock (_auditWriteLock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT task_id, event_type, from_state, to_state, trigger, timestamp, prev_hash
                FROM audit_entries ORDER BY id ASC
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((
                    reader.GetString(0),
                    reader.IsDBNull(1) ? "writeback_transition" : reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6)));
            }
        }

        var expectedPrev = _auditChainSeed;
        foreach (var row in rows)
        {
            if (row.StoredHash != expectedPrev) return false;
            expectedPrev = ComputeAuditHash(expectedPrev, row.TaskId, row.EventType,
                row.FromState, row.ToState, row.Trigger, row.Timestamp);
        }
        return true;
    }

#if DEBUG
    internal void TamperAuditEntryForTest(int id, string fromState, string toState)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE audit_entries SET from_state = @from, to_state = @to WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@from", fromState);
        cmd.Parameters.AddWithValue("@to", toState);
        cmd.ExecuteNonQuery();
    }
#endif

    public string ExportAuditArchiveJson()
    {
        var entries = new List<Dictionary<string, object?>>();
        using var cmd = _conn.CreateCommand();
        // Exclude rx_number — already stored as HMAC hash but omit entirely from cloud export
        // to minimise PHI surface area. The audit chain integrity is in prev_hash, not rx_number.
        cmd.CommandText = """
            SELECT id, task_id, from_state, to_state, trigger, timestamp, prev_hash,
                   event_type, command_id, requester_id
            FROM audit_entries ORDER BY id ASC
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            entries.Add(row);
        }
        return System.Text.Json.JsonSerializer.Serialize(entries);
    }

    public string ExportWritebackStatesJson()
    {
        var states = new List<Dictionary<string, object?>>();
        using var cmd = _conn.CreateCommand();
        // Exclude rx_number_enc (encrypted PHI) — state export is for operational monitoring only
        cmd.CommandText = """
            SELECT task_id, state, rx_number, retry_count, error, created_at, updated_at, next_retry_at
            FROM writeback_states
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                // Defense-in-depth: scrub the free-text `error` column on export so PHI that
                // might have leaked into a native SQL exception message can't ride into the
                // cloud-uploaded archive. (Salvaged from PR #33, which went stale on v3.13.)
                if (name == "error" && value is string errorText)
                    value = Learning.PhiScrubber.ScrubText(errorText);
                row[name] = value;
            }
            states.Add(row);
        }
        return System.Text.Json.JsonSerializer.Serialize(states);
    }

}
