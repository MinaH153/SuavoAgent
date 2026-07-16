using Microsoft.Data.Sqlite;

namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private void ApplyPricingAuthorityReplayMigration()
    {
        using var check = _conn.CreateCommand();
        check.CommandText =
            "SELECT 1 FROM schema_migrations WHERE version = 40 LIMIT 1";
        if (check.ExecuteScalar() is not null) return;

        using var transaction = _conn.BeginTransaction();
        try
        {
            ExecuteMigrationSql(transaction, """
                CREATE TABLE pricing_result_authority_send_attempts (
                    job_id TEXT NOT NULL CHECK(
                        length(job_id) BETWEEN 1 AND 200
                        AND job_id NOT GLOB '*[^A-Za-z0-9:_-]*'),
                    payload_sha256 TEXT NOT NULL CHECK(
                        length(payload_sha256) = 64
                        AND payload_sha256 NOT GLOB '*[^0-9a-f]*'),
                    approval_id TEXT NOT NULL CHECK(
                        length(approval_id) = 36
                        AND substr(approval_id, 9, 1) = '-'
                        AND substr(approval_id, 14, 1) = '-'
                        AND substr(approval_id, 19, 1) = '-'
                        AND substr(approval_id, 24, 1) = '-'
                        AND substr(approval_id, 15, 1) = '4'
                        AND substr(approval_id, 20, 1) IN ('8','9','a','b')
                        AND replace(approval_id, '-', '')
                            NOT GLOB '*[^0-9a-f]*'),
                    grant_digest TEXT NOT NULL CHECK(
                        length(grant_digest) = 64
                        AND grant_digest NOT GLOB '*[^0-9a-f]*'),
                    attempted_at_utc TEXT NOT NULL,
                    PRIMARY KEY(job_id, payload_sha256)
                );
                CREATE TRIGGER pricing_result_authority_attempt_evidence_required
                BEFORE INSERT ON pricing_result_authority_send_attempts
                WHEN NOT EXISTS (
                        SELECT 1 FROM pricing_result_outbox_v2
                         WHERE job_id = NEW.job_id
                           AND payload_sha256 = NEW.payload_sha256
                           AND json_extract(payload_json, '$.approvalId') =
                                NEW.approval_id
                           AND json_extract(payload_json, '$.grantDigest') =
                                NEW.grant_digest)
                 AND NOT EXISTS (
                        SELECT 1 FROM pricing_result_outbox
                         WHERE job_id = NEW.job_id
                           AND payload_sha256 = NEW.payload_sha256
                           AND json_extract(payload_json, '$.approvalId') =
                                NEW.approval_id
                           AND json_extract(payload_json, '$.grantDigest') =
                                NEW.grant_digest)
                BEGIN
                    SELECT RAISE(ABORT,
                        'pricing_result_authority_attempt_evidence_not_found');
                END;
                CREATE TRIGGER pricing_result_authority_attempt_immutable
                BEFORE UPDATE ON pricing_result_authority_send_attempts
                BEGIN
                    SELECT RAISE(ABORT,
                        'pricing_result_authority_attempt_immutable');
                END;
                CREATE TRIGGER pricing_result_authority_attempt_no_delete
                BEFORE DELETE ON pricing_result_authority_send_attempts
                BEGIN
                    SELECT RAISE(ABORT,
                        'pricing_result_authority_attempt_immutable');
                END;
                CREATE TABLE pricing_result_authority_recovery_attempts (
                    job_id TEXT NOT NULL,
                    payload_sha256 TEXT NOT NULL,
                    attempt_number INTEGER NOT NULL CHECK(
                        attempt_number BETWEEN 1 AND 3),
                    approval_id TEXT NOT NULL,
                    grant_digest TEXT NOT NULL,
                    attempted_at_utc TEXT NOT NULL,
                    PRIMARY KEY(job_id, payload_sha256, attempt_number)
                );
                CREATE TRIGGER pricing_result_authority_recovery_evidence_required
                BEFORE INSERT ON pricing_result_authority_recovery_attempts
                WHEN NOT EXISTS (
                    SELECT 1 FROM pricing_result_authority_send_attempts
                     WHERE job_id = NEW.job_id
                       AND payload_sha256 = NEW.payload_sha256
                       AND approval_id = NEW.approval_id
                       AND grant_digest = NEW.grant_digest)
                BEGIN
                    SELECT RAISE(ABORT,
                        'pricing_result_authority_recovery_evidence_not_found');
                END;
                CREATE TRIGGER pricing_result_authority_recovery_immutable
                BEFORE UPDATE ON pricing_result_authority_recovery_attempts
                BEGIN
                    SELECT RAISE(ABORT,
                        'pricing_result_authority_recovery_immutable');
                END;
                CREATE TRIGGER pricing_result_authority_recovery_no_delete
                BEFORE DELETE ON pricing_result_authority_recovery_attempts
                BEGIN
                    SELECT RAISE(ABORT,
                        'pricing_result_authority_recovery_immutable');
                END;
                """);

            RebuildPricingAuthorityTerminalTable(transaction);
            RebuildPricingAuthorityTerminalAckTable(transaction);

            using var mark = CreateCommand(transaction, """
                INSERT INTO schema_migrations (version, applied_at, description)
                VALUES (40, @at, @description)
                """);
            mark.Parameters.AddWithValue(
                "@at", DateTimeOffset.UtcNow.ToString("o"));
            mark.Parameters.AddWithValue(
                "@description",
                "Durable exact pricing send and bounded receipt recovery evidence");
            mark.ExecuteNonQuery();
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private void RebuildPricingAuthorityTerminalTable(
        SqliteTransaction transaction)
    {
        var createSql = ReadTableCreationSql(
            transaction,
            "pricing_result_outbox_terminal_receipts");
        var expanded = AddCloudRevocationConstraint(createSql);
        ExecuteMigrationSql(transaction, """
            DROP TRIGGER pricing_result_outbox_terminal_evidence_required;
            DROP TRIGGER pricing_result_outbox_terminal_immutable;
            DROP TRIGGER pricing_result_outbox_terminal_no_delete;
            DROP INDEX idx_pricing_result_outbox_terminal_time;
            ALTER TABLE pricing_result_outbox_terminal_receipts
                RENAME TO pricing_result_outbox_terminal_receipts_v40;
            """);
        ExecuteMigrationSql(transaction, expanded);
        ExecuteMigrationSql(transaction, """
            INSERT INTO pricing_result_outbox_terminal_receipts (
                job_id, payload_sha256, reason_code, http_status, response_json,
                response_sha256, response_key_id, response_signature,
                quarantined_at)
            SELECT job_id, payload_sha256, reason_code, http_status, response_json,
                   response_sha256, response_key_id, response_signature,
                   quarantined_at
              FROM pricing_result_outbox_terminal_receipts_v40;
            DROP TABLE pricing_result_outbox_terminal_receipts_v40;
            CREATE INDEX idx_pricing_result_outbox_terminal_time
                ON pricing_result_outbox_terminal_receipts(quarantined_at, job_id);
            CREATE TRIGGER pricing_result_outbox_terminal_evidence_required
            BEFORE INSERT ON pricing_result_outbox_terminal_receipts
            WHEN NOT EXISTS (
                    SELECT 1 FROM pricing_result_outbox_v2
                     WHERE job_id = NEW.job_id
                       AND payload_sha256 = NEW.payload_sha256)
             AND NOT EXISTS (
                    SELECT 1 FROM pricing_result_outbox
                     WHERE job_id = NEW.job_id
                       AND payload_sha256 = NEW.payload_sha256)
            BEGIN
                SELECT RAISE(ABORT, 'pricing_result_terminal_evidence_not_found');
            END;
            CREATE TRIGGER pricing_result_outbox_terminal_immutable
            BEFORE UPDATE ON pricing_result_outbox_terminal_receipts
            BEGIN
                SELECT RAISE(ABORT, 'pricing_result_outbox_terminal_immutable');
            END;
            CREATE TRIGGER pricing_result_outbox_terminal_no_delete
            BEFORE DELETE ON pricing_result_outbox_terminal_receipts
            BEGIN
                SELECT RAISE(ABORT, 'pricing_result_outbox_terminal_immutable');
            END;
            """);
    }

    private void RebuildPricingAuthorityTerminalAckTable(
        SqliteTransaction transaction)
    {
        var createSql = ReadTableCreationSql(
            transaction,
            "pricing_terminal_ack_outbox");
        var expanded = AddCloudRevocationConstraint(createSql);
        ExecuteMigrationSql(transaction, """
            DROP TRIGGER pricing_terminal_ack_identity_immutable;
            DROP TRIGGER pricing_terminal_ack_state_monotonic;
            DROP TRIGGER pricing_terminal_ack_no_delete;
            DROP INDEX idx_pricing_terminal_ack_pending;
            ALTER TABLE pricing_terminal_ack_outbox
                RENAME TO pricing_terminal_ack_outbox_v40;
            """);
        ExecuteMigrationSql(transaction, expanded);
        ExecuteMigrationSql(transaction, """
            INSERT INTO pricing_terminal_ack_outbox (
                command_id, result_kind, error_code, job_id, mode,
                total_items, completed_items, failed_items, reason_code,
                candidate_count, helper_version_suspect, payload_sha256,
                state, attempt_count, next_attempt_at, created_at, delivered_at)
            SELECT command_id, result_kind, error_code, job_id, mode,
                   total_items, completed_items, failed_items, reason_code,
                   candidate_count, helper_version_suspect, payload_sha256,
                   state, attempt_count, next_attempt_at, created_at, delivered_at
              FROM pricing_terminal_ack_outbox_v40;
            DROP TABLE pricing_terminal_ack_outbox_v40;
            CREATE INDEX idx_pricing_terminal_ack_pending
                ON pricing_terminal_ack_outbox(state, next_attempt_at, created_at);
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
              OR OLD.payload_sha256 != NEW.payload_sha256
              OR OLD.created_at != NEW.created_at
            BEGIN
                SELECT RAISE(ABORT, 'pricing_terminal_ack_identity_immutable');
            END;
            CREATE TRIGGER pricing_terminal_ack_state_monotonic
            BEFORE UPDATE ON pricing_terminal_ack_outbox
            WHEN OLD.state = 'delivered' AND NEW.state != 'delivered'
            BEGIN
                SELECT RAISE(ABORT, 'pricing_terminal_ack_state_immutable');
            END;
            CREATE TRIGGER pricing_terminal_ack_no_delete
            BEFORE DELETE ON pricing_terminal_ack_outbox
            BEGIN
                SELECT RAISE(ABORT, 'pricing_terminal_ack_immutable');
            END;
            """);
    }

    private string ReadTableCreationSql(
        SqliteTransaction transaction,
        string tableName)
    {
        using var command = CreateCommand(transaction, """
            SELECT sql FROM sqlite_master
             WHERE type = 'table' AND name = @table
            """);
        command.Parameters.AddWithValue("@table", tableName);
        return command.ExecuteScalar() as string ??
            throw new InvalidOperationException(
                "pricing_authority_terminal_schema_missing");
    }

    private static string AddCloudRevocationConstraint(string createSql)
    {
        const string manual =
            "'pricing_result_manual_reconciliation_required',";
        if (createSql.Contains(manual, StringComparison.Ordinal))
            return createSql;
        const string cloud = "'pricing_cloud_authority_revoked',";
        if (createSql.Contains(cloud, StringComparison.Ordinal))
            return createSql.Replace(
                cloud,
                cloud + " " + manual,
                StringComparison.Ordinal);
        const string existing = "'pricing_cost_basis_approval_revoked',";
        const string expanded =
            "'pricing_cost_basis_approval_revoked', " +
            cloud + " " + manual;
        var updated = createSql.Replace(
            existing,
            expanded,
            StringComparison.Ordinal);
        return updated == createSql
            ? throw new InvalidOperationException(
                "pricing_authority_terminal_schema_drift")
            : updated;
    }

    private void ExecuteMigrationSql(
        SqliteTransaction transaction,
        string sql)
    {
        using var command = CreateCommand(transaction, sql);
        command.ExecuteNonQuery();
    }
}
