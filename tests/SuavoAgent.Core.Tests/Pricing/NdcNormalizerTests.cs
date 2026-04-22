using SuavoAgent.Core.Pricing;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

public class NdcNormalizerTests
{
    [Theory]
    // 5-4-2 hyphenated — pass-through
    [InlineData("50242-0041-21", "50242004121", NdcNormalizer.NdcShape.Format542)]
    [InlineData("55111-0645-01", "55111064501", NdcNormalizer.NdcShape.Format542)]
    [InlineData("00093-5124-01", "00093512401", NdcNormalizer.NdcShape.Format542)]
    // 5-3-2 hyphenated — pad product segment to 4 digits
    [InlineData("50242-041-21", "50242004121", NdcNormalizer.NdcShape.Format532)]
    [InlineData("16714-234-01", "16714023401", NdcNormalizer.NdcShape.Format532)]
    // 4-4-2 hyphenated — prepend '0' to the 4-digit labeler so it becomes 5 digits (HIPAA rule)
    [InlineData("0006-0734-60", "00006073460", NdcNormalizer.NdcShape.Format442)]
    [InlineData("1234-5678-90", "01234567890", NdcNormalizer.NdcShape.Format442)]
    // 11-digit unhyphenated — already canonical
    [InlineData("50242004121", "50242004121", NdcNormalizer.NdcShape.Digits11)]
    public void Normalize_KnownShapes_ReturnsCanonical11(string input, string expected, NdcNormalizer.NdcShape expectedShape)
    {
        var outcome = NdcNormalizer.Normalize(input);

        Assert.True(outcome.Ok, outcome.Reason);
        Assert.Equal(expected, outcome.Canonical11);
        Assert.Equal(expectedShape, outcome.Shape);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_Empty_Fails(string? input)
    {
        var outcome = NdcNormalizer.Normalize(input);
        Assert.False(outcome.Ok);
    }

    [Fact]
    public void Normalize_TenDigitsUnhyphenated_FailsAsAmbiguous()
    {
        // 10 unhyphenated digits cannot be disambiguated between 4-4-2, 5-3-2, 5-4-2 strip-forms.
        // Force-failing here is intentional; prior implementation silently pad-left-prepended a zero.
        var outcome = NdcNormalizer.Normalize("5024204121");

        Assert.False(outcome.Ok);
        Assert.Equal(NdcNormalizer.NdcShape.Digits10, outcome.Shape);
        Assert.Contains("Ambiguous", outcome.Reason ?? "");
    }

    [Theory]
    [InlineData("abc-def-gh")]
    [InlineData("12345-XXXX-21")]
    [InlineData("not an ndc")]
    public void Normalize_NonDigits_Fails(string input)
    {
        var outcome = NdcNormalizer.Normalize(input);
        Assert.False(outcome.Ok);
    }

    [Theory]
    [InlineData("12-34-56")]         // 2-2-2 — not a known shape
    [InlineData("123456-789-01")]    // 6-3-2 — labeler too long
    [InlineData("50242-00041-21")]   // 5-5-2 — product too long
    [InlineData("50242-0041-213")]   // 5-4-3 — package too long
    [InlineData("50242-0041")]       // only 2 segments
    [InlineData("50242-0041-21-00")] // 4 segments
    public void Normalize_BadSegmentLengths_Fails(string input)
    {
        var outcome = NdcNormalizer.Normalize(input);
        Assert.False(outcome.Ok);
    }

    [Fact]
    public void TryNormalize_ReturnsNullForFailures()
    {
        Assert.Null(NdcNormalizer.TryNormalize(null));
        Assert.Null(NdcNormalizer.TryNormalize(""));
        Assert.Null(NdcNormalizer.TryNormalize("5024204121"));      // ambiguous 10
        Assert.Equal("50242004121", NdcNormalizer.TryNormalize("50242-041-21"));
    }
}
