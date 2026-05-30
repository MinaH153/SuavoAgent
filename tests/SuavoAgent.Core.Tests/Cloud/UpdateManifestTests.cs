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

    // ---- Self-heal Chunk B: optional Watchdog (back-compat) ----

    private const string WatchdogManifest = ValidManifest +
        "|https://github.com/watchdog.exe|wd789";

    [Fact]
    public void WatchdoglessManifest_HasNoWatchdog_AndCanonicalIsUnchanged()
    {
        var m = UpdateManifest.Parse(ValidManifest)!;
        Assert.False(m.HasWatchdog);
        // CRITICAL: a watchdog-less manifest's canonical must be byte-identical to the legacy form,
        // so existing signatures keep verifying after this change.
        Assert.Equal(ValidManifest, m.ToCanonical());
    }

    [Fact]
    public void Parse_WatchdogManifest_PopulatesWatchdogFields()
    {
        var m = UpdateManifest.Parse(WatchdogManifest);
        Assert.NotNull(m);
        Assert.True(m!.HasWatchdog);
        Assert.Equal("https://github.com/watchdog.exe", m.WatchdogUrl);
        Assert.Equal("wd789", m.WatchdogSha256);
        // The original 9 fields are unchanged.
        Assert.Equal("2.1.0", m.Version);
        Assert.Equal("abc123", m.CoreSha256);
    }

    [Fact]
    public void ToCanonical_WatchdogManifest_RoundTrips()
    {
        var m = UpdateManifest.Parse(WatchdogManifest)!;
        Assert.Equal(WatchdogManifest, m.ToCanonical());
    }

    [Fact]
    public void Parse_TenFields_ReturnsNull()
    {
        // Only 9 (legacy) or 11 (with watchdog) are valid — 10 is malformed.
        Assert.Null(UpdateManifest.Parse("a|b|c|d|e|f|g|h|i|j"));
    }

    [Fact]
    public void Parse_WatchdogManifest_EmptyWatchdogField_ReturnsNull()
    {
        Assert.Null(UpdateManifest.Parse(ValidManifest + "|https://wd|"));
    }
}
