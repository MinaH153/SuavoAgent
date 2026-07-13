namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private void ApplyPricingObservationIdentityMigration()
    {
        ApplyMigrationIfNeeded(29,
            "Bind pricing resume to modality schema status cost basis pharmacist authority and freshness",
            """
            ALTER TABLE pricing_job_input_identity ADD COLUMN modality TEXT;
            ALTER TABLE pricing_job_input_identity ADD COLUMN schema_digest TEXT;
            ALTER TABLE pricing_job_input_identity ADD COLUMN status_policy_digest TEXT;
            ALTER TABLE pricing_job_input_identity ADD COLUMN cost_basis TEXT;
            ALTER TABLE pricing_job_input_identity ADD COLUMN policy_digest TEXT;
            ALTER TABLE pricing_job_input_identity ADD COLUMN snapshot_contract TEXT;
            ALTER TABLE pricing_job_input_identity ADD COLUMN snapshot_id TEXT;
            ALTER TABLE pricing_job_input_identity ADD COLUMN observed_at_utc TEXT;
            ALTER TABLE pricing_job_input_identity ADD COLUMN fresh_until_utc TEXT;
            ALTER TABLE pricing_job_input_identity ADD COLUMN authority_pharmacy_id TEXT;
            ALTER TABLE pricing_job_input_identity ADD COLUMN authority_role TEXT;
            ALTER TABLE pricing_job_input_identity ADD COLUMN authority_approval_digest TEXT;
            ALTER TABLE pricing_job_input_identity ADD COLUMN authority_expires_at_utc TEXT;
            """);
    }
}
