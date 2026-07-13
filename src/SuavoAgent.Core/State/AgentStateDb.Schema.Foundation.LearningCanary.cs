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
    private void InitializeLearningAndCanarySchema()
    {
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
                hmac_salt TEXT,
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
            CREATE TABLE IF NOT EXISTS discovered_unique_columns (
                session_id TEXT NOT NULL,
                schema_name TEXT NOT NULL,
                table_name TEXT NOT NULL,
                column_name TEXT NOT NULL,
                discovered_at TEXT NOT NULL,
                PRIMARY KEY(session_id, schema_name, table_name, column_name)
            );
            CREATE TABLE IF NOT EXISTS schema_discovery_snapshots (
                session_id TEXT PRIMARY KEY,
                source_identity_digest TEXT NOT NULL,
                database_name TEXT NOT NULL,
                schema_contract_digest TEXT,
                fk_discovery_complete INTEGER NOT NULL DEFAULT 0,
                template_evidence_digest TEXT,
                template_evidence_complete INTEGER NOT NULL DEFAULT 0,
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

        TryAlter("ALTER TABLE schema_discovery_snapshots ADD COLUMN template_evidence_digest TEXT");
        TryAlter("ALTER TABLE schema_discovery_snapshots ADD COLUMN template_evidence_complete INTEGER NOT NULL DEFAULT 0");

        // Migrate: add pom_snapshot for frozen POM review (CRITICAL-6) — now in CREATE TABLE but needed for existing DBs
        TryAlter("ALTER TABLE learning_session ADD COLUMN pom_snapshot TEXT");

        // Migrate: add hmac_salt — secret per-session salt for PHI hashing (replaces non-secret AgentId)
        TryAlter("ALTER TABLE learning_session ADD COLUMN hmac_salt TEXT");

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
    }
}
