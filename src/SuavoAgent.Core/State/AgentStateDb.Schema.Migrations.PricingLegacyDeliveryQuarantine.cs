namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private void ApplyPricingLegacyDeliveryQuarantineMigration()
    {
        ApplyMigrationIfNeeded(41,
            "Append-only quarantine for retired manual pricing deliveries",
            """
            CREATE TABLE pricing_result_delivery_quarantine (
                job_id TEXT PRIMARY KEY,
                command_id TEXT,
                source_mode TEXT NOT NULL CHECK(source_mode = 'manual'),
                reason_code TEXT NOT NULL CHECK(
                    reason_code = 'pricing_result_source_invalid'),
                quarantined_at TEXT NOT NULL
            );
            CREATE TRIGGER pricing_result_delivery_quarantine_evidence_required
            BEFORE INSERT ON pricing_result_delivery_quarantine
            WHEN NOT EXISTS (
                SELECT 1
                  FROM pricing_result_delivery_intents delivery
                  JOIN pricing_jobs job ON job.job_id = delivery.job_id
                 WHERE delivery.job_id = NEW.job_id
                   AND delivery.command_id IS NEW.command_id
                   AND delivery.source_mode = NEW.source_mode
                   AND job.status = 'completed')
            BEGIN
                SELECT RAISE(ABORT,
                    'pricing_result_delivery_quarantine_evidence_not_found');
            END;
            CREATE TRIGGER pricing_result_delivery_quarantine_immutable
            BEFORE UPDATE ON pricing_result_delivery_quarantine
            BEGIN
                SELECT RAISE(ABORT,
                    'pricing_result_delivery_quarantine_immutable');
            END;
            CREATE TRIGGER pricing_result_delivery_quarantine_no_delete
            BEFORE DELETE ON pricing_result_delivery_quarantine
            BEGIN
                SELECT RAISE(ABORT,
                    'pricing_result_delivery_quarantine_immutable');
            END;
            """);
    }
}
