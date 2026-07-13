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
    private void ApplyLateVersionedMigrations()
    {
        ApplyMigrationIfNeeded(7,
            "Digest-bound learned PMS adapter activation receipts",
            """
            CREATE TABLE IF NOT EXISTS learned_adapter_activations (
                pharmacy_id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                template_digest TEXT NOT NULL,
                model_digest TEXT NOT NULL,
                approved_by TEXT NOT NULL,
                approved_at TEXT NOT NULL,
                activated_at TEXT NOT NULL,
                status TEXT NOT NULL CHECK(status IN ('active', 'inactive')),
                deactivated_at TEXT,
                deactivation_reason TEXT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_laa_active_session_template
                ON learned_adapter_activations(session_id, template_digest)
                WHERE status = 'active';
            """);
        ApplyMigrationIfNeeded(8,
            "Quarantine discovery-phase workflow templates as capture-only",
            """
            -- Defensive repair for early v3.12 boxes that recorded migration 1
            -- before the workflow-template DDL was durably present.
            CREATE TABLE IF NOT EXISTS workflow_templates (
                template_id TEXT PRIMARY KEY,
                template_version TEXT NOT NULL,
                skill_id TEXT NOT NULL,
                process_name_glob TEXT NOT NULL,
                pms_version_range_json TEXT NOT NULL,
                screen_signature TEXT NOT NULL,
                steps_hash TEXT NOT NULL,
                routine_hash_origin TEXT,
                steps_json TEXT NOT NULL,
                aggregate_confidence REAL NOT NULL,
                observation_count INTEGER NOT NULL,
                has_writeback INTEGER NOT NULL,
                extracted_at TEXT NOT NULL,
                extracted_by TEXT NOT NULL,
                retired_at TEXT,
                retirement_reason TEXT,
                consecutive_low_conf_runs INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_wt_skill
                ON workflow_templates(skill_id) WHERE retired_at IS NULL;
            CREATE INDEX IF NOT EXISTS idx_wt_writeback
                ON workflow_templates(has_writeback)
                WHERE retired_at IS NULL AND has_writeback = 1;
            CREATE UNIQUE INDEX IF NOT EXISTS uniq_wt_active_skill_screen
                ON workflow_templates(skill_id, screen_signature) WHERE retired_at IS NULL;
            ALTER TABLE workflow_templates ADD COLUMN capture_only INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE workflow_templates ADD COLUMN source_session_id TEXT;
            CREATE INDEX IF NOT EXISTS idx_wt_capture_only
                ON workflow_templates(capture_only) WHERE retired_at IS NULL;
            """);
        ApplyMigrationIfNeeded(9,
            "Bind applied collective-intelligence seeds to learning sessions",
            """
            ALTER TABLE applied_seeds ADD COLUMN session_id TEXT;
            CREATE INDEX IF NOT EXISTS idx_applied_seeds_session_phase
                ON applied_seeds(session_id, phase, applied_at DESC);
            """);
        ApplyMigrationIfNeeded(10,
            "Bind auto-rule approval safety metadata to workflow writeback risk",
            """
            -- Defensive CREATE covers early boxes that recorded migration 1
            -- before its multi-statement DDL was durably present.
            CREATE TABLE IF NOT EXISTS auto_rule_approvals (
                rule_id TEXT PRIMARY KEY,
                template_id TEXT NOT NULL,
                yaml_sha256 TEXT NOT NULL,
                status TEXT NOT NULL,
                shadow_runs INTEGER NOT NULL DEFAULT 0,
                shadow_matches INTEGER NOT NULL DEFAULT 0,
                shadow_mismatches INTEGER NOT NULL DEFAULT 0,
                approved_by TEXT,
                approved_at TEXT,
                rejected_reason TEXT
            );
            ALTER TABLE auto_rule_approvals
                ADD COLUMN has_writeback INTEGER NOT NULL DEFAULT 0;
            UPDATE auto_rule_approvals
               SET has_writeback = COALESCE(
                   (SELECT wt.has_writeback
                      FROM workflow_templates wt
                     WHERE wt.template_id = auto_rule_approvals.template_id),
                   0);
            CREATE INDEX IF NOT EXISTS idx_auto_rule_approvals_writeback
                ON auto_rule_approvals(has_writeback)
                WHERE has_writeback = 1;
            """);
        ApplyMigrationIfNeeded(11,
            "Durable learned-rule transition, active-registry, and no-replay run ledgers",
            """
            ALTER TABLE auto_rule_approvals ADD COLUMN approval_id TEXT;

            CREATE TABLE IF NOT EXISTS auto_rule_transition_commands (
                command_id TEXT PRIMARY KEY,
                payload_digest TEXT NOT NULL,
                approval_id TEXT NOT NULL,
                rule_id TEXT NOT NULL,
                template_id TEXT NOT NULL,
                yaml_sha256 TEXT NOT NULL,
                from_status TEXT NOT NULL,
                to_status TEXT NOT NULL,
                approved_by TEXT,
                approved_at TEXT,
                reason_code TEXT NOT NULL,
                result_code TEXT NOT NULL,
                applied_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_auto_rule_transition_rule
                ON auto_rule_transition_commands(rule_id, applied_at DESC);

            CREATE TABLE IF NOT EXISTS active_auto_rule_registry (
                approval_id TEXT PRIMARY KEY,
                rule_id TEXT NOT NULL UNIQUE,
                template_id TEXT NOT NULL,
                yaml_sha256 TEXT NOT NULL,
                activated_by_command_id TEXT NOT NULL,
                activated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS auto_rule_run_commands (
                command_id TEXT PRIMARY KEY,
                payload_digest TEXT NOT NULL,
                approval_id TEXT NOT NULL,
                rule_id TEXT NOT NULL,
                template_id TEXT NOT NULL,
                yaml_sha256 TEXT NOT NULL,
                run_id TEXT NOT NULL UNIQUE,
                deadline_seconds INTEGER NOT NULL,
                state TEXT NOT NULL,
                owner_instance_id TEXT NOT NULL,
                outcome_code TEXT,
                steps_completed INTEGER,
                failed_ordinal INTEGER,
                started_at TEXT NOT NULL,
                completed_at TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_auto_rule_run_rule
                ON auto_rule_run_commands(rule_id, started_at DESC);
            """);
        ApplyMigrationIfNeeded(12,
            "Durable exact-payload learned POM approval ledger",
            """
            CREATE TABLE IF NOT EXISTS pom_approval_commands (
                command_id TEXT PRIMARY KEY,
                payload_digest TEXT NOT NULL,
                pom_id TEXT,
                session_id TEXT,
                model_digest TEXT,
                template_digest TEXT,
                approved_by TEXT,
                result_code TEXT NOT NULL,
                applied_at TEXT NOT NULL,
                completed_at TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_pom_approval_session
                ON pom_approval_commands(session_id, applied_at DESC);
            """);
        ApplyMigrationIfNeeded(13,
            "Device-bound authority counters and exact retry receipts",
            """
            CREATE TABLE IF NOT EXISTS device_authority_counters (
                kind TEXT PRIMARY KEY CHECK (kind IN ('pom_activation', 'rx_source')),
                counter INTEGER NOT NULL CHECK (counter >= 0)
            );
            INSERT OR IGNORE INTO device_authority_counters(kind, counter)
            VALUES ('pom_activation', 0), ('rx_source', 0);
            CREATE TABLE IF NOT EXISTS device_pom_activation_receipts (
                command_id TEXT PRIMARY KEY,
                payload_digest TEXT NOT NULL,
                key_id TEXT NOT NULL,
                local_counter INTEGER NOT NULL UNIQUE,
                receipt_json TEXT NOT NULL,
                signature TEXT NOT NULL,
                canonical_digest TEXT NOT NULL,
                source_binding_id TEXT,
                committed_at TEXT NOT NULL,
                accepted_at TEXT
            );
            CREATE TABLE IF NOT EXISTS device_rx_source_receipts (
                batch_digest TEXT PRIMARY KEY,
                key_id TEXT NOT NULL,
                local_counter INTEGER NOT NULL UNIQUE,
                receipt_json TEXT NOT NULL,
                signature TEXT NOT NULL,
                canonical_digest TEXT NOT NULL,
                committed_at TEXT NOT NULL,
                accepted_at TEXT
            );
            """);
        ApplyMigrationIfNeeded(14,
            "Device-bound fleet seed application receipts",
            """
            CREATE TABLE IF NOT EXISTS device_seed_application_receipts (
                command_id TEXT PRIMARY KEY,
                seed_digest TEXT NOT NULL,
                seed_version INTEGER NOT NULL,
                phase TEXT NOT NULL,
                source_manifest_digest TEXT NOT NULL,
                session_id TEXT NOT NULL,
                key_id TEXT NOT NULL,
                local_counter INTEGER NOT NULL UNIQUE,
                receipt_json TEXT NOT NULL,
                signature TEXT NOT NULL,
                canonical_digest TEXT NOT NULL,
                committed_at TEXT NOT NULL,
                accepted_at TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_device_seed_receipt_digest
                ON device_seed_application_receipts(seed_digest, committed_at DESC);
            """);
        ApplyMigrationIfNeeded(15,
            "Device-signed exact-scope autonomy evidence outbox",
            """
            ALTER TABLE task_autonomy ADD COLUMN scope_json TEXT;
            ALTER TABLE task_autonomy ADD COLUMN device_key_id TEXT;
            CREATE TABLE autonomy_evidence_counter (
                singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
                counter INTEGER NOT NULL CHECK (counter >= 0)
            );
            INSERT OR IGNORE INTO autonomy_evidence_counter(singleton, counter)
            VALUES (1, 0);
            CREATE TABLE device_autonomy_evidence_outbox (
                receipt_id TEXT PRIMARY KEY,
                scope_digest TEXT NOT NULL,
                key_id TEXT NOT NULL,
                local_counter INTEGER NOT NULL UNIQUE,
                receipt_json TEXT NOT NULL,
                signature TEXT NOT NULL,
                canonical_digest TEXT NOT NULL,
                committed_at TEXT NOT NULL,
                accepted_at TEXT,
                attempt_count INTEGER NOT NULL DEFAULT 0,
                next_attempt_at TEXT NOT NULL
            );
            CREATE INDEX idx_autonomy_evidence_outbox_due
                ON device_autonomy_evidence_outbox(accepted_at, next_attempt_at, local_counter);
            """);
        ApplyMigrationIfNeeded(16,
            "Fail-closed autonomy evidence persistence latch",
            """
            CREATE TABLE autonomy_safety_latches (
                task_type TEXT PRIMARY KEY,
                disabled INTEGER NOT NULL CHECK(disabled IN (0, 1)),
                reason_code TEXT NOT NULL,
                latched_at TEXT NOT NULL,
                cleared_at TEXT,
                CHECK(
                    (disabled = 1 AND cleared_at IS NULL) OR
                    (disabled = 0 AND cleared_at IS NOT NULL)
                )
            );
            """);
        ApplyMigrationIfNeeded(17,
            "Immutable crash-safe pricing result outbox",
            """
            CREATE TABLE pricing_result_outbox (
                job_id TEXT PRIMARY KEY,
                command_id TEXT,
                source_upload_id TEXT UNIQUE,
                payload_json TEXT NOT NULL,
                payload_sha256 TEXT NOT NULL CHECK(length(payload_sha256) = 64),
                item_count INTEGER NOT NULL CHECK(item_count >= 0),
                execution_ok INTEGER NOT NULL CHECK(execution_ok IN (0, 1)),
                state TEXT NOT NULL CHECK(state IN ('pending', 'accepted')),
                attempt_count INTEGER NOT NULL DEFAULT 0 CHECK(attempt_count >= 0),
                next_attempt_at TEXT NOT NULL,
                created_at TEXT NOT NULL,
                accepted_at TEXT,
                accepted_code TEXT,
                accepted_recorded INTEGER,
                accepted_receipt_json TEXT,
                accepted_receipt_sha256 TEXT,
                source_finalized_at TEXT,
                CHECK(
                    (state = 'pending' AND accepted_at IS NULL AND accepted_code IS NULL
                        AND accepted_recorded IS NULL AND accepted_receipt_json IS NULL
                        AND accepted_receipt_sha256 IS NULL AND source_finalized_at IS NULL) OR
                    (state = 'accepted' AND accepted_at IS NOT NULL
                        AND accepted_code IS NOT NULL AND accepted_recorded = item_count
                        AND accepted_receipt_json IS NOT NULL
                        AND length(accepted_receipt_sha256) = 64
                        AND (source_finalized_at IS NULL OR source_upload_id IS NOT NULL))
                )
            );
            CREATE INDEX idx_pricing_result_outbox_due
                ON pricing_result_outbox(state, next_attempt_at, created_at);
            CREATE INDEX idx_pricing_result_outbox_source_finalize
                ON pricing_result_outbox(state, source_finalized_at, source_upload_id);
            CREATE TRIGGER pricing_result_outbox_immutable
            BEFORE UPDATE ON pricing_result_outbox
            WHEN OLD.job_id IS NOT NEW.job_id
              OR OLD.command_id IS NOT NEW.command_id
              OR OLD.source_upload_id IS NOT NEW.source_upload_id
              OR OLD.payload_json IS NOT NEW.payload_json
              OR OLD.payload_sha256 IS NOT NEW.payload_sha256
              OR OLD.item_count IS NOT NEW.item_count
              OR OLD.execution_ok IS NOT NEW.execution_ok
              OR OLD.created_at IS NOT NEW.created_at
              OR (OLD.state = 'accepted' AND (
                    OLD.state IS NOT NEW.state
                 OR OLD.accepted_at IS NOT NEW.accepted_at
                 OR OLD.accepted_code IS NOT NEW.accepted_code
                 OR OLD.accepted_recorded IS NOT NEW.accepted_recorded
                 OR OLD.accepted_receipt_json IS NOT NEW.accepted_receipt_json
                 OR OLD.accepted_receipt_sha256 IS NOT NEW.accepted_receipt_sha256
              ))
            BEGIN
                SELECT RAISE(ABORT, 'pricing_result_outbox_immutable');
            END;
            CREATE TRIGGER pricing_result_outbox_no_delete
            BEFORE DELETE ON pricing_result_outbox
            BEGIN
                SELECT RAISE(ABORT, 'pricing_result_outbox_immutable');
            END;
            """);
        ApplyMigrationIfNeeded(18,
            "Pre-execution pricing delivery intent",
            """
            CREATE TABLE pricing_result_delivery_intents (
                job_id TEXT PRIMARY KEY,
                command_id TEXT,
                source_upload_id TEXT UNIQUE,
                source_mode TEXT NOT NULL CHECK(source_mode IN ('sql', 'uia', 'manual')),
                prepared_at TEXT NOT NULL,
                terminal_at TEXT
            );
            CREATE INDEX idx_pricing_delivery_intent_terminal
                ON pricing_result_delivery_intents(terminal_at, prepared_at);
            CREATE TRIGGER pricing_result_delivery_intent_immutable
            BEFORE UPDATE ON pricing_result_delivery_intents
            WHEN OLD.job_id IS NOT NEW.job_id
              OR OLD.command_id IS NOT NEW.command_id
              OR OLD.source_upload_id IS NOT NEW.source_upload_id
              OR OLD.source_mode IS NOT NEW.source_mode
              OR OLD.prepared_at IS NOT NEW.prepared_at
              OR (OLD.terminal_at IS NOT NULL AND OLD.terminal_at IS NOT NEW.terminal_at)
            BEGIN
                SELECT RAISE(ABORT, 'pricing_result_delivery_intent_immutable');
            END;
            CREATE TRIGGER pricing_result_delivery_intent_no_delete
            BEFORE DELETE ON pricing_result_delivery_intents
            BEGIN
                SELECT RAISE(ABORT, 'pricing_result_delivery_intent_immutable');
            END;
            """);
        ApplyMigrationIfNeeded(19,
            "Append-only terminal quarantine for unsafe legacy pricing outbox evidence",
            """
            CREATE UNIQUE INDEX idx_pricing_result_outbox_evidence_identity
                ON pricing_result_outbox(job_id, payload_sha256);
            CREATE TABLE pricing_result_outbox_quarantine (
                job_id TEXT NOT NULL,
                payload_sha256 TEXT NOT NULL CHECK(length(payload_sha256) = 64),
                reason_code TEXT NOT NULL CHECK(
                    reason_code = 'pricing_result_outbox_content_blocked'),
                quarantined_at TEXT NOT NULL,
                PRIMARY KEY(job_id, payload_sha256),
                FOREIGN KEY(job_id, payload_sha256)
                    REFERENCES pricing_result_outbox(job_id, payload_sha256)
            );
            CREATE INDEX idx_pricing_result_outbox_quarantine_time
                ON pricing_result_outbox_quarantine(quarantined_at, job_id);
            CREATE TRIGGER pricing_result_outbox_quarantine_immutable
            BEFORE UPDATE ON pricing_result_outbox_quarantine
            BEGIN
                SELECT RAISE(ABORT, 'pricing_result_outbox_quarantine_immutable');
            END;
            CREATE TRIGGER pricing_result_outbox_quarantine_no_delete
            BEFORE DELETE ON pricing_result_outbox_quarantine
            BEGIN
                SELECT RAISE(ABORT, 'pricing_result_outbox_quarantine_immutable');
            END;
            """);
        ApplyMigrationIfNeeded(20,
            "Completed-only generational pricing outbox and terminal receipts",
            """
            ALTER TABLE pricing_result_outbox
                ADD COLUMN accepted_response_key_id TEXT;
            ALTER TABLE pricing_result_outbox
                ADD COLUMN accepted_response_signature TEXT;
            DROP TRIGGER IF EXISTS pricing_result_outbox_immutable;
            CREATE TRIGGER pricing_result_outbox_immutable
            BEFORE UPDATE ON pricing_result_outbox
            WHEN OLD.job_id IS NOT NEW.job_id
              OR OLD.command_id IS NOT NEW.command_id
              OR OLD.source_upload_id IS NOT NEW.source_upload_id
              OR OLD.payload_json IS NOT NEW.payload_json
              OR OLD.payload_sha256 IS NOT NEW.payload_sha256
              OR OLD.item_count IS NOT NEW.item_count
              OR OLD.execution_ok IS NOT NEW.execution_ok
              OR OLD.created_at IS NOT NEW.created_at
              OR (OLD.state = 'accepted' AND (
                    OLD.state IS NOT NEW.state
                 OR OLD.accepted_at IS NOT NEW.accepted_at
                 OR OLD.accepted_code IS NOT NEW.accepted_code
                 OR OLD.accepted_recorded IS NOT NEW.accepted_recorded
                 OR OLD.accepted_receipt_json IS NOT NEW.accepted_receipt_json
                 OR OLD.accepted_receipt_sha256 IS NOT NEW.accepted_receipt_sha256
                 OR OLD.accepted_response_key_id IS NOT NEW.accepted_response_key_id
                 OR OLD.accepted_response_signature IS NOT NEW.accepted_response_signature
                 OR OLD.attempt_count IS NOT NEW.attempt_count
                 OR OLD.next_attempt_at IS NOT NEW.next_attempt_at
                 OR (OLD.source_finalized_at IS NOT NULL
                     AND OLD.source_finalized_at IS NOT NEW.source_finalized_at)
              ))
            BEGIN
                SELECT RAISE(ABORT, 'pricing_result_outbox_immutable');
            END;
            CREATE TRIGGER pricing_result_outbox_contract_insert
            BEFORE INSERT ON pricing_result_outbox
            WHEN length(NEW.job_id) NOT BETWEEN 1 AND 200
              OR NEW.job_id GLOB '*[^A-Za-z0-9:_-]*'
              OR (NEW.command_id IS NOT NULL AND (
                    length(NEW.command_id) NOT BETWEEN 1 AND 200
                    OR NEW.command_id GLOB '*[^A-Za-z0-9:_-]*'))
              OR length(NEW.payload_sha256) != 64
              OR NEW.payload_sha256 GLOB '*[^0-9a-f]*'
              OR (NEW.accepted_receipt_sha256 IS NOT NULL AND (
                    length(NEW.accepted_receipt_sha256) != 64
                    OR NEW.accepted_receipt_sha256 GLOB '*[^0-9a-f]*'))
              OR (NEW.accepted_response_key_id IS NOT NULL
                    AND NEW.accepted_response_key_id != 'suavo-cmd-v1')
              OR (NEW.accepted_response_signature IS NOT NULL AND (
                    length(NEW.accepted_response_signature) != 88
                    OR substr(NEW.accepted_response_signature, -2) != '=='
                    OR substr(NEW.accepted_response_signature, 1, 86)
                        GLOB '*[^A-Za-z0-9+/]*'))
            BEGIN
                SELECT RAISE(ABORT, 'pricing_result_outbox_contract_invalid');
            END;
            CREATE TRIGGER pricing_result_outbox_acceptance_contract
            BEFORE UPDATE ON pricing_result_outbox
            WHEN (NEW.accepted_receipt_sha256 IS NOT NULL AND (
                    length(NEW.accepted_receipt_sha256) != 64
                    OR NEW.accepted_receipt_sha256 GLOB '*[^0-9a-f]*'))
              OR (NEW.accepted_response_key_id IS NOT NULL
                    AND NEW.accepted_response_key_id != 'suavo-cmd-v1')
              OR (NEW.accepted_response_signature IS NOT NULL AND (
                    length(NEW.accepted_response_signature) != 88
                    OR substr(NEW.accepted_response_signature, -2) != '=='
                    OR substr(NEW.accepted_response_signature, 1, 86)
                        GLOB '*[^A-Za-z0-9+/]*'))
              OR (OLD.state = 'pending' AND NEW.state = 'accepted' AND (
                    NEW.accepted_response_key_id IS NULL
                    OR NEW.accepted_response_signature IS NULL))
            BEGIN
                SELECT RAISE(ABORT, 'pricing_result_outbox_acceptance_invalid');
            END;
            CREATE TABLE pricing_result_outbox_v2 (
                job_id TEXT NOT NULL CHECK(
                    length(job_id) BETWEEN 1 AND 200
                    AND job_id NOT GLOB '*[^A-Za-z0-9:_-]*'),
                generation INTEGER NOT NULL CHECK(generation > 0),
                command_id TEXT CHECK(
                    command_id IS NULL OR (
                        length(command_id) BETWEEN 1 AND 200
                        AND command_id NOT GLOB '*[^A-Za-z0-9:_-]*')),
                source_upload_id TEXT,
                payload_json TEXT NOT NULL,
                payload_sha256 TEXT NOT NULL CHECK(
                    length(payload_sha256) = 64
                    AND payload_sha256 NOT GLOB '*[^0-9a-f]*'),
                item_count INTEGER NOT NULL CHECK(item_count >= 0),
                execution_ok INTEGER NOT NULL CHECK(execution_ok = 1),
                state TEXT NOT NULL CHECK(state IN ('pending', 'accepted')),
                attempt_count INTEGER NOT NULL DEFAULT 0 CHECK(attempt_count >= 0),
                next_attempt_at TEXT NOT NULL,
                created_at TEXT NOT NULL,
                accepted_at TEXT,
                accepted_code TEXT,
                accepted_recorded INTEGER,
                accepted_receipt_json TEXT,
                accepted_receipt_sha256 TEXT CHECK(
                    accepted_receipt_sha256 IS NULL OR (
                        length(accepted_receipt_sha256) = 64
                        AND accepted_receipt_sha256 NOT GLOB '*[^0-9a-f]*')),
                accepted_response_key_id TEXT CHECK(
                    accepted_response_key_id IS NULL OR
                    accepted_response_key_id = 'suavo-cmd-v1'),
                accepted_response_signature TEXT CHECK(
                    accepted_response_signature IS NULL OR (
                        length(accepted_response_signature) = 88
                        AND substr(accepted_response_signature, -2) = '=='
                        AND substr(accepted_response_signature, 1, 86)
                            NOT GLOB '*[^A-Za-z0-9+/]*')),
                source_finalized_at TEXT,
                PRIMARY KEY(job_id, generation),
                UNIQUE(job_id, payload_sha256),
                CHECK(
                    (state = 'pending' AND accepted_at IS NULL AND accepted_code IS NULL
                        AND accepted_recorded IS NULL AND accepted_receipt_json IS NULL
                        AND accepted_receipt_sha256 IS NULL
                        AND accepted_response_key_id IS NULL
                        AND accepted_response_signature IS NULL
                        AND source_finalized_at IS NULL) OR
                    (state = 'accepted' AND accepted_at IS NOT NULL
                        AND accepted_code IS NOT NULL AND accepted_recorded = item_count
                        AND accepted_receipt_json IS NOT NULL
                        AND length(accepted_receipt_sha256) = 64
                        AND accepted_response_key_id IS NOT NULL
                        AND accepted_response_signature IS NOT NULL
                        AND (source_finalized_at IS NULL OR source_upload_id IS NOT NULL))
                )
            );
            CREATE INDEX idx_pricing_result_outbox_v2_due
                ON pricing_result_outbox_v2(state, next_attempt_at, created_at);
            CREATE INDEX idx_pricing_result_outbox_v2_source_finalize
                ON pricing_result_outbox_v2(
                    state, source_finalized_at, source_upload_id);
            CREATE INDEX idx_pricing_result_outbox_v2_source
                ON pricing_result_outbox_v2(source_upload_id, generation);
            CREATE TRIGGER pricing_result_outbox_v2_immutable
            BEFORE UPDATE ON pricing_result_outbox_v2
            WHEN OLD.job_id IS NOT NEW.job_id
              OR OLD.generation IS NOT NEW.generation
              OR OLD.command_id IS NOT NEW.command_id
              OR OLD.source_upload_id IS NOT NEW.source_upload_id
              OR OLD.payload_json IS NOT NEW.payload_json
              OR OLD.payload_sha256 IS NOT NEW.payload_sha256
              OR OLD.item_count IS NOT NEW.item_count
              OR OLD.execution_ok IS NOT NEW.execution_ok
              OR OLD.created_at IS NOT NEW.created_at
              OR (OLD.state = 'accepted' AND (
                    OLD.state IS NOT NEW.state
                 OR OLD.accepted_at IS NOT NEW.accepted_at
                 OR OLD.accepted_code IS NOT NEW.accepted_code
                 OR OLD.accepted_recorded IS NOT NEW.accepted_recorded
                 OR OLD.accepted_receipt_json IS NOT NEW.accepted_receipt_json
                 OR OLD.accepted_receipt_sha256 IS NOT NEW.accepted_receipt_sha256
                 OR OLD.accepted_response_key_id IS NOT NEW.accepted_response_key_id
                 OR OLD.accepted_response_signature IS NOT NEW.accepted_response_signature
                 OR OLD.attempt_count IS NOT NEW.attempt_count
                 OR OLD.next_attempt_at IS NOT NEW.next_attempt_at
                 OR (OLD.source_finalized_at IS NOT NULL
                     AND OLD.source_finalized_at IS NOT NEW.source_finalized_at)
              ))
            BEGIN
                SELECT RAISE(ABORT, 'pricing_result_outbox_v2_immutable');
            END;
            CREATE TRIGGER pricing_result_outbox_v2_no_delete
            BEFORE DELETE ON pricing_result_outbox_v2
            BEGIN
                SELECT RAISE(ABORT, 'pricing_result_outbox_v2_immutable');
            END;

            CREATE TABLE pricing_result_outbox_supersessions (
                job_id TEXT NOT NULL CHECK(
                    length(job_id) BETWEEN 1 AND 200
                    AND job_id NOT GLOB '*[^A-Za-z0-9:_-]*'),
                superseded_payload_sha256 TEXT NOT NULL
                    CHECK(length(superseded_payload_sha256) = 64
                        AND superseded_payload_sha256 NOT GLOB '*[^0-9a-f]*'),
                successor_payload_sha256 TEXT NOT NULL
                    CHECK(length(successor_payload_sha256) = 64
                        AND successor_payload_sha256 NOT GLOB '*[^0-9a-f]*'),
                reason_code TEXT NOT NULL CHECK(
                    reason_code = 'legacy_partial_replaced_by_completed'),
                superseded_at TEXT NOT NULL,
                PRIMARY KEY(job_id, superseded_payload_sha256)
            );
            CREATE TRIGGER pricing_result_outbox_supersessions_immutable
            BEFORE UPDATE ON pricing_result_outbox_supersessions
            BEGIN
                SELECT RAISE(ABORT, 'pricing_result_outbox_supersessions_immutable');
            END;
            CREATE TRIGGER pricing_result_outbox_supersessions_no_delete
            BEFORE DELETE ON pricing_result_outbox_supersessions
            BEGIN
                SELECT RAISE(ABORT, 'pricing_result_outbox_supersessions_immutable');
            END;

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
        ApplyMigrationIfNeeded(21,
            "Durable selector-observation omission accounting",
            """
            ALTER TABLE pricing_results
                ADD COLUMN omitted_selector_observations INTEGER NOT NULL DEFAULT 0
                CHECK(omitted_selector_observations BETWEEN 0 AND 30000000);
            """);
        ApplyWorkflowAuditOutboxMigration();
        ApplyReceiptContractExpansionMigration();
        ApplyPricingCommandAuthorityMigration();
        ApplyPricingCandidatePrivacyMigration();
        ApplyPricingTerminalAckOutboxMigration();
        ApplyPricingCommandRecoveryMigration();
        ApplySelectorPatchApprovalMigration();
        ApplyPricingObservationIdentityMigration();
        ApplyPricingVisionDeliveryMigration();
        ApplyPricingSignedAdmissionRecoveryMigration();
        ApplyPricingCommandExpiryMigration();
        ApplyPricingApprovalHandshakeMigration();
        ApplyPricingTerminalAckExpiryMigration();
        ApplyPricingPreGrantRevocationMigration();
        ApplyPricingCloudAuthorityLeaseMigration();
        ApplyPricingResultAuthorityTerminalMigration();
        ApplyPricingAuthorityTerminalAckMigration();
        ApplyPricingGrantIdentityMigration();
        ApplyPricingAuthorityReplayMigration();
        ApplyPricingLegacyDeliveryQuarantineMigration();
    }
}
