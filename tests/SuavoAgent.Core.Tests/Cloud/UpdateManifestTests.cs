using SuavoAgent.Core.Cloud;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public class UpdateManifestTests
{
    private const string LegacyManifest =
        "https://github.com/core.exe|abc123|https://github.com/broker.exe|def456|https://github.com/helper.exe|789012|2.1.0|net8.0|win-x64";
    private const string ValidManifest = LegacyManifest +
        "|https://github.com/watchdog.exe|wd789";

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

    // Cloud and native privileged activation share the same 11/13-field cohort.
    private const string WatchdogManifest = ValidManifest;

    private const string FullCohortManifest = WatchdogManifest +
        "|https://github.com/setup.exe|setup012";

    [Fact]
    public void LegacyNineFieldManifest_IsRejectedBeforeSystemActivation()
    {
        Assert.Null(UpdateManifest.Parse(LegacyManifest));
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
    public void Parse_FullCohortManifest_IncludesMaintenanceHostAndRoundTrips()
    {
        var m = UpdateManifest.Parse(FullCohortManifest);

        Assert.NotNull(m);
        Assert.True(m!.HasWatchdog);
        Assert.True(m.HasMaintenance);
        Assert.Equal("https://github.com/setup.exe", m.MaintenanceUrl);
        Assert.Equal("setup012", m.MaintenanceSha256);
        Assert.Equal(FullCohortManifest, m.ToCanonical());
    }

    [Fact]
    public void Parse_TenFields_ReturnsNull()
    {
        // Only 9 (legacy) or 11 (with watchdog) are valid — 10 is malformed.
        Assert.Null(UpdateManifest.Parse("a|b|c|d|e|f|g|h|i|j"));
    }

    [Fact]
    public void Parse_TwelveFields_ReturnsNull()
    {
        Assert.Null(UpdateManifest.Parse("a|b|c|d|e|f|g|h|i|j|k|l"));
    }

    [Fact]
    public void Parse_WatchdogManifest_EmptyWatchdogField_ReturnsNull()
    {
        Assert.Null(UpdateManifest.Parse(LegacyManifest + "|https://wd|"));
    }

    [Fact]
    public void Parse_FullCohortManifest_EmptyMaintenanceField_ReturnsNull()
    {
        Assert.Null(UpdateManifest.Parse(WatchdogManifest + "|https://setup|"));
    }
}
