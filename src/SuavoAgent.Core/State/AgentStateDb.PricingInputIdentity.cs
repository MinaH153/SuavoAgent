using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Pricing;

namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    internal bool TryBindPricingInputIdentity(
        string jobId,
        string sourceSha256,
        string rowFingerprint,
        PricingObservationContract observation,
        PricingCostBasisAuthority authority,
        DateTimeOffset now,
        out string code)
    {
        now = now.ToUniversalTime();
        if (!IsValidIdentity(jobId, sourceSha256, rowFingerprint, observation, authority, now))
        {
            code = "pricing_observation_contract_invalid";
            return false;
        }

        var snapshotId = ComputeSnapshotId(
            sourceSha256,
            rowFingerprint,
            observation.PolicyDigest,
            authority.ApprovalDigest);
        var observedAt = now.ToString("O", CultureInfo.InvariantCulture);
        var freshUntilValue = now + observation.FreshnessWindow;
        if (freshUntilValue > authority.ExpiresAtUtc)
            freshUntilValue = authority.ExpiresAtUtc;
        var freshUntil = freshUntilValue.ToString("O", CultureInfo.InvariantCulture);

        lock (_connLock)
        {
            using var transaction = _conn.BeginTransaction();
            using (var existing = CreateCommand(transaction, """
                SELECT source_sha256, row_fingerprint, modality, schema_digest,
                       status_policy_digest, cost_basis, policy_digest,
                       snapshot_contract, snapshot_id, observed_at_utc, fresh_until_utc,
                       authority_pharmacy_id, authority_role,
                       authority_approval_id, authority_approval_digest,
                       authority_expires_at_utc
                  FROM pricing_job_input_identity
                 WHERE job_id = @job
                """))
            {
                existing.Parameters.AddWithValue("@job", jobId);
                using var reader = existing.ExecuteReader();
                if (reader.Read())
                {
                    // Legacy identities have NULL contract fields and are never silently blessed.
                    if (Enumerable.Range(0, 16).Any(reader.IsDBNull))
                    {
                        transaction.Commit();
                        code = "pricing_observation_contract_missing_for_existing_identity";
                        return false;
                    }

                    if (!DateTimeOffset.TryParseExact(
                            reader.GetString(10),
                            "O",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind,
                            out var persistedFreshUntil) ||
                        !DateTimeOffset.TryParseExact(
                            reader.GetString(15),
                            "O",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind,
                            out var persistedAuthorityExpiry) ||
                        now > persistedFreshUntil ||
                        now > persistedAuthorityExpiry)
                    {
                        transaction.Commit();
                        code = "pricing_observation_snapshot_stale";
                        return false;
                    }

                    var matches =
                        reader.GetString(0) == sourceSha256 &&
                        reader.GetString(1) == rowFingerprint &&
                        reader.GetString(2) == observation.Modality &&
                        reader.GetString(3) == observation.SchemaDigest &&
                        reader.GetString(4) == observation.StatusPolicyDigest &&
                        reader.GetString(5) == observation.CostBasis &&
                        reader.GetString(6) == observation.PolicyDigest &&
                        reader.GetString(7) == observation.SnapshotContract &&
                        reader.GetString(8) == snapshotId &&
                        reader.GetString(11) == authority.PharmacyId &&
                        reader.GetString(12) == authority.ApprovedByRole &&
                        reader.GetString(13) == authority.ApprovalId &&
                        reader.GetString(14) == authority.ApprovalDigest &&
                        reader.GetString(15) == authority.ExpiresAtUtc.ToString("O", CultureInfo.InvariantCulture);
                    transaction.Commit();
                    code = matches
                        ? "pricing_observation_identity_matched"
                        : "pricing_observation_contract_conflict";
                    return matches;
                }
            }

            // Do not silently bless legacy row results against whatever file/policy happens to be
            // present on the first upgraded resume.
            using (var legacy = CreateCommand(transaction, """
                SELECT 1 FROM pricing_results WHERE job_id = @job LIMIT 1
                """))
            {
                legacy.Parameters.AddWithValue("@job", jobId);
                if (legacy.ExecuteScalar() is not null)
                {
                    transaction.Commit();
                    code = "pricing_observation_identity_missing_for_existing_results";
                    return false;
                }
            }

            using var insert = CreateCommand(transaction, """
                INSERT INTO pricing_job_input_identity (
                    job_id, source_sha256, row_fingerprint, modality, schema_digest,
                    status_policy_digest, cost_basis, policy_digest, snapshot_contract,
                    snapshot_id, observed_at_utc, fresh_until_utc,
                    authority_pharmacy_id, authority_role, authority_approval_id,
                    authority_approval_digest, authority_expires_at_utc
                ) VALUES (
                    @job, @source, @rows, @modality, @schema, @status, @basis, @policy,
                    @snapshot_contract, @snapshot, @observed, @fresh,
                    @pharmacy, @role, @approval_id, @approval, @authority_expiry
                )
                """);
            insert.Parameters.AddWithValue("@job", jobId);
            insert.Parameters.AddWithValue("@source", sourceSha256);
            insert.Parameters.AddWithValue("@rows", rowFingerprint);
            insert.Parameters.AddWithValue("@modality", observation.Modality);
            insert.Parameters.AddWithValue("@schema", observation.SchemaDigest);
            insert.Parameters.AddWithValue("@status", observation.StatusPolicyDigest);
            insert.Parameters.AddWithValue("@basis", observation.CostBasis);
            insert.Parameters.AddWithValue("@policy", observation.PolicyDigest);
            insert.Parameters.AddWithValue("@snapshot_contract", observation.SnapshotContract);
            insert.Parameters.AddWithValue("@snapshot", snapshotId);
            insert.Parameters.AddWithValue("@observed", observedAt);
            insert.Parameters.AddWithValue("@fresh", freshUntil);
            insert.Parameters.AddWithValue("@pharmacy", authority.PharmacyId);
            insert.Parameters.AddWithValue("@role", authority.ApprovedByRole);
            insert.Parameters.AddWithValue("@approval_id", authority.ApprovalId);
            insert.Parameters.AddWithValue("@approval", authority.ApprovalDigest);
            insert.Parameters.AddWithValue(
                "@authority_expiry",
                authority.ExpiresAtUtc.ToString("O", CultureInfo.InvariantCulture));
            try
            {
                if (insert.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("pricing_observation_identity_insert_failed");
            }
            catch (SqliteException)
            {
                transaction.Rollback();
                code = "pricing_observation_identity_insert_failed";
                return false;
            }
            transaction.Commit();
            code = "pricing_observation_identity_bound";
            return true;
        }
    }

    private static bool IsValidIdentity(
        string jobId,
        string sourceSha256,
        string rowFingerprint,
        PricingObservationContract observation,
        PricingCostBasisAuthority authority,
        DateTimeOffset now)
    {
        if (!SafePricingEvidenceId.IsMatch(jobId) ||
            !IsLowerHexSha256(sourceSha256) ||
            !IsLowerHexSha256(rowFingerprint) ||
            observation.Modality is not ("sql" or "uia" or "vision") ||
            !IsLowerHexSha256(observation.SchemaDigest) ||
            !IsLowerHexSha256(observation.StatusPolicyDigest) ||
            !PricingApprovalContract.IsSupportedCostBasis(observation.CostBasis) ||
            !IsLowerHexSha256(observation.PolicyDigest) ||
            observation.SnapshotContract != PricingApprovalContract
                .SnapshotContractForCostBasis(observation.CostBasis) ||
            (observation.CostBasis == PricingApprovalContract.PackageCostBasis &&
             observation.Modality != "uia") ||
            observation.FreshnessWindow <= TimeSpan.Zero ||
            observation.FreshnessWindow > TimeSpan.FromHours(24) ||
            !SafePricingEvidenceId.IsMatch(authority.PharmacyId) ||
            authority.ApprovedByRole != PricingObservationPolicy.PharmacistInChargeRole ||
            authority.CostBasis != observation.CostBasis ||
            authority.PolicyDigest != observation.PolicyDigest ||
            !IsCanonicalPricingApprovalId(authority.ApprovalId) ||
            !IsLowerHexSha256(authority.ApprovalDigest) ||
            authority.ExpiresAtUtc <= now)
            return false;
        return true;
    }

    private static string ComputeSnapshotId(params string[] values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var value in values)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            hash.AppendData(bytes);
            hash.AppendData([0]);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool IsLowerHexSha256(string value)
    {
        if (value is not { Length: 64 }) return false;
        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }
        return true;
    }

    private static bool IsCanonicalPricingApprovalId(string value) =>
        value is { Length: 36 } &&
        Guid.TryParseExact(value, "D", out var parsed) &&
        string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal) &&
        value[14] == '4' && value[19] is '8' or '9' or 'a' or 'b';
}
