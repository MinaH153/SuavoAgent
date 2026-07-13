namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private void ApplyWorkflowAuditOutboxMigration()
    {
        ApplyMigrationIfNeeded(22,
            "Append-only PHI-negative workflow audit and completion outbox",
            """
            CREATE TABLE workflow_audit_event_outbox (
                event_id TEXT PRIMARY KEY CHECK(
                    length(event_id) = 36
                    AND event_id = lower(event_id)
                    AND substr(event_id, 9, 1) = '-'
                    AND substr(event_id, 14, 1) = '-'
                    AND substr(event_id, 15, 1) = '4'
                    AND substr(event_id, 19, 1) = '-'
                    AND substr(event_id, 24, 1) = '-'
                    AND substr(event_id, 20, 1) IN ('8', '9', 'a', 'b')
                    AND event_id NOT GLOB '*[^0-9a-f-]*'),
                workflow_run_id TEXT NOT NULL CHECK(
                    length(workflow_run_id) = 36
                    AND workflow_run_id = lower(workflow_run_id)
                    AND substr(workflow_run_id, 9, 1) = '-'
                    AND substr(workflow_run_id, 14, 1) = '-'
                    AND substr(workflow_run_id, 19, 1) = '-'
                    AND substr(workflow_run_id, 24, 1) = '-'
                    AND workflow_run_id NOT GLOB '*[^0-9a-f-]*'),
                execution_ordinal INTEGER NOT NULL
                    CHECK(execution_ordinal BETWEEN 0 AND 99999),
                step_index INTEGER NOT NULL CHECK(step_index BETWEEN 0 AND 1024),
                verb_name TEXT NOT NULL CHECK(verb_name IN (
                    'assert_element', 'click_by_label', 'click_by_signature',
                    'launch_sandbox_app', 'lookup_patient', 'pioneerrx_click',
                    'pioneerrx_query', 'pioneerrx_writeback_rx_delivery',
                    'press_keys', 'query_top_ndcs_for_patient',
                    'type_into_field')),
                verb_version TEXT NOT NULL CHECK(
                    length(verb_version) BETWEEN 1 AND 60),
                requested_dry_run INTEGER NOT NULL
                    CHECK(requested_dry_run IN (0, 1)),
                effective_dry_run INTEGER
                    CHECK(effective_dry_run IS NULL OR effective_dry_run IN (0, 1)),
                outcome TEXT NOT NULL
                    CHECK(outcome IN ('success', 'rejected', 'failed', 'skipped')),
                exec_duration_ms INTEGER
                    CHECK(exec_duration_ms IS NULL OR exec_duration_ms BETWEEN 0 AND 600000),
                error_kind TEXT CHECK(error_kind IS NULL OR error_kind IN (
                    'authz_denied', 'condition_not_met', 'execution_exception',
                    'execution_failed', 'execution_timeout',
                    'manifest_resolution_failed', 'parameter_validation_failed',
                    'postcondition_exception', 'postcondition_failed',
                    'precondition_exception', 'precondition_failed',
                    'rollback_capture_exception')),
                params_field_count INTEGER NOT NULL
                    CHECK(params_field_count BETWEEN 0 AND 64),
                before_state_field_count INTEGER
                    CHECK(before_state_field_count IS NULL
                        OR before_state_field_count BETWEEN 0 AND 64),
                after_state_field_count INTEGER
                    CHECK(after_state_field_count IS NULL
                        OR after_state_field_count BETWEEN 0 AND 64),
                payload_json TEXT NOT NULL
                    CHECK(length(payload_json) BETWEEN 2 AND 8192),
                payload_sha256 TEXT NOT NULL CHECK(
                    length(payload_sha256) = 64
                    AND payload_sha256 NOT GLOB '*[^0-9a-f]*'),
                created_at TEXT NOT NULL,
                UNIQUE(workflow_run_id, execution_ordinal),
                CHECK(NOT (requested_dry_run = 1 AND effective_dry_run = 0)),
                CHECK(verb_name NOT IN (
                        'click_by_label', 'click_by_signature',
                        'launch_sandbox_app', 'pioneerrx_click',
                        'pioneerrx_writeback_rx_delivery', 'press_keys',
                        'type_into_field')
                    OR effective_dry_run IS NOT NULL),
                CHECK((outcome = 'success' AND error_kind IS NULL)
                    OR (outcome != 'success' AND error_kind IS NOT NULL))
            );
            CREATE INDEX idx_workflow_audit_event_run
                ON workflow_audit_event_outbox(workflow_run_id, execution_ordinal);
            CREATE TRIGGER workflow_audit_event_contiguous
            BEFORE INSERT ON workflow_audit_event_outbox
            WHEN NEW.execution_ordinal != COALESCE((
                SELECT MAX(execution_ordinal) + 1
                  FROM workflow_audit_event_outbox
                 WHERE workflow_run_id = NEW.workflow_run_id
            ), 0)
            BEGIN
                SELECT RAISE(ABORT, 'workflow_audit_event_ordinal_gap');
            END;
            CREATE TRIGGER workflow_audit_event_immutable
            BEFORE UPDATE ON workflow_audit_event_outbox
            BEGIN
                SELECT RAISE(ABORT, 'workflow_audit_event_immutable');
            END;
            CREATE TRIGGER workflow_audit_event_no_delete
            BEFORE DELETE ON workflow_audit_event_outbox
            BEGIN
                SELECT RAISE(ABORT, 'workflow_audit_event_immutable');
            END;

            CREATE TABLE workflow_audit_event_attempts (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id TEXT NOT NULL REFERENCES workflow_audit_event_outbox(event_id),
                attempt_number INTEGER NOT NULL CHECK(attempt_number > 0),
                outcome_code TEXT NOT NULL CHECK(outcome_code IN (
                    'accepted', 'terminal_rejection', 'retry_transport',
                    'retry_unsigned', 'retry_invalid_receipt',
                    'retry_server_unavailable')),
                attempted_at TEXT NOT NULL,
                next_attempt_at TEXT,
                http_status INTEGER CHECK(
                    http_status IS NULL OR http_status BETWEEN 100 AND 599),
                receipt_digest TEXT CHECK(receipt_digest IS NULL OR (
                    length(receipt_digest) = 64
                    AND receipt_digest NOT GLOB '*[^0-9a-f]*')),
                rejection_code TEXT CHECK(rejection_code IS NULL OR rejection_code IN (
                    'workflow_audit_invalid', 'workflow_run_not_found',
                    'workflow_tenant_mismatch', 'workflow_definition_invalid',
                    'workflow_step_mismatch', 'workflow_audit_ordinal_gap',
                    'workflow_audit_idempotency_conflict',
                    'workflow_run_terminal', 'workflow_audit_unavailable')),
                response_json TEXT CHECK(response_json IS NULL OR
                    length(response_json) BETWEEN 2 AND 16384),
                response_sha256 TEXT CHECK(response_sha256 IS NULL OR (
                    length(response_sha256) = 64
                    AND response_sha256 NOT GLOB '*[^0-9a-f]*')),
                response_key_id TEXT CHECK(
                    response_key_id IS NULL OR response_key_id = 'suavo-cmd-v1'),
                response_signature TEXT CHECK(response_signature IS NULL OR (
                    length(response_signature) = 88
                    AND substr(response_signature, -2) = '=='
                    AND substr(response_signature, 1, 86)
                        NOT GLOB '*[^A-Za-z0-9+/]*')),
                UNIQUE(event_id, attempt_number),
                CHECK(
                    (outcome_code IN ('retry_transport', 'retry_unsigned',
                        'retry_invalid_receipt')
                     AND next_attempt_at IS NOT NULL
                     AND receipt_digest IS NULL AND rejection_code IS NULL
                     AND response_json IS NULL AND response_sha256 IS NULL
                     AND response_key_id IS NULL AND response_signature IS NULL)
                    OR
                    (outcome_code = 'retry_server_unavailable'
                     AND next_attempt_at IS NOT NULL
                     AND receipt_digest IS NULL AND rejection_code IS NOT NULL
                     AND response_json IS NOT NULL AND response_sha256 IS NOT NULL
                     AND response_key_id IS NOT NULL
                     AND response_signature IS NOT NULL)
                    OR
                    (outcome_code = 'accepted'
                     AND next_attempt_at IS NULL
                     AND receipt_digest IS NOT NULL AND rejection_code IS NULL
                     AND response_json IS NOT NULL AND response_sha256 IS NOT NULL
                     AND response_key_id IS NOT NULL
                     AND response_signature IS NOT NULL)
                    OR
                    (outcome_code = 'terminal_rejection'
                     AND next_attempt_at IS NULL
                     AND receipt_digest IS NULL AND rejection_code IS NOT NULL
                     AND response_json IS NOT NULL AND response_sha256 IS NOT NULL
                     AND response_key_id IS NOT NULL
                     AND response_signature IS NOT NULL)
                )
            );
            CREATE UNIQUE INDEX idx_workflow_audit_event_accepted
                ON workflow_audit_event_attempts(event_id)
                WHERE outcome_code = 'accepted';
            CREATE UNIQUE INDEX idx_workflow_audit_event_terminal
                ON workflow_audit_event_attempts(event_id)
                WHERE outcome_code = 'terminal_rejection';
            CREATE TRIGGER workflow_audit_event_attempt_immutable
            BEFORE UPDATE ON workflow_audit_event_attempts
            BEGIN
                SELECT RAISE(ABORT, 'workflow_audit_event_attempt_immutable');
            END;
            CREATE TRIGGER workflow_audit_event_attempt_no_delete
            BEFORE DELETE ON workflow_audit_event_attempts
            BEGIN
                SELECT RAISE(ABORT, 'workflow_audit_event_attempt_immutable');
            END;

            CREATE TABLE workflow_completion_intents (
                completion_id TEXT PRIMARY KEY CHECK(
                    length(completion_id) = 36
                    AND completion_id = lower(completion_id)
                    AND substr(completion_id, 9, 1) = '-'
                    AND substr(completion_id, 14, 1) = '-'
                    AND substr(completion_id, 15, 1) = '4'
                    AND substr(completion_id, 19, 1) = '-'
                    AND substr(completion_id, 20, 1) IN ('8', '9', 'a', 'b')
                    AND substr(completion_id, 24, 1) = '-'
                    AND completion_id NOT GLOB '*[^0-9a-f-]*'),
                workflow_run_id TEXT NOT NULL UNIQUE CHECK(
                    length(workflow_run_id) = 36
                    AND workflow_run_id = lower(workflow_run_id)
                    AND substr(workflow_run_id, 9, 1) = '-'
                    AND substr(workflow_run_id, 14, 1) = '-'
                    AND substr(workflow_run_id, 19, 1) = '-'
                    AND substr(workflow_run_id, 24, 1) = '-'
                    AND workflow_run_id NOT GLOB '*[^0-9a-f-]*'),
                outcome TEXT NOT NULL CHECK(outcome IN ('completed', 'failed', 'aborted')),
                reason_code TEXT CHECK(reason_code IS NULL OR reason_code IN (
                    'authz_denied', 'condition_not_met', 'cooperative_cancel',
                    'cycle_limit_exceeded', 'execution_exception',
                    'execution_failed', 'execution_timeout', 'gate_disabled',
                    'gate_paused', 'goto_target_unresolved',
                    'kill_switch_tripped', 'manifest_resolution_failed',
                    'no_steps_in_definition', 'parameter_validation_failed',
                    'postcondition_exception', 'postcondition_failed',
                    'precondition_exception', 'precondition_failed',
                    'retry_exhausted', 'rollback_capture_exception',
                    'unknown_control_flow')),
                audit_event_count INTEGER NOT NULL
                    CHECK(audit_event_count BETWEEN 0 AND 100000),
                final_event_id TEXT REFERENCES workflow_audit_event_outbox(event_id),
                created_at TEXT NOT NULL,
                CHECK((outcome = 'completed' AND reason_code IS NULL
                        AND audit_event_count > 0)
                    OR (outcome != 'completed' AND reason_code IS NOT NULL)),
                CHECK((audit_event_count = 0 AND final_event_id IS NULL)
                    OR (audit_event_count > 0 AND final_event_id IS NOT NULL))
            );
            CREATE TRIGGER workflow_completion_intent_immutable
            BEFORE UPDATE ON workflow_completion_intents
            BEGIN
                SELECT RAISE(ABORT, 'workflow_completion_intent_immutable');
            END;
            CREATE TRIGGER workflow_completion_intent_no_delete
            BEFORE DELETE ON workflow_completion_intents
            BEGIN
                SELECT RAISE(ABORT, 'workflow_completion_intent_immutable');
            END;
            CREATE TRIGGER workflow_completion_intent_exact_chain
            BEFORE INSERT ON workflow_completion_intents
            WHEN NEW.audit_event_count != (
                    SELECT COUNT(*) FROM workflow_audit_event_outbox
                     WHERE workflow_run_id = NEW.workflow_run_id)
              OR (NEW.audit_event_count > 0 AND NOT EXISTS (
                    SELECT 1 FROM workflow_audit_event_outbox
                     WHERE workflow_run_id = NEW.workflow_run_id
                       AND execution_ordinal = NEW.audit_event_count - 1
                       AND event_id = NEW.final_event_id))
            BEGIN
                SELECT RAISE(ABORT, 'workflow_completion_intent_chain_mismatch');
            END;
            CREATE TRIGGER workflow_audit_event_before_completion
            BEFORE INSERT ON workflow_audit_event_outbox
            WHEN EXISTS (
                SELECT 1 FROM workflow_completion_intents
                 WHERE workflow_run_id = NEW.workflow_run_id)
            BEGIN
                SELECT RAISE(ABORT, 'workflow_audit_event_after_completion');
            END;

            CREATE TABLE workflow_completion_outbox (
                completion_id TEXT PRIMARY KEY
                    REFERENCES workflow_completion_intents(completion_id),
                workflow_run_id TEXT NOT NULL UNIQUE,
                audit_chain_digest TEXT NOT NULL CHECK(
                    length(audit_chain_digest) = 64
                    AND audit_chain_digest NOT GLOB '*[^0-9a-f]*'),
                payload_json TEXT NOT NULL
                    CHECK(length(payload_json) BETWEEN 2 AND 8192),
                payload_sha256 TEXT NOT NULL CHECK(
                    length(payload_sha256) = 64
                    AND payload_sha256 NOT GLOB '*[^0-9a-f]*'),
                created_at TEXT NOT NULL,
                FOREIGN KEY(workflow_run_id)
                    REFERENCES workflow_completion_intents(workflow_run_id)
            );
            CREATE TRIGGER workflow_completion_outbox_identity_match
            BEFORE INSERT ON workflow_completion_outbox
            WHEN NOT EXISTS (
                SELECT 1 FROM workflow_completion_intents i
                 WHERE i.completion_id = NEW.completion_id
                   AND i.workflow_run_id = NEW.workflow_run_id)
            BEGIN
                SELECT RAISE(ABORT, 'workflow_completion_outbox_identity_mismatch');
            END;
            CREATE TRIGGER workflow_completion_outbox_immutable
            BEFORE UPDATE ON workflow_completion_outbox
            BEGIN
                SELECT RAISE(ABORT, 'workflow_completion_outbox_immutable');
            END;
            CREATE TRIGGER workflow_completion_outbox_no_delete
            BEFORE DELETE ON workflow_completion_outbox
            BEGIN
                SELECT RAISE(ABORT, 'workflow_completion_outbox_immutable');
            END;

            CREATE TABLE workflow_completion_attempts (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                completion_id TEXT NOT NULL
                    REFERENCES workflow_completion_outbox(completion_id),
                attempt_number INTEGER NOT NULL CHECK(attempt_number > 0),
                outcome_code TEXT NOT NULL CHECK(outcome_code IN (
                    'accepted', 'terminal_rejection', 'retry_transport',
                    'retry_unsigned', 'retry_invalid_receipt',
                    'retry_server_unavailable')),
                attempted_at TEXT NOT NULL,
                next_attempt_at TEXT,
                http_status INTEGER CHECK(
                    http_status IS NULL OR http_status BETWEEN 100 AND 599),
                completion_receipt_digest TEXT CHECK(
                    completion_receipt_digest IS NULL OR (
                        length(completion_receipt_digest) = 64
                        AND completion_receipt_digest NOT GLOB '*[^0-9a-f]*')),
                rejection_code TEXT CHECK(rejection_code IS NULL OR rejection_code IN (
                    'workflow_completion_invalid', 'workflow_run_not_found',
                    'workflow_tenant_mismatch', 'workflow_run_not_started',
                    'workflow_completion_without_audit',
                    'workflow_audit_incomplete',
                    'workflow_completion_event_count_mismatch',
                    'workflow_completion_digest_mismatch',
                    'workflow_completion_idempotency_conflict',
                    'workflow_run_wrong_state',
                    'workflow_legacy_terminal_unverified',
                    'workflow_completion_unavailable')),
                response_json TEXT CHECK(response_json IS NULL OR
                    length(response_json) BETWEEN 2 AND 16384),
                response_sha256 TEXT CHECK(response_sha256 IS NULL OR (
                    length(response_sha256) = 64
                    AND response_sha256 NOT GLOB '*[^0-9a-f]*')),
                response_key_id TEXT CHECK(
                    response_key_id IS NULL OR response_key_id = 'suavo-cmd-v1'),
                response_signature TEXT CHECK(response_signature IS NULL OR (
                    length(response_signature) = 88
                    AND substr(response_signature, -2) = '=='
                    AND substr(response_signature, 1, 86)
                        NOT GLOB '*[^A-Za-z0-9+/]*')),
                UNIQUE(completion_id, attempt_number),
                CHECK(
                    (outcome_code IN ('retry_transport', 'retry_unsigned',
                        'retry_invalid_receipt')
                     AND next_attempt_at IS NOT NULL
                     AND completion_receipt_digest IS NULL
                     AND rejection_code IS NULL
                     AND response_json IS NULL AND response_sha256 IS NULL
                     AND response_key_id IS NULL AND response_signature IS NULL)
                    OR
                    (outcome_code = 'retry_server_unavailable'
                     AND next_attempt_at IS NOT NULL
                     AND completion_receipt_digest IS NULL
                     AND rejection_code IS NOT NULL
                     AND response_json IS NOT NULL AND response_sha256 IS NOT NULL
                     AND response_key_id IS NOT NULL
                     AND response_signature IS NOT NULL)
                    OR
                    (outcome_code = 'accepted'
                     AND next_attempt_at IS NULL
                     AND completion_receipt_digest IS NOT NULL
                     AND rejection_code IS NULL
                     AND response_json IS NOT NULL AND response_sha256 IS NOT NULL
                     AND response_key_id IS NOT NULL
                     AND response_signature IS NOT NULL)
                    OR
                    (outcome_code = 'terminal_rejection'
                     AND next_attempt_at IS NULL
                     AND completion_receipt_digest IS NULL
                     AND rejection_code IS NOT NULL
                     AND response_json IS NOT NULL AND response_sha256 IS NOT NULL
                     AND response_key_id IS NOT NULL
                     AND response_signature IS NOT NULL)
                )
            );
            CREATE UNIQUE INDEX idx_workflow_completion_accepted
                ON workflow_completion_attempts(completion_id)
                WHERE outcome_code = 'accepted';
            CREATE UNIQUE INDEX idx_workflow_completion_terminal
                ON workflow_completion_attempts(completion_id)
                WHERE outcome_code = 'terminal_rejection';
            CREATE TRIGGER workflow_completion_attempt_immutable
            BEFORE UPDATE ON workflow_completion_attempts
            BEGIN
                SELECT RAISE(ABORT, 'workflow_completion_attempt_immutable');
            END;
            CREATE TRIGGER workflow_completion_attempt_no_delete
            BEFORE DELETE ON workflow_completion_attempts
            BEGIN
                SELECT RAISE(ABORT, 'workflow_completion_attempt_immutable');
            END;
            """);
    }
}
