using SuavoAgent.Core.Cloud;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public class UpdateManifestTests
{
    private const string ValidManifest =
        "https://github.com/core.exe|abc123|https://github.com/broker.exe|def456|https://github.com/helper.exe|789012|2.1.0|net8.0|win-x64";

    [Fact]
    public void Parse_ValidManifest_ReturnsRecord()
    {
        var m = UpdateManifest.Parse(ValidManifest);
        Assert.NotNull(m);
        Assert.Equal("2.1.0", m.Version);
        Assert.Equal("abc123", m.CoreSha256);
        Assert.Equal("def456", m.BrokerSha256);
        Assert.Equal("789012", m.HelperSha256);
    }

    [Fact]
    public void Parse_WrongFieldCount_ReturnsNull()
    {
        Assert.Null(UpdateManifest.Parse("a|b|c"));
    }

    [Fact]
    public void Parse_EmptyField_ReturnsNull()
    {
        Assert.Null(UpdateManifest.Parse("a|b|c|d|e|f|g||i"));
    }

    [Fact]
    public void ToCanonical_RoundTrips()
    {
        var m = UpdateManifest.Parse(ValidManifest);
        Assert.Equal(ValidManifest, m!.ToCanonical());
    }

    [Fact]
    public void MatchesRuntime_Correct()
    {
        var m = UpdateManifest.Parse(ValidManifest)!;
        Assert.True(m.MatchesRuntime("net8.0", "win-x64"));
        Assert.False(m.MatchesRuntime("net8.0", "linux-x64"));
    }
}
