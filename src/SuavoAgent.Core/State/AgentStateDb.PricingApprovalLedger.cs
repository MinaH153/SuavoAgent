using System.Globalization;
using Microsoft.Data.Sqlite;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Pricing;

namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    internal enum PricingApprovalLedgerKind
    {
        Applied,
        Idempotent,
        Conflict,
        Rejected,
    }

    internal sealed record PricingApprovalLedgerResult(
        PricingApprovalLedgerKind Kind,
        string Code,
        string? ApprovalId = null,
        string? PolicyDigest = null)
    {
        internal bool Succeeded =>
            Kind is PricingApprovalLedgerKind.Applied or
                PricingApprovalLedgerKind.Idempotent;
    }

    internal PricingApprovalProposal? StagePricingApprovalProposal(
        string pharmacyId,
        string agentId,
        string machineFingerprint,
        PricingObservationContract observation,
        DateTimeOffset now,
        out string code)
    {
        now = now.ToUniversalTime();
        lock (_connLock)
        {
            var existing = ReadLatestProposalForScope(
                pharmacyId,
                agentId,
                machineFingerprint,
                observation,
                now);
            if (existing is not null)
            {
                code = "pricing_approval_proposal_pending";
                return existing;
            }

            var observedAt = now;
            var unsigned = new PricingApprovalProposal(
                PricingApprovalContract.SchemaVersionForCostBasis(
                    observation.CostBasis),
                Guid.NewGuid().ToString("D"),
                new string('0', 64),
                pharmacyId,
                agentId,
                machineFingerprint,
                observation.Modality,
                observation.SchemaDigest,
                observation.StatusPolicyDigest,
                observation.CostBasis,
                observation.PolicyDigest,
                observation.SnapshotContract,
                (long)observation.FreshnessWindow.TotalSeconds,
                observedAt,
                observedAt + PricingApprovalContract.ProposalLifetime);
            var proposal = unsigned with
            {
                ProposalDigest = PricingApprovalContract.ComputeProposalDigest(unsigned),
            };
            if (!PricingApprovalContract.IsValidProposal(proposal, now, out code))
                return null;

            using var command = _conn.CreateCommand();
            command.CommandText = """
                INSERT INTO pricing_approval_proposals (
                    proposal_id, proposal_digest, pharmacy_id, agent_id,
                    machine_fingerprint, modality, schema_digest,
                    status_policy_digest, cost_basis, policy_digest,
                    snapshot_contract, freshness_seconds, observed_at_utc,
                    expires_at_utc, recorded_at_utc)
                VALUES (
                    @proposal, @digest, @pharmacy, @agent, @machine,
                    @modality, @schema, @status, @basis, @policy,
                    @snapshot, @freshness, @observed, @expires, @recorded)
                """;
            AddProposalParameters(command, proposal, now);
            try
            {
                if (command.ExecuteNonQuery() != 1)
                {
                    code = "pricing_approval_proposal_persist_failed";
                    return null;
                }
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                var converged = ReadLatestProposalForScope(
                    pharmacyId,
                    agentId,
                    machineFingerprint,
                    observation,
                    now);
                code = converged is null
                    ? "pricing_approval_proposal_conflict"
                    : "pricing_approval_proposal_pending";
                return converged;
            }
            code = "pricing_approval_proposal_staged";
            return proposal;
        }
    }

    internal IReadOnlyList<PricingApprovalProposal> GetPendingPricingApprovalProposals(
        int maximum,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string>? trustedPublicKeys = null)
    {
        if (maximum is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(maximum));
        trustedPublicKeys ??= RemoteCommandTrust.CreateProductionKeyRegistry();
        now = now.ToUniversalTime();
        List<PricingApprovalProposal> candidates;
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                SELECT proposal_id, proposal_digest, pharmacy_id, agent_id,
                       machine_fingerprint, modality, schema_digest,
                       status_policy_digest, cost_basis, policy_digest,
                       snapshot_contract, freshness_seconds, observed_at_utc,
                       expires_at_utc
                  FROM pricing_approval_proposals
                 WHERE expires_at_utc > @now
                   AND NOT EXISTS (
                       SELECT 1
                         FROM pricing_approval_proposal_acknowledgements ack
                        WHERE ack.proposal_id = pricing_approval_proposals.proposal_id
                          AND ack.proposal_digest = pricing_approval_proposals.proposal_digest)
                   AND NOT EXISTS (
                       SELECT 1
                         FROM pricing_approval_grants grant
                        WHERE grant.proposal_id = pricing_approval_proposals.proposal_id)
                   AND NOT EXISTS (
                       SELECT 1
                         FROM pricing_approval_pregrant_revocations revocation
                        WHERE revocation.proposal_id = pricing_approval_proposals.proposal_id)
                 ORDER BY observed_at_utc DESC, proposal_id DESC
                 LIMIT @limit
                """;
            command.Parameters.AddWithValue("@now", Utc(now));
            command.Parameters.AddWithValue("@limit", Math.Min(400, maximum * 4));
            candidates = [];
            using var reader = command.ExecuteReader();
            while (reader.Read()) candidates.Add(ReadProposal(reader));
        }

        var pending = new List<PricingApprovalProposal>(maximum);
        foreach (var proposal in candidates)
        {
            if (!PricingApprovalContract.IsValidProposal(proposal, now, out _))
                continue;
            var observation = ObservationFrom(proposal);
            if (TryGetInstalledPricingAuthority(
                    proposal.PharmacyId,
                    proposal.AgentId,
                    proposal.MachineFingerprint,
                    observation,
                    now,
                    trustedPublicKeys,
                    out _,
                    out _))
                continue;
            pending.Add(proposal);
            if (pending.Count == maximum) break;
        }
        return pending;
    }

    internal PricingApprovalLedgerResult ApplyPricingApprovalGrant(
        SignedCommand envelope,
        string rawDataJson,
        string commandId,
        PricingApprovalGrant grant,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string>? trustedPublicKeys = null)
    {
        trustedPublicKeys ??= RemoteCommandTrust.CreateProductionKeyRegistry();
        now = now.ToUniversalTime();
        var envelopeCode = ValidateApprovalEnvelope(
            envelope,
            rawDataJson,
            commandId,
            PricingApprovalContract.InstallCommandName,
            grant.AgentId,
            grant.MachineFingerprint,
            now,
            trustedPublicKeys);
        if (envelopeCode is not null)
            return Rejected(envelopeCode);
        if (!PricingApprovalContract.IsValidGrant(
                grant,
                now,
                trustedPublicKeys,
                out var grantCode))
            return Rejected(grantCode);

        var grantDigest = PricingApprovalContract.ComputeGrantDigest(grant);
        lock (_connLock)
        {
            using var transaction = _conn.BeginTransaction(
                System.Data.IsolationLevel.Serializable);
            var commandMatch = ReadInstalledCommandDigest(
                transaction,
                "pricing_approval_grants",
                commandId);
            if (commandMatch is not null)
            {
                transaction.Commit();
                return FixedApprovalHexEquals(commandMatch, grantDigest)
                    ? Idempotent("pricing_approval_already_installed", grant)
                    : Conflict("pricing_approval_command_conflict");
            }

            var existingGrant = ReadGrantByApprovalId(transaction, grant.ApprovalId);
            if (existingGrant is not null)
            {
                transaction.Commit();
                return FixedApprovalHexEquals(
                        PricingApprovalContract.ComputeGrantDigest(existingGrant),
                        grantDigest)
                    ? Idempotent("pricing_approval_already_installed", grant)
                    : Conflict("pricing_approval_id_conflict");
            }

            var proposal = ReadProposalById(transaction, grant.ProposalId);
            if (proposal is null ||
                !PricingApprovalContract.IsValidProposal(
                    proposal,
                    grant.IssuedAtUtc,
                    out _) ||
                !PricingApprovalContract.GrantMatchesProposal(grant, proposal))
            {
                transaction.Commit();
                return Rejected("pricing_approval_proposal_binding_invalid");
            }

            var preGrantRevocation = ReadPreGrantPricingApprovalRevocation(
                transaction,
                grant.ApprovalId,
                grant.ProposalId);
            if (preGrantRevocation is not null)
            {
                transaction.Commit();
                return PricingApprovalContract.RevocationMatchesGrant(
                        preGrantRevocation,
                        grant)
                    ? Idempotent("pricing_approval_already_revoked", grant)
                    : Conflict("pricing_approval_pregrant_revocation_conflict");
            }

            using var insert = CreateCommand(transaction, """
                INSERT INTO pricing_approval_grants (
                    approval_id, proposal_id, proposal_digest, pharmacy_id,
                    agent_id, machine_fingerprint, approver_id,
                    approved_by_role, modality, schema_digest,
                    status_policy_digest, cost_basis, policy_digest,
                    snapshot_contract, freshness_seconds, issued_at_utc,
                    expires_at_utc, key_id, signature, grant_digest,
                    installed_command_id, installed_envelope_nonce,
                    installed_envelope_data_hash, installed_at_utc)
                VALUES (
                    @approval, @proposal, @proposal_digest, @pharmacy,
                    @agent, @machine, @approver, @role, @modality, @schema,
                    @status, @basis, @policy, @snapshot, @freshness, @issued,
                    @expires, @key, @signature, @grant_digest, @command,
                    @nonce, @data_hash, @installed)
                """);
            AddGrantParameters(insert, grant, grantDigest, envelope, commandId, now);
            try
            {
                insert.ExecuteNonQuery();
                transaction.Commit();
                return Applied("pricing_approval_installed", grant);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                transaction.Rollback();
                return Conflict("pricing_approval_install_conflict");
            }
        }
    }

    internal PricingApprovalLedgerResult ApplyPricingApprovalRevocation(
        SignedCommand envelope,
        string rawDataJson,
        string commandId,
        PricingApprovalRevocation revocation,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string>? trustedPublicKeys = null)
    {
        trustedPublicKeys ??= RemoteCommandTrust.CreateProductionKeyRegistry();
        now = now.ToUniversalTime();
        var envelopeCode = ValidateApprovalEnvelope(
            envelope,
            rawDataJson,
            commandId,
            PricingApprovalContract.RevokeCommandName,
            revocation.AgentId,
            revocation.MachineFingerprint,
            now,
            trustedPublicKeys);
        if (envelopeCode is not null)
            return Rejected(envelopeCode);
        if (!PricingApprovalContract.IsValidRevocation(
                revocation,
                now,
                trustedPublicKeys,
                out var revocationCode))
            return Rejected(revocationCode);

        var revocationDigest = PricingApprovalContract.ComputeRevocationDigest(revocation);
        BeginPricingApprovalRevocation(revocation.ApprovalId);
        try
        {
            using var authorityMutation = EnterPricingAuthorityMutation();
            lock (_connLock)
            {
            using var transaction = _conn.BeginTransaction(
                System.Data.IsolationLevel.Serializable);
            var commandMatch = ReadPricingApprovalRevocationCommandDigest(
                transaction,
                commandId);
            if (commandMatch is not null)
            {
                transaction.Commit();
                var matches = FixedApprovalHexEquals(
                    commandMatch,
                    revocationDigest);
                return matches
                    ? new(
                        PricingApprovalLedgerKind.Idempotent,
                        "pricing_approval_already_revoked",
                        revocation.ApprovalId,
                        revocation.PolicyDigest)
                    : Conflict("pricing_approval_revocation_command_conflict");
            }

            var preGrantRevocation = ReadPreGrantPricingApprovalRevocation(
                transaction,
                revocation.ApprovalId,
                revocation.ProposalId);
            if (preGrantRevocation is not null)
            {
                transaction.Commit();
                var matches = FixedApprovalHexEquals(
                        PricingApprovalContract.ComputeRevocationDigest(
                            preGrantRevocation),
                        revocationDigest);
                return matches
                    ? new(
                        PricingApprovalLedgerKind.Idempotent,
                        "pricing_approval_already_revoked",
                        revocation.ApprovalId,
                        revocation.PolicyDigest)
                    : Conflict("pricing_approval_pregrant_revocation_conflict");
            }

            var grant = ReadGrantByApprovalId(transaction, revocation.ApprovalId);
            if (grant is null)
            {
                if (ReadGrantByProposalId(transaction, revocation.ProposalId) is not null)
                {
                    transaction.Commit();
                    return Rejected("pricing_approval_revocation_binding_invalid");
                }
                var proposal = ReadProposalById(transaction, revocation.ProposalId);
                if (proposal is null ||
                    !PricingApprovalContract.IsValidProposal(
                        proposal,
                        proposal.ObservedAtUtc,
                        out _) ||
                    !PricingRevocationMatchesProposal(revocation, proposal))
                {
                    transaction.Commit();
                    return Rejected("pricing_approval_revocation_binding_invalid");
                }

                try
                {
                    InsertPreGrantPricingApprovalRevocation(
                        transaction,
                        revocation,
                        revocationDigest,
                        envelope,
                        commandId,
                        now);
                    transaction.Commit();
                    return new(
                        PricingApprovalLedgerKind.Applied,
                        "pricing_approval_revoked_pregrant",
                        revocation.ApprovalId,
                        revocation.PolicyDigest);
                }
                catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
                {
                    transaction.Rollback();
                    return Conflict("pricing_approval_pregrant_revocation_conflict");
                }
            }

            if (!PricingApprovalContract.IsValidGrant(
                    grant,
                    grant.IssuedAtUtc,
                    trustedPublicKeys,
                    out _) ||
                !PricingApprovalContract.RevocationMatchesGrant(revocation, grant))
            {
                transaction.Commit();
                return Rejected("pricing_approval_revocation_binding_invalid");
            }

            var existing = ReadRevocationByApprovalId(transaction, revocation.ApprovalId);
            if (existing is not null)
            {
                transaction.Commit();
                var matches = FixedApprovalHexEquals(
                        PricingApprovalContract.ComputeRevocationDigest(existing),
                        revocationDigest);
                return matches
                    ? new(
                        PricingApprovalLedgerKind.Idempotent,
                        "pricing_approval_already_revoked",
                        revocation.ApprovalId,
                        revocation.PolicyDigest)
                    : Conflict("pricing_approval_revocation_conflict");
            }

            using var insert = CreateCommand(transaction, """
                INSERT INTO pricing_approval_revocations (
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
                """);
            AddRevocationParameters(
                insert,
                revocation,
                revocationDigest,
                envelope,
                commandId,
                now);
            try
            {
                insert.ExecuteNonQuery();
                transaction.Commit();
                return new(
                    PricingApprovalLedgerKind.Applied,
                    "pricing_approval_revoked",
                    revocation.ApprovalId,
                    revocation.PolicyDigest);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                transaction.Rollback();
                return Conflict("pricing_approval_revocation_conflict");
            }
            }
        }
        finally
        {
            EndPricingApprovalRevocation(revocation.ApprovalId);
        }
    }

    internal bool TryGetInstalledPricingAuthority(
        string pharmacyId,
        string agentId,
        string machineFingerprint,
        PricingObservationContract observation,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string>? trustedPublicKeys,
        out PricingCostBasisAuthority? authority,
        out string code) => TryGetInstalledPricingAuthority(
            pharmacyId,
            agentId,
            machineFingerprint,
            observation,
            now,
            trustedPublicKeys,
            expectedApprovalDigest: null,
            out authority,
            out code);

    internal bool TryGetInstalledPricingAuthority(
        string pharmacyId,
        string agentId,
        string machineFingerprint,
        PricingObservationContract observation,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string>? trustedPublicKeys,
        string? expectedApprovalDigest,
        out PricingCostBasisAuthority? authority,
        out string code)
    {
        authority = null;
        code = "pricing_cost_basis_approval_required";
        trustedPublicKeys ??= RemoteCommandTrust.CreateProductionKeyRegistry();
        now = now.ToUniversalTime();
        List<(PricingApprovalGrant Grant, PricingApprovalProposal Proposal, bool Revoked)> rows;
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                SELECT grant.approval_id, grant.proposal_id,
                       grant.proposal_digest, grant.pharmacy_id, grant.agent_id,
                       grant.machine_fingerprint, grant.approver_id,
                       grant.approved_by_role, grant.modality, grant.schema_digest,
                       grant.status_policy_digest, grant.cost_basis,
                       grant.policy_digest, grant.snapshot_contract,
                       grant.freshness_seconds, grant.issued_at_utc,
                       grant.expires_at_utc, grant.key_id, grant.signature,
                       proposal.proposal_id, proposal.proposal_digest,
                       proposal.pharmacy_id, proposal.agent_id,
                       proposal.machine_fingerprint, proposal.modality,
                       proposal.schema_digest, proposal.status_policy_digest,
                       proposal.cost_basis, proposal.policy_digest,
                       proposal.snapshot_contract, proposal.freshness_seconds,
                       proposal.observed_at_utc, proposal.expires_at_utc,
                       (EXISTS(
                           SELECT 1 FROM pricing_approval_revocations revocation
                            WHERE revocation.approval_id = grant.approval_id)
                        OR EXISTS(
                           SELECT 1
                             FROM pricing_approval_pregrant_revocations revocation
                            WHERE revocation.approval_id = grant.approval_id
                               OR revocation.proposal_id = grant.proposal_id))
                  FROM pricing_approval_grants grant
                  JOIN pricing_approval_proposals proposal
                    ON proposal.proposal_id = grant.proposal_id
                 WHERE grant.pharmacy_id = @pharmacy
                   AND grant.agent_id = @agent
                   AND grant.machine_fingerprint = @machine
                   AND grant.modality = @modality
                   AND grant.policy_digest = @policy
                 ORDER BY grant.issued_at_utc DESC, grant.approval_id DESC
                 LIMIT 20
                """;
            command.Parameters.AddWithValue("@pharmacy", pharmacyId);
            command.Parameters.AddWithValue("@agent", agentId);
            command.Parameters.AddWithValue("@machine", machineFingerprint);
            command.Parameters.AddWithValue("@modality", observation.Modality);
            command.Parameters.AddWithValue("@policy", observation.PolicyDigest);
            rows = [];
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((
                    ReadGrant(reader, 0),
                    ReadProposal(reader, 19),
                    reader.GetInt32(33) == 1));
            }
        }

        var sawRevoked = HasPreGrantPricingApprovalRevocationForScope(
            pharmacyId,
            agentId,
            machineFingerprint,
            observation);
        var sawExpired = false;
        var sawInvalid = false;
        foreach (var row in rows)
        {
            if (!PricingApprovalContract.GrantMatchesProposal(row.Grant, row.Proposal) ||
                !PricingApprovalContract.IsValidProposal(
                    row.Proposal,
                    row.Grant.IssuedAtUtc,
                    out _) ||
                !PricingApprovalContract.IsValidGrant(
                    row.Grant,
                    row.Grant.IssuedAtUtc,
                    trustedPublicKeys,
                    out _))
            {
                sawInvalid = true;
                continue;
            }
            if (expectedApprovalDigest is not null &&
                !FixedApprovalHexEquals(
                    PricingApprovalContract.ComputeGrantDigest(row.Grant),
                    expectedApprovalDigest))
                continue;
            if (row.Revoked)
            {
                sawRevoked = true;
                continue;
            }
            if (row.Grant.ExpiresAtUtc <= now)
            {
                sawExpired = true;
                continue;
            }
            if (
                PricingObservationPolicy.TryAdmitAuthority(
                    row.Grant,
                    pharmacyId,
                    agentId,
                    machineFingerprint,
                    observation,
                    now,
                    trustedPublicKeys,
                    out code) is not { } admitted)
            {
                sawInvalid = true;
                continue;
            }
            authority = admitted;
            return true;
        }

        code = sawInvalid
            ? "pricing_cost_basis_approval_invalid"
            : sawExpired
                ? "pricing_cost_basis_approval_expired"
                : sawRevoked
                    ? "pricing_cost_basis_approval_revoked"
                    : "pricing_cost_basis_approval_required";
        return false;
    }

}
