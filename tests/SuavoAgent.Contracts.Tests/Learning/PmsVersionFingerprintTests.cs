using SuavoAgent.Contracts.Learning;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Learning;

public class PmsVersionFingerprintTests
{
    [Fact]
    public void Matches_SamePmsTypeCaseInsensitive()
    {
        var a = new PmsVersionFingerprint("PioneerRx", "h1", "h2", "v1");
        var b = new PmsVersionFingerprint("pioneerrx", "h1", "h2", "v2");
        Assert.True(a.Matches(b));
    }

    [Fact]
    public void Matches_DifferentSchemaHash_DoesNotMatch()
    {
        var a = new PmsVersionFingerprint("PioneerRx", "h1", "h2", null);
        var b = new PmsVersionFingerprint("PioneerRx", "h1x", "h2", null);
        Assert.False(a.Matches(b));
    }

    [Fact]
    public void Matches_DifferentDialectHash_DoesNotMatch()
    {
        var a = new PmsVersionFingerprint("PioneerRx", "h1", "h2", null);
        var b = new PmsVersionFingerprint("PioneerRx", "h1", "h2x", null);
        Assert.False(a.Matches(b));
    }

    [Fact]
    public void Matches_DifferentProductVersion_StillMatches()
    {
        // ProductVersionString is audit-only; it must not affect equality.
        var a = new PmsVersionFingerprint("PioneerRx", "h1", "h2", "2026.3.1");
        var b = new PmsVersionFingerprint("PioneerRx", "h1", "h2", "2026.3.7");
        Assert.True(a.Matches(b));
    }

    [Fact]
    public void Matches_Null_ReturnsFalse()
    {
        var a = new PmsVersionFingerprint("PioneerRx", "h1", "h2", null);
        Assert.False(a.Matches(null!));
    }
}
