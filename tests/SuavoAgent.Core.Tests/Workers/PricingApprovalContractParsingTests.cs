using System.Text.Json;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Tests.Pricing;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public sealed class PricingApprovalContractParsingTests
{
    private static readonly DateTimeOffset Now = new(
        2026, 7, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ProposalReceiptResponse_RequiresExactUniqueBoundedShape()
    {
        var proposal = PricingTestAuthority.Proposal(
            PricingTestAuthority.Contract(),
            Now);
        var receipt = PricingTestAuthority.Receipt(proposal, Now.AddMinutes(1));
        var exact = JsonSerializer.SerializeToElement(new[] { receipt });

        Assert.True(PricingApprovalResponseContract.TryParseProposalReceipts(
            exact,
            out var parsed,
            out var code));
        Assert.Equal("valid", code);
        Assert.Equal(receipt, Assert.Single(parsed));

        using var duplicate = JsonDocument.Parse($$"""
            [{
              "schemaVersion":1,
              "proposalId":"{{receipt.ProposalId}}",
              "proposalId":"{{receipt.ProposalId}}",
              "proposalDigest":"{{receipt.ProposalDigest}}",
              "pharmacyId":"{{receipt.PharmacyId}}",
              "agentId":"{{receipt.AgentId}}",
              "machineFingerprint":"{{receipt.MachineFingerprint}}",
              "receivedAtUtc":"{{receipt.ReceivedAtUtc:O}}",
              "keyId":"{{receipt.KeyId}}",
              "signature":"{{receipt.Signature}}"
            }]
            """);
        Assert.False(PricingApprovalResponseContract.TryParseProposalReceipts(
            duplicate.RootElement,
            out _,
            out _));

        var tooMany = JsonSerializer.SerializeToElement(
            Enumerable.Repeat(receipt, 21).ToArray());
        Assert.False(PricingApprovalResponseContract.TryParseProposalReceipts(
            tooMany,
            out _,
            out var limitCode));
        Assert.Equal(
            "pricing_approval_proposal_receipts_limit_exceeded",
            limitCode);
    }

    [Fact]
    public void InstallAndRevokeCommands_RejectUnknownOrDuplicateNestedFields()
    {
        var proposal = PricingTestAuthority.Proposal(
            PricingTestAuthority.Contract(),
            Now);
        var grant = PricingTestAuthority.Grant(proposal, Now);
        var commandId = Guid.NewGuid().ToString("D");
        var exactInstall = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = PricingApprovalContract.SchemaVersion,
            commandId,
            grant,
        });
        Assert.True(PricingApprovalCommandContract.TryParseInstall(
            exactInstall,
            out var parsedInstall,
            out var installCode));
        Assert.Equal("valid", installCode);
        Assert.Equal(grant, parsedInstall!.Grant);

        var unknownInstall = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = PricingApprovalContract.SchemaVersion,
            commandId,
            grant,
            ignored = true,
        });
        Assert.False(PricingApprovalCommandContract.TryParseInstall(
            unknownInstall,
            out _,
            out _));

        var revocation = PricingTestAuthority.Revocation(
            grant,
            Now.AddMinutes(1));
        var exactRevoke = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = PricingApprovalContract.SchemaVersion,
            commandId,
            revocation,
        });
        Assert.True(PricingApprovalCommandContract.TryParseRevoke(
            exactRevoke,
            out var parsedRevoke,
            out var revokeCode));
        Assert.Equal("valid", revokeCode);
        Assert.Equal(revocation, parsedRevoke!.Revocation);
    }

    [Fact]
    public void PackageInstall_RequiresV2EnvelopeAndV2PackageGrant()
    {
        var proposal = PricingTestAuthority.Proposal(
            PricingTestAuthority.Contract(
                modality: "uia",
                costBasis: PricingApprovalContract.PackageCostBasis),
            Now);
        var grant = PricingTestAuthority.Grant(proposal, Now);
        var commandId = Guid.NewGuid().ToString("D");
        var exact = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = PricingApprovalContract.PackageSchemaVersion,
            commandId,
            grant,
        });

        Assert.True(PricingApprovalCommandContract.TryParseInstall(
            exact,
            out var parsed,
            out var code));
        Assert.Equal("valid", code);
        Assert.Equal(PricingApprovalContract.PackageSchemaVersion, parsed!.Grant.SchemaVersion);

        var mismatchedEnvelope = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = PricingApprovalContract.SchemaVersion,
            commandId,
            grant,
        });
        Assert.False(PricingApprovalCommandContract.TryParseInstall(
            mismatchedEnvelope,
            out _,
            out _));
    }
}
