using Microsoft.Data.Sqlite;

namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private const string LegacySelectColumns = """
        SELECT job_id, command_id, source_upload_id, payload_json, payload_sha256,
               item_count, execution_ok, state, attempt_count, created_at, accepted_at,
               accepted_receipt_json, accepted_receipt_sha256,
               accepted_response_key_id, accepted_response_signature,
               source_finalized_at,
               0 AS generation, 1 AS legacy
          FROM pricing_result_outbox
        """;

    private const string V2SelectColumns = """
        SELECT job_id, command_id, source_upload_id, payload_json, payload_sha256,
               item_count, execution_ok, state, attempt_count, created_at, accepted_at,
               accepted_receipt_json, accepted_receipt_sha256,
               accepted_response_key_id, accepted_response_signature,
               source_finalized_at,
               generation, 0 AS legacy
          FROM pricing_result_outbox_v2
        """;

    private const string PendingPricingResultPayloadsSql = """
        SELECT * FROM (
            SELECT source.job_id, source.command_id, source.source_upload_id,
                   source.payload_json, source.payload_sha256, source.item_count,
                   source.execution_ok, source.state, source.attempt_count,
                   source.created_at, source.accepted_at,
                   source.accepted_receipt_json, source.accepted_receipt_sha256,
                   source.accepted_response_key_id, source.accepted_response_signature,
                   source.source_finalized_at, source.generation, 0 AS legacy
              FROM pricing_result_outbox_v2 source
             WHERE source.state = 'pending'
               AND (@due_only = 0 OR source.next_attempt_at <= @now)
               AND NOT EXISTS (
                   SELECT 1 FROM pricing_result_outbox_terminal_receipts terminal
                    WHERE terminal.job_id = source.job_id
                      AND terminal.payload_sha256 = source.payload_sha256)
               AND NOT EXISTS (
                   SELECT 1 FROM pricing_result_outbox_supersessions supersession
                    WHERE supersession.job_id = source.job_id
                      AND supersession.superseded_payload_sha256 = source.payload_sha256)
            UNION ALL
            SELECT source.job_id, source.command_id, source.source_upload_id,
                   source.payload_json, source.payload_sha256, source.item_count,
                   source.execution_ok, source.state, source.attempt_count,
                   source.created_at, source.accepted_at,
                   source.accepted_receipt_json, source.accepted_receipt_sha256,
                   source.accepted_response_key_id, source.accepted_response_signature,
                   source.source_finalized_at, 0 AS generation, 1 AS legacy
              FROM pricing_result_outbox source
             WHERE source.state = 'pending'
               AND (@due_only = 0 OR source.next_attempt_at <= @now)
               AND NOT EXISTS (
                   SELECT 1 FROM pricing_result_outbox_quarantine quarantine
                    WHERE quarantine.job_id = source.job_id
                      AND quarantine.payload_sha256 = source.payload_sha256)
               AND NOT EXISTS (
                   SELECT 1 FROM pricing_result_outbox_terminal_receipts terminal
                    WHERE terminal.job_id = source.job_id
                      AND terminal.payload_sha256 = source.payload_sha256)
               AND NOT EXISTS (
                   SELECT 1 FROM pricing_result_outbox_supersessions supersession
                    WHERE supersession.job_id = source.job_id
                      AND supersession.superseded_payload_sha256 = source.payload_sha256)
        ) ORDER BY created_at LIMIT @limit
        """;

    private const string AcceptedPricingSourcesToFinalizeSql = """
        SELECT * FROM (
            SELECT job_id, command_id, source_upload_id, payload_json, payload_sha256,
                   item_count, execution_ok, state, attempt_count, created_at, accepted_at,
                   accepted_receipt_json, accepted_receipt_sha256,
                   accepted_response_key_id, accepted_response_signature,
                   source_finalized_at, generation, 0 AS legacy
              FROM pricing_result_outbox_v2
             WHERE state = 'accepted' AND source_upload_id IS NOT NULL
               AND source_finalized_at IS NULL
            UNION ALL
            SELECT job_id, command_id, source_upload_id, payload_json, payload_sha256,
                   item_count, execution_ok, state, attempt_count, created_at, accepted_at,
                   accepted_receipt_json, accepted_receipt_sha256,
                   accepted_response_key_id, accepted_response_signature,
                   source_finalized_at, 0 AS generation, 1 AS legacy
              FROM pricing_result_outbox
             WHERE state = 'accepted' AND source_upload_id IS NOT NULL
               AND source_finalized_at IS NULL
        ) ORDER BY accepted_at LIMIT @limit
        """;

    private static PricingResultOutboxEntry MapPricingResultOutbox(
        SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.IsDBNull(1) ? null : reader.GetString(1),
        reader.IsDBNull(2) ? null : Guid.ParseExact(reader.GetString(2), "D"),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetInt32(5),
        reader.GetInt32(6) == 1,
        reader.GetString(7),
        reader.GetInt32(8),
        ParseOutboxTimestamp(reader.GetString(9)),
        reader.IsDBNull(10) ? null : ParseOutboxTimestamp(reader.GetString(10)),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.IsDBNull(13) ? null : reader.GetString(13),
        reader.IsDBNull(14) ? null : reader.GetString(14),
        reader.IsDBNull(15) ? null : ParseOutboxTimestamp(reader.GetString(15)),
        reader.GetInt32(16),
        reader.GetInt32(17) == 1);

    private static DateTimeOffset ParseOutboxTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            System.Globalization.CultureInfo.InvariantCulture);
}
