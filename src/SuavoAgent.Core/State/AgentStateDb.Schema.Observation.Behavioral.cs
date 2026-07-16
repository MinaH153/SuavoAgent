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
    private void InitializeBehavioralAndFeedbackSchema()
    {
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
                received_at TEXT NOT NULL,
                source_channel TEXT NOT NULL DEFAULT 'pms'
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
        InitializeBehavioralDeliverySchema();

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
    }
}
