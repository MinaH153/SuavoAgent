using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Core.Cloud;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

// Regression guard for the field bug where an OTA/bootstrap binary swap updated the
// .exes but left binaries.manifest stale, so the Broker's H-8 guard
// (SessionWatcher.VerifyHelperIntegrity) refused to launch the new Helper and the
// agent went blind. After a swap, RegenerateBinariesManifest must rewrite the
// manifest to match the binaries now on disk — AND return false (not silently
// succeed) when it cannot, so the caller can mark the update degraded.
public class SelfUpdaterManifestTests
{
    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "suavo-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Sha256Hex(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    [Fact]
    public void RegenerateBinariesManifest_WritesHashesMatchingOnDiskBinaries_AndReturnsTrue()
    {
        var installDir = MakeTempDir();
        var manifestPath = Path.Combine(MakeTempDir(), "binaries.manifest");
        try
        {
            File.WriteAllText(Path.Combine(installDir, "SuavoAgent.Core.exe"), "core-v2-bytes");
            File.WriteAllText(Path.Combine(installDir, "SuavoAgent.Broker.exe"), "broker-v2-bytes");
            File.WriteAllText(Path.Combine(installDir, "SuavoAgent.Helper.exe"), "helper-v2-bytes");
            File.WriteAllText(Path.Combine(installDir, "SuavoAgent.Watchdog.exe"), "watchdog-v2-bytes");

            var ok = SelfUpdater.RegenerateBinariesManifest(installDir, NullLogger.Instance, manifestPath);
            Assert.True(ok);

            Assert.True(File.Exists(manifestPath));
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = doc.RootElement;
            foreach (var bin in new[]
            {
                "SuavoAgent.Core.exe", "SuavoAgent.Broker.exe",
                "SuavoAgent.Helper.exe", "SuavoAgent.Watchdog.exe",
            })
            {
                Assert.True(root.TryGetProperty(bin, out var hashEl), $"manifest missing {bin}");
                Assert.Equal(Sha256Hex(Path.Combine(installDir, bin)), hashEl.GetString());
            }
        }
        finally
        {
            Directory.Delete(installDir, recursive: true);
            Directory.Delete(Path.GetDirectoryName(manifestPath)!, recursive: true);
        }
    }

    [Fact]
    public void RegenerateBinariesManifest_OmitsMissingWatchdog()
    {
        var installDir = MakeTempDir();
        var manifestPath = Path.Combine(MakeTempDir(), "binaries.manifest");
        try
        {
            File.WriteAllText(Path.Combine(installDir, "SuavoAgent.Core.exe"), "core");
            File.WriteAllText(Path.Combine(installDir, "SuavoAgent.Broker.exe"), "broker");
            File.WriteAllText(Path.Combine(installDir, "SuavoAgent.Helper.exe"), "helper");

            var ok = SelfUpdater.RegenerateBinariesManifest(installDir, NullLogger.Instance, manifestPath);
            Assert.True(ok);

            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("SuavoAgent.Helper.exe", out _));
            Assert.False(root.TryGetProperty("SuavoAgent.Watchdog.exe", out _));
        }
        finally
        {
            Directory.Delete(installDir, recursive: true);
            Directory.Delete(Path.GetDirectoryName(manifestPath)!, recursive: true);
        }
    }

    [Fact]
    public void RegenerateBinariesManifest_OverwritesStaleManifest()
    {
        var installDir = MakeTempDir();
        var manifestPath = Path.Combine(MakeTempDir(), "binaries.manifest");
        try
        {
            File.WriteAllText(manifestPath, "{ \"SuavoAgent.Helper.exe\": \"deadbeefstalehash\" }");
            File.WriteAllText(Path.Combine(installDir, "SuavoAgent.Helper.exe"), "fresh-helper-bytes");

            var ok = SelfUpdater.RegenerateBinariesManifest(installDir, NullLogger.Instance, manifestPath);
            Assert.True(ok);

            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var actual = doc.RootElement.GetProperty("SuavoAgent.Helper.exe").GetString();
            Assert.Equal(Sha256Hex(Path.Combine(installDir, "SuavoAgent.Helper.exe")), actual);
            Assert.NotEqual("deadbeefstalehash", actual);
        }
        finally
        {
            Directory.Delete(installDir, recursive: true);
            Directory.Delete(Path.GetDirectoryName(manifestPath)!, recursive: true);
        }
    }

    [Fact]
    public void RegenerateBinariesManifest_ReturnsFalse_WhenNoBinariesPresent()
    {
        // fix1 contract: an empty/missing install dir must NOT silently succeed — it
        // returns false so the caller marks the update degraded instead of reporting
        // clean success (the silent-blind archetype this whole fix prevents).
        var installDir = MakeTempDir(); // exists but contains no agent binaries
        var manifestPath = Path.Combine(MakeTempDir(), "binaries.manifest");
        try
        {
            var ok = SelfUpdater.RegenerateBinariesManifest(installDir, NullLogger.Instance, manifestPath);
            Assert.False(ok);
            // And it must not leave a bogus/empty manifest behind.
            Assert.False(File.Exists(manifestPath));
        }
        finally
        {
            Directory.Delete(installDir, recursive: true);
            Directory.Delete(Path.GetDirectoryName(manifestPath)!, recursive: true);
        }
    }

    [Fact]
    public void RegenerateBinariesManifest_ReturnsFalse_OnWriteFailure()
    {
        // Force an IO failure by pointing the manifest at a path whose parent is a
        // FILE, not a directory — Directory.CreateDirectory(parent) throws, the method
        // catches and must return false (degraded), never throw and never claim success.
        var installDir = MakeTempDir();
        File.WriteAllText(Path.Combine(installDir, "SuavoAgent.Helper.exe"), "helper");
        var parentFile = Path.Combine(MakeTempDir(), "not-a-dir");
        File.WriteAllText(parentFile, "i am a file");
        var manifestPath = Path.Combine(parentFile, "binaries.manifest"); // parent is a file
        try
        {
            var ok = SelfUpdater.RegenerateBinariesManifest(installDir, NullLogger.Instance, manifestPath);
            Assert.False(ok);
        }
        finally
        {
            Directory.Delete(installDir, recursive: true);
            Directory.Delete(Path.GetDirectoryName(parentFile)!, recursive: true);
        }
    }
}
