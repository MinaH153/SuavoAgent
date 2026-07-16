using System.Security.Cryptography;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Pricing;

namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private static PricingObservationContract ObservationFrom(
        PricingApprovalProposal proposal) => new(
            proposal.Modality,
            proposal.SchemaDigest,
            proposal.StatusPolicyDigest,
            proposal.CostBasis,
            proposal.PolicyDigest,
            proposal.SnapshotContract,
            TimeSpan.FromSeconds(proposal.FreshnessSeconds));

    private static PricingApprovalLedgerResult Applied(
        string code,
        PricingApprovalGrant grant) => new(
            PricingApprovalLedgerKind.Applied,
            code,
            grant.ApprovalId,
            grant.PolicyDigest);

    private static PricingApprovalLedgerResult Idempotent(
        string code,
        PricingApprovalGrant grant) => new(
            PricingApprovalLedgerKind.Idempotent,
            code,
            grant.ApprovalId,
            grant.PolicyDigest);

    private static PricingApprovalLedgerResult Conflict(string code) =>
        new(PricingApprovalLedgerKind.Conflict, code);

    private static PricingApprovalLedgerResult Rejected(string code) =>
        new(PricingApprovalLedgerKind.Rejected, code);

    private static bool FixedApprovalHexEquals(string? left, string? right)
    {
        if (!LowerHex64(left) || !LowerHex64(right)) return false;
        var leftBytes = Convert.FromHexString(left!);
        var rightBytes = Convert.FromHexString(right!);
        try { return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes); }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private static bool LowerHex64(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    internal bool TryRecordPricingApprovalProposalReceipt(
        PricingApprovalProposalReceipt receipt,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string>? trustedPublicKeys,
        out string code)
    {
        code = "pricing_approval_proposal_not_found";
        trustedPublicKeys ??=
            SuavoAgent.Contracts.Maintenance.RemoteCommandTrust
                .CreateProductionKeyRegistry();
        now = now.ToUniversalTime();
        lock (_connLock)
        {
            using var transaction = _conn.BeginTransaction(
                System.Data.IsolationLevel.Serializable);
            var proposal = ReadProposalById(transaction, receipt.ProposalId);
            if (proposal is null ||
                !PricingApprovalContract.IsValidProposalReceipt(
                    receipt,
                    proposal,
                    now,
                    trustedPublicKeys,
                    out code))
            {
                transaction.Commit();
                return false;
            }

            var receiptDigest = PricingApprovalContract
                .ComputeProposalReceiptDigest(receipt);
            using (var existing = CreateCommand(transaction, """
                SELECT receipt_digest
                  FROM pricing_approval_proposal_acknowledgements
                 WHERE proposal_id = @proposal
                 LIMIT 1
                """))
            {
                existing.Parameters.AddWithValue("@proposal", receipt.ProposalId);
                if (existing.ExecuteScalar() is string persisted)
                {
                    transaction.Commit();
                    code = FixedApprovalHexEquals(persisted, receiptDigest)
                        ? "pricing_approval_proposal_already_acknowledged"
                        : "pricing_approval_proposal_receipt_conflict";
                    return FixedApprovalHexEquals(persisted, receiptDigest);
                }
            }

            using var insert = CreateCommand(transaction, """
                INSERT INTO pricing_approval_proposal_acknowledgements (
                    proposal_id, proposal_digest, pharmacy_id, agent_id,
                    machine_fingerprint, received_at_utc, key_id, signature,
                    receipt_digest, recorded_at_utc)
                VALUES (
                    @proposal, @proposal_digest, @pharmacy, @agent, @machine,
                    @received, @key, @signature, @receipt_digest, @recorded)
                """);
            insert.Parameters.AddWithValue("@proposal", receipt.ProposalId);
            insert.Parameters.AddWithValue(
                "@proposal_digest",
                receipt.ProposalDigest);
            insert.Parameters.AddWithValue("@pharmacy", receipt.PharmacyId);
            insert.Parameters.AddWithValue("@agent", receipt.AgentId);
            insert.Parameters.AddWithValue(
                "@machine",
                receipt.MachineFingerprint);
            insert.Parameters.AddWithValue("@received", Utc(receipt.ReceivedAtUtc));
            insert.Parameters.AddWithValue("@key", receipt.KeyId);
            insert.Parameters.AddWithValue("@signature", receipt.Signature);
            insert.Parameters.AddWithValue("@receipt_digest", receiptDigest);
            insert.Parameters.AddWithValue("@recorded", Utc(now));
            try
            {
                insert.ExecuteNonQuery();
                transaction.Commit();
                code = "pricing_approval_proposal_acknowledged";
                return true;
            }
            catch (Microsoft.Data.Sqlite.SqliteException exception)
                when (exception.SqliteErrorCode == 19)
            {
                transaction.Rollback();
                code = "pricing_approval_proposal_receipt_conflict";
                return false;
            }
        }
    }

    private static string? ValidateApprovalEnvelope(
        SignedCommand envelope,
        string rawDataJson,
        string commandId,
        string expectedCommand,
        string expectedAgentId,
        string expectedMachineFingerprint,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string> trustedPublicKeys)
    {
        if (!CanonicalUuid(commandId) ||
            envelope.Command != expectedCommand ||
            envelope.AgentId != expectedAgentId ||
            envelope.MachineFingerprint != expectedMachineFingerprint ||
            !FixedApprovalHexEquals(
                envelope.DataHash,
                SignedCommandVerifier.ComputeDataHash(rawDataJson)))
            return "pricing_approval_envelope_binding_invalid";
        var verifier = new SignedCommandVerifier(
            trustedPublicKeys.ToDictionary(
                item => item.Key,
                item => item.Value,
                StringComparer.Ordinal),
            expectedAgentId,
            expectedMachineFingerprint,
            new FixedTimeProvider(now));
        return verifier.Verify(envelope, consumeNonce: false).IsValid
            ? null
            : "pricing_approval_envelope_signature_invalid";
    }

    private PricingApprovalProposal? ReadLatestProposalForScope(
        string pharmacyId,
        string agentId,
        string machineFingerprint,
        PricingObservationContract observation,
        DateTimeOffset now)
    {
        using var command = _conn.CreateCommand();
        command.CommandText = """
            SELECT proposal_id, proposal_digest, pharmacy_id, agent_id,
                   machine_fingerprint, modality, schema_digest,
                   status_policy_digest, cost_basis, policy_digest,
                   snapshot_contract, freshness_seconds, observed_at_utc,
                   expires_at_utc
              FROM pricing_approval_proposals
             WHERE pharmacy_id = @pharmacy
               AND agent_id = @agent
               AND machine_fingerprint = @machine
               AND modality = @modality
               AND schema_digest = @schema
               AND status_policy_digest = @status
               AND cost_basis = @basis
               AND policy_digest = @policy
               AND snapshot_contract = @snapshot
               AND freshness_seconds = @freshness
               AND expires_at_utc > @now
               AND NOT EXISTS (
                   SELECT 1
                     FROM pricing_approval_grants grant
                    WHERE grant.proposal_id = pricing_approval_proposals.proposal_id)
               AND NOT EXISTS (
                   SELECT 1
                     FROM pricing_approval_pregrant_revocations revocation
                    WHERE revocation.proposal_id = pricing_approval_proposals.proposal_id)
             ORDER BY observed_at_utc DESC, proposal_id DESC
             LIMIT 1
            """;
        command.Parameters.AddWithValue("@pharmacy", pharmacyId);
        command.Parameters.AddWithValue("@agent", agentId);
        command.Parameters.AddWithValue("@machine", machineFingerprint);
        command.Parameters.AddWithValue("@modality", observation.Modality);
        command.Parameters.AddWithValue("@schema", observation.SchemaDigest);
        command.Parameters.AddWithValue("@status", observation.StatusPolicyDigest);
        command.Parameters.AddWithValue("@basis", observation.CostBasis);
        command.Parameters.AddWithValue("@policy", observation.PolicyDigest);
        command.Parameters.AddWithValue("@snapshot", observation.SnapshotContract);
        command.Parameters.AddWithValue(
            "@freshness",
            (long)observation.FreshnessWindow.TotalSeconds);
        command.Parameters.AddWithValue("@now", Utc(now));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var proposal = ReadProposal(reader);
        return PricingApprovalContract.IsValidProposal(proposal, now, out _)
            ? proposal
            : null;
    }
}
