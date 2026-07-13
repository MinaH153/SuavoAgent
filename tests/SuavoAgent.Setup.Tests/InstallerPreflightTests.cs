using SuavoAgent.Setup.Preflight;
using Xunit;

namespace SuavoAgent.Setup.Tests;

public sealed class InstallerPreflightTests
{
    [Fact]
    public void PinnedShaIsNotAPlaceholder()
    {
        Assert.NotEqual("REPLACE_WITH_PINNED_SHA256", VcRedistPreflight.Sha256);
        Assert.Matches("^[0-9a-f]{64}$", VcRedistPreflight.Sha256);
    }
}
