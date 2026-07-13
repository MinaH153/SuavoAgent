namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private void ApplyPricingSignedAdmissionRecoveryMigration()
    {
        ApplyMigrationIfNeeded(31,
            "Bind pricing crash resume to verified signed envelope and admitted autonomy identity",
            """
            ALTER TABLE pricing_command_execution_intents
                ADD COLUMN signed_agent_id TEXT;
            ALTER TABLE pricing_command_execution_intents
                ADD COLUMN signed_machine_fingerprint TEXT;
            ALTER TABLE pricing_command_execution_intents
                ADD COLUMN signed_timestamp TEXT;
            ALTER TABLE pricing_command_execution_intents
                ADD COLUMN signed_nonce TEXT;
            ALTER TABLE pricing_command_execution_intents
                ADD COLUMN signed_data_hash TEXT;
            ALTER TABLE pricing_command_execution_intents
                ADD COLUMN signed_key_id TEXT;
            ALTER TABLE pricing_command_execution_intents
                ADD COLUMN signed_signature TEXT;
            ALTER TABLE pricing_command_execution_intents
                ADD COLUMN signed_checkpoint_digest TEXT;
            ALTER TABLE pricing_command_execution_intents
                ADD COLUMN execution_mode TEXT CHECK(
                    execution_mode IS NULL OR execution_mode IN ('sql','uia','vision'));
            ALTER TABLE pricing_command_execution_intents
                ADD COLUMN autonomy_execution_mode TEXT CHECK(
                    autonomy_execution_mode IS NULL OR
                    autonomy_execution_mode IN ('supervised','auto'));
            ALTER TABLE pricing_command_execution_intents
                ADD COLUMN admission_scope_digest TEXT;
            ALTER TABLE pricing_command_execution_intents
                ADD COLUMN admission_trusted_identity INTEGER CHECK(
                    admission_trusted_identity IS NULL OR
                    admission_trusted_identity IN (0,1));
            ALTER TABLE pricing_command_execution_intents
                ADD COLUMN admitted_at TEXT;

            DROP TRIGGER IF EXISTS pricing_command_execution_identity_immutable;
            CREATE TRIGGER pricing_command_execution_identity_immutable
            BEFORE UPDATE ON pricing_command_execution_intents
            WHEN OLD.command_id IS NOT NEW.command_id
              OR OLD.command_kind IS NOT NEW.command_kind
              OR OLD.owner_id IS NOT NEW.owner_id
              OR OLD.registered_at IS NOT NEW.registered_at
              OR OLD.signed_agent_id IS NOT NEW.signed_agent_id
              OR OLD.signed_machine_fingerprint IS NOT NEW.signed_machine_fingerprint
              OR OLD.signed_timestamp IS NOT NEW.signed_timestamp
              OR OLD.signed_nonce IS NOT NEW.signed_nonce
              OR OLD.signed_data_hash IS NOT NEW.signed_data_hash
              OR OLD.signed_key_id IS NOT NEW.signed_key_id
              OR OLD.signed_signature IS NOT NEW.signed_signature
              OR OLD.signed_checkpoint_digest IS NOT NEW.signed_checkpoint_digest
              OR (OLD.admitted_at IS NOT NULL AND (
                    OLD.execution_mode IS NOT NEW.execution_mode
                 OR OLD.autonomy_execution_mode IS NOT NEW.autonomy_execution_mode
                 OR OLD.admission_scope_digest IS NOT NEW.admission_scope_digest
                 OR OLD.admission_trusted_identity IS NOT NEW.admission_trusted_identity
                 OR OLD.admitted_at IS NOT NEW.admitted_at))
            BEGIN
                SELECT RAISE(ABORT, 'pricing_command_execution_identity_immutable');
            END;

            CREATE TRIGGER pricing_command_admission_coherent
            BEFORE UPDATE ON pricing_command_execution_intents
            WHEN (NEW.admitted_at IS NULL) != (NEW.execution_mode IS NULL)
              OR (NEW.admitted_at IS NULL) != (NEW.autonomy_execution_mode IS NULL)
              OR (NEW.admitted_at IS NULL) != (NEW.admission_scope_digest IS NULL)
              OR (NEW.admitted_at IS NULL) != (NEW.admission_trusted_identity IS NULL)
              OR (OLD.admitted_at IS NULL AND NEW.admitted_at IS NOT NULL
                  AND OLD.state != 'in_progress')
            BEGIN
                SELECT RAISE(ABORT, 'pricing_command_admission_invalid');
            END;

            CREATE INDEX idx_pricing_command_admitted_recovery
                ON pricing_command_execution_intents(
                    state, admitted_at, owner_id, registered_at);
            """);
    }
}
