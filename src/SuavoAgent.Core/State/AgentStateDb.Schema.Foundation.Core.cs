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
    private void InitializeSchemaFoundation()
    {
        // SQLite hardening PRAGMAs (journal_mode returns a result row, so use ExecuteScalar)
        using (var walCmd = _conn.CreateCommand())
        {
            walCmd.CommandText = "PRAGMA journal_mode=WAL";
            walCmd.ExecuteScalar();
        }
        using (var fkCmd = _conn.CreateCommand())
        {
            fkCmd.CommandText = "PRAGMA foreign_keys=ON";
            fkCmd.ExecuteNonQuery();
        }
        // Prevent SQLITE_BUSY errors under concurrent worker access
        using (var btCmd = _conn.CreateCommand())
        {
            btCmd.CommandText = "PRAGMA busy_timeout=5000";
            btCmd.ExecuteNonQuery();
        }
        using (var syncCmd = _conn.CreateCommand())
        {
            syncCmd.CommandText = "PRAGMA synchronous=NORMAL";
            syncCmd.ExecuteNonQuery();
        }

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
            CREATE TABLE IF NOT EXISTS update_command_receipts (
                command_id TEXT PRIMARY KEY,
                envelope_nonce TEXT NOT NULL UNIQUE,
                data_hash TEXT NOT NULL,
                target_version TEXT NOT NULL,
                state TEXT NOT NULL CHECK (state IN ('pending_stage','staged','confirmed')),
                registered_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS vision_configuration_commands (
                command_id TEXT PRIMARY KEY,
                config_digest TEXT NOT NULL,
                options_document TEXT NOT NULL,
                bundle_url TEXT,
                bundle_sha256 TEXT,
                envelope_nonce TEXT NOT NULL UNIQUE,
                envelope_binding TEXT NOT NULL,
                state TEXT NOT NULL CHECK (state IN ('pending_apply','pending_ack','acked')),
                apply_succeeded INTEGER NOT NULL DEFAULT 1 CHECK (apply_succeeded IN (0,1)),
                generation INTEGER,
                result_code TEXT,
                registered_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS vision_configuration_failures (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                envelope_binding TEXT NOT NULL,
                command_id TEXT,
                code TEXT NOT NULL,
                recorded_at TEXT NOT NULL,
                UNIQUE (envelope_binding, code)
            );
            """;
        cmd.ExecuteNonQuery();

        TryAlter(
            "ALTER TABLE vision_configuration_commands " +
            "ADD COLUMN apply_succeeded INTEGER NOT NULL DEFAULT 1");

        // Migrate: add columns for chained audit entries
        TryAlter("ALTER TABLE audit_entries ADD COLUMN event_type TEXT DEFAULT 'writeback_transition'");
        TryAlter("ALTER TABLE audit_entries ADD COLUMN command_id TEXT");
        TryAlter("ALTER TABLE audit_entries ADD COLUMN requester_id TEXT");
        TryAlter("ALTER TABLE audit_entries ADD COLUMN rx_number TEXT");

        // Codex 2026-04-26 audit-schema gap closure. Forensic metadata —
        // does NOT participate in the chained hash (existing rows would
        // fail to verify if we changed the hash inputs). Recorded for
        // reconstruction of capture intent at audit time.
        TryAlter("ALTER TABLE audit_entries ADD COLUMN actor TEXT");
        TryAlter("ALTER TABLE audit_entries ADD COLUMN source_component TEXT");
        TryAlter("ALTER TABLE audit_entries ADD COLUMN capture_reason TEXT");
        TryAlter("ALTER TABLE audit_entries ADD COLUMN window_title_hash TEXT");
        TryAlter("ALTER TABLE audit_entries ADD COLUMN element_count INTEGER");
        TryAlter("ALTER TABLE audit_entries ADD COLUMN scrubber_version TEXT");
        TryAlter("ALTER TABLE audit_entries ADD COLUMN storage_id TEXT");

        // Migrate: add next_retry_at for exponential backoff
        TryAlter("ALTER TABLE writeback_states ADD COLUMN next_retry_at TEXT");

        // Migrate: add DPAPI-encrypted rx number (actual value for crash recovery); rx_number column now stores HMAC hash
        TryAlter("ALTER TABLE writeback_states ADD COLUMN rx_number_enc TEXT");
    }
}
