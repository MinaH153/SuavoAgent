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
    private void InitializeUniversalObservationSchema()
    {
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
                    UNIQUE(seed_digest, item_type, item_key),
                    CHECK (confirmed_at IS NULL OR rejected_at IS NULL)
                )";
            itemCmd.ExecuteNonQuery();
        }

        // Universal Observation tables
        using (var appSessionCmd = _conn.CreateCommand())
        {
            appSessionCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS app_sessions (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    session_id TEXT NOT NULL,
                    app_name TEXT NOT NULL,
                    window_title_hash TEXT,
                    start_ts TEXT NOT NULL,
                    end_ts TEXT,
                    focus_ms INTEGER DEFAULT 0,
                    preceding_app TEXT,
                    following_app TEXT,
                    created_at TEXT DEFAULT (datetime('now'))
                )
            """;
            appSessionCmd.ExecuteNonQuery();
        }

        using (var temporalCmd = _conn.CreateCommand())
        {
            temporalCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS temporal_profiles (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    session_id TEXT NOT NULL,
                    period_type TEXT NOT NULL,
                    period_key TEXT NOT NULL,
                    app_distribution TEXT,
                    action_volume INTEGER DEFAULT 0,
                    peak_load_score REAL DEFAULT 0,
                    updated_at TEXT DEFAULT (datetime('now')),
                    UNIQUE(session_id, period_type, period_key)
                )
            """;
            temporalCmd.ExecuteNonQuery();
        }

        using (var stationCmd = _conn.CreateCommand())
        {
            stationCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS station_profiles (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    machine_hash TEXT NOT NULL,
                    processor_count INTEGER,
                    ram_bucket_gb INTEGER,
                    monitor_count INTEGER,
                    os_version TEXT,
                    profile_json TEXT,
                    captured_at TEXT DEFAULT (datetime('now'))
                )
            """;
            stationCmd.ExecuteNonQuery();
        }

        Execute("""
            CREATE TABLE IF NOT EXISTS document_profiles (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                doc_hash TEXT NOT NULL,
                file_type TEXT,
                schema_fingerprint TEXT,
                column_count INTEGER,
                row_count_bucket TEXT,
                category TEXT DEFAULT 'unknown',
                last_touched TEXT DEFAULT (datetime('now')),
                touch_count INTEGER DEFAULT 1,
                UNIQUE(session_id, doc_hash)
            )
        """);

        Execute("""
            CREATE TABLE IF NOT EXISTS business_meta (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                business_id TEXT NOT NULL UNIQUE,
                industry TEXT DEFAULT 'unknown',
                detected_apps TEXT,
                station_role TEXT,
                software_stack_hash TEXT,
                onboard_ts TEXT DEFAULT (datetime('now')),
                learning_phase TEXT,
                agent_version TEXT
            )
        """);

        Execute("CREATE TABLE IF NOT EXISTS config_kv (key TEXT PRIMARY KEY, value TEXT)");

        // Readiness timing pipeline
        Execute("""
            CREATE TABLE IF NOT EXISTS readiness_samples (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                rx_number_hash TEXT NOT NULL,
                entered_at TEXT,
                filled_at TEXT,
                verified_at TEXT,
                ready_at TEXT,
                picked_up_at TEXT,
                elapsed_minutes REAL,
                day_of_week INTEGER,
                hour_of_day INTEGER,
                is_controlled INTEGER DEFAULT 0,
                concurrent_queue_depth INTEGER DEFAULT 0,
                created_at TEXT DEFAULT (datetime('now'))
            )
        """);
        Execute("CREATE INDEX IF NOT EXISTS idx_readiness_day ON readiness_samples(day_of_week, hour_of_day)");

        Execute("CREATE INDEX IF NOT EXISTS idx_wb_state ON writeback_states(state)");
        Execute("CREATE INDEX IF NOT EXISTS idx_ub_status ON unsynced_batches(status)");
        Execute("CREATE INDEX IF NOT EXISTS idx_audit_id ON audit_entries(id)");
        Execute("CREATE INDEX IF NOT EXISTS idx_canary_incidents_resolved ON schema_canary_incidents(resolved_at)");

    }
}
