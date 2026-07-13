using System.Text.Json;
using SuavoAgent.Contracts.Pricing;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Pricing;

public sealed class PricingApprovalGoldenVectorTests
{
    [Fact]
    public void DotNetContract_MatchesFrozenCrossPlatformGoldenVector()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "pricing-approval-contract-v1.json")));
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            "ECDSA_P256_SHA256_IEEE_P1363",
            root.GetProperty("signatureAlgorithm").GetString());
        Assert.Equal(
            "uint32_little_endian",
            root.GetProperty("policyDigestLengthPrefix").GetString());

        var proposalVector = root.GetProperty("proposal");
        var receiptVector = root.GetProperty("proposalReceipt");
        var grantVector = root.GetProperty("grant");
        var revocationVector = root.GetProperty("revocation");
        var proposal = proposalVector.GetProperty("json")
            .Deserialize<PricingApprovalProposal>()!;
        var receipt = receiptVector.GetProperty("json")
            .Deserialize<PricingApprovalProposalReceipt>()!;
        var grant = grantVector.GetProperty("json")
            .Deserialize<PricingApprovalGrant>()!;
        var revocation = revocationVector.GetProperty("json")
            .Deserialize<PricingApprovalRevocation>()!;
        var trustedKeys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [grant.KeyId] = root.GetProperty("publicKeySpkiBase64").GetString()!,
        };

        Assert.Equal(
            root.GetProperty("policyDigest").GetString(),
            PricingApprovalContract.ComputeObservationPolicyDigest(
                proposal.Modality,
                proposal.SchemaDigest,
                proposal.StatusPolicyDigest,
                proposal.CostBasis,
                proposal.SnapshotContract,
                proposal.FreshnessSeconds));
        Assert.Equal(
            proposalVector.GetProperty("canonical").GetString(),
            PricingApprovalContract.ProposalCanonical(proposal));
        Assert.Equal(
            proposalVector.GetProperty("digest").GetString(),
            PricingApprovalContract.ComputeProposalDigest(proposal));
        Assert.Equal(
            receiptVector.GetProperty("canonical").GetString(),
            PricingApprovalContract.ProposalReceiptCanonical(receipt));
        Assert.Equal(
            receiptVector.GetProperty("digest").GetString(),
            PricingApprovalContract.ComputeProposalReceiptDigest(receipt));
        Assert.Equal(
            grantVector.GetProperty("canonical").GetString(),
            PricingApprovalContract.GrantCanonical(grant));
        Assert.Equal(
            grantVector.GetProperty("digest").GetString(),
            PricingApprovalContract.ComputeGrantDigest(grant));
        Assert.Equal(
            revocationVector.GetProperty("canonical").GetString(),
            PricingApprovalContract.RevocationCanonical(revocation));
        Assert.Equal(
            revocationVector.GetProperty("digest").GetString(),
            PricingApprovalContract.ComputeRevocationDigest(revocation));

        Assert.True(PricingApprovalContract.IsValidProposal(
            proposal,
            proposal.ObservedAtUtc,
            out _));
        Assert.True(PricingApprovalContract.IsValidProposalReceipt(
            receipt,
            proposal,
            receipt.ReceivedAtUtc,
            trustedKeys,
            out _));
        Assert.True(PricingApprovalContract.IsValidGrant(
            grant,
            grant.IssuedAtUtc,
            trustedKeys,
            out _));
        Assert.True(PricingApprovalContract.IsValidRevocation(
            revocation,
            revocation.RevokedAtUtc,
            trustedKeys,
            out _));
        Assert.True(PricingApprovalContract.GrantMatchesProposal(grant, proposal));
        Assert.True(PricingApprovalContract.RevocationMatchesGrant(
            revocation,
            grant));
        Assert.Equal(64, Convert.FromBase64String(receipt.Signature).Length);
        Assert.Equal(64, Convert.FromBase64String(grant.Signature).Length);
        Assert.Equal(64, Convert.FromBase64String(revocation.Signature).Length);
    }
}
