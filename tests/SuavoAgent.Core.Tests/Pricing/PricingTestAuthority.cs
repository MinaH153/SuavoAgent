using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Tests.Pricing;

internal static class PricingTestAuthority
{
    internal const string PharmacyId = "pharmacy-test";
    internal const string AgentId = "agent-test";
    internal const string MachineFingerprint = "machine-test";
    internal const string KeyId = "pricing-test-key";
    private static readonly ECDsa SigningKey =
        ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly object SigningLock = new();

    internal static IReadOnlyDictionary<string, string> TrustedPublicKeys { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KeyId] = Convert.ToBase64String(
                SigningKey.ExportSubjectPublicKeyInfo()),
        };

    internal static PricingObservationContract Contract(
        string modality = "sql",
        string schemaMarker = "schema-v1",
        string statusMarker = "status-v1",
        string costBasis = PricingObservationPolicy.CostPerUnitBasis,
        TimeSpan? freshness = null)
    {
        var schema = PricingObservationPolicy.Digest(schemaMarker);
        var status = PricingObservationPolicy.Digest(statusMarker);
        var window = freshness ?? PricingObservationPolicy.DefaultFreshnessWindow;
        var policy = PricingApprovalContract.ComputeObservationPolicyDigest(
            modality,
            schema,
            status,
            costBasis,
            PricingObservationPolicy.SnapshotContractV1,
            (long)window.TotalSeconds);
        return new PricingObservationContract(
            modality,
            schema,
            status,
            costBasis,
            policy,
            PricingObservationPolicy.SnapshotContractV1,
            window);
    }

    internal static PricingCostBasisAuthority Authority(
        PricingObservationContract contract,
        DateTimeOffset? expires = null,
        string pharmacyId = PharmacyId)
    {
        var expiry = (expires ?? new DateTimeOffset(
            2099, 1, 1, 0, 0, 0, TimeSpan.Zero)).ToUniversalTime();
        var digest = PricingObservationPolicy.Digest(
            "test-pricing-approval-v1",
            pharmacyId,
            PricingObservationPolicy.PharmacistInChargeRole,
            contract.CostBasis,
            contract.PolicyDigest,
            expiry.ToString("O"));
        return new PricingCostBasisAuthority(
            pharmacyId,
            PricingObservationPolicy.PharmacistInChargeRole,
            contract.CostBasis,
            contract.PolicyDigest,
            "11111111-1111-4111-8111-111111111111",
            digest,
            expiry);
    }

    internal static PricingApprovalProposal Proposal(
        PricingObservationContract contract,
        DateTimeOffset observedAt,
        string pharmacyId = PharmacyId,
        string agentId = AgentId,
        string machineFingerprint = MachineFingerprint)
    {
        var unsigned = new PricingApprovalProposal(
            PricingApprovalContract.SchemaVersion,
            Guid.NewGuid().ToString("D"),
            new string('0', 64),
            pharmacyId,
            agentId,
            machineFingerprint,
            contract.Modality,
            contract.SchemaDigest,
            contract.StatusPolicyDigest,
            contract.CostBasis,
            contract.PolicyDigest,
            contract.SnapshotContract,
            (long)contract.FreshnessWindow.TotalSeconds,
            observedAt.ToUniversalTime(),
            observedAt.ToUniversalTime() + PricingApprovalContract.ProposalLifetime);
        return unsigned with
        {
            ProposalDigest = PricingApprovalContract.ComputeProposalDigest(unsigned),
        };
    }

    internal static PricingApprovalGrant Grant(
        PricingApprovalProposal proposal,
        DateTimeOffset issuedAt,
        DateTimeOffset? expiresAt = null)
    {
        var unsigned = new PricingApprovalGrant(
            PricingApprovalContract.SchemaVersion,
            Guid.NewGuid().ToString("D"),
            proposal.ProposalId,
            proposal.ProposalDigest,
            proposal.PharmacyId,
            proposal.AgentId,
            proposal.MachineFingerprint,
            "pic-test",
            PricingApprovalContract.PharmacistInChargeRole,
            proposal.Modality,
            proposal.SchemaDigest,
            proposal.StatusPolicyDigest,
            proposal.CostBasis,
            proposal.PolicyDigest,
            proposal.SnapshotContract,
            proposal.FreshnessSeconds,
            issuedAt.ToUniversalTime(),
            (expiresAt ?? issuedAt.AddDays(7)).ToUniversalTime(),
            KeyId,
            string.Empty);
        return unsigned with
        {
            Signature = SignInner(PricingApprovalContract.GrantCanonical(unsigned)),
        };
    }

    internal static PricingApprovalProposalReceipt Receipt(
        PricingApprovalProposal proposal,
        DateTimeOffset receivedAt)
    {
        var unsigned = new PricingApprovalProposalReceipt(
            PricingApprovalContract.SchemaVersion,
            proposal.ProposalId,
            proposal.ProposalDigest,
            proposal.PharmacyId,
            proposal.AgentId,
            proposal.MachineFingerprint,
            receivedAt.ToUniversalTime(),
            KeyId,
            string.Empty);
        return unsigned with
        {
            Signature = SignInner(
                PricingApprovalContract.ProposalReceiptCanonical(unsigned)),
        };
    }

    internal static PricingApprovalRevocation Revocation(
        PricingApprovalGrant grant,
        DateTimeOffset revokedAt,
        string reasonCode = "pic_revoked")
    {
        var unsigned = new PricingApprovalRevocation(
            PricingApprovalContract.SchemaVersion,
            Guid.NewGuid().ToString("D"),
            grant.ApprovalId,
            grant.ProposalId,
            grant.ProposalDigest,
            grant.PharmacyId,
            grant.AgentId,
            grant.MachineFingerprint,
            grant.PolicyDigest,
            reasonCode,
            revokedAt.ToUniversalTime(),
            KeyId,
            string.Empty);
        return unsigned with
        {
            Signature = SignInner(
                PricingApprovalContract.RevocationCanonical(unsigned)),
        };
    }

    internal static PricingApprovalGrant InstallApproval(
        AgentStateDb db,
        PricingObservationContract contract,
        DateTimeOffset? now = null,
        DateTimeOffset? expiresAt = null,
        string pharmacyId = PharmacyId,
        string agentId = AgentId,
        string machineFingerprint = MachineFingerprint)
    {
        var issuedAt = (now ?? DateTimeOffset.UtcNow).ToUniversalTime();
        if (!db.RecordPricingCloudAuthorityHeartbeat(
                issuedAt,
                issuedAt,
                out var leaseCode))
            throw new InvalidOperationException(leaseCode);
        var proposal = db.StagePricingApprovalProposal(
            pharmacyId,
            agentId,
            machineFingerprint,
            contract,
            issuedAt,
            out var proposalCode) ?? throw new InvalidOperationException(proposalCode);
        var grant = Grant(proposal, issuedAt, expiresAt);
        var result = InstallGrant(db, grant, issuedAt);
        if (!result.Succeeded) throw new InvalidOperationException(result.Code);
        return grant;
    }

    internal static PricingCostBasisAuthority InstallAuthority(
        AgentStateDb db,
        PricingObservationContract contract,
        DateTimeOffset? now = null,
        DateTimeOffset? expiresAt = null,
        string pharmacyId = PharmacyId,
        string agentId = AgentId,
        string machineFingerprint = MachineFingerprint)
    {
        var evaluatedAt = (now ?? DateTimeOffset.UtcNow).ToUniversalTime();
        if (expiresAt is null &&
            db.TryGetInstalledPricingAuthority(
                pharmacyId,
                agentId,
                machineFingerprint,
                contract,
                evaluatedAt,
                TrustedPublicKeys,
                out var existing,
                out _) &&
            existing is not null)
            return existing;
        var grant = InstallApproval(
            db,
            contract,
            evaluatedAt,
            expiresAt,
            pharmacyId,
            agentId,
            machineFingerprint);
        return PricingObservationPolicy.TryAdmitAuthority(
                   grant,
                   pharmacyId,
                   agentId,
                   machineFingerprint,
                   contract,
                   evaluatedAt,
                   TrustedPublicKeys,
                   out var code)
               ?? throw new InvalidOperationException(code);
    }

    internal static AgentStateDb.PricingApprovalLedgerResult InstallGrant(
        AgentStateDb db,
        PricingApprovalGrant grant,
        DateTimeOffset now)
    {
        var commandId = Guid.NewGuid().ToString("D");
        var dataJson = JsonSerializer.Serialize(new
        {
            schemaVersion = PricingApprovalContract.SchemaVersion,
            commandId,
            grant,
        });
        var envelope = SignedEnvelope(
            PricingApprovalContract.InstallCommandName,
            grant.AgentId,
            grant.MachineFingerprint,
            dataJson,
            now);
        var result = db.ApplyPricingApprovalGrant(
            envelope,
            dataJson,
            commandId,
            grant,
            now,
            TrustedPublicKeys);
        return result;
    }

    internal static AgentStateDb.PricingApprovalLedgerResult InstallRevocation(
        AgentStateDb db,
        PricingApprovalRevocation revocation,
        DateTimeOffset now)
    {
        var commandId = Guid.NewGuid().ToString("D");
        var dataJson = JsonSerializer.Serialize(new
        {
            schemaVersion = PricingApprovalContract.SchemaVersion,
            commandId,
            revocation,
        });
        var envelope = SignedEnvelope(
            PricingApprovalContract.RevokeCommandName,
            revocation.AgentId,
            revocation.MachineFingerprint,
            dataJson,
            now);
        return db.ApplyPricingApprovalRevocation(
            envelope,
            dataJson,
            commandId,
            revocation,
            now,
            TrustedPublicKeys);
    }

    internal static SignedCommand SignedEnvelope(
        string command,
        string agentId,
        string machineFingerprint,
        string dataJson,
        DateTimeOffset now)
    {
        var timestamp = now.ToUniversalTime().ToString("O");
        var nonce = Guid.NewGuid().ToString("D");
        var dataHash = SignedCommandVerifier.ComputeDataHash(dataJson);
        var canonical = RemoteCommandTrust.BuildCommandCanonical(
            command,
            agentId,
            machineFingerprint,
            timestamp,
            nonce,
            dataHash);
        string signature;
        lock (SigningLock)
        {
            signature = Convert.ToBase64String(SigningKey.SignData(
                Encoding.UTF8.GetBytes(canonical),
                HashAlgorithmName.SHA256));
        }
        return new SignedCommand(
            command,
            agentId,
            machineFingerprint,
            timestamp,
            nonce,
            KeyId,
            signature,
            dataHash);
    }

    // Legacy config is deliberately inert; this helper proves that setting it
    // cannot replace an installed signed ledger grant.
    internal static PricingCostBasisApprovalOptions ApprovalOptions(
        PricingObservationContract contract,
        string pharmacyId = PharmacyId,
        DateTimeOffset? expires = null) => new()
    {
        Approved = true,
        SchemaVersion = PricingApprovalContract.SchemaVersion,
        ApprovalId = Guid.NewGuid().ToString("D"),
        PharmacyId = pharmacyId,
        ApproverId = "pic-test",
        ApprovedByRole = PricingApprovalContract.PharmacistInChargeRole,
        CostBasis = contract.CostBasis,
        PolicyDigest = contract.PolicyDigest,
        IssuedAtUtc = DateTimeOffset.UtcNow,
        ExpiresAtUtc = expires ?? DateTimeOffset.UtcNow.AddDays(7),
        KeyId = KeyId,
        Signature = string.Empty,
    };

    private static string SignInner(string canonical)
    {
        lock (SigningLock)
        {
            return Convert.ToBase64String(SigningKey.SignData(
                Encoding.UTF8.GetBytes(canonical),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }
    }
}
