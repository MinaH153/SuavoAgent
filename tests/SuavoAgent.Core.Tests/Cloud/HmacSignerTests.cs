using SuavoAgent.Core.Cloud;

namespace SuavoAgent.Core.Tests.Cloud;

public class HmacSignerTests
{
    [Fact]
    public void Sign_Deterministic()
    {
        var signer = new HmacSigner("test-key");
        var sig1 = signer.Sign("2026-01-01T00:00:00Z", "body");
        var sig2 = signer.Sign("2026-01-01T00:00:00Z", "body");
        Assert.Equal(sig1, sig2);
        Assert.NotEmpty(sig1);
    }

    [Fact]
    public void Sign_DifferentKeys_DifferentSigs()
    {
        var s1 = new HmacSigner("key1").Sign("ts", "body");
        var s2 = new HmacSigner("key2").Sign("ts", "body");
        Assert.NotEqual(s1, s2);
    }

    [Fact]
    public void Sign_DifferentBodies_DifferentSigs()
    {
        var signer = new HmacSigner("key");
        Assert.NotEqual(signer.Sign("ts", "a"), signer.Sign("ts", "b"));
    }

    [Fact]
    public void IsWithinReplayWindow_Recent_Accepted()
    {
        Assert.True(HmacSigner.IsWithinReplayWindow(DateTimeOffset.UtcNow.ToString("o"), TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void IsWithinReplayWindow_Old_Rejected()
    {
        var old = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("o");
        Assert.False(HmacSigner.IsWithinReplayWindow(old, TimeSpan.FromSeconds(60)));
    }
}
