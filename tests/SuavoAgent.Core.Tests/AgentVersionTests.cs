using SuavoAgent.Core;
using Xunit;

namespace SuavoAgent.Core.Tests;

/// <summary>
/// Bug A regression (2026-06-03): the agent must report the STAMPED ASSEMBLY version, not appsettings.json
/// (which the OTA swap never rewrites — a freshly-OTA'd v3.19.0 binary kept reporting "3.18.4").
/// </summary>
public class AgentVersionTests
{
    [Theory]
    [InlineData("3.19.0", "3.18.4", "3.19.0")]        // stamped release wins over the stale config value
    [InlineData("3.19.0", null, "3.19.0")]            // stamped release, no config
    [InlineData("0.0.0", "3.13.9-dev", "3.13.9-dev")] // local dev build (unstamped) → fall back to config
    [InlineData(null, "3.13.9-dev", "3.13.9-dev")]    // no assembly stamp → config
    [InlineData("", "3.13.9-dev", "3.13.9-dev")]      // blank assembly stamp → config
    [InlineData(null, null, "unknown")]               // nothing available
    [InlineData("0.0.0", null, "unknown")]            // local build with no config → unknown, never "0.0.0"
    public void Resolve_PrefersStampedAssembly_FallsBackToConfig(string? asm, string? configured, string expected)
        => Assert.Equal(expected, AgentVersion.Resolve(asm, configured));

    [Theory]
    [InlineData("3.19.0", "3.19.0")]
    [InlineData("3.19.0+abc1234", "3.19.0")] // strip SourceLink "+<git-sha>" build metadata
    [InlineData("3.19.0-rc1", "3.19.0-rc1")] // keep a pre-release suffix (only "+metadata" is stripped)
    [InlineData("v3.23.0", "3.23.0")]        // strip a leading "v" tag prefix (MKM#1181 rollout-stuck class)
    [InlineData("V3.23.0", "3.23.0")]        // case-insensitive prefix strip
    [InlineData("v3.23.0+abc1234", "3.23.0")] // strip BOTH "v" prefix and "+<git-sha>" metadata
    [InlineData("v3.23.0-rc1", "3.23.0-rc1")] // strip "v", keep pre-release suffix
    [InlineData("version", "version")]        // do NOT strip 'v' unless followed by a digit
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Normalize_StripsBuildMetadataAndLeadingV(string? input, string? expected)
        => Assert.Equal(expected, AgentVersion.Normalize(input));

    [Fact]
    public void FromAssembly_DoesNotThrow_AndReturnsVersionOrNull()
    {
        var v = AgentVersion.FromAssembly();
        Assert.True(v is null || v.Length > 0);
    }
}
