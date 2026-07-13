using System.Globalization;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Core.Pricing;

namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private sealed record PricingJobAuthorityBinding(
        PricingObservationContract Observation,
        string PharmacyId,
        string AgentId,
        string MachineFingerprint,
        string ApprovalId,
        string ApprovalDigest,
        string ApprovedByRole,
        DateTimeOffset FreshUntilUtc,
        DateTimeOffset AuthorityExpiresAtUtc);

    /// <summary>
    /// Revalidates the exact append-only PIC grant bound to a pricing job and
    /// the authenticated cloud lease. This is the execution/send-time gate;
    /// the immutable input identity alone is historical evidence, not current
    /// authority.
    /// </summary>
    internal bool TryAdmitPricingJobAuthority(
        string jobId,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string>? trustedPublicKeys,
        out string code) => TryAdmitPricingJobAuthority(
            jobId,
            expectedApprovalId: null,
            expectedGrantDigest: null,
            now,
            trustedPublicKeys,
            out code);

    internal bool TryAdmitPricingJobAuthority(
        string jobId,
        string? expectedApprovalId,
        string? expectedGrantDigest,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string>? trustedPublicKeys,
        out string code)
    {
        if (!SafePricingEvidenceId.IsMatch(jobId))
        {
            code = "pricing_job_authority_identity_invalid";
            return false;
        }
        if ((expectedApprovalId is null) != (expectedGrantDigest is null) ||
            expectedApprovalId is not null &&
            !IsCanonicalPricingApprovalId(expectedApprovalId) ||
            expectedGrantDigest is not null &&
            !IsLowerHexSha256(expectedGrantDigest))
        {
            code = "pricing_job_authority_binding_invalid";
            return false;
        }

        now = now.ToUniversalTime();
        if (!TryAdmitPricingCloudAuthority(now, out code))
            return false;

        trustedPublicKeys ??= RemoteCommandTrust.CreateProductionKeyRegistry();
        PricingJobAuthorityBinding? binding;
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                SELECT identity.modality, identity.schema_digest,
                       identity.status_policy_digest, identity.cost_basis,
                       identity.policy_digest, identity.snapshot_contract,
                       identity.fresh_until_utc,
                       identity.authority_pharmacy_id,
                       identity.authority_role,
                       identity.authority_approval_id,
                       identity.authority_approval_digest,
                       identity.authority_expires_at_utc,
                       grant.approval_id, grant.pharmacy_id, grant.agent_id,
                       grant.machine_fingerprint, grant.freshness_seconds,
                       grant.expires_at_utc, grant.grant_digest
                  FROM pricing_job_input_identity identity
                  JOIN pricing_approval_grants grant
                    ON grant.grant_digest = identity.authority_approval_digest
                 WHERE identity.job_id = @job
                 LIMIT 2
                """;
            command.Parameters.AddWithValue("@job", jobId);
            using var reader = command.ExecuteReader();
            if (!reader.Read() || Enumerable.Range(0, 19).Any(reader.IsDBNull))
            {
                code = "pricing_job_authority_binding_missing";
                return false;
            }

            if (!DateTimeOffset.TryParseExact(
                    reader.GetString(6),
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var freshUntil) ||
                !DateTimeOffset.TryParseExact(
                    reader.GetString(11),
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var identityExpiry) ||
                !DateTimeOffset.TryParseExact(
                    reader.GetString(17),
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var grantExpiry) ||
                reader.GetInt64(16) is <= 0 or > 86_400)
            {
                code = "pricing_job_authority_binding_invalid";
                return false;
            }

            var identityPharmacyId = reader.GetString(7);
            var identityApprovalId = reader.GetString(9);
            var identityApprovalDigest = reader.GetString(10);
            var grantApprovalId = reader.GetString(12);
            var grantPharmacyId = reader.GetString(13);
            var grantDigest = reader.GetString(18);
            binding = new PricingJobAuthorityBinding(
                new PricingObservationContract(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    TimeSpan.FromSeconds(reader.GetInt64(16))),
                grantPharmacyId,
                reader.GetString(14),
                reader.GetString(15),
                grantApprovalId,
                identityApprovalDigest,
                reader.GetString(8),
                freshUntil.ToUniversalTime(),
                identityExpiry.ToUniversalTime());

            if (reader.Read() ||
                !string.Equals(
                    identityApprovalId,
                    grantApprovalId,
                    StringComparison.Ordinal) ||
                !FixedApprovalHexEquals(identityApprovalDigest, grantDigest) ||
                expectedApprovalId is not null && !string.Equals(
                    expectedApprovalId,
                    grantApprovalId,
                    StringComparison.Ordinal) ||
                expectedGrantDigest is not null && !FixedApprovalHexEquals(
                    expectedGrantDigest,
                    grantDigest) ||
                !string.Equals(
                    identityPharmacyId,
                    grantPharmacyId,
                    StringComparison.Ordinal) ||
                binding.ApprovedByRole != PricingObservationPolicy.PharmacistInChargeRole ||
                binding.AuthorityExpiresAtUtc != grantExpiry.ToUniversalTime() ||
                binding.FreshUntilUtc <= now ||
                binding.AuthorityExpiresAtUtc <= now)
            {
                code = binding.FreshUntilUtc <= now ||
                       binding.AuthorityExpiresAtUtc <= now
                    ? "pricing_cost_basis_approval_expired"
                    : "pricing_job_authority_binding_invalid";
                return false;
            }

            if (expectedApprovalId is not null)
            {
                using var exact = _conn.CreateCommand();
                exact.CommandText = """
                    SELECT 1
                      FROM pricing_jobs job
                      JOIN pricing_job_input_identity identity
                        ON identity.job_id = job.job_id
                      JOIN pricing_result_delivery_intents delivery
                        ON delivery.job_id = job.job_id
                      JOIN pricing_command_execution_intents intent
                        ON intent.command_id = delivery.command_id
                     WHERE job.job_id = @job
                       AND job.approval_id = @approval
                       AND job.grant_digest = @grant
                       AND identity.authority_approval_id = @approval
                       AND identity.authority_approval_digest = @grant
                       AND delivery.approval_id = @approval
                       AND delivery.grant_digest = @grant
                       AND delivery.source_mode = identity.modality
                       AND intent.pricing_approval_id = @approval
                       AND intent.pricing_grant_digest = @grant
                     LIMIT 1
                    """;
                exact.Parameters.AddWithValue("@job", jobId);
                exact.Parameters.AddWithValue("@approval", expectedApprovalId);
                exact.Parameters.AddWithValue("@grant", expectedGrantDigest!);
                if (exact.ExecuteScalar() is null)
                {
                    code = "pricing_job_authority_binding_invalid";
                    return false;
                }
            }
            if (IsPricingApprovalRevocationPending(binding.ApprovalId))
            {
                code = "pricing_cost_basis_approval_revoked";
                return false;
            }
        }

        if (!TryGetInstalledPricingAuthority(
                binding.PharmacyId,
                binding.AgentId,
                binding.MachineFingerprint,
                binding.Observation,
                now,
                trustedPublicKeys,
                binding.ApprovalDigest,
                out var authority,
                out code) ||
            authority is null ||
            !string.Equals(
                authority.ApprovalId,
                binding.ApprovalId,
                StringComparison.Ordinal) ||
            !FixedApprovalHexEquals(
                authority.ApprovalDigest,
                binding.ApprovalDigest) ||
            authority.ExpiresAtUtc != binding.AuthorityExpiresAtUtc)
            return false;
        if (IsPricingApprovalRevocationPending(binding.ApprovalId))
        {
            code = "pricing_cost_basis_approval_revoked";
            return false;
        }

        code = "pricing_job_authority_active";
        return true;
    }

    /// <summary>
    /// Linearizes the last exact-authority check and the filesystem publication
    /// against signed PIC revocation on this process-wide state ledger. The
    /// callback must contain only the final atomic Move/Replace operation.
    /// </summary>
    internal bool TryPublishPricingArtifact(
        string jobId,
        TimeProvider clock,
        IReadOnlyDictionary<string, string>? trustedPublicKeys,
        Action publish,
        out string code) => TryPublishPricingArtifact(
            jobId,
            expectedApprovalId: null,
            expectedGrantDigest: null,
            clock,
            trustedPublicKeys,
            publish,
            out code);

    internal bool TryPublishPricingArtifact(
        string jobId,
        string? expectedApprovalId,
        string? expectedGrantDigest,
        TimeProvider clock,
        IReadOnlyDictionary<string, string>? trustedPublicKeys,
        Action publish,
        out string code)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(publish);

        lock (_connLock)
        {
            if (!TryAdmitPricingJobAuthority(
                    jobId,
                    expectedApprovalId,
                    expectedGrantDigest,
                    clock.GetUtcNow(),
                    trustedPublicKeys,
                    out code))
                return false;

            publish();
            code = "pricing_job_authority_active";
            return true;
        }
    }
}
