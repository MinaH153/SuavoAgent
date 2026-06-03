using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Core.Cloud;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

/// <summary>
/// Self-heal Chunk B: the staged Watchdog binary is swapped best-effort — it drops the binary in
/// when there's no lock (the box-missing-watchdog case), and never fails the (already-applied)
/// Core/Broker/Helper swap when it can't. Uses a real temp dir for the file moves.
/// </summary>
public class SelfUpdaterWatchdogSwapTests
{
    [Fact]
    public void SwapWatchdogBestEffort_DropsStagedBinary_WhenNotLocked()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"suavo_wd_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var newPath = Path.Combine(dir, "SuavoAgent.Watchdog.exe.new");
            File.WriteAllText(newPath, "new-watchdog-binary");

            var swapped = SelfUpdater.SwapWatchdogBestEffort(dir, NullLogger.Instance);

            Assert.True(swapped);
            Assert.True(File.Exists(Path.Combine(dir, "SuavoAgent.Watchdog.exe")));
            Assert.False(File.Exists(newPath));
            Assert.Equal("new-watchdog-binary", File.ReadAllText(Path.Combine(dir, "SuavoAgent.Watchdog.exe")));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SwapWatchdogBestEffort_ReplacesExistingBinary()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"suavo_wd_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var current = Path.Combine(dir, "SuavoAgent.Watchdog.exe");
            File.WriteAllText(current, "old-binary");
            File.WriteAllText(current + ".new", "new-binary");

            Assert.True(SelfUpdater.SwapWatchdogBestEffort(dir, NullLogger.Instance));
            Assert.Equal("new-binary", File.ReadAllText(current));
            Assert.False(File.Exists(current + ".new"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SwapWatchdogBestEffort_NoOp_WhenNothingStaged()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"suavo_wd_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            Assert.False(SelfUpdater.SwapWatchdogBestEffort(dir, NullLogger.Instance));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
