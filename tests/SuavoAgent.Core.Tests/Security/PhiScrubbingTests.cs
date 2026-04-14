using SuavoAgent.Core.Learning;
using Xunit;

namespace SuavoAgent.Core.Tests.Security;

public class PhiScrubbingTests
{
    [Fact]
    public void HmacHash_ProducesConsistentNonPlaintextOutput()
    {
        var hash = PhiScrubber.HmacHash("12345", "test-salt");
        Assert.NotEqual("12345", hash);
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, PhiScrubber.HmacHash("12345", "test-salt"));
    }

    [Fact]
    public void HmacHash_DifferentSalts_ProduceDifferentHashes()
    {
        var hash1 = PhiScrubber.HmacHash("12345", "salt-a");
        var hash2 = PhiScrubber.HmacHash("12345", "salt-b");
        Assert.NotEqual(hash1, hash2);
    }
}
