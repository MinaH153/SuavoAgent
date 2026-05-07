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
    [InlineData("Patient: Mina H", "structured_field")]
    [InlineData("InsuranceId ABC12345", "structured_field")]
    [InlineData("NPI: 1234567890", "structured_field")]
    [InlineData("AB1234567", "dea")]
    [InlineData("123 Main Street", "street_address")]
    [InlineData("8005551212", "ndc_or_phone")]
    [InlineData("phone 555-123-4567", "phone")]
    [InlineData("DOB 04/12/1980", "structured_field")]
    [InlineData("Policy RX-4455", "structured_field")]
    [InlineData("MemberId XZ-998812", "structured_field")]
    public void PhiShapes_AreFlagged(string input, string expectedPattern)
    {
        var flagged = PhiPatternGuard.ContainsPotentialPhi(input, out var matched);
        Assert.True(flagged, $"expected '{input}' to be flagged");
        Assert.Equal(expectedPattern, matched);
    }

    [Fact]
    public void SyntheticRealShapedPhiCorpus_IsFlagged()
    {
        const string input = "Patient Jane Rivera DOB 04/12/1980 phone 555-123-4567 email jane.rivera@example.com address 123 Main Street DEA AB1234567 NPI 1234567890 MemberId XZ-998812 Policy RX-4455";

        var flagged = PhiPatternGuard.ContainsPotentialPhi(input, out var matched);

        Assert.True(flagged);
        Assert.NotNull(matched);
    }

    [Fact]
    public void EmptyInput_NotFlagged()
    {
        Assert.False(PhiPatternGuard.ContainsPotentialPhi(string.Empty, out var matched));
        Assert.Null(matched);
    }
}
