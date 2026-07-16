using Microsoft.Data.Sqlite;

namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private const int MaximumPricingAuthorityRecoveryAttempts = 3;

    private void RecordPricingAuthoritySendAttempt(
        string jobId,
        string payloadSha256,
        string approvalId,
        string grantDigest,
        DateTimeOffset attemptedAt)
    {
        lock (_connLock)
        {
            using var insert = _conn.CreateCommand();
            insert.CommandText = """
                INSERT INTO pricing_result_authority_send_attempts (
                    job_id, payload_sha256, approval_id, grant_digest,
                    attempted_at_utc)
                VALUES (@job, @payload, @approval, @grant, @attempted)
                ON CONFLICT(job_id, payload_sha256) DO NOTHING
                """;
            insert.Parameters.AddWithValue("@job", jobId);
            insert.Parameters.AddWithValue("@payload", payloadSha256);
            insert.Parameters.AddWithValue("@approval", approvalId);
            insert.Parameters.AddWithValue("@grant", grantDigest);
            insert.Parameters.AddWithValue(
                "@attempted", attemptedAt.ToUniversalTime().ToString("O"));
            insert.ExecuteNonQuery();

            if (!HasExactPricingAuthoritySendAttempt(
                    jobId,
                    payloadSha256,
                    approvalId,
                    grantDigest))
                throw new InvalidOperationException(
                    "pricing_result_authority_attempt_conflict");
        }
    }

    private bool HasExactPricingAuthoritySendAttempt(
        string jobId,
        string payloadSha256,
        string approvalId,
        string grantDigest)
    {
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                SELECT approval_id, grant_digest
                  FROM pricing_result_authority_send_attempts
                 WHERE job_id = @job AND payload_sha256 = @payload
                 LIMIT 1
                """;
            command.Parameters.AddWithValue("@job", jobId);
            command.Parameters.AddWithValue("@payload", payloadSha256);
            using var reader = command.ExecuteReader();
            return reader.Read() &&
                string.Equals(
                    reader.GetString(0), approvalId, StringComparison.Ordinal) &&
                FixedApprovalHexEquals(reader.GetString(1), grantDigest);
        }
    }

    private bool TryRecordPricingAuthorityRecoveryAttempt(
        string jobId,
        string payloadSha256,
        string approvalId,
        string grantDigest,
        DateTimeOffset attemptedAt,
        out int attemptNumber)
    {
        lock (_connLock)
        {
            using var count = _conn.CreateCommand();
            count.CommandText = """
                SELECT count(*)
                  FROM pricing_result_authority_recovery_attempts
                 WHERE job_id = @job AND payload_sha256 = @payload
                """;
            count.Parameters.AddWithValue("@job", jobId);
            count.Parameters.AddWithValue("@payload", payloadSha256);
            var priorAttempts = Convert.ToInt32(count.ExecuteScalar());
            if (priorAttempts >= MaximumPricingAuthorityRecoveryAttempts)
            {
                attemptNumber = priorAttempts;
                return false;
            }

            attemptNumber = priorAttempts + 1;
            using var insert = _conn.CreateCommand();
            insert.CommandText = """
                INSERT INTO pricing_result_authority_recovery_attempts (
                    job_id, payload_sha256, attempt_number, approval_id,
                    grant_digest, attempted_at_utc)
                VALUES (
                    @job, @payload, @attempt, @approval, @grant, @attempted)
                """;
            insert.Parameters.AddWithValue("@job", jobId);
            insert.Parameters.AddWithValue("@payload", payloadSha256);
            insert.Parameters.AddWithValue("@attempt", attemptNumber);
            insert.Parameters.AddWithValue("@approval", approvalId);
            insert.Parameters.AddWithValue("@grant", grantDigest);
            insert.Parameters.AddWithValue(
                "@attempted", attemptedAt.ToUniversalTime().ToString("O"));
            insert.ExecuteNonQuery();
            return true;
        }
    }
}
