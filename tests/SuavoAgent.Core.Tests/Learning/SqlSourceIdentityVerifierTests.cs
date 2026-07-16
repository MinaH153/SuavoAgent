using SuavoAgent.Core.Learning;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

public sealed class SqlSourceIdentityVerifierTests
{
    [Fact]
    public void FixedDigestEquals_AcceptsOnlyExactSixtyFourByteHexDigest()
    {
        var digest = new string('a', 64);

        Assert.True(SqlSourceIdentityVerifier.FixedDigestEquals(digest, digest));
        Assert.False(SqlSourceIdentityVerifier.FixedDigestEquals(digest, new string('b', 64)));
        Assert.False(SqlSourceIdentityVerifier.FixedDigestEquals(digest[..63], digest));
        Assert.False(SqlSourceIdentityVerifier.FixedDigestEquals(digest, digest + "0"));
        Assert.False(SqlSourceIdentityVerifier.FixedDigestEquals(new string('g', 64), digest));
        Assert.False(SqlSourceIdentityVerifier.FixedDigestEquals(digest, new string('z', 64)));
    }
}
