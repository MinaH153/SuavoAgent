namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private void ApplyPricingGrantIdentityMigration()
    {
        ApplyMigrationIfNeeded(39,
            "Bind pricing command, job, delivery, and observation state to one exact approval grant",
            """
            ALTER TABLE pricing_jobs
                ADD COLUMN approval_id TEXT CHECK(
                    approval_id IS NULL OR (
                        length(approval_id) = 36
                        AND substr(approval_id, 9, 1) = '-'
                        AND substr(approval_id, 14, 1) = '-'
                        AND substr(approval_id, 19, 1) = '-'
                        AND substr(approval_id, 24, 1) = '-'
                        AND substr(approval_id, 15, 1) = '4'
                        AND substr(approval_id, 20, 1) IN ('8','9','a','b')
                        AND replace(approval_id, '-', '')
                            NOT GLOB '*[^0-9a-f]*'));
            ALTER TABLE pricing_jobs
                ADD COLUMN grant_digest TEXT CHECK(
                    (approval_id IS NULL AND grant_digest IS NULL) OR
                    (approval_id IS NOT NULL AND grant_digest IS NOT NULL
                        AND length(grant_digest) = 64
                        AND grant_digest NOT GLOB '*[^0-9a-f]*'));

            CREATE TRIGGER pricing_job_authority_identity_immutable
            BEFORE UPDATE ON pricing_jobs
            WHEN OLD.approval_id IS NOT NEW.approval_id
              OR OLD.grant_digest IS NOT NEW.grant_digest
            BEGIN
                SELECT RAISE(ABORT, 'pricing_job_authority_identity_immutable');
            END;

            ALTER TABLE pricing_job_input_identity
                ADD COLUMN authority_approval_id TEXT CHECK(
                    authority_approval_id IS NULL OR (
                        length(authority_approval_id) = 36
                        AND substr(authority_approval_id, 9, 1) = '-'
                        AND substr(authority_approval_id, 14, 1) = '-'
                        AND substr(authority_approval_id, 19, 1) = '-'
                        AND substr(authority_approval_id, 24, 1) = '-'
                        AND substr(authority_approval_id, 15, 1) = '4'
                        AND substr(authority_approval_id, 20, 1) IN ('8','9','a','b')
                        AND replace(authority_approval_id, '-', '')
                            NOT GLOB '*[^0-9a-f]*'
                        AND authority_approval_digest IS NOT NULL
                        AND length(authority_approval_digest) = 64
                        AND authority_approval_digest
                            NOT GLOB '*[^0-9a-f]*'));

            CREATE TRIGGER pricing_input_identity_authority_binding_coherent
            BEFORE INSERT ON pricing_job_input_identity
            WHEN (NEW.authority_approval_id IS NULL
                    AND NEW.authority_approval_digest IS NOT NULL)
              OR (NEW.authority_approval_id IS NOT NULL
                    AND NEW.authority_approval_digest IS NULL)
            BEGIN
                SELECT RAISE(ABORT,
                    'pricing_input_identity_authority_binding_invalid');
            END;

            ALTER TABLE pricing_result_delivery_intents
                ADD COLUMN approval_id TEXT CHECK(
                    approval_id IS NULL OR (
                        length(approval_id) = 36
                        AND substr(approval_id, 9, 1) = '-'
                        AND substr(approval_id, 14, 1) = '-'
                        AND substr(approval_id, 19, 1) = '-'
                        AND substr(approval_id, 24, 1) = '-'
                        AND substr(approval_id, 15, 1) = '4'
                        AND substr(approval_id, 20, 1) IN ('8','9','a','b')
                        AND replace(approval_id, '-', '')
                            NOT GLOB '*[^0-9a-f]*'));
            ALTER TABLE pricing_result_delivery_intents
                ADD COLUMN grant_digest TEXT CHECK(
                    (approval_id IS NULL AND grant_digest IS NULL) OR
                    (approval_id IS NOT NULL AND grant_digest IS NOT NULL
                        AND length(grant_digest) = 64
                        AND grant_digest NOT GLOB '*[^0-9a-f]*'));

            DROP TRIGGER IF EXISTS pricing_result_delivery_intent_immutable;
            CREATE TRIGGER pricing_result_delivery_intent_immutable
            BEFORE UPDATE ON pricing_result_delivery_intents
            WHEN OLD.job_id IS NOT NEW.job_id
              OR OLD.command_id IS NOT NEW.command_id
              OR OLD.source_upload_id IS NOT NEW.source_upload_id
              OR OLD.source_mode IS NOT NEW.source_mode
              OR OLD.approval_id IS NOT NEW.approval_id
              OR OLD.grant_digest IS NOT NEW.grant_digest
              OR OLD.prepared_at IS NOT NEW.prepared_at
              OR (OLD.terminal_at IS NOT NULL
                    AND OLD.terminal_at IS NOT NEW.terminal_at)
            BEGIN
                SELECT RAISE(ABORT, 'pricing_result_delivery_intent_immutable');
            END;

            ALTER TABLE pricing_command_execution_intents
                ADD COLUMN pricing_approval_id TEXT CHECK(
                    pricing_approval_id IS NULL OR (
                        length(pricing_approval_id) = 36
                        AND substr(pricing_approval_id, 9, 1) = '-'
                        AND substr(pricing_approval_id, 14, 1) = '-'
                        AND substr(pricing_approval_id, 19, 1) = '-'
                        AND substr(pricing_approval_id, 24, 1) = '-'
                        AND substr(pricing_approval_id, 15, 1) = '4'
                        AND substr(pricing_approval_id, 20, 1) IN ('8','9','a','b')
                        AND replace(pricing_approval_id, '-', '')
                            NOT GLOB '*[^0-9a-f]*'));
            ALTER TABLE pricing_command_execution_intents
                ADD COLUMN pricing_grant_digest TEXT CHECK(
                    (pricing_approval_id IS NULL
                        AND pricing_grant_digest IS NULL) OR
                    (pricing_approval_id IS NOT NULL
                        AND pricing_grant_digest IS NOT NULL
                        AND length(pricing_grant_digest) = 64
                        AND pricing_grant_digest NOT GLOB '*[^0-9a-f]*'));

            DROP TRIGGER IF EXISTS pricing_command_execution_identity_immutable;
            CREATE TRIGGER pricing_command_execution_identity_immutable
            BEFORE UPDATE ON pricing_command_execution_intents
            WHEN OLD.command_id IS NOT NEW.command_id
              OR OLD.command_kind IS NOT NEW.command_kind
              OR OLD.owner_id IS NOT NEW.owner_id
              OR OLD.registered_at IS NOT NEW.registered_at
              OR OLD.signed_agent_id IS NOT NEW.signed_agent_id
              OR OLD.signed_machine_fingerprint
                    IS NOT NEW.signed_machine_fingerprint
              OR OLD.signed_timestamp IS NOT NEW.signed_timestamp
              OR OLD.signed_nonce IS NOT NEW.signed_nonce
              OR OLD.signed_data_hash IS NOT NEW.signed_data_hash
              OR OLD.signed_key_id IS NOT NEW.signed_key_id
              OR OLD.signed_signature IS NOT NEW.signed_signature
              OR OLD.signed_expires_at IS NOT NEW.signed_expires_at
              OR OLD.signed_checkpoint_digest
                    IS NOT NEW.signed_checkpoint_digest
              OR OLD.pricing_approval_id IS NOT NEW.pricing_approval_id
              OR OLD.pricing_grant_digest IS NOT NEW.pricing_grant_digest
              OR (OLD.admitted_at IS NOT NULL AND (
                    OLD.execution_mode IS NOT NEW.execution_mode
                 OR OLD.autonomy_execution_mode
                        IS NOT NEW.autonomy_execution_mode
                 OR OLD.admission_scope_digest
                        IS NOT NEW.admission_scope_digest
                 OR OLD.admission_trusted_identity
                        IS NOT NEW.admission_trusted_identity
                 OR OLD.admitted_at IS NOT NEW.admitted_at))
            BEGIN
                SELECT RAISE(ABORT,
                    'pricing_command_execution_identity_immutable');
            END;
            """);
    }
}
