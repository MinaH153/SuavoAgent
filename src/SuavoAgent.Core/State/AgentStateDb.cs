using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Canary;
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.Learning;

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
            CREATE TABLE IF NOT EXISTS command_nonces (
                nonce TEXT PRIMARY KEY,
                received_at TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        // Migrate: add columns for chained audit entries
        TryAlter("ALTER TABLE audit_entries ADD COLUMN event_type TEXT DEFAULT 'writeback_transition'");
        TryAlter("ALTER TABLE audit_entries ADD COLUMN command_id TEXT");
        TryAlter("ALTER TABLE audit_entries ADD COLUMN requester_id TEXT");
        TryAlter("ALTER TABLE audit_entries ADD COLUMN rx_number TEXT");

        // Migrate: add next_retry_at for exponential backoff
        TryAlter("ALTER TABLE writeback_states ADD COLUMN next_retry_at TEXT");

        // Migrate: add pom_snapshot for frozen POM review (CRITICAL-6)
        TryAlter("ALTER TABLE learning_session ADD COLUMN pom_snapshot TEXT");

        // Migrate: add hmac_salt — secret per-session salt for PHI hashing (replaces non-secret AgentId)
        TryAlter("ALTER TABLE learning_session ADD COLUMN hmac_salt TEXT");

        // POM tables for Learning Agent
        using var pomCmd = _conn.CreateCommand();
        pomCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS learning_session (
                id TEXT PRIMARY KEY,
                pharmacy_id TEXT NOT NULL,
                phase TEXT NOT NULL DEFAULT 'discovery',
                mode TEXT NOT NULL DEFAULT 'observer',
                started_at TEXT NOT NULL,
                phase_changed_at TEXT NOT NULL,
                approved_at TEXT,
                approved_by TEXT,
                approved_model_digest TEXT,
                pom_snapshot TEXT,
                schema_fingerprint TEXT,
                schema_epoch INTEGER DEFAULT 1,
                promoted_to_supervised_at TEXT,
                promoted_to_autonomous_at TEXT,
                supervised_success_count INTEGER DEFAULT 0,
                supervised_correction_count INTEGER DEFAULT 0,
                promotion_threshold INTEGER DEFAULT 50,
                config_json TEXT
            );
            CREATE TABLE IF NOT EXISTS observed_processes (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                process_name TEXT NOT NULL,
                exe_path TEXT NOT NULL,
                window_title_hash TEXT,
                window_title_scrubbed TEXT,
                parent_process TEXT,
                session_user_sid_hash TEXT,
                first_seen TEXT NOT NULL,
                last_seen TEXT NOT NULL,
                occurrence_count INTEGER DEFAULT 1,
                is_service INTEGER DEFAULT 0,
                is_pms_candidate INTEGER DEFAULT 0,
                confidence REAL DEFAULT 0.0,
                UNIQUE(session_id, process_name, exe_path)
            );
            CREATE TABLE IF NOT EXISTS discovered_schemas (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                server_hash TEXT NOT NULL,
                database_name TEXT NOT NULL,
                schema_name TEXT NOT NULL,
                table_name TEXT NOT NULL,
                column_name TEXT NOT NULL,
                data_type TEXT NOT NULL,
                max_length INTEGER,
                is_nullable INTEGER,
                is_pk INTEGER DEFAULT 0,
                is_fk INTEGER DEFAULT 0,
                fk_target_table TEXT,
                fk_target_column TEXT,
                inferred_purpose TEXT,
                discovered_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS table_access_patterns (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                schema_table TEXT NOT NULL,
                read_count INTEGER DEFAULT 0,
                write_count INTEGER DEFAULT 0,
                avg_rows_returned REAL,
                last_accessed TEXT,
                is_hot INTEGER DEFAULT 0,
                observed_at TEXT NOT NULL,
                UNIQUE(session_id, schema_table)
            );
            CREATE TABLE IF NOT EXISTS observed_query_shapes (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                query_shape_hash TEXT NOT NULL,
                query_shape TEXT NOT NULL,
                tables_referenced TEXT NOT NULL,
                execution_count INTEGER DEFAULT 1,
                avg_elapsed_ms REAL,
                first_seen TEXT NOT NULL,
                last_seen TEXT NOT NULL,
                UNIQUE(session_id, query_shape_hash)
            );
            CREATE TABLE IF NOT EXISTS rx_queue_candidates (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                primary_table TEXT NOT NULL,
                join_tables TEXT,
                rx_number_column TEXT,
                rx_number_table TEXT,
                status_column TEXT,
                status_table TEXT,
                status_is_lookup INTEGER DEFAULT 0,
                status_lookup_table TEXT,
                date_column TEXT,
                patient_fk_column TEXT,
                patient_fk_table TEXT,
                composite_key_columns TEXT,
                confidence REAL DEFAULT 0.0,
                evidence_json TEXT NOT NULL,
                negative_evidence_json TEXT,
                stability_days INTEGER DEFAULT 0,
                discovered_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS discovered_statuses (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                schema_table TEXT NOT NULL,
                status_column TEXT NOT NULL,
                status_value TEXT NOT NULL,
                inferred_meaning TEXT,
                transition_order INTEGER,
                occurrence_count INTEGER DEFAULT 0,
                confidence REAL DEFAULT 0.0,
                discovered_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS learning_audit (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                observer TEXT NOT NULL,
                action TEXT NOT NULL,
                target TEXT,
                phi_scrubbed INTEGER DEFAULT 0,
                timestamp TEXT NOT NULL,
                prev_hash TEXT
            );
            """;
        pomCmd.ExecuteNonQuery();

        // Canary tables
        using var canaryCmd = _conn.CreateCommand();
        canaryCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_canary_baselines (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                pharmacy_id TEXT NOT NULL,
                adapter_type TEXT NOT NULL,
                object_fingerprint TEXT NOT NULL,
                status_map_fingerprint TEXT NOT NULL,
                query_fingerprint TEXT NOT NULL,
                result_shape_fingerprint TEXT NOT NULL,
                contract_fingerprint TEXT NOT NULL,
                contract_json TEXT NOT NULL,
                schema_epoch INTEGER NOT NULL DEFAULT 1,
                contract_version INTEGER NOT NULL DEFAULT 1,
                approved_at TEXT,
                approved_by TEXT,
                approved_command_id TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                UNIQUE(pharmacy_id, adapter_type)
            );
            CREATE TABLE IF NOT EXISTS schema_canary_incidents (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                pharmacy_id TEXT NOT NULL,
                adapter_type TEXT NOT NULL,
                severity TEXT NOT NULL CHECK (severity IN ('warning','critical')),
                drifted_components TEXT NOT NULL,
                baseline_contract_fingerprint TEXT NOT NULL,
                observed_contract_fingerprint TEXT NOT NULL,
                drift_details TEXT,
                dropped_batch_row_count INTEGER,
                blocked_cycle_count INTEGER DEFAULT 1,
                opened_at TEXT NOT NULL,
                last_seen_at TEXT NOT NULL,
                resolved_at TEXT,
                resolved_by TEXT,
                resolution TEXT CHECK (resolution IN ('auto_cleared','operator_acknowledged','relearned')),
                ack_command_id TEXT
            );
            CREATE TABLE IF NOT EXISTS schema_canary_hold (
                pharmacy_id TEXT NOT NULL,
                adapter_type TEXT NOT NULL,
                severity TEXT NOT NULL CHECK (severity IN ('warning','critical')),
                drift_hold_since TEXT NOT NULL,
                blocked_cycle_count INTEGER NOT NULL DEFAULT 0,
                last_seen_at TEXT NOT NULL,
                baseline_contract_fingerprint TEXT NOT NULL,
                acknowledged_at TEXT,
                acknowledged_by TEXT,
                ack_command_id TEXT,
                PRIMARY KEY (pharmacy_id, adapter_type)
            );
            """;
        canaryCmd.ExecuteNonQuery();

        // Canary migrations
        TryAlter("ALTER TABLE unsynced_batches ADD COLUMN baseline_contract_fingerprint TEXT");
        TryAlter("ALTER TABLE unsynced_batches ADD COLUMN row_count INTEGER");
        TryAlter("ALTER TABLE learning_session ADD COLUMN approved_contract_fingerprint TEXT");

        // Behavioral learning tables
        using var behavioralCmd = _conn.CreateCommand();
        behavioralCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS behavioral_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                sequence_num INTEGER NOT NULL,
                event_type TEXT NOT NULL,
                event_subtype TEXT,
                tree_hash TEXT,
                element_id TEXT,
                element_control_type TEXT,
                element_class_name TEXT,
                element_name_hash TEXT,
                element_bounding_rect TEXT,
                keystroke_category TEXT,
                keystroke_timing_bucket TEXT,
                keystroke_sequence_count INTEGER,
                occurrence_count INTEGER DEFAULT 1,
                helper_timestamp TEXT NOT NULL,
                received_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_be_session_seq ON behavioral_events(session_id, sequence_num);
            CREATE INDEX IF NOT EXISTS idx_be_session_type ON behavioral_events(session_id, event_type);
            CREATE INDEX IF NOT EXISTS idx_be_tree_hash ON behavioral_events(session_id, tree_hash);

            CREATE TABLE IF NOT EXISTS dmv_query_observations (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                query_shape_hash TEXT NOT NULL,
                query_shape TEXT NOT NULL,
                tables_referenced TEXT NOT NULL,
                is_write INTEGER NOT NULL DEFAULT 0,
                execution_count INTEGER DEFAULT 1,
                last_execution_time TEXT NOT NULL,
                clock_offset_ms INTEGER DEFAULT 0,
                first_seen TEXT NOT NULL,
                last_seen TEXT NOT NULL,
                UNIQUE(session_id, query_shape_hash)
            );
            CREATE INDEX IF NOT EXISTS idx_dqo_session_time ON dmv_query_observations(session_id, last_execution_time);
            CREATE INDEX IF NOT EXISTS idx_dqo_shape ON dmv_query_observations(session_id, query_shape_hash);

            CREATE TABLE IF NOT EXISTS correlated_actions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                correlation_key TEXT NOT NULL,
                tree_hash TEXT NOT NULL,
                element_id TEXT NOT NULL,
                element_control_type TEXT,
                query_shape_hash TEXT,
                query_is_write INTEGER DEFAULT 0,
                tables_referenced TEXT,
                occurrence_count INTEGER DEFAULT 1,
                confidence REAL DEFAULT 0.3,
                first_seen TEXT NOT NULL,
                last_seen TEXT NOT NULL,
                UNIQUE(session_id, correlation_key)
            );
            CREATE INDEX IF NOT EXISTS idx_ca_session_key ON correlated_actions(session_id, correlation_key);
            CREATE INDEX IF NOT EXISTS idx_ca_writeback ON correlated_actions(session_id, query_is_write) WHERE query_is_write = 1;

            CREATE TABLE IF NOT EXISTS learned_routines (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                routine_hash TEXT NOT NULL,
                path_json TEXT NOT NULL,
                path_length INTEGER NOT NULL,
                frequency INTEGER NOT NULL,
                confidence REAL DEFAULT 0.0,
                start_element_id TEXT,
                end_element_id TEXT,
                correlated_write_queries TEXT,
                has_writeback_candidate INTEGER DEFAULT 0,
                discovered_at TEXT NOT NULL,
                last_observed TEXT NOT NULL,
                UNIQUE(session_id, routine_hash)
            );
            CREATE INDEX IF NOT EXISTS idx_lr_session ON learned_routines(session_id);
            CREATE INDEX IF NOT EXISTS idx_lr_writeback ON learned_routines(session_id, has_writeback_candidate) WHERE has_writeback_candidate = 1;
            """;
        behavioralCmd.ExecuteNonQuery();

        // Feedback system tables
        using var feedbackCmd = _conn.CreateCommand();
        feedbackCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS feedback_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                event_type TEXT NOT NULL,
                source TEXT NOT NULL,
                source_id TEXT,
                target_type TEXT NOT NULL,
                target_id TEXT NOT NULL,
                payload_json TEXT,
                directive_type TEXT NOT NULL,
                directive_json TEXT,
                applied_at TEXT,
                applied_by TEXT,
                causal_chain_json TEXT,
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_fe_pending ON feedback_events(session_id, applied_at)
                WHERE applied_at IS NULL;
            CREATE INDEX IF NOT EXISTS idx_fe_target ON feedback_events(session_id, target_type, target_id);
            CREATE INDEX IF NOT EXISTS idx_fe_type ON feedback_events(session_id, directive_type);
            CREATE INDEX IF NOT EXISTS idx_fe_source_decay ON feedback_events(session_id, target_id, source, created_at)
                WHERE source = 'decay';

            CREATE TABLE IF NOT EXISTS correlation_window_overrides (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                tree_hash TEXT NOT NULL,
                element_id TEXT NOT NULL,
                window_seconds REAL NOT NULL,
                sample_count INTEGER NOT NULL,
                computed_at TEXT NOT NULL,
                UNIQUE(session_id, tree_hash, element_id)
            );
            """;
        feedbackCmd.ExecuteNonQuery();

        // Feedback column migrations on correlated_actions
        TryAlter("ALTER TABLE correlated_actions ADD COLUMN operator_approved INTEGER DEFAULT 0");
        TryAlter("ALTER TABLE correlated_actions ADD COLUMN operator_rejected INTEGER DEFAULT 0");
        TryAlter("ALTER TABLE correlated_actions ADD COLUMN promotion_suspended INTEGER DEFAULT 0");
        TryAlter("ALTER TABLE correlated_actions ADD COLUMN consecutive_failures INTEGER DEFAULT 0");
        TryAlter("ALTER TABLE correlated_actions ADD COLUMN stale INTEGER DEFAULT 0");
        TryAlter("ALTER TABLE correlated_actions ADD COLUMN stale_since TEXT");

        // Spec D: Collective Intelligence — seed provenance on correlated_actions
        TryAlter("ALTER TABLE correlated_actions ADD COLUMN source TEXT NOT NULL DEFAULT 'local'");
        TryAlter("ALTER TABLE correlated_actions ADD COLUMN seed_digest TEXT");
        TryAlter("ALTER TABLE correlated_actions ADD COLUMN seeded_at TEXT");

        // Spec D: applied_seeds
        using (var seedCmd = _conn.CreateCommand())
        {
            seedCmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS applied_seeds (
                    seed_digest TEXT PRIMARY KEY,
                    phase TEXT NOT NULL,
                    applied_at TEXT NOT NULL,
                    correlations_applied INTEGER NOT NULL,
                    correlations_skipped INTEGER NOT NULL
                )";
            seedCmd.ExecuteNonQuery();
        }

        // Spec D: seed_items
        using (var itemCmd = _conn.CreateCommand())
        {
            itemCmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS seed_items (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    seed_digest TEXT NOT NULL,
                    item_type TEXT NOT NULL,
                    item_key TEXT NOT NULL,
                    applied_at TEXT NOT NULL,
                    confirmed_at TEXT,
                    local_match_count INTEGER NOT NULL DEFAULT 0,
                    rejected_at TEXT,
                    UNIQUE(seed_digest, item_type, item_key)
                )";
            itemCmd.ExecuteNonQuery();
        }
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
            SELECT task_id, state, rx_number, retry_count, next_retry_at FROM writeback_states
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
            results.Add((reader.GetString(0), state, reader.GetString(2), reader.GetInt32(3), nextRetry));
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

    public string ExportAuditArchiveJson()
    {
        var entries = new List<Dictionary<string, object?>>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM audit_entries ORDER BY id ASC";
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
        cmd.CommandText = "SELECT * FROM writeback_states";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            states.Add(row);
        }
        return System.Text.Json.JsonSerializer.Serialize(states);
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
            fs.Flush();
        }
        File.Delete(path);
    }

    public bool TryRecordNonce(string nonce)
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
        catch { return false; }
    }

    public void PruneOldNonces(TimeSpan maxAge)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM command_nonces WHERE received_at < @cutoff";
        cmd.Parameters.AddWithValue("@cutoff", DateTimeOffset.UtcNow.Subtract(maxAge).ToString("o"));
        cmd.ExecuteNonQuery();
    }

    // ── Learning Session CRUD ──

    public void CreateLearningSession(string id, string pharmacyId)
    {
        using var cmd = _conn.CreateCommand();
        var now = DateTimeOffset.UtcNow.ToString("o");
        cmd.CommandText = """
            INSERT INTO learning_session (id, pharmacy_id, phase, mode, started_at, phase_changed_at)
            VALUES (@id, @pharmacyId, 'discovery', 'observer', @now, @now)
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@pharmacyId", pharmacyId);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    public (string Id, string PharmacyId, string Phase, string Mode,
            string? ApprovedModelDigest, int SchemaEpoch,
            int SupervisedSuccessCount, int SupervisedCorrectionCount)?
        GetLearningSession(string id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, pharmacy_id, phase, mode, approved_model_digest,
                   schema_epoch, supervised_success_count, supervised_correction_count
            FROM learning_session WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return (
            reader.GetString(0), reader.GetString(1),
            reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7));
    }

    public void UpdateLearningPhase(string sessionId, string phase)
    {
        var session = GetLearningSession(sessionId);
        if (session is null)
            throw new InvalidOperationException($"Learning session '{sessionId}' not found");

        if (!LearningSession.IsValidPhaseTransition(session.Value.Phase, phase))
            throw new InvalidOperationException(
                $"Invalid phase transition: '{session.Value.Phase}' → '{phase}'");

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE learning_session SET phase = @phase, phase_changed_at = @now WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@phase", phase);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@id", sessionId);
        cmd.ExecuteNonQuery();
    }

    public void UpdateLearningMode(string sessionId, string mode)
    {
        var session = GetLearningSession(sessionId);
        if (session is null)
            throw new InvalidOperationException($"Learning session '{sessionId}' not found");

        if (!LearningSession.IsValidModeTransition(session.Value.Mode, mode))
            throw new InvalidOperationException(
                $"Invalid mode transition: '{session.Value.Mode}' → '{mode}'");

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE learning_session SET mode = @mode WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@mode", mode);
        cmd.Parameters.AddWithValue("@id", sessionId);
        cmd.ExecuteNonQuery();
    }

    // ── Observed Processes ──

    public void UpsertObservedProcess(string sessionId, string processName, string exePath,
        string? windowTitleScrubbed = null, bool isPmsCandidate = false,
        string? windowTitleHash = null, string? parentProcess = null, bool isService = false)
    {
        using var cmd = _conn.CreateCommand();
        var now = DateTimeOffset.UtcNow.ToString("o");
        cmd.CommandText = """
            INSERT INTO observed_processes
                (session_id, process_name, exe_path, window_title_hash, window_title_scrubbed,
                 parent_process, is_service, is_pms_candidate, first_seen, last_seen, occurrence_count)
            VALUES (@sid, @name, @path, @titleHash, @titleScrub, @parent, @isSvc, @isPms, @now, @now, 1)
            ON CONFLICT(session_id, process_name, exe_path) DO UPDATE SET
                last_seen = @now,
                occurrence_count = occurrence_count + 1,
                window_title_scrubbed = COALESCE(@titleScrub, window_title_scrubbed),
                window_title_hash = COALESCE(@titleHash, window_title_hash),
                is_pms_candidate = MAX(is_pms_candidate, @isPms)
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@name", processName);
        cmd.Parameters.AddWithValue("@path", exePath);
        cmd.Parameters.AddWithValue("@titleHash", (object?)windowTitleHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@titleScrub", (object?)windowTitleScrubbed ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@parent", (object?)parentProcess ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@isSvc", isService ? 1 : 0);
        cmd.Parameters.AddWithValue("@isPms", isPmsCandidate ? 1 : 0);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(string ProcessName, string ExePath, string? WindowTitleScrubbed,
        int OccurrenceCount, bool IsPmsCandidate)> GetObservedProcesses(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT process_name, exe_path, window_title_scrubbed, occurrence_count, is_pms_candidate
            FROM observed_processes WHERE session_id = @sid ORDER BY occurrence_count DESC
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        var results = new List<(string, string, string?, int, bool)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((
                reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt32(3), reader.GetInt32(4) == 1));
        }
        return results;
    }

    // ── Discovered Schemas ──

    public void InsertDiscoveredSchema(string sessionId, string serverHash,
        string databaseName, string schemaName, string tableName, string columnName,
        string dataType, int? maxLength, bool isNullable, bool isPk, bool isFk,
        string? fkTargetTable, string? fkTargetColumn, string? inferredPurpose)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO discovered_schemas
                (session_id, server_hash, database_name, schema_name, table_name,
                 column_name, data_type, max_length, is_nullable, is_pk, is_fk,
                 fk_target_table, fk_target_column, inferred_purpose, discovered_at)
            VALUES (@sid, @svr, @db, @schema, @tbl, @col, @dtype, @maxLen,
                    @nullable, @pk, @fk, @fkTbl, @fkCol, @purpose, @now)
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@svr", serverHash);
        cmd.Parameters.AddWithValue("@db", databaseName);
        cmd.Parameters.AddWithValue("@schema", schemaName);
        cmd.Parameters.AddWithValue("@tbl", tableName);
        cmd.Parameters.AddWithValue("@col", columnName);
        cmd.Parameters.AddWithValue("@dtype", dataType);
        cmd.Parameters.AddWithValue("@maxLen", (object?)maxLength ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@nullable", isNullable ? 1 : 0);
        cmd.Parameters.AddWithValue("@pk", isPk ? 1 : 0);
        cmd.Parameters.AddWithValue("@fk", isFk ? 1 : 0);
        cmd.Parameters.AddWithValue("@fkTbl", (object?)fkTargetTable ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fkCol", (object?)fkTargetColumn ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@purpose", (object?)inferredPurpose ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(string SchemaName, string TableName, string ColumnName,
        string DataType, bool IsPk, bool IsFk, string? InferredPurpose)>
        GetDiscoveredSchemas(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT schema_name, table_name, column_name, data_type, is_pk, is_fk, inferred_purpose
            FROM discovered_schemas WHERE session_id = @sid
            ORDER BY schema_name, table_name, id
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        var results = new List<(string, string, string, string, bool, bool, string?)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt32(4) == 1, reader.GetInt32(5) == 1,
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        return results;
    }

    // ── Learning Audit ──

    public void AppendLearningAudit(string sessionId, string observer, string action,
        string? target, bool phiScrubbed)
    {
        var now = DateTimeOffset.UtcNow.ToString("o");
        string? prevHash = null;

        using (var hashCmd = _conn.CreateCommand())
        {
            hashCmd.CommandText = "SELECT prev_hash FROM learning_audit WHERE session_id = @sid ORDER BY id DESC LIMIT 1";
            hashCmd.Parameters.AddWithValue("@sid", sessionId);
            prevHash = hashCmd.ExecuteScalar() as string;
        }

        var chainInput = $"{sessionId}|{observer}|{action}|{target}|{now}|{prevHash}";
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(chainInput))).ToLowerInvariant();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO learning_audit (session_id, observer, action, target, phi_scrubbed, timestamp, prev_hash)
            VALUES (@sid, @obs, @act, @target, @phi, @now, @hash)
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@obs", observer);
        cmd.Parameters.AddWithValue("@act", action);
        cmd.Parameters.AddWithValue("@target", (object?)target ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@phi", phiScrubbed ? 1 : 0);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.Parameters.AddWithValue("@hash", hash);
        cmd.ExecuteNonQuery();
    }

    public int GetLearningAuditCount(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM learning_audit WHERE session_id = @sid";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // ── Rx Queue Candidates ──

    public void InsertRxQueueCandidate(string sessionId, string primaryTable,
        string? rxNumberColumn, string? statusColumn, string? dateColumn,
        string? patientFkColumn, double confidence, string evidenceJson,
        string? negativeEvidenceJson = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rx_queue_candidates
                (session_id, primary_table, rx_number_column, status_column,
                 date_column, patient_fk_column, confidence, evidence_json,
                 negative_evidence_json, stability_days, discovered_at)
            VALUES (@sid, @tbl, @rxCol, @statusCol, @dateCol, @patientCol,
                    @conf, @evidence, @negEvidence, 0, @now)
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@tbl", primaryTable);
        cmd.Parameters.AddWithValue("@rxCol", (object?)rxNumberColumn ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@statusCol", (object?)statusColumn ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dateCol", (object?)dateColumn ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@patientCol", (object?)patientFkColumn ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@conf", confidence);
        cmd.Parameters.AddWithValue("@evidence", evidenceJson);
        cmd.Parameters.AddWithValue("@negEvidence", (object?)negativeEvidenceJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(string PrimaryTable, string? RxNumberColumn, string? StatusColumn,
        string? DateColumn, string? PatientFkColumn, double Confidence, string EvidenceJson)>
        GetRxQueueCandidates(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT primary_table, rx_number_column, status_column, date_column,
                   patient_fk_column, confidence, evidence_json
            FROM rx_queue_candidates WHERE session_id = @sid
            ORDER BY confidence DESC
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        var results = new List<(string, string?, string?, string?, string?, double, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetDouble(5),
                reader.GetString(6)));
        }
        return results;
    }

    // ── Discovered Statuses ──

    public void InsertDiscoveredStatus(string sessionId, string schemaTable,
        string statusColumn, string statusValue, string? inferredMeaning,
        int transitionOrder, int occurrenceCount, double confidence)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO discovered_statuses
                (session_id, schema_table, status_column, status_value,
                 inferred_meaning, transition_order, occurrence_count, confidence, discovered_at)
            VALUES (@sid, @tbl, @col, @val, @meaning, @order, @count, @conf, @now)
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@tbl", schemaTable);
        cmd.Parameters.AddWithValue("@col", statusColumn);
        cmd.Parameters.AddWithValue("@val", statusValue);
        cmd.Parameters.AddWithValue("@meaning", (object?)inferredMeaning ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@order", transitionOrder);
        cmd.Parameters.AddWithValue("@count", occurrenceCount);
        cmd.Parameters.AddWithValue("@conf", confidence);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(string StatusValue, string? InferredMeaning, int TransitionOrder, double Confidence)>
        GetDiscoveredStatuses(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT status_value, inferred_meaning, transition_order, confidence
            FROM discovered_statuses WHERE session_id = @sid
            ORDER BY transition_order
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        var results = new List<(string, string?, int, double)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt32(2),
                reader.GetDouble(3)));
        }
        return results;
    }

    public IReadOnlyList<(string StatusValue, string? InferredMeaning, int TransitionOrder, double Confidence)>
        GetDiscoveredStatusesForTable(string sessionId, string schemaTable)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT status_value, inferred_meaning, transition_order, confidence
            FROM discovered_statuses WHERE session_id = @sid AND schema_table = @tbl
            ORDER BY transition_order
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@tbl", schemaTable);
        var results = new List<(string, string?, int, double)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt32(2),
                reader.GetDouble(3)));
        }
        return results;
    }

    // ── Approval Digest & POM Snapshot (CRITICAL-5, CRITICAL-6) ──

    public void SetApprovalDigest(string sessionId, string digest, string approvedBy)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE learning_session
            SET approved_model_digest = @digest,
                approved_at = @now,
                approved_by = @approvedBy
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@digest", digest);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@approvedBy", approvedBy);
        cmd.Parameters.AddWithValue("@id", sessionId);
        cmd.ExecuteNonQuery();
    }

    public void StorePomSnapshot(string sessionId, string pomJson)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE learning_session SET pom_snapshot = @pom WHERE id = @id";
        cmd.Parameters.AddWithValue("@pom", pomJson);
        cmd.Parameters.AddWithValue("@id", sessionId);
        cmd.ExecuteNonQuery();
    }

    public string? GetPomSnapshot(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT pom_snapshot FROM learning_session WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", sessionId);
        var result = cmd.ExecuteScalar();
        return result is DBNull or null ? null : (string)result;
    }

    // ── HMAC Salt (secret, per-session) ──

    /// <summary>
    /// Returns the per-session HMAC salt, generating a random 32-byte one on first call.
    /// This replaces AgentId (non-secret, sent in heartbeats) as the HMAC key for PHI hashing.
    /// </summary>
    public string GetOrCreateHmacSalt(string sessionId)
    {
        using var readCmd = _conn.CreateCommand();
        readCmd.CommandText = "SELECT hmac_salt FROM learning_session WHERE id = @id";
        readCmd.Parameters.AddWithValue("@id", sessionId);
        var existing = readCmd.ExecuteScalar();
        if (existing is not null and not DBNull)
            return (string)existing;

        var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var writeCmd = _conn.CreateCommand();
        writeCmd.CommandText = "UPDATE learning_session SET hmac_salt = @salt WHERE id = @id";
        writeCmd.Parameters.AddWithValue("@salt", salt);
        writeCmd.Parameters.AddWithValue("@id", sessionId);
        writeCmd.ExecuteNonQuery();
        return salt;
    }

    // ── Active Session Lookup (CRITICAL-7) ──

    public string? GetActiveSessionId(string pharmacyId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id FROM learning_session
            WHERE pharmacy_id = @pid AND phase NOT IN ('active')
            ORDER BY started_at DESC LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@pid", pharmacyId);
        var result = cmd.ExecuteScalar();
        return result is DBNull or null ? null : (string)result;
    }

    // ── Canary Baselines ──

    public void UpsertCanaryBaseline(string pharmacyId, ContractBaseline baseline)
    {
        var now = DateTimeOffset.UtcNow.ToString("o");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO schema_canary_baselines
                (pharmacy_id, adapter_type, object_fingerprint, status_map_fingerprint,
                 query_fingerprint, result_shape_fingerprint, contract_fingerprint,
                 contract_json, schema_epoch, contract_version, created_at, updated_at)
            VALUES
                (@pid, @adapter, @obj, @stat, @qry, @shape, @contract,
                 @json, @epoch, @version, @now, @now)
            ON CONFLICT(pharmacy_id, adapter_type) DO UPDATE SET
                object_fingerprint = @obj,
                status_map_fingerprint = @stat,
                query_fingerprint = @qry,
                result_shape_fingerprint = @shape,
                contract_fingerprint = @contract,
                contract_json = @json,
                schema_epoch = @epoch,
                contract_version = @version,
                updated_at = @now
            """;
        cmd.Parameters.AddWithValue("@pid", pharmacyId);
        cmd.Parameters.AddWithValue("@adapter", baseline.AdapterType);
        cmd.Parameters.AddWithValue("@obj", baseline.ObjectFingerprint);
        cmd.Parameters.AddWithValue("@stat", baseline.StatusMapFingerprint);
        cmd.Parameters.AddWithValue("@qry", baseline.QueryFingerprint);
        cmd.Parameters.AddWithValue("@shape", baseline.ResultShapeFingerprint);
        cmd.Parameters.AddWithValue("@contract", baseline.ContractFingerprint);
        cmd.Parameters.AddWithValue("@json", baseline.ContractJson);
        cmd.Parameters.AddWithValue("@epoch", baseline.SchemaEpoch);
        cmd.Parameters.AddWithValue("@version", baseline.ContractVersion);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    public ContractBaseline? GetCanaryBaseline(string pharmacyId, string adapterType)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT adapter_type, object_fingerprint, status_map_fingerprint,
                   query_fingerprint, result_shape_fingerprint, contract_fingerprint,
                   contract_json, schema_epoch, contract_version
            FROM schema_canary_baselines
            WHERE pharmacy_id = @pid AND adapter_type = @adapter
            """;
        cmd.Parameters.AddWithValue("@pid", pharmacyId);
        cmd.Parameters.AddWithValue("@adapter", adapterType);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new ContractBaseline(
            AdapterType: reader.GetString(0),
            ObjectFingerprint: reader.GetString(1),
            StatusMapFingerprint: reader.GetString(2),
            QueryFingerprint: reader.GetString(3),
            ResultShapeFingerprint: reader.GetString(4),
            ContractFingerprint: reader.GetString(5),
            ContractJson: reader.GetString(6),
            SchemaEpoch: reader.GetInt32(7),
            ContractVersion: reader.GetInt32(8));
    }

    // ── Canary Incidents ──

    public void InsertCanaryIncident(string pharmacyId, string adapterType, string severity,
        string driftedComponents, string baselineFingerprint, string observedFingerprint,
        string? details, int? droppedRowCount)
    {
        var now = DateTimeOffset.UtcNow.ToString("o");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO schema_canary_incidents
                (pharmacy_id, adapter_type, severity, drifted_components,
                 baseline_contract_fingerprint, observed_contract_fingerprint,
                 drift_details, dropped_batch_row_count, opened_at, last_seen_at)
            VALUES
                (@pid, @adapter, @severity, @drifted,
                 @baseline, @observed, @details, @dropped, @now, @now)
            """;
        cmd.Parameters.AddWithValue("@pid", pharmacyId);
        cmd.Parameters.AddWithValue("@adapter", adapterType);
        cmd.Parameters.AddWithValue("@severity", severity);
        cmd.Parameters.AddWithValue("@drifted", driftedComponents);
        cmd.Parameters.AddWithValue("@baseline", baselineFingerprint);
        cmd.Parameters.AddWithValue("@observed", observedFingerprint);
        cmd.Parameters.AddWithValue("@details", (object?)details ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dropped", (object?)droppedRowCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(string Severity, int? DroppedBatchRowCount, string OpenedAt)>
        GetOpenCanaryIncidents(string pharmacyId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT severity, dropped_batch_row_count, opened_at
            FROM schema_canary_incidents
            WHERE pharmacy_id = @pid AND resolved_at IS NULL
            ORDER BY opened_at ASC
            """;
        cmd.Parameters.AddWithValue("@pid", pharmacyId);
        var results = new List<(string, int?, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            int? dropped = reader.IsDBNull(1) ? null : reader.GetInt32(1);
            results.Add((reader.GetString(0), dropped, reader.GetString(2)));
        }
        return results;
    }

    // ── Canary Hold ──

    public void UpsertCanaryHold(string pharmacyId, string adapterType, string severity, string baselineFingerprint)
    {
        var now = DateTimeOffset.UtcNow.ToString("o");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO schema_canary_hold
                (pharmacy_id, adapter_type, severity, drift_hold_since,
                 blocked_cycle_count, last_seen_at, baseline_contract_fingerprint)
            VALUES (@pid, @adapter, @severity, @now, 0, @now, @baseline)
            ON CONFLICT(pharmacy_id, adapter_type) DO UPDATE SET
                severity = @severity,
                last_seen_at = @now,
                baseline_contract_fingerprint = @baseline
            """;
        cmd.Parameters.AddWithValue("@pid", pharmacyId);
        cmd.Parameters.AddWithValue("@adapter", adapterType);
        cmd.Parameters.AddWithValue("@severity", severity);
        cmd.Parameters.AddWithValue("@baseline", baselineFingerprint);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    public void IncrementCanaryHoldCycles(string pharmacyId, string adapterType)
    {
        var now = DateTimeOffset.UtcNow.ToString("o");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE schema_canary_hold
            SET blocked_cycle_count = blocked_cycle_count + 1,
                last_seen_at = @now
            WHERE pharmacy_id = @pid AND adapter_type = @adapter
            """;
        cmd.Parameters.AddWithValue("@pid", pharmacyId);
        cmd.Parameters.AddWithValue("@adapter", adapterType);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    public (string Severity, int BlockedCycles, string DriftHoldSince)? GetCanaryHold(string pharmacyId, string adapterType)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT severity, blocked_cycle_count, drift_hold_since
            FROM schema_canary_hold
            WHERE pharmacy_id = @pid AND adapter_type = @adapter
            """;
        cmd.Parameters.AddWithValue("@pid", pharmacyId);
        cmd.Parameters.AddWithValue("@adapter", adapterType);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return (reader.GetString(0), reader.GetInt32(1), reader.GetString(2));
    }

    public void ClearCanaryHold(string pharmacyId, string adapterType)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM schema_canary_hold
            WHERE pharmacy_id = @pid AND adapter_type = @adapter
            """;
        cmd.Parameters.AddWithValue("@pid", pharmacyId);
        cmd.Parameters.AddWithValue("@adapter", adapterType);
        cmd.ExecuteNonQuery();
    }

    // ── Behavioral Events ──

    public void InsertBehavioralEvent(string sessionId, int sequenceNum, string eventType,
        string? eventSubtype, string? treeHash, string? elementId, string? controlType,
        string? className, string? nameHash, string? boundingRect,
        string? keystrokeCategory, string? timingBucket, int? keystrokeCount, int occurrenceCount,
        string helperTimestamp)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO behavioral_events
                (session_id, sequence_num, event_type, event_subtype, tree_hash,
                 element_id, element_control_type, element_class_name, element_name_hash,
                 element_bounding_rect, keystroke_category, keystroke_timing_bucket,
                 keystroke_sequence_count, occurrence_count, helper_timestamp, received_at)
            VALUES
                (@sid, @seq, @type, @subtype, @treeHash,
                 @elemId, @ctrlType, @className, @nameHash,
                 @boundRect, @ksCat, @ksBucket,
                 @ksCount, @occCount, @helperTs, @now)
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@seq", sequenceNum);
        cmd.Parameters.AddWithValue("@type", eventType);
        cmd.Parameters.AddWithValue("@subtype", (object?)eventSubtype ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@treeHash", (object?)treeHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@elemId", (object?)elementId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ctrlType", (object?)controlType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@className", (object?)className ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@nameHash", (object?)nameHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@boundRect", (object?)boundingRect ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ksCat", (object?)keystrokeCategory ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ksBucket", (object?)timingBucket ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ksCount", (object?)keystrokeCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@occCount", occurrenceCount);
        cmd.Parameters.AddWithValue("@helperTs", helperTimestamp);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(long Id, int SequenceNum, string EventType, string? EventSubtype,
        string? TreeHash, string? ElementId, string? ControlType, int OccurrenceCount, string HelperTimestamp)>
        GetBehavioralEvents(string sessionId, string? eventType = null, int limit = 1000)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = eventType is null
            ? """
              SELECT id, sequence_num, event_type, event_subtype, tree_hash, element_id,
                     element_control_type, occurrence_count, helper_timestamp
              FROM behavioral_events
              WHERE session_id = @sid
              ORDER BY sequence_num
              LIMIT @limit
              """
            : """
              SELECT id, sequence_num, event_type, event_subtype, tree_hash, element_id,
                     element_control_type, occurrence_count, helper_timestamp
              FROM behavioral_events
              WHERE session_id = @sid AND event_type = @type
              ORDER BY sequence_num
              LIMIT @limit
              """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@limit", limit);
        if (eventType is not null)
            cmd.Parameters.AddWithValue("@type", eventType);

        var results = new List<(long, int, string, string?, string?, string?, string?, int, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetInt32(7),
                reader.GetString(8)));
        }
        return results;
    }

    // ── DMV Query Observations ──

    public void UpsertDmvQueryObservation(string sessionId, string queryShapeHash, string queryShape,
        string tablesReferenced, bool isWrite, int executionCount, string lastExecutionTime, int clockOffsetMs)
    {
        var now = DateTimeOffset.UtcNow.ToString("o");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO dmv_query_observations
                (session_id, query_shape_hash, query_shape, tables_referenced, is_write,
                 execution_count, last_execution_time, clock_offset_ms, first_seen, last_seen)
            VALUES
                (@sid, @hash, @shape, @tables, @isWrite,
                 @execCount, @lastExec, @clockOffset, @now, @now)
            ON CONFLICT(session_id, query_shape_hash) DO UPDATE SET
                execution_count = execution_count + @execCount,
                last_execution_time = @lastExec,
                clock_offset_ms = @clockOffset,
                last_seen = @now
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@hash", queryShapeHash);
        cmd.Parameters.AddWithValue("@shape", queryShape);
        cmd.Parameters.AddWithValue("@tables", tablesReferenced);
        cmd.Parameters.AddWithValue("@isWrite", isWrite ? 1 : 0);
        cmd.Parameters.AddWithValue("@execCount", executionCount);
        cmd.Parameters.AddWithValue("@lastExec", lastExecutionTime);
        cmd.Parameters.AddWithValue("@clockOffset", clockOffsetMs);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(string QueryShapeHash, string QueryShape, string TablesReferenced,
        bool IsWrite, int ExecutionCount, string LastExecutionTime)>
        GetDmvQueryObservations(string sessionId, int limit = 500)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT query_shape_hash, query_shape, tables_referenced, is_write, execution_count, last_execution_time
            FROM dmv_query_observations
            WHERE session_id = @sid
            ORDER BY last_execution_time DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<(string, string, string, bool, int, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3) == 1,
                reader.GetInt32(4),
                reader.GetString(5)));
        }
        return results;
    }

    public IReadOnlyList<(string QueryShapeHash, string QueryShape, string TablesReferenced,
        bool IsWrite, int ExecutionCount, string LastExecutionTime)>
        GetRecentDmvQueries(string sessionId, string sinceTimestamp)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT query_shape_hash, query_shape, tables_referenced, is_write, execution_count, last_execution_time
            FROM dmv_query_observations
            WHERE session_id = @sid AND last_execution_time > @since
            ORDER BY last_execution_time DESC
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@since", sinceTimestamp);

        var results = new List<(string, string, string, bool, int, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3) == 1,
                reader.GetInt32(4),
                reader.GetString(5)));
        }
        return results;
    }

    // ── Correlated Actions ──

    public void UpsertCorrelatedAction(string sessionId, string correlationKey, string treeHash,
        string elementId, string? controlType, string? queryShapeHash, bool isWrite, string? tablesReferenced)
    {
        var now = DateTimeOffset.UtcNow.ToString("o");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO correlated_actions
                (session_id, correlation_key, tree_hash, element_id, element_control_type,
                 query_shape_hash, query_is_write, tables_referenced, occurrence_count,
                 confidence, first_seen, last_seen)
            VALUES
                (@sid, @key, @treeHash, @elemId, @ctrlType,
                 @qHash, @isWrite, @tables, 1,
                 0.3, @now, @now)
            ON CONFLICT(session_id, correlation_key) DO UPDATE SET
                occurrence_count = occurrence_count + 1,
                confidence = CASE
                    WHEN occurrence_count + 1 >= 10 THEN 0.9
                    WHEN occurrence_count + 1 >= 3 THEN 0.6
                    ELSE 0.3
                END,
                last_seen = @now
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@key", correlationKey);
        cmd.Parameters.AddWithValue("@treeHash", treeHash);
        cmd.Parameters.AddWithValue("@elemId", elementId);
        cmd.Parameters.AddWithValue("@ctrlType", (object?)controlType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@qHash", (object?)queryShapeHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@isWrite", isWrite ? 1 : 0);
        cmd.Parameters.AddWithValue("@tables", (object?)tablesReferenced ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(string CorrelationKey, string TreeHash, string ElementId,
        string? ControlType, string? QueryShapeHash, bool IsWrite, string? TablesReferenced,
        int OccurrenceCount, double Confidence)>
        GetCorrelatedActions(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT correlation_key, tree_hash, element_id, element_control_type,
                   query_shape_hash, query_is_write, tables_referenced,
                   occurrence_count, confidence
            FROM correlated_actions
            WHERE session_id = @sid
            ORDER BY occurrence_count DESC
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);

        var results = new List<(string, string, string, string?, string?, bool, string?, int, double)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5) == 1,
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetInt32(7),
                reader.GetDouble(8)));
        }
        return results;
    }

    public IReadOnlyList<(string CorrelationKey, string TreeHash, string ElementId,
        string? ControlType, string? QueryShape, string? TablesReferenced,
        int OccurrenceCount, double Confidence)>
        GetWritebackCandidates(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT ca.correlation_key, ca.tree_hash, ca.element_id, ca.element_control_type,
                   dqo.query_shape, ca.tables_referenced,
                   ca.occurrence_count, ca.confidence
            FROM correlated_actions ca
            LEFT JOIN dmv_query_observations dqo
                ON ca.session_id = dqo.session_id AND ca.query_shape_hash = dqo.query_shape_hash
            WHERE ca.session_id = @sid AND ca.query_is_write = 1
            ORDER BY ca.occurrence_count DESC
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);

        var results = new List<(string, string, string, string?, string?, string?, int, double)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt32(6),
                reader.GetDouble(7)));
        }
        return results;
    }

    // ── Learned Routines ──

    public void UpsertLearnedRoutine(string sessionId, string routineHash, string pathJson,
        int pathLength, int frequency, double confidence, string? startElementId, string? endElementId,
        string? correlatedWriteQueries, bool hasWritebackCandidate)
    {
        var now = DateTimeOffset.UtcNow.ToString("o");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO learned_routines
                (session_id, routine_hash, path_json, path_length, frequency, confidence,
                 start_element_id, end_element_id, correlated_write_queries,
                 has_writeback_candidate, discovered_at, last_observed)
            VALUES
                (@sid, @hash, @path, @len, @freq, @conf,
                 @startElem, @endElem, @writeQueries,
                 @hasWriteback, @now, @now)
            ON CONFLICT(session_id, routine_hash) DO UPDATE SET
                frequency = @freq,
                confidence = @conf,
                correlated_write_queries = @writeQueries,
                has_writeback_candidate = @hasWriteback,
                last_observed = @now
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@hash", routineHash);
        cmd.Parameters.AddWithValue("@path", pathJson);
        cmd.Parameters.AddWithValue("@len", pathLength);
        cmd.Parameters.AddWithValue("@freq", frequency);
        cmd.Parameters.AddWithValue("@conf", confidence);
        cmd.Parameters.AddWithValue("@startElem", (object?)startElementId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@endElem", (object?)endElementId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@writeQueries", (object?)correlatedWriteQueries ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@hasWriteback", hasWritebackCandidate ? 1 : 0);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(string RoutineHash, string PathJson, int PathLength, int Frequency,
        double Confidence, string? StartElementId, string? EndElementId,
        string? CorrelatedWriteQueries, bool HasWritebackCandidate)>
        GetLearnedRoutines(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT routine_hash, path_json, path_length, frequency, confidence,
                   start_element_id, end_element_id, correlated_write_queries, has_writeback_candidate
            FROM learned_routines
            WHERE session_id = @sid
            ORDER BY frequency DESC
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);

        var results = new List<(string, string, int, int, double, string?, string?, string?, bool)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetDouble(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetInt32(8) == 1));
        }
        return results;
    }

    // ── Behavioral Telemetry Counts ──

    public int GetBehavioralEventCount(string sessionId, string? eventType = null)
    {
        using var cmd = _conn.CreateCommand();
        if (eventType is null)
        {
            cmd.CommandText = "SELECT COUNT(*) FROM behavioral_events WHERE session_id = @sid";
            cmd.Parameters.AddWithValue("@sid", sessionId);
        }
        else
        {
            cmd.CommandText = "SELECT COUNT(*) FROM behavioral_events WHERE session_id = @sid AND event_type = @type";
            cmd.Parameters.AddWithValue("@sid", sessionId);
            cmd.Parameters.AddWithValue("@type", eventType);
        }
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int GetUniqueScreenCount(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(DISTINCT tree_hash) FROM behavioral_events
            WHERE session_id = @sid AND tree_hash IS NOT NULL
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public IReadOnlyList<string> GetDistinctTreeHashes(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT tree_hash FROM behavioral_events
            WHERE session_id = @sid AND tree_hash IS NOT NULL
            ORDER BY tree_hash
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        using var reader = cmd.ExecuteReader();
        var results = new List<string>();
        while (reader.Read())
            results.Add(reader.GetString(0));
        return results;
    }

    public string? GetFirstBehavioralEventTimestamp(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT MIN(received_at) FROM behavioral_events
            WHERE session_id = @sid
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        var result = cmd.ExecuteScalar();
        return result is DBNull || result is null ? null : result.ToString();
    }

    public int GetDmvWriteShapeCount(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM dmv_query_observations WHERE session_id = @sid AND is_write = 1";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int GetCorrelatedActionCount(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM correlated_actions WHERE session_id = @sid";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int GetWritebackCandidateCount(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM correlated_actions WHERE session_id = @sid AND query_is_write = 1";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int GetLearnedRoutineCount(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM learned_routines WHERE session_id = @sid";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int GetRoutinesWithWritebackCount(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM learned_routines WHERE session_id = @sid AND has_writeback_candidate = 1";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // ── Behavioral Event Pruning ──

    /// <summary>
    /// Deletes behavioral_events older than <paramref name="olderThanDays"/> days
    /// where the event's tree_hash appears in a stable learned routine (frequency >= 5).
    /// Returns the number of rows deleted.
    /// </summary>
    public int PruneBehavioralEvents(string sessionId, int olderThanDays)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-olderThanDays).ToString("o");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM behavioral_events
            WHERE session_id = @sid
              AND received_at < @cutoff
              AND tree_hash IN (
                  SELECT DISTINCT je.value
                  FROM learned_routines lr, json_each(lr.path_json) je
                  WHERE lr.session_id = @sid AND lr.frequency >= 5
              )
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        cmd.ExecuteNonQuery();

        // Return affected row count via changes()
        using var countCmd = _conn.CreateCommand();
        countCmd.CommandText = "SELECT changes()";
        return Convert.ToInt32(countCmd.ExecuteScalar());
    }

    // ── Feedback Events CRUD ──

    public int InsertFeedbackEvent(FeedbackEvent evt)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO feedback_events
                (session_id, event_type, source, source_id, target_type, target_id,
                 payload_json, directive_type, directive_json, applied_at, applied_by,
                 causal_chain_json, created_at)
            VALUES
                (@sid, @eventType, @source, @sourceId, @targetType, @targetId,
                 @payload, @directive, @directiveJson, @appliedAt, @appliedBy,
                 @causalChain, @createdAt);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@sid", evt.SessionId);
        cmd.Parameters.AddWithValue("@eventType", evt.EventType);
        cmd.Parameters.AddWithValue("@source", evt.Source);
        cmd.Parameters.AddWithValue("@sourceId", (object?)evt.SourceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@targetType", evt.TargetType);
        cmd.Parameters.AddWithValue("@targetId", evt.TargetId);
        cmd.Parameters.AddWithValue("@payload", (object?)evt.PayloadJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@directive", evt.DirectiveType.ToString());
        cmd.Parameters.AddWithValue("@directiveJson", (object?)evt.DirectiveJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@appliedAt", (object?)evt.AppliedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@appliedBy", (object?)evt.AppliedBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@causalChain", (object?)evt.CausalChainJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@createdAt", evt.CreatedAt);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public FeedbackEvent? GetFeedbackEvent(int id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, session_id, event_type, source, source_id, target_type, target_id,
                   payload_json, directive_type, directive_json, applied_at, applied_by,
                   causal_chain_json, created_at
            FROM feedback_events WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadFeedbackEvent(r) : null;
    }

    public IReadOnlyList<FeedbackEvent> GetPendingFeedbackEvents(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, session_id, event_type, source, source_id, target_type, target_id,
                   payload_json, directive_type, directive_json, applied_at, applied_by,
                   causal_chain_json, created_at
            FROM feedback_events
            WHERE session_id = @sid AND applied_at IS NULL
            ORDER BY id ASC
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        var results = new List<FeedbackEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) results.Add(ReadFeedbackEvent(r));
        return results;
    }

    public void MarkFeedbackEventApplied(int id, string appliedBy)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE feedback_events SET applied_at = @now, applied_by = @by WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@by", appliedBy);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public bool HasDecayEventToday(string sessionId, string targetId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM feedback_events
            WHERE session_id = @sid AND target_id = @tid AND source = 'decay'
              AND created_at >= @today
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@tid", targetId);
        cmd.Parameters.AddWithValue("@today", DateTime.UtcNow.Date.ToString("o"));
        return cmd.ExecuteScalar() is not null;
    }

    public void UpdateCorrelationConfidence(string sessionId, string correlationKey, double newConfidence)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE correlated_actions SET confidence = @conf
            WHERE session_id = @sid AND correlation_key = @key
            """;
        cmd.Parameters.AddWithValue("@conf", newConfidence);
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@key", correlationKey);
        cmd.ExecuteNonQuery();
    }

    public void UpdateCorrelationFlags(string sessionId, string correlationKey,
        bool? operatorApproved = null, bool? operatorRejected = null,
        bool? promotionSuspended = null, int? consecutiveFailures = null,
        bool? stale = null, string? staleSince = null)
    {
        var setClauses = new List<string>();
        var parameters = new List<(string Name, object Value)>();

        if (operatorApproved.HasValue)
        {
            setClauses.Add("operator_approved = @opApproved");
            parameters.Add(("@opApproved", operatorApproved.Value ? 1 : 0));
        }
        if (operatorRejected.HasValue)
        {
            setClauses.Add("operator_rejected = @opRejected");
            parameters.Add(("@opRejected", operatorRejected.Value ? 1 : 0));
        }
        if (promotionSuspended.HasValue)
        {
            setClauses.Add("promotion_suspended = @promoSuspended");
            parameters.Add(("@promoSuspended", promotionSuspended.Value ? 1 : 0));
        }
        if (consecutiveFailures.HasValue)
        {
            setClauses.Add("consecutive_failures = @consecFail");
            parameters.Add(("@consecFail", consecutiveFailures.Value));
        }
        if (stale.HasValue)
        {
            setClauses.Add("stale = @stale");
            parameters.Add(("@stale", stale.Value ? 1 : 0));
        }
        if (staleSince is not null)
        {
            setClauses.Add("stale_since = @staleSince");
            parameters.Add(("@staleSince", staleSince));
        }

        if (setClauses.Count == 0) return;

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"UPDATE correlated_actions SET {string.Join(", ", setClauses)} WHERE session_id = @sid AND correlation_key = @key";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@key", correlationKey);
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    public (bool OperatorApproved, bool OperatorRejected, bool PromotionSuspended,
        int ConsecutiveFailures, bool Stale, string? StaleSince)?
        GetCorrelatedActionExtended(string sessionId, string correlationKey)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT operator_approved, operator_rejected, promotion_suspended,
                   consecutive_failures, stale, stale_since
            FROM correlated_actions
            WHERE session_id = @sid AND correlation_key = @key
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@key", correlationKey);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return (
            reader.GetInt32(0) == 1,
            reader.GetInt32(1) == 1,
            reader.GetInt32(2) == 1,
            reader.GetInt32(3),
            reader.GetInt32(4) == 1,
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    public int GetFeedbackEventCount(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM feedback_events WHERE session_id = @sid";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int GetFeedbackEventCountByApplier(string sessionId, string appliedBy)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM feedback_events WHERE session_id = @sid AND applied_by = @by";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@by", appliedBy);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public IReadOnlyList<string> GetSuspendedPromotions(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT correlation_key FROM correlated_actions
            WHERE session_id = @sid AND promotion_suspended = 1
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        var results = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) results.Add(reader.GetString(0));
        return results;
    }

    public IReadOnlyList<(string CorrelationKey, string StaleSince)> GetExpiredStaleCorrelations(string sessionId, int ttlDays)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-ttlDays).ToString("o");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT correlation_key, stale_since FROM correlated_actions
            WHERE session_id = @sid AND stale = 1 AND stale_since < @cutoff
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        var results = new List<(string, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) results.Add((reader.GetString(0), reader.GetString(1)));
        return results;
    }

    public bool HasReplacementCorrelation(string sessionId, string treeHash, string elementId, string excludeCorrelationKey)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM correlated_actions
            WHERE session_id = @sid AND tree_hash = @th AND element_id = @eid
              AND stale = 0 AND correlation_key != @exclude
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@th", treeHash);
        cmd.Parameters.AddWithValue("@eid", elementId);
        cmd.Parameters.AddWithValue("@exclude", excludeCorrelationKey);
        return cmd.ExecuteScalar() is not null;
    }

    public IReadOnlyList<(string CorrelationKey, double Confidence, string LastSeen)> GetIdleCorrelations(string sessionId, int idleDays)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-idleDays).ToString("o");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT correlation_key, confidence, last_seen FROM correlated_actions
            WHERE session_id = @sid AND last_seen < @cutoff AND confidence > 0.5
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        var results = new List<(string, double, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) results.Add((reader.GetString(0), reader.GetDouble(1), reader.GetString(2)));
        return results;
    }

    public void DeleteCorrelation(string sessionId, string correlationKey)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM correlated_actions WHERE session_id = @sid AND correlation_key = @key";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@key", correlationKey);
        cmd.ExecuteNonQuery();
    }

    public void UpsertWindowOverride(string sessionId, string treeHash, string elementId, double windowSeconds, int sampleCount)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO correlation_window_overrides
                (session_id, tree_hash, element_id, window_seconds, sample_count, computed_at)
            VALUES (@sid, @th, @eid, @window, @samples, @now)
            ON CONFLICT(session_id, tree_hash, element_id) DO UPDATE SET
                window_seconds = @window,
                sample_count = @samples,
                computed_at = @now
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@th", treeHash);
        cmd.Parameters.AddWithValue("@eid", elementId);
        cmd.Parameters.AddWithValue("@window", windowSeconds);
        cmd.Parameters.AddWithValue("@samples", sampleCount);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public double? GetWindowOverride(string sessionId, string treeHash, string elementId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT window_seconds FROM correlation_window_overrides
            WHERE session_id = @sid AND tree_hash = @th AND element_id = @eid
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@th", treeHash);
        cmd.Parameters.AddWithValue("@eid", elementId);
        var result = cmd.ExecuteScalar();
        return result is DBNull or null ? null : Convert.ToDouble(result);
    }

    public int GetWindowOverrideCount(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM correlation_window_overrides WHERE session_id = @sid";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public IReadOnlyList<FeedbackEvent> GetFeedbackEventsForTarget(string sessionId, string targetId, string? source = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = source is null
            ? """
              SELECT id, session_id, event_type, source, source_id, target_type, target_id,
                     payload_json, directive_type, directive_json, applied_at, applied_by,
                     causal_chain_json, created_at
              FROM feedback_events
              WHERE session_id = @sid AND target_id = @tid
              ORDER BY id ASC
              """
            : """
              SELECT id, session_id, event_type, source, source_id, target_type, target_id,
                     payload_json, directive_type, directive_json, applied_at, applied_by,
                     causal_chain_json, created_at
              FROM feedback_events
              WHERE session_id = @sid AND target_id = @tid AND source = @src
              ORDER BY id ASC
              """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@tid", targetId);
        if (source is not null)
            cmd.Parameters.AddWithValue("@src", source);
        var results = new List<FeedbackEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) results.Add(ReadFeedbackEvent(r));
        return results;
    }

    public void RemoveWritebackFlagForCorrelation(string sessionId, string correlationKey)
    {
        // Look up the query_shape_hash for this correlation
        using var lookupCmd = _conn.CreateCommand();
        lookupCmd.CommandText = """
            SELECT query_shape_hash FROM correlated_actions
            WHERE session_id = @sid AND correlation_key = @key
            """;
        lookupCmd.Parameters.AddWithValue("@sid", sessionId);
        lookupCmd.Parameters.AddWithValue("@key", correlationKey);
        var hash = lookupCmd.ExecuteScalar();
        if (hash is null or DBNull) return;

        // Clear has_writeback_candidate on routines referencing this query shape
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE learned_routines SET has_writeback_candidate = 0
            WHERE session_id = @sid
              AND correlated_write_queries LIKE @pattern
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@pattern", $"%{(string)hash}%");
        cmd.ExecuteNonQuery();
    }

    private static FeedbackEvent ReadFeedbackEvent(SqliteDataReader r)
    {
        return new FeedbackEvent(
            SessionId: r.GetString(1),
            EventType: r.GetString(2),
            Source: r.GetString(3),
            SourceId: r.IsDBNull(4) ? null : r.GetString(4),
            TargetType: r.GetString(5),
            TargetId: r.GetString(6),
            PayloadJson: r.IsDBNull(7) ? null : r.GetString(7),
            DirectiveType: Enum.Parse<DirectiveType>(r.GetString(8)),
            DirectiveJson: r.IsDBNull(9) ? null : r.GetString(9),
            CausalChainJson: r.IsDBNull(12) ? null : r.GetString(12))
        {
            Id = r.GetInt32(0),
            AppliedAt = r.IsDBNull(10) ? null : r.GetString(10),
            AppliedBy = r.IsDBNull(11) ? null : r.GetString(11),
            CreatedAt = r.GetString(13)
        };
    }

    public IReadOnlyList<string> GetRecentWritebackTargets(string sessionId, int withinDays)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-withinDays).ToString("o");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT target_id FROM feedback_events
            WHERE session_id = @sid AND source = 'writeback' AND created_at >= @cutoff
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        var results = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) results.Add(reader.GetString(0));
        return results;
    }

    // --- Spec D: Seed state methods ---

    public record CorrelationSource(string Source, string? SeedDigest, string? SeededAt);

    public CorrelationSource GetCorrelatedActionSource(string sessionId, string correlationKey)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT source, seed_digest, seeded_at FROM correlated_actions WHERE session_id = @sid AND correlation_key = @key";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@key", correlationKey);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return new("local", null, null);
        return new(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
    }

    public void SetCorrelatedActionSource(string sessionId, string correlationKey, string source, string? seedDigest, string? seededAt)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE correlated_actions SET source = @src, seed_digest = @dig, seeded_at = @at WHERE session_id = @sid AND correlation_key = @key";
        cmd.Parameters.AddWithValue("@src", source);
        cmd.Parameters.AddWithValue("@dig", (object?)seedDigest ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@at", (object?)seededAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@key", correlationKey);
        cmd.ExecuteNonQuery();
    }

    public record AppliedSeed(string SeedDigest, string Phase, string AppliedAt, int CorrelationsApplied, int CorrelationsSkipped);

    public void InsertAppliedSeed(string seedDigest, string phase, string appliedAt, int correlationsApplied, int correlationsSkipped)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO applied_seeds (seed_digest, phase, applied_at, correlations_applied, correlations_skipped) VALUES (@d, @p, @a, @ca, @cs)";
        cmd.Parameters.AddWithValue("@d", seedDigest);
        cmd.Parameters.AddWithValue("@p", phase);
        cmd.Parameters.AddWithValue("@a", appliedAt);
        cmd.Parameters.AddWithValue("@ca", correlationsApplied);
        cmd.Parameters.AddWithValue("@cs", correlationsSkipped);
        cmd.ExecuteNonQuery();
    }

    public AppliedSeed? GetAppliedSeed(string seedDigest)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT seed_digest, phase, applied_at, correlations_applied, correlations_skipped FROM applied_seeds WHERE seed_digest = @d";
        cmd.Parameters.AddWithValue("@d", seedDigest);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4));
    }

    public record SeedItem(int Id, string SeedDigest, string ItemType, string ItemKey, string AppliedAt, string? ConfirmedAt, int LocalMatchCount, string? RejectedAt);

    public void InsertSeedItem(string seedDigest, string itemType, string itemKey, string appliedAt)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO seed_items (seed_digest, item_type, item_key, applied_at) VALUES (@d, @t, @k, @a)";
        cmd.Parameters.AddWithValue("@d", seedDigest);
        cmd.Parameters.AddWithValue("@t", itemType);
        cmd.Parameters.AddWithValue("@k", itemKey);
        cmd.Parameters.AddWithValue("@a", appliedAt);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<SeedItem> GetSeedItems(string seedDigest)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, seed_digest, item_type, item_key, applied_at, confirmed_at, local_match_count, rejected_at FROM seed_items WHERE seed_digest = @d";
        cmd.Parameters.AddWithValue("@d", seedDigest);
        var items = new List<SeedItem>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            items.Add(new(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.GetInt32(6), r.IsDBNull(7) ? null : r.GetString(7)));
        return items;
    }

    public void ConfirmSeedItem(string seedDigest, string itemType, string itemKey, string confirmedAt)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE seed_items SET confirmed_at = COALESCE(confirmed_at, @c), local_match_count = local_match_count + 1
            WHERE seed_digest = @d AND item_type = @t AND item_key = @k AND rejected_at IS NULL";
        cmd.Parameters.AddWithValue("@c", confirmedAt);
        cmd.Parameters.AddWithValue("@d", seedDigest);
        cmd.Parameters.AddWithValue("@t", itemType);
        cmd.Parameters.AddWithValue("@k", itemKey);
        cmd.ExecuteNonQuery();
    }

    public void RejectSeedItem(string seedDigest, string itemType, string itemKey, string rejectedAt)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE seed_items SET rejected_at = @r WHERE seed_digest = @d AND item_type = @t AND item_key = @k AND confirmed_at IS NULL";
        cmd.Parameters.AddWithValue("@r", rejectedAt);
        cmd.Parameters.AddWithValue("@d", seedDigest);
        cmd.Parameters.AddWithValue("@t", itemType);
        cmd.Parameters.AddWithValue("@k", itemKey);
        cmd.ExecuteNonQuery();
    }

    public double GetSeedConfirmationRatio(string seedDigest)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                CAST(SUM(CASE WHEN confirmed_at IS NOT NULL THEN 1 ELSE 0 END) AS REAL) /
                NULLIF(SUM(CASE WHEN rejected_at IS NULL THEN 1 ELSE 0 END), 0)
            FROM seed_items WHERE seed_digest = @d";
        cmd.Parameters.AddWithValue("@d", seedDigest);
        var result = cmd.ExecuteScalar();
        return result is double d ? d : 0.0;
    }

    public void Dispose()
    {
        _conn.Dispose();
    }
}
