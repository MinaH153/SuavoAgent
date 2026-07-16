using System.Globalization;
using Microsoft.Data.Sqlite;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Cloud;

namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private static void AddProposalParameters(
        SqliteCommand command,
        PricingApprovalProposal proposal,
        DateTimeOffset recordedAt)
    {
        command.Parameters.AddWithValue("@proposal", proposal.ProposalId);
        command.Parameters.AddWithValue("@digest", proposal.ProposalDigest);
        command.Parameters.AddWithValue("@pharmacy", proposal.PharmacyId);
        command.Parameters.AddWithValue("@agent", proposal.AgentId);
        command.Parameters.AddWithValue("@machine", proposal.MachineFingerprint);
        command.Parameters.AddWithValue("@modality", proposal.Modality);
        command.Parameters.AddWithValue("@schema", proposal.SchemaDigest);
        command.Parameters.AddWithValue("@status", proposal.StatusPolicyDigest);
        command.Parameters.AddWithValue("@basis", proposal.CostBasis);
        command.Parameters.AddWithValue("@policy", proposal.PolicyDigest);
        command.Parameters.AddWithValue("@snapshot", proposal.SnapshotContract);
        command.Parameters.AddWithValue("@freshness", proposal.FreshnessSeconds);
        command.Parameters.AddWithValue("@observed", Utc(proposal.ObservedAtUtc));
        command.Parameters.AddWithValue("@expires", Utc(proposal.ExpiresAtUtc));
        command.Parameters.AddWithValue("@recorded", Utc(recordedAt));
    }

    private static void AddGrantParameters(
        SqliteCommand command,
        PricingApprovalGrant grant,
        string grantDigest,
        SignedCommand envelope,
        string commandId,
        DateTimeOffset installedAt)
    {
        command.Parameters.AddWithValue("@approval", grant.ApprovalId);
        command.Parameters.AddWithValue("@proposal", grant.ProposalId);
        command.Parameters.AddWithValue("@proposal_digest", grant.ProposalDigest);
        command.Parameters.AddWithValue("@pharmacy", grant.PharmacyId);
        command.Parameters.AddWithValue("@agent", grant.AgentId);
        command.Parameters.AddWithValue("@machine", grant.MachineFingerprint);
        command.Parameters.AddWithValue("@approver", grant.ApproverId);
        command.Parameters.AddWithValue("@role", grant.ApprovedByRole);
        command.Parameters.AddWithValue("@modality", grant.Modality);
        command.Parameters.AddWithValue("@schema", grant.SchemaDigest);
        command.Parameters.AddWithValue("@status", grant.StatusPolicyDigest);
        command.Parameters.AddWithValue("@basis", grant.CostBasis);
        command.Parameters.AddWithValue("@policy", grant.PolicyDigest);
        command.Parameters.AddWithValue("@snapshot", grant.SnapshotContract);
        command.Parameters.AddWithValue("@freshness", grant.FreshnessSeconds);
        command.Parameters.AddWithValue("@issued", Utc(grant.IssuedAtUtc));
        command.Parameters.AddWithValue("@expires", Utc(grant.ExpiresAtUtc));
        command.Parameters.AddWithValue("@key", grant.KeyId);
        command.Parameters.AddWithValue("@signature", grant.Signature);
        command.Parameters.AddWithValue("@grant_digest", grantDigest);
        command.Parameters.AddWithValue("@command", commandId);
        command.Parameters.AddWithValue("@nonce", envelope.Nonce);
        command.Parameters.AddWithValue("@data_hash", envelope.DataHash);
        command.Parameters.AddWithValue("@installed", Utc(installedAt));
    }

    private static void AddRevocationParameters(
        SqliteCommand command,
        PricingApprovalRevocation revocation,
        string revocationDigest,
        SignedCommand envelope,
        string commandId,
        DateTimeOffset installedAt)
    {
        command.Parameters.AddWithValue("@revocation", revocation.RevocationId);
        command.Parameters.AddWithValue("@approval", revocation.ApprovalId);
        command.Parameters.AddWithValue("@proposal", revocation.ProposalId);
        command.Parameters.AddWithValue("@proposal_digest", revocation.ProposalDigest);
        command.Parameters.AddWithValue("@pharmacy", revocation.PharmacyId);
        command.Parameters.AddWithValue("@agent", revocation.AgentId);
        command.Parameters.AddWithValue("@machine", revocation.MachineFingerprint);
        command.Parameters.AddWithValue("@policy", revocation.PolicyDigest);
        command.Parameters.AddWithValue("@reason", revocation.ReasonCode);
        command.Parameters.AddWithValue("@revoked", Utc(revocation.RevokedAtUtc));
        command.Parameters.AddWithValue("@key", revocation.KeyId);
        command.Parameters.AddWithValue("@signature", revocation.Signature);
        command.Parameters.AddWithValue("@digest", revocationDigest);
        command.Parameters.AddWithValue("@command", commandId);
        command.Parameters.AddWithValue("@nonce", envelope.Nonce);
        command.Parameters.AddWithValue("@data_hash", envelope.DataHash);
        command.Parameters.AddWithValue("@installed", Utc(installedAt));
    }

    private static string? ReadInstalledCommandDigest(
        SqliteTransaction transaction,
        string table,
        string commandId)
    {
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = table switch
        {
            "pricing_approval_grants" => """
                SELECT grant_digest
                  FROM pricing_approval_grants
                 WHERE installed_command_id = @command
                 LIMIT 1
                """,
            "pricing_approval_revocations" => """
                SELECT revocation_digest
                  FROM pricing_approval_revocations
                 WHERE installed_command_id = @command
                 LIMIT 1
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(table)),
        };
        command.Parameters.AddWithValue("@command", commandId);
        return command.ExecuteScalar() as string;
    }

    private static PricingApprovalProposal? ReadProposalById(
        SqliteTransaction transaction,
        string proposalId)
    {
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT proposal_id, proposal_digest, pharmacy_id, agent_id,
                   machine_fingerprint, modality, schema_digest,
                   status_policy_digest, cost_basis, policy_digest,
                   snapshot_contract, freshness_seconds, observed_at_utc,
                   expires_at_utc
              FROM pricing_approval_proposals
             WHERE proposal_id = @proposal
             LIMIT 1
            """;
        command.Parameters.AddWithValue("@proposal", proposalId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadProposal(reader) : null;
    }

    private static PricingApprovalGrant? ReadGrantByApprovalId(
        SqliteTransaction transaction,
        string approvalId)
    {
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT approval_id, proposal_id, proposal_digest, pharmacy_id,
                   agent_id, machine_fingerprint, approver_id, approved_by_role,
                   modality, schema_digest, status_policy_digest, cost_basis,
                   policy_digest, snapshot_contract, freshness_seconds,
                   issued_at_utc, expires_at_utc, key_id, signature
              FROM pricing_approval_grants
             WHERE approval_id = @approval
             LIMIT 1
            """;
        command.Parameters.AddWithValue("@approval", approvalId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadGrant(reader) : null;
    }

    private static PricingApprovalGrant? ReadGrantByProposalId(
        SqliteTransaction transaction,
        string proposalId)
    {
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT approval_id, proposal_id, proposal_digest, pharmacy_id,
                   agent_id, machine_fingerprint, approver_id, approved_by_role,
                   modality, schema_digest, status_policy_digest, cost_basis,
                   policy_digest, snapshot_contract, freshness_seconds,
                   issued_at_utc, expires_at_utc, key_id, signature
              FROM pricing_approval_grants
             WHERE proposal_id = @proposal
             ORDER BY issued_at_utc ASC, approval_id ASC
             LIMIT 1
            """;
        command.Parameters.AddWithValue("@proposal", proposalId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadGrant(reader) : null;
    }

    private static PricingApprovalRevocation? ReadRevocationByApprovalId(
        SqliteTransaction transaction,
        string approvalId)
    {
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT revocation.revocation_id, revocation.approval_id,
                   revocation.proposal_id, revocation.proposal_digest,
                   revocation.pharmacy_id, revocation.agent_id,
                   revocation.machine_fingerprint, revocation.policy_digest,
                   revocation.reason_code, revocation.revoked_at_utc,
                   revocation.key_id, revocation.signature,
                   proposal.cost_basis
              FROM pricing_approval_revocations revocation
              JOIN pricing_approval_proposals proposal
                ON proposal.proposal_id = revocation.proposal_id
             WHERE revocation.approval_id = @approval
             ORDER BY revocation.revoked_at_utc ASC,
                      revocation.revocation_id ASC
             LIMIT 1
            """;
        command.Parameters.AddWithValue("@approval", approvalId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRevocation(reader) : null;
    }

    private static PricingApprovalProposal ReadProposal(
        SqliteDataReader reader,
        int offset = 0) => new(
            PricingApprovalContract.SchemaVersionForCostBasis(
                reader.GetString(offset + 8)),
            reader.GetString(offset),
            reader.GetString(offset + 1),
            reader.GetString(offset + 2),
            reader.GetString(offset + 3),
            reader.GetString(offset + 4),
            reader.GetString(offset + 5),
            reader.GetString(offset + 6),
            reader.GetString(offset + 7),
            reader.GetString(offset + 8),
            reader.GetString(offset + 9),
            reader.GetString(offset + 10),
            reader.GetInt64(offset + 11),
            ParseUtc(reader.GetString(offset + 12)),
            ParseUtc(reader.GetString(offset + 13)));

    private static PricingApprovalGrant ReadGrant(
        SqliteDataReader reader,
        int offset = 0) => new(
            PricingApprovalContract.SchemaVersionForCostBasis(
                reader.GetString(offset + 11)),
            reader.GetString(offset),
            reader.GetString(offset + 1),
            reader.GetString(offset + 2),
            reader.GetString(offset + 3),
            reader.GetString(offset + 4),
            reader.GetString(offset + 5),
            reader.GetString(offset + 6),
            reader.GetString(offset + 7),
            reader.GetString(offset + 8),
            reader.GetString(offset + 9),
            reader.GetString(offset + 10),
            reader.GetString(offset + 11),
            reader.GetString(offset + 12),
            reader.GetString(offset + 13),
            reader.GetInt64(offset + 14),
            ParseUtc(reader.GetString(offset + 15)),
            ParseUtc(reader.GetString(offset + 16)),
            reader.GetString(offset + 17),
            reader.GetString(offset + 18));

    private static PricingApprovalRevocation ReadRevocation(
        SqliteDataReader reader) => new(
            PricingApprovalContract.SchemaVersionForCostBasis(reader.GetString(12)),
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            ParseUtc(reader.GetString(9)),
            reader.GetString(10),
            reader.GetString(11));

    private static string Utc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.ParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static bool CanonicalUuid(string value) =>
        Guid.TryParseExact(value, "D", out var parsed) &&
        string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal);
}
