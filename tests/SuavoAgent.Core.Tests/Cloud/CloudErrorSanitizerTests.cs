using SuavoAgent.Core.Cloud;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public sealed class CloudErrorSanitizerTests
{
    [Fact]
    public void FromBody_ExtractsSafeJsonError()
    {
        var reason = CloudErrorSanitizer.FromBody("""{"success":false,"error":"Agent not found"}""");

        Assert.Equal("Agent not found", reason);
    }

    [Fact]
    public void FromBody_RedactsUnsafeErrorText()
    {
        var reason = CloudErrorSanitizer.FromBody("""{"error":"patient Jane Doe <script>"}""");

        Assert.Equal("redacted_error_body", reason);
    }

    [Theory]
    [InlineData("""{"error":"Patient Jane Doe not found"}""")]
    [InlineData("""{"error":"Could not find 123 Main Street"}""")]
    [InlineData("""{"error":"Rx 123456 is locked"}""")]
    public void FromBody_RedactsFreeFormPhiLikeEnglishEvenWhenCharactersAreSafe(string body)
    {
        var reason = CloudErrorSanitizer.FromBody(body);

        Assert.Equal("redacted_error_body", reason);
    }

    [Theory]
    [InlineData("""{"error":"invalid_token"}""", "invalid_token")]
    [InlineData("""{"error":"http_401_Agent_not_found"}""", "http_401_Agent_not_found")]
    [InlineData("""{"error":"agent.config.auth_failed"}""", "agent.config.auth_failed")]
    public void FromBody_AllowsCompactOperationalCodes(string body, string expected)
    {
        var reason = CloudErrorSanitizer.FromBody(body);

        Assert.Equal(expected, reason);
    }
}
