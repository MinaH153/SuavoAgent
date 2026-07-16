namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private void ApplyPricingCloudAuthorityLeaseMigration()
    {
        ApplyMigrationIfNeeded(36,
            "Add fail-closed pricing cloud-authority lease",
            """
            CREATE TABLE pricing_cloud_authority_lease (
                singleton_id INTEGER PRIMARY KEY CHECK(singleton_id = 1),
                last_success_server_utc TEXT,
                local_high_water_utc TEXT NOT NULL,
                terminal_at_utc TEXT,
                terminal_reason TEXT,
                updated_at_utc TEXT NOT NULL,
                CHECK (
                    (terminal_at_utc IS NULL AND terminal_reason IS NULL) OR
                    (terminal_at_utc IS NOT NULL AND
                     terminal_reason = 'agent_binding_inactive'))
            );

            CREATE TRIGGER pricing_cloud_authority_terminal_immutable
            BEFORE UPDATE ON pricing_cloud_authority_lease
            WHEN OLD.terminal_reason IS NOT NULL AND (
                NEW.terminal_reason IS NOT OLD.terminal_reason OR
                NEW.terminal_at_utc IS NOT OLD.terminal_at_utc OR
                NEW.last_success_server_utc IS NOT OLD.last_success_server_utc)
            BEGIN
                SELECT RAISE(ABORT,
                    'pricing_cloud_authority_terminal_immutable');
            END;

            CREATE TRIGGER pricing_cloud_authority_lease_no_delete
            BEFORE DELETE ON pricing_cloud_authority_lease
            BEGIN
                SELECT RAISE(ABORT,
                    'pricing_cloud_authority_lease_no_delete');
            END;
            """);
    }
}
