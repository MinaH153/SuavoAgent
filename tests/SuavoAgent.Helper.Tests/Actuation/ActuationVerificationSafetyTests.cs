using SuavoAgent.Helper.Actuation;
using Xunit;

namespace SuavoAgent.Helper.Tests.Actuation;

public sealed class ActuationVerificationSafetyTests
{
    [Theory]
    [MemberData(nameof(UnverifiedReadbacks))]
    public void TypeReadback_FailsClosedWithoutPositiveMatch(string?[] readbacks)
        => Assert.False(SendInputDriver.IsTypeReadbackVerified("rx123", readbacks));

    public static IEnumerable<object[]> UnverifiedReadbacks()
    {
        yield return new object[] { Array.Empty<string?>() };
        yield return new object[] { new string?[] { null, null } };
        yield return new object[] { new[] { "", "   " } };
        yield return new object[] { new[] { "rx12", "different" } };
    }

    [Fact]
    public void TypeReadback_AllowsOnlyPositiveNormalizedMatch()
        => Assert.True(SendInputDriver.IsTypeReadbackVerified(
            "RX-123",
            new string?[] { null, "Rx 123" }));

    [Theory]
    [InlineData("SaveButton", "SaveButton")]
    [InlineData("num7Button", "num7Button")]
    [InlineData("patient_123456789", "")]
    [InlineData("550e8400-e29b-41d4-a716-446655440000", "")]
    [InlineData("bad id", "")]
    [InlineData("_dynamic", "")]
    [InlineData("", "")]
    public void DiscoveryAutomationId_EmitsOnlyBoundedStructuralTokens(string input, string expected)
        => Assert.Equal(expected, UiaLabelResolver.SanitizeAutomationId(input));
}
