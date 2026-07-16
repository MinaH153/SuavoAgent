using Microsoft.Data.Sqlite;

namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private void ApplyPricingPackageCostMigration()
    {
        ApplyMigrationIfNeeded(43,
            "Admit explicit package-cost pricing approvals without relabeling per-unit cost",
            """
            PRAGMA defer_foreign_keys = ON;

            CREATE TABLE pricing_approval_proposals_v43 (
                proposal_id TEXT PRIMARY KEY,
                proposal_digest TEXT NOT NULL UNIQUE,
                pharmacy_id TEXT NOT NULL,
                agent_id TEXT NOT NULL,
                machine_fingerprint TEXT NOT NULL,
                modality TEXT NOT NULL CHECK(modality IN ('sql','uia','vision')),
                schema_digest TEXT NOT NULL,
                status_policy_digest TEXT NOT NULL,
                cost_basis TEXT NOT NULL
                    CHECK(cost_basis IN ('cost_per_unit','package_cost')),
                policy_digest TEXT NOT NULL,
                snapshot_contract TEXT NOT NULL,
                freshness_seconds INTEGER NOT NULL,
                observed_at_utc TEXT NOT NULL,
                expires_at_utc TEXT NOT NULL,
                recorded_at_utc TEXT NOT NULL,
                CHECK(
                    (cost_basis = 'cost_per_unit' AND
                     snapshot_contract = 'source_policy_snapshot_v1') OR
                    (cost_basis = 'package_cost' AND modality = 'uia' AND
                     snapshot_contract = 'source_policy_snapshot_v2'))
            );
            INSERT INTO pricing_approval_proposals_v43
            SELECT * FROM pricing_approval_proposals;

            CREATE TABLE pricing_approval_grants_v43 (
                approval_id TEXT PRIMARY KEY,
                proposal_id TEXT NOT NULL,
                proposal_digest TEXT NOT NULL,
                pharmacy_id TEXT NOT NULL,
                agent_id TEXT NOT NULL,
                machine_fingerprint TEXT NOT NULL,
                approver_id TEXT NOT NULL,
                approved_by_role TEXT NOT NULL
                    CHECK(approved_by_role = 'pharmacist_in_charge'),
                modality TEXT NOT NULL CHECK(modality IN ('sql','uia','vision')),
                schema_digest TEXT NOT NULL,
                status_policy_digest TEXT NOT NULL,
                cost_basis TEXT NOT NULL
                    CHECK(cost_basis IN ('cost_per_unit','package_cost')),
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
                FOREIGN KEY(proposal_id)
                    REFERENCES pricing_approval_proposals_v43(proposal_id),
                CHECK(
                    (cost_basis = 'cost_per_unit' AND
                     snapshot_contract = 'source_policy_snapshot_v1') OR
                    (cost_basis = 'package_cost' AND modality = 'uia' AND
                     snapshot_contract = 'source_policy_snapshot_v2'))
            );
            INSERT INTO pricing_approval_grants_v43
            SELECT * FROM pricing_approval_grants;

            DROP TABLE pricing_approval_grants;
            DROP TABLE pricing_approval_proposals;
            ALTER TABLE pricing_approval_proposals_v43
                RENAME TO pricing_approval_proposals;
            ALTER TABLE pricing_approval_grants_v43
                RENAME TO pricing_approval_grants;

            CREATE INDEX idx_pricing_approval_proposal_scope
                ON pricing_approval_proposals(
                    pharmacy_id, agent_id, machine_fingerprint,
                    modality, policy_digest, expires_at_utc);
            CREATE INDEX idx_pricing_approval_grant_scope
                ON pricing_approval_grants(
                    pharmacy_id, agent_id, machine_fingerprint,
                    modality, policy_digest, expires_at_utc, issued_at_utc);

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
            """);

        ApplyMigrationIfNeeded(44,
            "Persist explicit package-cost basis in terminal pricing acknowledgements",
            """
            ALTER TABLE pricing_terminal_ack_outbox
                ADD COLUMN cost_basis TEXT
                CHECK(cost_basis IS NULL OR
                      cost_basis IN ('cost_per_unit','package_cost'));
            DROP TRIGGER pricing_terminal_ack_identity_immutable;
            CREATE TRIGGER pricing_terminal_ack_identity_immutable
            BEFORE UPDATE ON pricing_terminal_ack_outbox
            WHEN OLD.command_id != NEW.command_id
              OR OLD.result_kind != NEW.result_kind
              OR OLD.error_code != NEW.error_code
              OR OLD.job_id IS NOT NEW.job_id
              OR OLD.mode IS NOT NEW.mode
              OR OLD.total_items IS NOT NEW.total_items
              OR OLD.completed_items IS NOT NEW.completed_items
              OR OLD.failed_items IS NOT NEW.failed_items
              OR OLD.reason_code IS NOT NEW.reason_code
              OR OLD.candidate_count IS NOT NEW.candidate_count
              OR OLD.helper_version_suspect IS NOT NEW.helper_version_suspect
              OR OLD.cost_basis IS NOT NEW.cost_basis
              OR OLD.payload_sha256 != NEW.payload_sha256
              OR OLD.created_at != NEW.created_at
            BEGIN
                SELECT RAISE(ABORT, 'pricing_terminal_ack_identity_immutable');
            END;
            """);

        ApplyGeneratedPricingTerminalAckMigration();
    }

    private void ApplyGeneratedPricingTerminalAckMigration()
    {
        using var check = _conn.CreateCommand();
        check.CommandText =
            "SELECT 1 FROM schema_migrations WHERE version = 45 LIMIT 1";
        if (check.ExecuteScalar() is not null) return;

        using var transaction = _conn.BeginTransaction();
        try
        {
            var createSql = ReadTableCreationSql(
                transaction,
                "pricing_terminal_ack_outbox");
            var expanded = AddGeneratedPricingFailureConstraints(createSql);

            ExecuteMigrationSql(transaction, """
                DROP TRIGGER pricing_terminal_ack_identity_immutable;
                DROP TRIGGER pricing_terminal_ack_state_monotonic;
                DROP TRIGGER pricing_terminal_ack_no_delete;
                DROP INDEX idx_pricing_terminal_ack_pending;
                ALTER TABLE pricing_terminal_ack_outbox
                    RENAME TO pricing_terminal_ack_outbox_v45;
                """);
            ExecuteMigrationSql(transaction, expanded);
            ExecuteMigrationSql(transaction, """
                INSERT INTO pricing_terminal_ack_outbox (
                    command_id, result_kind, error_code, job_id, mode,
                    total_items, completed_items, failed_items, reason_code,
                    candidate_count, helper_version_suspect, payload_sha256,
                    state, attempt_count, next_attempt_at, created_at, delivered_at,
                    cost_basis)
                SELECT command_id, result_kind, error_code, job_id, mode,
                       total_items, completed_items, failed_items, reason_code,
                       candidate_count, helper_version_suspect, payload_sha256,
                       state, attempt_count, next_attempt_at, created_at, delivered_at,
                       cost_basis
                  FROM pricing_terminal_ack_outbox_v45;
                DROP TABLE pricing_terminal_ack_outbox_v45;
                CREATE INDEX idx_pricing_terminal_ack_pending
                    ON pricing_terminal_ack_outbox(
                        state, next_attempt_at, created_at);
                CREATE TRIGGER pricing_terminal_ack_identity_immutable
                BEFORE UPDATE ON pricing_terminal_ack_outbox
                WHEN OLD.command_id != NEW.command_id
                  OR OLD.result_kind != NEW.result_kind
                  OR OLD.error_code != NEW.error_code
                  OR OLD.job_id IS NOT NEW.job_id
                  OR OLD.mode IS NOT NEW.mode
                  OR OLD.total_items IS NOT NEW.total_items
                  OR OLD.completed_items IS NOT NEW.completed_items
                  OR OLD.failed_items IS NOT NEW.failed_items
                  OR OLD.reason_code IS NOT NEW.reason_code
                  OR OLD.candidate_count IS NOT NEW.candidate_count
                  OR OLD.helper_version_suspect IS NOT NEW.helper_version_suspect
                  OR OLD.cost_basis IS NOT NEW.cost_basis
                  OR OLD.payload_sha256 != NEW.payload_sha256
                  OR OLD.created_at != NEW.created_at
                BEGIN
                    SELECT RAISE(ABORT,
                        'pricing_terminal_ack_identity_immutable');
                END;
                CREATE TRIGGER pricing_terminal_ack_state_monotonic
                BEFORE UPDATE ON pricing_terminal_ack_outbox
                WHEN OLD.state = 'delivered' AND NEW.state != 'delivered'
                BEGIN
                    SELECT RAISE(ABORT,
                        'pricing_terminal_ack_state_immutable');
                END;
                CREATE TRIGGER pricing_terminal_ack_no_delete
                BEFORE DELETE ON pricing_terminal_ack_outbox
                BEGIN
                    SELECT RAISE(ABORT, 'pricing_terminal_ack_immutable');
                END;
                """);

            using var mark = CreateCommand(transaction, """
                INSERT INTO schema_migrations (version, applied_at, description)
                VALUES (45, @at, @description)
                """);
            mark.Parameters.AddWithValue(
                "@at", DateTimeOffset.UtcNow.ToString("o"));
            mark.Parameters.AddWithValue(
                "@description",
                "Allow finite PHI-free generated-report terminal ACKs");
            mark.ExecuteNonQuery();
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static string AddGeneratedPricingFailureConstraints(
        string createSql)
    {
        const string anchor = "'pricing_discovery_exception'";
        const string additions = """
            'pricing_worklist_source_unavailable',
            'pricing_worklist_generation_failed',
            'pricing_worklist_validation_failed',
            'pricing_worklist_empty',
            'pricing_report_permission_blocked',
            'pricing_pioneerrx_not_open',
            'pricing_report_open_failed',
            'pricing_report_filters_failed',
            'pricing_report_generation_failed',
            'pricing_report_export_failed',
            'pricing_report_save_dialog_blocked',
            'pricing_report_storage_unavailable',
            'pricing_report_validation_failed',
            'pricing_report_cancelled',
            'pricing_output_publication_failed'
            """;
        if (createSql.Contains(
                "'pricing_output_publication_failed'",
                StringComparison.Ordinal))
            return createSql;

        var occurrences = createSql.Split(
            anchor,
            StringSplitOptions.None).Length - 1;
        if (occurrences != 2)
            throw new InvalidOperationException(
                "pricing_generated_terminal_schema_drift");
        return createSql.Replace(
            anchor,
            $"{anchor}, {additions}",
            StringComparison.Ordinal);
    }
}
