// tests/SuavoAgent.Setup.Tests/ConsoleInstallerPreflightTests.cs
using SuavoAgent.Setup.Preflight;
using Xunit;

namespace SuavoAgent.Setup.Tests;

public class ConsoleInstallerPreflightTests
{
    [Fact]
    public void Pinned_sha_is_not_a_placeholder()
    {
        Assert.NotEqual("REPLACE_WITH_PINNED_SHA256", VcRedistPreflight.Sha256);
        Assert.Matches("^[0-9a-f]{64}$", VcRedistPreflight.Sha256);
    }
}
