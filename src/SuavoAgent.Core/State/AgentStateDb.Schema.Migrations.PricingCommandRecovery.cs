namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private void ApplyPricingCommandRecoveryMigration()
    {
        ApplyMigrationIfNeeded(27,
            "Crash-safe pricing command execution intent recovery",
            """
            CREATE TABLE pricing_command_execution_intents (
                command_id TEXT PRIMARY KEY CHECK(
                    length(command_id) = 36
                    AND substr(command_id, 9, 1) = '-'
                    AND substr(command_id, 14, 1) = '-'
                    AND substr(command_id, 19, 1) = '-'
                    AND substr(command_id, 24, 1) = '-'
                    AND substr(command_id, 15, 1) = '4'
                    AND substr(command_id, 20, 1) IN ('8','9','a','b')
                    AND replace(command_id, '-', '') NOT GLOB '*[^0-9a-f]*'),
                command_kind TEXT NOT NULL CHECK(command_kind IN (
                    'run_pricing_job','find_and_run_pricing_job')),
                owner_id TEXT NOT NULL CHECK(
                    length(owner_id) = 32
                    AND owner_id NOT GLOB '*[^0-9a-f]*'),
                state TEXT NOT NULL CHECK(state IN (
                    'in_progress','result_pending','completed','terminal')),
                registered_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX idx_pricing_command_execution_recovery
                ON pricing_command_execution_intents(state, owner_id, registered_at);
            CREATE TRIGGER pricing_command_execution_identity_immutable
            BEFORE UPDATE ON pricing_command_execution_intents
            WHEN OLD.command_id != NEW.command_id
              OR OLD.command_kind != NEW.command_kind
              OR OLD.owner_id != NEW.owner_id
              OR OLD.registered_at != NEW.registered_at
            BEGIN
                SELECT RAISE(ABORT, 'pricing_command_execution_identity_immutable');
            END;
            CREATE TRIGGER pricing_command_execution_state_monotonic
            BEFORE UPDATE ON pricing_command_execution_intents
            WHEN (OLD.state IN ('completed','terminal') AND NEW.state != OLD.state)
              OR (OLD.state = 'result_pending' AND NEW.state = 'in_progress')
            BEGIN
                SELECT RAISE(ABORT, 'pricing_command_execution_state_immutable');
            END;
            CREATE TRIGGER pricing_command_execution_no_delete
            BEFORE DELETE ON pricing_command_execution_intents
            BEGIN
                SELECT RAISE(ABORT, 'pricing_command_execution_immutable');
            END;
            """);
    }
}
