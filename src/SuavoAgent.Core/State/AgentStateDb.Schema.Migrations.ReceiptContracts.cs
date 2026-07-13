namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private void ApplyReceiptContractExpansionMigration()
    {
        ApplyMigrationIfNeeded(23,
            "Exact workflow and pricing receipt rejection contracts",
            """
            DROP TRIGGER workflow_completion_attempt_immutable;
            DROP TRIGGER workflow_completion_attempt_no_delete;
            DROP INDEX idx_workflow_completion_accepted;
            DROP INDEX idx_workflow_completion_terminal;
            ALTER TABLE workflow_completion_attempts
                RENAME TO workflow_completion_attempts_v22;

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
                    'workflow_completion_control_flow_mismatch',
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
            INSERT INTO workflow_completion_attempts (
                id, completion_id, attempt_number, outcome_code, attempted_at,
                next_attempt_at, http_status, completion_receipt_digest,
                rejection_code, response_json, response_sha256,
                response_key_id, response_signature)
            SELECT id, completion_id, attempt_number, outcome_code, attempted_at,
                next_attempt_at, http_status, completion_receipt_digest,
                rejection_code, response_json, response_sha256,
                response_key_id, response_signature
              FROM workflow_completion_attempts_v22;
            DROP TABLE workflow_completion_attempts_v22;
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

            DROP TRIGGER pricing_result_outbox_terminal_evidence_required;
            DROP TRIGGER pricing_result_outbox_terminal_immutable;
            DROP TRIGGER pricing_result_outbox_terminal_no_delete;
            DROP INDEX idx_pricing_result_outbox_terminal_time;
            ALTER TABLE pricing_result_outbox_terminal_receipts
                RENAME TO pricing_result_outbox_terminal_receipts_v20;

            CREATE TABLE pricing_result_outbox_terminal_receipts (
                job_id TEXT NOT NULL CHECK(
                    length(job_id) BETWEEN 1 AND 200
                    AND job_id NOT GLOB '*[^A-Za-z0-9:_-]*'),
                payload_sha256 TEXT NOT NULL CHECK(
                    length(payload_sha256) = 64
                    AND payload_sha256 NOT GLOB '*[^0-9a-f]*'),
                reason_code TEXT NOT NULL CHECK(reason_code IN (
                    'pricing_result_outbox_content_blocked',
                    'pricing_result_payload_invalid',
                    'pricing_result_payload_conflict',
                    'pricing_result_job_agent_conflict',
                    'pricing_result_job_not_eligible',
                    'pricing_result_command_binding_invalid',
                    'pricing_result_not_complete')),
                http_status INTEGER,
                response_json TEXT,
                response_sha256 TEXT CHECK(
                    response_sha256 IS NULL OR (
                        length(response_sha256) = 64
                        AND response_sha256 NOT GLOB '*[^0-9a-f]*')),
                response_key_id TEXT CHECK(
                    response_key_id IS NULL OR response_key_id = 'suavo-cmd-v1'),
                response_signature TEXT CHECK(
                    response_signature IS NULL OR (
                        length(response_signature) = 88
                        AND substr(response_signature, -2) = '=='
                        AND substr(response_signature, 1, 86)
                            NOT GLOB '*[^A-Za-z0-9+/]*')),
                quarantined_at TEXT NOT NULL,
                PRIMARY KEY(job_id, payload_sha256),
                CHECK(
                    (reason_code = 'pricing_result_outbox_content_blocked'
                        AND http_status IS NULL AND response_json IS NULL
                        AND response_sha256 IS NULL AND response_key_id IS NULL
                        AND response_signature IS NULL) OR
                    (reason_code != 'pricing_result_outbox_content_blocked'
                        AND http_status IN (400, 409, 413, 422)
                        AND response_json IS NOT NULL
                        AND length(response_sha256) = 64
                        AND response_key_id IS NOT NULL
                        AND response_signature IS NOT NULL)
                )
            );
            INSERT INTO pricing_result_outbox_terminal_receipts (
                job_id, payload_sha256, reason_code, http_status, response_json,
                response_sha256, response_key_id, response_signature,
                quarantined_at)
            SELECT job_id, payload_sha256, reason_code, http_status, response_json,
                response_sha256, response_key_id, response_signature,
                quarantined_at
              FROM pricing_result_outbox_terminal_receipts_v20;
            DROP TABLE pricing_result_outbox_terminal_receipts_v20;
            CREATE INDEX idx_pricing_result_outbox_terminal_time
                ON pricing_result_outbox_terminal_receipts(quarantined_at, job_id);
            CREATE TRIGGER pricing_result_outbox_terminal_evidence_required
            BEFORE INSERT ON pricing_result_outbox_terminal_receipts
            WHEN NOT EXISTS (
                    SELECT 1 FROM pricing_result_outbox_v2
                     WHERE job_id = NEW.job_id
                       AND payload_sha256 = NEW.payload_sha256)
             AND NOT EXISTS (
                    SELECT 1 FROM pricing_result_outbox
                     WHERE job_id = NEW.job_id
                       AND payload_sha256 = NEW.payload_sha256)
            BEGIN
                SELECT RAISE(ABORT, 'pricing_result_terminal_evidence_not_found');
            END;
            CREATE TRIGGER pricing_result_outbox_terminal_immutable
            BEFORE UPDATE ON pricing_result_outbox_terminal_receipts
            BEGIN
                SELECT RAISE(ABORT, 'pricing_result_outbox_terminal_immutable');
            END;
            CREATE TRIGGER pricing_result_outbox_terminal_no_delete
            BEFORE DELETE ON pricing_result_outbox_terminal_receipts
            BEGIN
                SELECT RAISE(ABORT, 'pricing_result_outbox_terminal_immutable');
            END;
            """);
    }
}
