namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private void ApplyPricingCommandAuthorityMigration()
    {
        ApplyMigrationIfNeeded(24,
            "Terminalize pricing results without cloud command authority",
            """
            DROP TRIGGER pricing_result_outbox_terminal_evidence_required;
            DROP TRIGGER pricing_result_outbox_terminal_immutable;
            DROP TRIGGER pricing_result_outbox_terminal_no_delete;
            DROP INDEX idx_pricing_result_outbox_terminal_time;
            ALTER TABLE pricing_result_outbox_terminal_receipts
                RENAME TO pricing_result_outbox_terminal_receipts_v23;

            CREATE TABLE pricing_result_outbox_terminal_receipts (
                job_id TEXT NOT NULL CHECK(
                    length(job_id) BETWEEN 1 AND 200
                    AND job_id NOT GLOB '*[^A-Za-z0-9:_-]*'),
                payload_sha256 TEXT NOT NULL CHECK(
                    length(payload_sha256) = 64
                    AND payload_sha256 NOT GLOB '*[^0-9a-f]*'),
                reason_code TEXT NOT NULL CHECK(reason_code IN (
                    'pricing_result_outbox_content_blocked',
                    'pricing_result_command_ineligible',
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
                    (reason_code IN (
                        'pricing_result_outbox_content_blocked',
                        'pricing_result_command_ineligible')
                        AND http_status IS NULL AND response_json IS NULL
                        AND response_sha256 IS NULL AND response_key_id IS NULL
                        AND response_signature IS NULL) OR
                    (reason_code NOT IN (
                        'pricing_result_outbox_content_blocked',
                        'pricing_result_command_ineligible')
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
              FROM pricing_result_outbox_terminal_receipts_v23;
            DROP TABLE pricing_result_outbox_terminal_receipts_v23;
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
