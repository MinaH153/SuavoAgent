using System.Collections.Generic;
using SuavoAgent.Broker.Honeytoken;
using Xunit;

namespace SuavoAgent.Broker.Tests.Honeytoken;

/// <summary>
/// The NT-device path normalizer — the defense against silent blindness (Kernel-File ETW emits
/// \Device\HarddiskVolumeN\... not C:\..., so a naive C:\ filter never matches). Tests the PURE Normalize core
/// + the injected Refresh seam — no P/Invoke, runs on the ubuntu/macOS gate.
/// </summary>
public sealed class DevicePathNormalizerTests
{
    private static readonly Dictionary<string, string> Map = new(System.StringComparer.OrdinalIgnoreCase)
    {
        [@"\Device\HarddiskVolume3"] = "C:",
        [@"\Device\HarddiskVolume4"] = "D:",
    };

    [Fact]
    public void Normalize_MapsDriveDirToNtPrefix()
    {
        Assert.Equal(
            @"\Device\HarddiskVolume3\ProgramData\SuavoAgent\honeytokens",
            DevicePathNormalizer.Normalize(Map, @"C:\ProgramData\SuavoAgent\honeytokens"));
    }

    [Fact]
    public void Normalize_TrimsTrailingSeparator()
    {
        Assert.Equal(
            @"\Device\HarddiskVolume4\x",
            DevicePathNormalizer.Normalize(Map, @"D:\x\"));
    }

    [Fact]
    public void Normalize_DriveAbsentFromMap_ReturnsNull_NotMatchAll()
    {
        // The critical safety case: an unmapped drive → null → caller refuses to arm (NEVER an empty prefix
        // that would match every file open).
        Assert.Null(DevicePathNormalizer.Normalize(Map, @"Z:\whatever"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"\\server\share\x")] // UNC, no drive letter
    [InlineData("notapath")]
    public void Normalize_NonDriveQualified_ReturnsNull(string input)
    {
        Assert.Null(DevicePathNormalizer.Normalize(Map, input));
    }

    [Fact]
    public void RefreshSeam_BuildsMapFromProviders_ThenResolves()
    {
        var norm = new DevicePathNormalizer();
        norm.Refresh(
            driveRootProvider: () => new[] { @"C:\", @"D:\" },
            deviceResolver: d => d == "C:" ? @"\Device\HarddiskVolume7" : @"\Device\HarddiskVolume8");

        Assert.Equal(@"\Device\HarddiskVolume7\ProgramData\SuavoAgent\honeytokens",
            norm.ToNtPrefix(@"C:\ProgramData\SuavoAgent\honeytokens"));
    }

    [Fact]
    public void RefreshSeam_EmptyResult_DoesNotClobberPreviousMap()
    {
        var norm = new DevicePathNormalizer();
        norm.Refresh(() => new[] { @"C:\" }, d => @"\Device\HarddiskVolume7");
        // A later refresh that resolves nothing must not wipe the good map (volume-query transient).
        norm.Refresh(() => new[] { @"C:\" }, d => null);
        Assert.Equal(@"\Device\HarddiskVolume7\x", norm.ToNtPrefix(@"C:\x"));
    }
}
