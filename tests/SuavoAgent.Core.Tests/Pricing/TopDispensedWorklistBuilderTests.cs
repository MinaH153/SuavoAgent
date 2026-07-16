using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Pricing;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

public sealed class TopDispensedWorklistBuilderTests
{
    [Fact]
    public void Canonicalize_merges_equivalent_ndcs_and_preserves_rank()
    {
        var result = TopDispensedWorklistBuilder.Canonicalize(
            new[]
            {
                new TopDispensedRow("Drug A", "10 mg", "0006-0734-60", 8m),
                new TopDispensedRow("Drug A", "10 mg", "00006073460", 5m),
                new TopDispensedRow("Drug B", "20 mg", "50242-041-21", 9m),
            },
            maximumItems: 500);

        Assert.NotNull(result);
        Assert.Collection(
            result!,
            row =>
            {
                Assert.Equal("00006073460", row.Ndc);
                Assert.Equal(13m, row.TotalDispensed);
            },
            row =>
            {
                Assert.Equal("50242004121", row.Ndc);
                Assert.Equal(9m, row.TotalDispensed);
            });
    }

    [Fact]
    public void Canonicalize_rejects_ambiguous_ndc_instead_of_guessing()
    {
        var result = TopDispensedWorklistBuilder.Canonicalize(
            new[]
            {
                new TopDispensedRow("Drug A", "10 mg", "1234567890", 8m),
            },
            maximumItems: 500);

        Assert.Null(result);
    }
}
