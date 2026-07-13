namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private void ApplyPricingApprovalHandshakeMigration()
    {
        ApplyMigrationIfNeeded(33,
            "Add append-only PHI-free pricing PIC proposal, grant, and revocation ledger",
            """
            CREATE TABLE pricing_approval_proposals (
                proposal_id TEXT PRIMARY KEY,
                proposal_digest TEXT NOT NULL UNIQUE,
                pharmacy_id TEXT NOT NULL,
                agent_id TEXT NOT NULL,
                machine_fingerprint TEXT NOT NULL,
                modality TEXT NOT NULL CHECK(modality IN ('sql','uia','vision')),
                schema_digest TEXT NOT NULL,
                status_policy_digest TEXT NOT NULL,
                cost_basis TEXT NOT NULL CHECK(cost_basis = 'cost_per_unit'),
                policy_digest TEXT NOT NULL,
                snapshot_contract TEXT NOT NULL,
                freshness_seconds INTEGER NOT NULL,
                observed_at_utc TEXT NOT NULL,
                expires_at_utc TEXT NOT NULL,
                recorded_at_utc TEXT NOT NULL
            );

            CREATE TABLE pricing_approval_grants (
                approval_id TEXT PRIMARY KEY,
                proposal_id TEXT NOT NULL,
                proposal_digest TEXT NOT NULL,
                pharmacy_id TEXT NOT NULL,
                agent_id TEXT NOT NULL,
                machine_fingerprint TEXT NOT NULL,
                approver_id TEXT NOT NULL,
                approved_by_role TEXT NOT NULL CHECK(approved_by_role = 'pharmacist_in_charge'),
                modality TEXT NOT NULL CHECK(modality IN ('sql','uia','vision')),
                schema_digest TEXT NOT NULL,
                status_policy_digest TEXT NOT NULL,
                cost_basis TEXT NOT NULL CHECK(cost_basis = 'cost_per_unit'),
                policy_digest TEXT NOT NULL,
                snapshot_contract TEXT NOT NULL,
                freshness_seconds INTEGER NOT NULL,
                issued_at_utc TEXT NOT NULL,
                expires_at_utc TEXT NOT NULL,
                key_id TEXT NOT NULL,
                signature TEXT NOT NULL,
                grant_digest TEXT NOT NULL UNIQUE,
                installed_command_id TEXT NOT NULL UNIQUE,
                installed_envelope_nonce TEXT NOT NULL,
                installed_envelope_data_hash TEXT NOT NULL,
                installed_at_utc TEXT NOT NULL,
                FOREIGN KEY(proposal_id) REFERENCES pricing_approval_proposals(proposal_id)
            );

            CREATE TABLE pricing_approval_proposal_acknowledgements (
                proposal_id TEXT PRIMARY KEY,
                proposal_digest TEXT NOT NULL,
                pharmacy_id TEXT NOT NULL,
                agent_id TEXT NOT NULL,
                machine_fingerprint TEXT NOT NULL,
                received_at_utc TEXT NOT NULL,
                key_id TEXT NOT NULL,
                signature TEXT NOT NULL,
                receipt_digest TEXT NOT NULL UNIQUE,
                recorded_at_utc TEXT NOT NULL,
                FOREIGN KEY(proposal_id) REFERENCES pricing_approval_proposals(proposal_id)
            );

            CREATE TABLE pricing_approval_revocations (
                revocation_id TEXT PRIMARY KEY,
                approval_id TEXT NOT NULL,
                proposal_id TEXT NOT NULL,
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
                FOREIGN KEY(approval_id) REFERENCES pricing_approval_grants(approval_id)
            );

            CREATE INDEX idx_pricing_approval_proposal_scope
                ON pricing_approval_proposals(
                    pharmacy_id, agent_id, machine_fingerprint,
                    modality, policy_digest, expires_at_utc);
            CREATE INDEX idx_pricing_approval_grant_scope
                ON pricing_approval_grants(
                    pharmacy_id, agent_id, machine_fingerprint,
                    modality, policy_digest, expires_at_utc, issued_at_utc);
            CREATE INDEX idx_pricing_approval_revocation_approval
                ON pricing_approval_revocations(approval_id, revoked_at_utc);

            CREATE TRIGGER pricing_approval_proposals_no_update
            BEFORE UPDATE ON pricing_approval_proposals
            BEGIN
                SELECT RAISE(ABORT, 'pricing_approval_proposals_append_only');
            END;
            CREATE TRIGGER pricing_approval_proposals_no_delete
            BEFORE DELETE ON pricing_approval_proposals
            BEGIN
                SELECT RAISE(ABORT, 'pricing_approval_proposals_append_only');
            END;
            CREATE TRIGGER pricing_approval_grants_no_update
            BEFORE UPDATE ON pricing_approval_grants
            BEGIN
                SELECT RAISE(ABORT, 'pricing_approval_grants_append_only');
            END;
            CREATE TRIGGER pricing_approval_grants_no_delete
            BEFORE DELETE ON pricing_approval_grants
            BEGIN
                SELECT RAISE(ABORT, 'pricing_approval_grants_append_only');
            END;
            CREATE TRIGGER pricing_approval_proposal_acks_no_update
            BEFORE UPDATE ON pricing_approval_proposal_acknowledgements
            BEGIN
                SELECT RAISE(ABORT, 'pricing_approval_proposal_acks_append_only');
            END;
            CREATE TRIGGER pricing_approval_proposal_acks_no_delete
            BEFORE DELETE ON pricing_approval_proposal_acknowledgements
            BEGIN
                SELECT RAISE(ABORT, 'pricing_approval_proposal_acks_append_only');
            END;
            CREATE TRIGGER pricing_approval_revocations_no_update
            BEFORE UPDATE ON pricing_approval_revocations
            BEGIN
                SELECT RAISE(ABORT, 'pricing_approval_revocations_append_only');
            END;
            CREATE TRIGGER pricing_approval_revocations_no_delete
            BEFORE DELETE ON pricing_approval_revocations
            BEGIN
                SELECT RAISE(ABORT, 'pricing_approval_revocations_append_only');
            END;
            """);
    }
}
