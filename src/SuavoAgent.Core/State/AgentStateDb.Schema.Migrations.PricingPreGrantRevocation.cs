namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private void ApplyPricingPreGrantRevocationMigration()
    {
        ApplyMigrationIfNeeded(35,
            "Add append-only pricing approval pre-grant revocation tombstones",
            """
            CREATE TABLE pricing_approval_pregrant_revocations (
                revocation_id TEXT PRIMARY KEY,
                approval_id TEXT NOT NULL UNIQUE,
                proposal_id TEXT NOT NULL UNIQUE,
                proposal_digest TEXT NOT NULL,
                pharmacy_id TEXT NOT NULL,
                agent_id TEXT NOT NULL,
                machine_fingerprint TEXT NOT NULL,
                policy_digest TEXT NOT NULL,
                reason_code TEXT NOT NULL,
                revoked_at_utc TEXT NOT NULL,
                key_id TEXT NOT NULL,
                signature TEXT NOT NULL,
                revocation_digest TEXT NOT NULL UNIQUE,
                installed_command_id TEXT NOT NULL UNIQUE,
                installed_envelope_nonce TEXT NOT NULL,
                installed_envelope_data_hash TEXT NOT NULL,
                installed_at_utc TEXT NOT NULL,
                FOREIGN KEY(proposal_id)
                    REFERENCES pricing_approval_proposals(proposal_id)
            );

            CREATE INDEX idx_pricing_approval_pregrant_scope
                ON pricing_approval_pregrant_revocations(
                    pharmacy_id, agent_id, machine_fingerprint,
                    policy_digest, revoked_at_utc);

            CREATE TRIGGER pricing_approval_pregrant_revocations_no_update
            BEFORE UPDATE ON pricing_approval_pregrant_revocations
            BEGIN
                SELECT RAISE(ABORT,
                    'pricing_approval_pregrant_revocations_append_only');
            END;
            CREATE TRIGGER pricing_approval_pregrant_revocations_no_delete
            BEFORE DELETE ON pricing_approval_pregrant_revocations
            BEGIN
                SELECT RAISE(ABORT,
                    'pricing_approval_pregrant_revocations_append_only');
            END;
            """);
    }
}
