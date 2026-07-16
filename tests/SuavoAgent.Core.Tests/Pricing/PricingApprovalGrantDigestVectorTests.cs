using SuavoAgent.Contracts.Pricing;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

public sealed class PricingApprovalGrantDigestVectorTests
{
    [Fact]
    public void GrantDigest_MatchesCrossPlatformFixedVector()
    {
        var grant = new PricingApprovalGrant(
            1,
            "11111111-1111-4111-8111-111111111111",
            "22222222-2222-4222-8222-222222222222",
            new string('a', 64),
            "pharmacy-vector",
            "agent-vector",
            "machine-vector",
            "pic-vector",
            "pharmacist_in_charge",
            "sql",
            new string('b', 64),
            new string('c', 64),
            "cost_per_unit",
            new string('d', 64),
            "source_policy_snapshot_v1",
            43_200,
            new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero),
            "suavo-cmd-v1",
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAA==");

        Assert.Equal(
            "0ebb660bf48228a2a64980adef47cac5d7c413b01e9ef7649e28e9b28379e1c5",
            PricingApprovalContract.ComputeGrantDigest(grant));
    }
}
