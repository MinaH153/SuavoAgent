using System.Globalization;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Pricing;

namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private sealed record RecoverablePricingCandidate(
        PricingJobSpec Spec,
        PricingObservationContract Observation,
        string ApprovalId,
        string ApprovalDigest,
        DateTimeOffset AuthorityExpiresAtUtc);

    /// <summary>
    /// Returns a same-job crash/halt recovery candidate only when workbook
    /// identity, modality, the exact installed PIC grant, and freshness still
    /// match. Configuration cannot create recovery authority.
    /// </summary>
    internal PricingJobSpec? GetRecoverablePricingJob(
        string modality,
        string expectedPharmacyId,
        string expectedAgentId,
        string expectedMachineFingerprint,
        DateTimeOffset now,
        string? commandId = null,
        string? exactWorkbookPath = null,
        IReadOnlyDictionary<string, string>? trustedApprovalKeys = null,
        string? expectedCostBasis = null)
    {
        if (modality is not ("sql" or "uia" or "vision"))
            return null;
        if (expectedCostBasis is not null &&
            !PricingApprovalContract.IsSupportedCostBasis(expectedCostBasis))
            return null;
        trustedApprovalKeys ??= RemoteCommandTrust.CreateProductionKeyRegistry();
        now = now.ToUniversalTime();

        List<RecoverablePricingCandidate> candidates;
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                SELECT j.job_id, j.excel_path, j.ndc_column,
                       j.supplier_column, j.cost_column,
                       j.approval_id, j.grant_digest,
                       identity.schema_digest, identity.status_policy_digest,
                       identity.cost_basis, identity.policy_digest,
                       identity.snapshot_contract,
                       identity.authority_approval_id,
                       identity.authority_approval_digest,
                       identity.authority_expires_at_utc
                  FROM pricing_jobs j
                  JOIN pricing_job_input_identity identity
                    ON identity.job_id = j.job_id
                  LEFT JOIN pricing_result_delivery_intents delivery
                    ON delivery.job_id = j.job_id
                 WHERE j.status IN ('running', 'halted')
                   AND identity.modality = @modality
                   AND (@basis IS NULL OR identity.cost_basis = @basis)
                   AND identity.authority_pharmacy_id = @pharmacy
                   AND identity.authority_role = @role
                   AND identity.fresh_until_utc >= @now
                   AND identity.authority_expires_at_utc >= @now
                   AND (@path IS NULL OR j.excel_path = @path)
                   AND ((@command IS NULL AND delivery.job_id IS NULL)
                        OR delivery.command_id = @command)
                 ORDER BY j.updated_at DESC, j.job_id DESC
                 LIMIT 20
                """;
            command.Parameters.AddWithValue("@modality", modality);
            command.Parameters.AddWithValue(
                "@basis",
                (object?)expectedCostBasis ?? DBNull.Value);
            command.Parameters.AddWithValue("@pharmacy", expectedPharmacyId);
            command.Parameters.AddWithValue(
                "@role",
                PricingObservationPolicy.PharmacistInChargeRole);
            command.Parameters.AddWithValue("@now", Utc(now));
            command.Parameters.AddWithValue(
                "@path",
                (object?)exactWorkbookPath ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "@command",
                (object?)commandId ?? DBNull.Value);
            candidates = [];
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                candidates.Add(new(
                    new PricingJobSpec(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.IsDBNull(6) ? null : reader.GetString(6),
                        reader.GetString(9)),
                    new PricingObservationContract(
                        modality,
                        reader.GetString(7),
                        reader.GetString(8),
                        reader.GetString(9),
                        reader.GetString(10),
                        reader.GetString(11),
                        PricingObservationPolicy.DefaultFreshnessWindow),
                    reader.GetString(12),
                    reader.GetString(13),
                    ParseUtc(reader.GetString(14))));
            }
        }

        foreach (var candidate in candidates)
        {
            if (commandId is not null &&
                (!string.Equals(
                    candidate.Spec.ApprovalId,
                    candidate.ApprovalId,
                    StringComparison.Ordinal) ||
                 !FixedApprovalHexEquals(
                    candidate.Spec.GrantDigest,
                    candidate.ApprovalDigest)))
                continue;
            if (!TryGetInstalledPricingAuthority(
                    expectedPharmacyId,
                    expectedAgentId,
                    expectedMachineFingerprint,
                    candidate.Observation,
                    now,
                    trustedApprovalKeys,
                    candidate.ApprovalDigest,
                    out var authority,
                    out _) ||
                authority is null ||
                !string.Equals(
                    authority.ApprovalId,
                    candidate.ApprovalId,
                    StringComparison.Ordinal) ||
                !FixedApprovalHexEquals(
                    authority.ApprovalDigest,
                    candidate.ApprovalDigest) ||
                authority.ExpiresAtUtc != candidate.AuthorityExpiresAtUtc)
                continue;
            return candidate.Spec;
        }
        return null;
    }
}
