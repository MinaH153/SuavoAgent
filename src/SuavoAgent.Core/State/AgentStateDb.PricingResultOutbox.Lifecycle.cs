namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private enum PricingResultOutboxTable
    {
        Legacy,
        V2,
    }

    internal void MarkPricingResultPayloadAccepted(
        string jobId,
        string payloadDigest,
        int recorded,
        string code,
        string acceptedReceiptJson,
        string responseKeyId,
        string responseSignature)
    {
        var receiptDigest = Digest(acceptedReceiptJson);
        lock (_connLock)
        {
            var affected = MarkAccepted(
                PricingResultOutboxTable.V2,
                jobId,
                payloadDigest,
                recorded,
                code,
                acceptedReceiptJson,
                receiptDigest,
                responseKeyId,
                responseSignature);
            if (affected == 0)
                affected = MarkAccepted(
                    PricingResultOutboxTable.Legacy,
                    jobId,
                    payloadDigest,
                    recorded,
                    code,
                    acceptedReceiptJson,
                    receiptDigest,
                    responseKeyId,
                    responseSignature);
            if (affected != 1)
                throw new InvalidOperationException(
                    "Pricing result acceptance conflict.");
        }
    }

    internal void DelayPricingResultPayload(
        string jobId,
        string payloadDigest,
        int priorAttempts)
    {
        var delaySeconds = Math.Min(3600, 15 * (1 << Math.Min(priorAttempts, 7)));
        lock (_connLock)
        {
            var affected = Delay(
                PricingResultOutboxTable.V2,
                jobId,
                payloadDigest,
                delaySeconds);
            if (affected == 0)
                affected = Delay(
                    PricingResultOutboxTable.Legacy,
                    jobId,
                    payloadDigest,
                    delaySeconds);
            if (affected != 1)
                throw new InvalidOperationException(
                    "Pricing result retry conflict.");
        }
    }

    internal void MarkPricingResultSourceFinalized(
        Guid sourceUploadId,
        string jobId,
        string payloadDigest)
    {
        lock (_connLock)
        {
            var affected = FinalizeSource(
                PricingResultOutboxTable.V2,
                sourceUploadId,
                jobId,
                payloadDigest);
            if (affected == 0)
                affected = FinalizeSource(
                    PricingResultOutboxTable.Legacy,
                    sourceUploadId,
                    jobId,
                    payloadDigest);
            if (affected != 1)
                throw new InvalidOperationException(
                    "Pricing result source finalization conflict.");
        }
    }

    private int MarkAccepted(
        PricingResultOutboxTable table,
        string jobId,
        string payloadDigest,
        int recorded,
        string code,
        string receipt,
        string receiptDigest,
        string responseKeyId,
        string responseSignature)
    {
        using var command = _conn.CreateCommand();
        command.CommandText = table switch
        {
            PricingResultOutboxTable.Legacy => MarkAcceptedLegacySql,
            PricingResultOutboxTable.V2 => MarkAcceptedV2Sql,
            _ => throw new ArgumentOutOfRangeException(nameof(table)),
        };
        command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        command.Parameters.AddWithValue("@code", code);
        command.Parameters.AddWithValue("@recorded", recorded);
        command.Parameters.AddWithValue("@receipt", receipt);
        command.Parameters.AddWithValue("@receipt_digest", receiptDigest);
        command.Parameters.AddWithValue("@key_id", responseKeyId);
        command.Parameters.AddWithValue("@signature", responseSignature);
        command.Parameters.AddWithValue("@job", jobId);
        command.Parameters.AddWithValue("@digest", payloadDigest);
        return command.ExecuteNonQuery();
    }

    private int Delay(
        PricingResultOutboxTable table,
        string jobId,
        string payloadDigest,
        int delaySeconds)
    {
        using var command = _conn.CreateCommand();
        command.CommandText = table switch
        {
            PricingResultOutboxTable.Legacy => DelayLegacySql,
            PricingResultOutboxTable.V2 => DelayV2Sql,
            _ => throw new ArgumentOutOfRangeException(nameof(table)),
        };
        command.Parameters.AddWithValue(
            "@next", DateTimeOffset.UtcNow.AddSeconds(delaySeconds).ToString("o"));
        command.Parameters.AddWithValue("@job", jobId);
        command.Parameters.AddWithValue("@digest", payloadDigest);
        return command.ExecuteNonQuery();
    }

    private int FinalizeSource(
        PricingResultOutboxTable table,
        Guid sourceUploadId,
        string jobId,
        string payloadDigest)
    {
        using var command = _conn.CreateCommand();
        command.CommandText = table switch
        {
            PricingResultOutboxTable.Legacy => FinalizeLegacySql,
            PricingResultOutboxTable.V2 => FinalizeV2Sql,
            _ => throw new ArgumentOutOfRangeException(nameof(table)),
        };
        command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        command.Parameters.AddWithValue("@source", sourceUploadId.ToString("D"));
        command.Parameters.AddWithValue("@job", jobId);
        command.Parameters.AddWithValue("@digest", payloadDigest);
        return command.ExecuteNonQuery();
    }

    private const string MarkAcceptedLegacySql = """
        UPDATE pricing_result_outbox
           SET state = 'accepted', accepted_at = COALESCE(accepted_at, @now),
               accepted_code = COALESCE(accepted_code, @code),
               accepted_recorded = COALESCE(accepted_recorded, @recorded),
               accepted_receipt_json = COALESCE(accepted_receipt_json, @receipt),
               accepted_receipt_sha256 = COALESCE(accepted_receipt_sha256, @receipt_digest),
               accepted_response_key_id = COALESCE(accepted_response_key_id, @key_id),
               accepted_response_signature = COALESCE(accepted_response_signature, @signature)
         WHERE job_id = @job AND payload_sha256 = @digest AND item_count = @recorded
           AND (state = 'pending' OR (state = 'accepted' AND accepted_code = @code
             AND accepted_recorded = @recorded AND accepted_receipt_json = @receipt
             AND accepted_receipt_sha256 = @receipt_digest
             AND accepted_response_key_id = @key_id
             AND accepted_response_signature = @signature))
        """;

    private const string MarkAcceptedV2Sql = """
        UPDATE pricing_result_outbox_v2
           SET state = 'accepted', accepted_at = COALESCE(accepted_at, @now),
               accepted_code = COALESCE(accepted_code, @code),
               accepted_recorded = COALESCE(accepted_recorded, @recorded),
               accepted_receipt_json = COALESCE(accepted_receipt_json, @receipt),
               accepted_receipt_sha256 = COALESCE(accepted_receipt_sha256, @receipt_digest),
               accepted_response_key_id = COALESCE(accepted_response_key_id, @key_id),
               accepted_response_signature = COALESCE(accepted_response_signature, @signature)
         WHERE job_id = @job AND payload_sha256 = @digest AND item_count = @recorded
           AND (state = 'pending' OR (state = 'accepted' AND accepted_code = @code
             AND accepted_recorded = @recorded AND accepted_receipt_json = @receipt
             AND accepted_receipt_sha256 = @receipt_digest
             AND accepted_response_key_id = @key_id
             AND accepted_response_signature = @signature))
        """;

    private const string DelayLegacySql = """
        UPDATE pricing_result_outbox
           SET attempt_count = attempt_count + 1, next_attempt_at = @next
         WHERE job_id = @job AND payload_sha256 = @digest AND state = 'pending'
        """;

    private const string DelayV2Sql = """
        UPDATE pricing_result_outbox_v2
           SET attempt_count = attempt_count + 1, next_attempt_at = @next
         WHERE job_id = @job AND payload_sha256 = @digest AND state = 'pending'
        """;

    private const string FinalizeLegacySql = """
        UPDATE pricing_result_outbox
           SET source_finalized_at = COALESCE(source_finalized_at, @now)
         WHERE source_upload_id = @source AND job_id = @job
           AND payload_sha256 = @digest AND state = 'accepted'
        """;

    private const string FinalizeV2Sql = """
        UPDATE pricing_result_outbox_v2
           SET source_finalized_at = COALESCE(source_finalized_at, @now)
         WHERE source_upload_id = @source AND job_id = @job
           AND payload_sha256 = @digest AND state = 'accepted'
        """;
}
