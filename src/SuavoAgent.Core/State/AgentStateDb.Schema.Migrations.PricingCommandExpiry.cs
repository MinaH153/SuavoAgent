namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private void ApplyPricingCommandExpiryMigration()
    {
        ApplyMigrationIfNeeded(32,
            "Bind crash-resumable pricing admission to signed live-command expiry",
            """
            ALTER TABLE pricing_command_execution_intents
                ADD COLUMN signed_expires_at TEXT;

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
              OR OLD.signed_expires_at IS NOT NEW.signed_expires_at
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
            """);
    }
}
