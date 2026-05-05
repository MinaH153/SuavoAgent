using SuavoAgent.Helper.Actuation;
using Xunit;

namespace SuavoAgent.Helper.Tests.Actuation;

public sealed class PhiPatternGuardTests
{
    [Theory]
    [InlineData("hello world", false)]
    [InlineData("just a test message", false)]
    [InlineData("foo bar", false)]
    [InlineData("Suavo demo", false)]
    public void PlainText_NotFlagged(string input, bool expectFlagged)
    {
        var actual = PhiPatternGuard.ContainsPotentialPhi(input, out var matched);
        Assert.Equal(expectFlagged, actual);
        if (!expectFlagged) Assert.Null(matched);
    }

    [Theory]
    [InlineData("123-45-6789", "ssn")]
    [InlineData("user@example.com", "email")]
    [InlineData("DOB: 1990-04-12", "structured_field")]
    [InlineData("MRN # 12345", "structured_field")]
    [InlineData("123 Main Street", "street_address")]
    [InlineData("8005551212", "ndc_or_phone")]
    public void PhiShapes_AreFlagged(string input, string expectedPattern)
    {
        var flagged = PhiPatternGuard.ContainsPotentialPhi(input, out var matched);
        Assert.True(flagged, $"expected '{input}' to be flagged");
        Assert.Equal(expectedPattern, matched);
    }

    [Fact]
    public void EmptyInput_NotFlagged()
    {
        Assert.False(PhiPatternGuard.ContainsPotentialPhi(string.Empty, out var matched));
        Assert.Null(matched);
    }
}
