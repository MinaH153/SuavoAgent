using Microsoft.Data.Sqlite;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Pricing;

namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private static string? ReadPricingApprovalRevocationCommandDigest(
        SqliteTransaction transaction,
        string commandId)
    {
        var installed = ReadInstalledCommandDigest(
            transaction,
            "pricing_approval_revocations",
            commandId);
        if (installed is not null) return installed;

        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT revocation_digest
              FROM pricing_approval_pregrant_revocations
             WHERE installed_command_id = @command
             LIMIT 1
            """;
        command.Parameters.AddWithValue("@command", commandId);
        return command.ExecuteScalar() as string;
    }

    private static PricingApprovalRevocation? ReadPreGrantPricingApprovalRevocation(
        SqliteTransaction transaction,
        string approvalId,
        string proposalId)
    {
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT revocation_id, approval_id, proposal_id, proposal_digest,
                   pharmacy_id, agent_id, machine_fingerprint, policy_digest,
                   reason_code, revoked_at_utc, key_id, signature
              FROM pricing_approval_pregrant_revocations
             WHERE approval_id = @approval OR proposal_id = @proposal
             ORDER BY revoked_at_utc ASC, revocation_id ASC
             LIMIT 1
            """;
        command.Parameters.AddWithValue("@approval", approvalId);
        command.Parameters.AddWithValue("@proposal", proposalId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRevocation(reader) : null;
    }

    private static bool PricingRevocationMatchesProposal(
        PricingApprovalRevocation revocation,
        PricingApprovalProposal proposal) =>
        revocation.ProposalId == proposal.ProposalId &&
        FixedApprovalHexEquals(
            revocation.ProposalDigest,
            proposal.ProposalDigest) &&
        revocation.PharmacyId == proposal.PharmacyId &&
        revocation.AgentId == proposal.AgentId &&
        revocation.MachineFingerprint == proposal.MachineFingerprint &&
        FixedApprovalHexEquals(revocation.PolicyDigest, proposal.PolicyDigest) &&
        revocation.RevokedAtUtc >=
            proposal.ObservedAtUtc - PricingApprovalContract.MaximumFutureSkew;

    private static void InsertPreGrantPricingApprovalRevocation(
        SqliteTransaction transaction,
        PricingApprovalRevocation revocation,
        string revocationDigest,
        SignedCommand envelope,
        string commandId,
        DateTimeOffset installedAt)
    {
        using var insert = transaction.Connection!.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO pricing_approval_pregrant_revocations (
                revocation_id, approval_id, proposal_id, proposal_digest,
                pharmacy_id, agent_id, machine_fingerprint, policy_digest,
                reason_code, revoked_at_utc, key_id, signature,
                revocation_digest, installed_command_id,
                installed_envelope_nonce, installed_envelope_data_hash,
                installed_at_utc)
            VALUES (
                @revocation, @approval, @proposal, @proposal_digest,
                @pharmacy, @agent, @machine, @policy, @reason, @revoked,
                @key, @signature, @digest, @command, @nonce, @data_hash,
                @installed)
            """;
        AddRevocationParameters(
            insert,
            revocation,
            revocationDigest,
            envelope,
            commandId,
            installedAt);
        insert.ExecuteNonQuery();
    }

    private bool HasPreGrantPricingApprovalRevocationForScope(
        string pharmacyId,
        string agentId,
        string machineFingerprint,
        PricingObservationContract observation)
    {
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                SELECT 1
                  FROM pricing_approval_pregrant_revocations revocation
                  JOIN pricing_approval_proposals proposal
                    ON proposal.proposal_id = revocation.proposal_id
                 WHERE revocation.pharmacy_id = @pharmacy
                   AND revocation.agent_id = @agent
                   AND revocation.machine_fingerprint = @machine
                   AND revocation.policy_digest = @policy
                   AND proposal.modality = @modality
                   AND proposal.schema_digest = @schema
                   AND proposal.status_policy_digest = @status
                   AND proposal.cost_basis = @basis
                   AND proposal.snapshot_contract = @snapshot
                   AND proposal.freshness_seconds = @freshness
                 LIMIT 1
                """;
            command.Parameters.AddWithValue("@pharmacy", pharmacyId);
            command.Parameters.AddWithValue("@agent", agentId);
            command.Parameters.AddWithValue("@machine", machineFingerprint);
            command.Parameters.AddWithValue("@policy", observation.PolicyDigest);
            command.Parameters.AddWithValue("@modality", observation.Modality);
            command.Parameters.AddWithValue("@schema", observation.SchemaDigest);
            command.Parameters.AddWithValue("@status", observation.StatusPolicyDigest);
            command.Parameters.AddWithValue("@basis", observation.CostBasis);
            command.Parameters.AddWithValue("@snapshot", observation.SnapshotContract);
            command.Parameters.AddWithValue(
                "@freshness",
                (long)observation.FreshnessWindow.TotalSeconds);
            return command.ExecuteScalar() is not null;
        }
    }
}
