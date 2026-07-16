namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private void ApplyRelease1ConvergenceMigration()
    {
        ApplyMigrationIfNeeded(47,
            "Append-only Release 1 device convergence outbox",
            """
            CREATE TABLE release1_install_receipt_uploads (
                install_receipt_sha256 TEXT PRIMARY KEY CHECK(
                    length(install_receipt_sha256) = 64
                    AND install_receipt_sha256 NOT GLOB '*[^0-9a-f]*'),
                request_json TEXT NOT NULL,
                request_sha256 TEXT NOT NULL UNIQUE CHECK(
                    length(request_sha256) = 64
                    AND request_sha256 NOT GLOB '*[^0-9a-f]*'),
                installed_release_tag TEXT NOT NULL,
                installed_source_sha TEXT NOT NULL CHECK(
                    length(installed_source_sha) = 40
                    AND installed_source_sha NOT GLOB '*[^0-9a-f]*'),
                created_at_utc TEXT NOT NULL
            );
            CREATE TRIGGER release1_install_receipt_uploads_no_update
            BEFORE UPDATE ON release1_install_receipt_uploads
            BEGIN
                SELECT RAISE(ABORT, 'release1_install_receipt_uploads_append_only');
            END;
            CREATE TRIGGER release1_install_receipt_uploads_no_delete
            BEFORE DELETE ON release1_install_receipt_uploads
            BEGIN
                SELECT RAISE(ABORT, 'release1_install_receipt_uploads_append_only');
            END;

            CREATE TABLE release1_install_receipt_deliveries (
                install_receipt_sha256 TEXT PRIMARY KEY,
                request_sha256 TEXT NOT NULL,
                accepted_at_utc TEXT NOT NULL,
                FOREIGN KEY(install_receipt_sha256)
                    REFERENCES release1_install_receipt_uploads(
                        install_receipt_sha256)
            );
            CREATE TRIGGER release1_install_receipt_deliveries_no_update
            BEFORE UPDATE ON release1_install_receipt_deliveries
            BEGIN
                SELECT RAISE(ABORT, 'release1_install_receipt_deliveries_append_only');
            END;
            CREATE TRIGGER release1_install_receipt_deliveries_no_delete
            BEFORE DELETE ON release1_install_receipt_deliveries
            BEGIN
                SELECT RAISE(ABORT, 'release1_install_receipt_deliveries_append_only');
            END;

            CREATE TABLE release1_convergence_challenges (
                command_id TEXT PRIMARY KEY,
                envelope_nonce TEXT NOT NULL UNIQUE,
                command_name TEXT NOT NULL CHECK(
                    command_name = 'release1_convergence_challenge'),
                agent_id TEXT NOT NULL,
                machine_fingerprint TEXT NOT NULL,
                command_timestamp TEXT NOT NULL,
                command_data_hash TEXT NOT NULL CHECK(
                    length(command_data_hash) = 64
                    AND command_data_hash NOT GLOB '*[^0-9a-f]*'),
                command_key_id TEXT NOT NULL,
                command_signature TEXT NOT NULL CHECK(
                    length(command_signature) = 88
                    AND substr(command_signature, -2) = '=='
                    AND substr(command_signature, 1, 86)
                        NOT GLOB '*[^A-Za-z0-9+/]*'),
                inventory_sha256 TEXT NOT NULL CHECK(
                    length(inventory_sha256) = 64
                    AND inventory_sha256 NOT GLOB '*[^0-9a-f]*'),
                bridge_release_tag TEXT NOT NULL,
                bridge_source_sha TEXT NOT NULL CHECK(
                    length(bridge_source_sha) = 40
                    AND bridge_source_sha NOT GLOB '*[^0-9a-f]*'),
                expires_at_utc TEXT NOT NULL,
                registered_at_utc TEXT NOT NULL
            );
            CREATE TRIGGER release1_convergence_challenges_no_update
            BEFORE UPDATE ON release1_convergence_challenges
            BEGIN
                SELECT RAISE(ABORT, 'release1_convergence_challenges_append_only');
            END;
            CREATE TRIGGER release1_convergence_challenges_no_delete
            BEFORE DELETE ON release1_convergence_challenges
            BEGIN
                SELECT RAISE(ABORT, 'release1_convergence_challenges_append_only');
            END;

            CREATE TABLE release1_convergence_preliminary_proofs (
                command_id TEXT PRIMARY KEY,
                request_json TEXT NOT NULL,
                request_sha256 TEXT NOT NULL CHECK(
                    length(request_sha256) = 64
                    AND request_sha256 NOT GLOB '*[^0-9a-f]*'),
                install_receipt_sha256 TEXT NOT NULL CHECK(
                    length(install_receipt_sha256) = 64
                    AND install_receipt_sha256 NOT GLOB '*[^0-9a-f]*'),
                restart_receipt_sha256 TEXT NOT NULL CHECK(
                    length(restart_receipt_sha256) = 64
                    AND restart_receipt_sha256 NOT GLOB '*[^0-9a-f]*'),
                verified_at_utc TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                FOREIGN KEY(command_id)
                    REFERENCES release1_convergence_challenges(command_id)
            );
            CREATE TRIGGER release1_convergence_preliminary_no_update
            BEFORE UPDATE ON release1_convergence_preliminary_proofs
            BEGIN
                SELECT RAISE(ABORT, 'release1_convergence_preliminary_append_only');
            END;
            CREATE TRIGGER release1_convergence_preliminary_no_delete
            BEFORE DELETE ON release1_convergence_preliminary_proofs
            BEGIN
                SELECT RAISE(ABORT, 'release1_convergence_preliminary_append_only');
            END;

            CREATE TABLE release1_convergence_final_evidence (
                command_id TEXT PRIMARY KEY,
                noop_command_id TEXT NOT NULL UNIQUE,
                request_json TEXT NOT NULL,
                request_sha256 TEXT NOT NULL CHECK(
                    length(request_sha256) = 64
                    AND request_sha256 NOT GLOB '*[^0-9a-f]*'),
                verified_at_utc TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                FOREIGN KEY(command_id)
                    REFERENCES release1_convergence_challenges(command_id),
                FOREIGN KEY(noop_command_id)
                    REFERENCES update_noop_device_receipts(command_id)
            );
            CREATE TRIGGER release1_convergence_final_no_update
            BEFORE UPDATE ON release1_convergence_final_evidence
            BEGIN
                SELECT RAISE(ABORT, 'release1_convergence_final_append_only');
            END;
            CREATE TRIGGER release1_convergence_final_no_delete
            BEFORE DELETE ON release1_convergence_final_evidence
            BEGIN
                SELECT RAISE(ABORT, 'release1_convergence_final_append_only');
            END;

            CREATE TABLE release1_convergence_deliveries (
                command_id TEXT NOT NULL,
                phase TEXT NOT NULL CHECK(
                    phase IN ('challenge_ack','preliminary','final')),
                request_sha256 TEXT NOT NULL CHECK(
                    length(request_sha256) = 64
                    AND request_sha256 NOT GLOB '*[^0-9a-f]*'),
                response_command_id TEXT,
                accepted_at_utc TEXT NOT NULL,
                PRIMARY KEY(command_id, phase),
                FOREIGN KEY(command_id)
                    REFERENCES release1_convergence_challenges(command_id),
                CHECK(
                    (phase = 'preliminary' AND response_command_id IS NOT NULL)
                    OR (phase != 'preliminary' AND response_command_id IS NULL))
            );
            CREATE TRIGGER release1_convergence_deliveries_no_update
            BEFORE UPDATE ON release1_convergence_deliveries
            BEGIN
                SELECT RAISE(ABORT, 'release1_convergence_deliveries_append_only');
            END;
            CREATE TRIGGER release1_convergence_deliveries_no_delete
            BEFORE DELETE ON release1_convergence_deliveries
            BEGIN
                SELECT RAISE(ABORT, 'release1_convergence_deliveries_append_only');
            END;
            """);
    }
}
