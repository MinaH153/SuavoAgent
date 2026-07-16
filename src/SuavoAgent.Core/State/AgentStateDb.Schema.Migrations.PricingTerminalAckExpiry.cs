namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private void ApplyPricingTerminalAckExpiryMigration()
    {
        ApplyMigrationIfNeeded(34,
            "Allow PHI-free signed-command and PIC-authority expiry terminal ACKs",
            """
            CREATE TABLE pricing_terminal_ack_outbox_v34 (
                command_id TEXT PRIMARY KEY CHECK(
                    length(command_id) = 36
                    AND substr(command_id, 9, 1) = '-'
                    AND substr(command_id, 14, 1) = '-'
                    AND substr(command_id, 19, 1) = '-'
                    AND substr(command_id, 24, 1) = '-'
                    AND substr(command_id, 15, 1) = '4'
                    AND substr(command_id, 20, 1) IN ('8','9','a','b')
                    AND replace(command_id, '-', '') NOT GLOB '*[^0-9a-f]*'),
                result_kind TEXT NOT NULL CHECK(result_kind IN (
                    'none',
                    'cancelled',
                    'path_rejected',
                    'not_found',
                    'pricing_failed',
                    'local_confirmation_required',
                    'autopilot_rejected',
                    'discovery_failed')),
                error_code TEXT NOT NULL CHECK(error_code IN (
                    'pricing_executor_unavailable',
                    'autonomy_not_earned',
                    'pricing_candidate_token_required',
                    'ipc_not_configured',
                    'pipe_unreachable',
                    'ping_unanswered',
                    'ping_bad_status',
                    'ping_no_diagnostics',
                    'not_interactive',
                    'helper_preflight_failed',
                    'pricing_candidate_expired',
                    'pricing_candidate_resolution_failed',
                    'pricing_candidate_extension_invalid',
                    'pricing_candidate_path_invalid',
                    'autonomy_latch_persistence_failed',
                    'pricing_job_in_flight',
                    'helper_restart_in_progress',
                    'pricing_execution_exception',
                    'pricing_command_authority_expired',
                    'pricing_cost_basis_approval_expired',
                    'unknown_pack',
                    'helper_unreachable',
                    'pricing_discovery_unavailable',
                    'pricing_discovery_exception',
                    'pricing_workbook_validation_failed',
                    'pricing_result_payload_too_large',
                    'actuation_gate_closed',
                    'pioneerrx_not_attached',
                    'pricing_brain_operator_required',
                    'pricing_job_failed',
                    'pricing_cancelled',
                    'pricing_workbook_not_found',
                    'pricing_local_confirmation_required',
                    'autopilot_paused',
                    'autopilot_stopped',
                    'ipc_unavailable',
                    'helper_timeout',
                    'ipc_desync',
                    'helper_error',
                    'no_data',
                    'deserialize_error',
                    'unknown')),
                job_id TEXT CHECK(job_id IS NULL OR (
                    length(job_id) = 32
                    AND job_id NOT GLOB '*[^0-9a-f]*')),
                mode TEXT CHECK(mode IS NULL OR mode IN ('sql','uia','vision')),
                total_items INTEGER CHECK(
                    total_items IS NULL OR total_items BETWEEN 0 AND 1000000),
                completed_items INTEGER CHECK(
                    completed_items IS NULL OR completed_items BETWEEN 0 AND 1000000),
                failed_items INTEGER CHECK(
                    failed_items IS NULL OR failed_items BETWEEN 0 AND 1000000),
                reason_code TEXT,
                candidate_count INTEGER CHECK(
                    candidate_count IS NULL OR candidate_count BETWEEN 1 AND 100),
                helper_version_suspect INTEGER CHECK(
                    helper_version_suspect IS NULL OR helper_version_suspect IN (0,1)),
                payload_sha256 TEXT NOT NULL CHECK(
                    length(payload_sha256) = 64
                    AND payload_sha256 NOT GLOB '*[^0-9a-f]*'),
                state TEXT NOT NULL DEFAULT 'pending' CHECK(state IN ('pending','delivered')),
                attempt_count INTEGER NOT NULL DEFAULT 0 CHECK(attempt_count >= 0),
                next_attempt_at TEXT NOT NULL,
                created_at TEXT NOT NULL,
                delivered_at TEXT,
                CHECK(
                    (result_kind = 'none'
                     AND error_code IN (
                        'pricing_executor_unavailable', 'autonomy_not_earned',
                        'pricing_candidate_token_required', 'ipc_not_configured',
                        'pipe_unreachable', 'ping_unanswered', 'ping_bad_status',
                        'ping_no_diagnostics', 'not_interactive',
                        'helper_preflight_failed', 'pricing_candidate_expired',
                        'pricing_candidate_resolution_failed',
                        'autonomy_latch_persistence_failed', 'pricing_job_in_flight',
                        'helper_restart_in_progress', 'pricing_execution_exception',
                        'pricing_command_authority_expired',
                        'pricing_cost_basis_approval_expired',
                        'unknown_pack', 'helper_unreachable',
                        'pricing_discovery_unavailable', 'pricing_discovery_exception')
                     AND job_id IS NULL AND mode IS NULL
                     AND total_items IS NULL AND completed_items IS NULL
                     AND failed_items IS NULL AND reason_code IS NULL
                     AND candidate_count IS NULL AND helper_version_suspect IS NULL)
                    OR
                    (result_kind = 'cancelled' AND error_code = 'pricing_cancelled'
                     AND job_id IS NULL AND mode IS NULL
                     AND total_items IS NULL AND completed_items IS NULL
                     AND failed_items IS NULL AND reason_code IS NULL
                     AND candidate_count IS NULL AND helper_version_suspect IS NULL)
                    OR
                    (result_kind = 'path_rejected'
                     AND error_code IN (
                        'pricing_candidate_extension_invalid',
                        'pricing_candidate_path_invalid')
                     AND job_id IS NULL AND mode IS NULL
                     AND total_items IS NULL AND completed_items IS NULL
                     AND failed_items IS NULL AND reason_code IS NULL
                     AND candidate_count IS NULL AND helper_version_suspect IS NULL)
                    OR
                    (result_kind = 'not_found'
                     AND error_code = 'pricing_workbook_not_found'
                     AND job_id IS NULL AND mode IS NULL
                     AND total_items IS NULL AND completed_items IS NULL
                     AND failed_items IS NULL AND reason_code IS NULL
                     AND candidate_count IS NULL AND helper_version_suspect IS NULL)
                    OR
                    (result_kind = 'pricing_failed'
                     AND error_code IN (
                        'pricing_workbook_validation_failed',
                        'pricing_result_payload_too_large', 'helper_unreachable',
                        'actuation_gate_closed', 'pioneerrx_not_attached',
                        'pricing_brain_operator_required', 'pricing_job_failed')
                     AND reason_code = error_code
                     AND job_id IS NOT NULL AND mode IS NOT NULL
                     AND total_items IS NOT NULL AND completed_items IS NOT NULL
                     AND failed_items IS NOT NULL
                     AND completed_items + failed_items <= total_items
                     AND candidate_count IS NULL AND helper_version_suspect IS NULL)
                    OR
                    (result_kind = 'local_confirmation_required'
                     AND error_code = 'pricing_local_confirmation_required'
                     AND candidate_count IS NOT NULL
                     AND job_id IS NULL AND mode IS NULL
                     AND total_items IS NULL AND completed_items IS NULL
                     AND failed_items IS NULL AND reason_code IS NULL
                     AND helper_version_suspect IS NULL)
                    OR
                    (result_kind = 'autopilot_rejected'
                     AND error_code IN ('autopilot_paused','autopilot_stopped')
                     AND reason_code = error_code
                     AND job_id IS NULL AND mode IS NULL
                     AND total_items IS NULL AND completed_items IS NULL
                     AND failed_items IS NULL AND candidate_count IS NULL
                     AND helper_version_suspect IS NULL)
                    OR
                    (result_kind = 'discovery_failed'
                     AND error_code IN (
                        'ipc_unavailable', 'helper_timeout', 'ipc_desync',
                        'helper_error', 'no_data', 'deserialize_error', 'unknown')
                     AND reason_code = error_code
                     AND helper_version_suspect IS NOT NULL
                     AND job_id IS NULL AND mode IS NULL
                     AND total_items IS NULL AND completed_items IS NULL
                     AND failed_items IS NULL AND candidate_count IS NULL)
                )
            );

            INSERT INTO pricing_terminal_ack_outbox_v34 (
                command_id, result_kind, error_code, job_id, mode,
                total_items, completed_items, failed_items, reason_code,
                candidate_count, helper_version_suspect, payload_sha256,
                state, attempt_count, next_attempt_at, created_at, delivered_at)
            SELECT command_id, result_kind, error_code, job_id, mode,
                   total_items, completed_items, failed_items, reason_code,
                   candidate_count, helper_version_suspect, payload_sha256,
                   state, attempt_count, next_attempt_at, created_at, delivered_at
              FROM pricing_terminal_ack_outbox;

            DROP TRIGGER pricing_terminal_ack_identity_immutable;
            DROP TRIGGER pricing_terminal_ack_state_monotonic;
            DROP TRIGGER pricing_terminal_ack_no_delete;
            DROP INDEX idx_pricing_terminal_ack_pending;
            DROP TABLE pricing_terminal_ack_outbox;
            ALTER TABLE pricing_terminal_ack_outbox_v34
                RENAME TO pricing_terminal_ack_outbox;

            CREATE INDEX idx_pricing_terminal_ack_pending
                ON pricing_terminal_ack_outbox(state, next_attempt_at, created_at);
            CREATE TRIGGER pricing_terminal_ack_identity_immutable
            BEFORE UPDATE ON pricing_terminal_ack_outbox
            WHEN OLD.command_id != NEW.command_id
              OR OLD.result_kind != NEW.result_kind
              OR OLD.error_code != NEW.error_code
              OR OLD.job_id IS NOT NEW.job_id
              OR OLD.mode IS NOT NEW.mode
              OR OLD.total_items IS NOT NEW.total_items
              OR OLD.completed_items IS NOT NEW.completed_items
              OR OLD.failed_items IS NOT NEW.failed_items
              OR OLD.reason_code IS NOT NEW.reason_code
              OR OLD.candidate_count IS NOT NEW.candidate_count
              OR OLD.helper_version_suspect IS NOT NEW.helper_version_suspect
              OR OLD.payload_sha256 != NEW.payload_sha256
              OR OLD.created_at != NEW.created_at
            BEGIN
                SELECT RAISE(ABORT, 'pricing_terminal_ack_identity_immutable');
            END;
            CREATE TRIGGER pricing_terminal_ack_state_monotonic
            BEFORE UPDATE ON pricing_terminal_ack_outbox
            WHEN OLD.state = 'delivered' AND NEW.state != 'delivered'
            BEGIN
                SELECT RAISE(ABORT, 'pricing_terminal_ack_state_immutable');
            END;
            CREATE TRIGGER pricing_terminal_ack_no_delete
            BEFORE DELETE ON pricing_terminal_ack_outbox
            BEGIN
                SELECT RAISE(ABORT, 'pricing_terminal_ack_immutable');
            END;
            """);
    }
}
