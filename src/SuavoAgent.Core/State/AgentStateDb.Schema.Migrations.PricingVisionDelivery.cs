namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private void ApplyPricingVisionDeliveryMigration()
    {
        ApplyMigrationIfNeeded(30,
            "Admit vision as an immutable pricing result source",
            """
            DROP TRIGGER IF EXISTS pricing_result_delivery_intent_immutable;
            DROP TRIGGER IF EXISTS pricing_result_delivery_intent_no_delete;
            DROP INDEX IF EXISTS idx_pricing_delivery_intent_terminal;
            ALTER TABLE pricing_result_delivery_intents
                RENAME TO pricing_result_delivery_intents_v18;
            CREATE TABLE pricing_result_delivery_intents (
                job_id TEXT PRIMARY KEY,
                command_id TEXT,
                source_upload_id TEXT UNIQUE,
                source_mode TEXT NOT NULL CHECK(
                    source_mode IN ('sql', 'uia', 'vision', 'manual')),
                prepared_at TEXT NOT NULL,
                terminal_at TEXT
            );
            INSERT INTO pricing_result_delivery_intents (
                job_id, command_id, source_upload_id, source_mode,
                prepared_at, terminal_at)
            SELECT job_id, command_id, source_upload_id, source_mode,
                   prepared_at, terminal_at
              FROM pricing_result_delivery_intents_v18;
            DROP TABLE pricing_result_delivery_intents_v18;
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
    }
}
