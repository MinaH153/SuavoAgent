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
// manifest to match the binaries now on disk.
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
    public void RegenerateBinariesManifest_WritesHashesMatchingOnDiskBinaries()
    {
        var installDir = MakeTempDir();
        var manifestPath = Path.Combine(MakeTempDir(), "binaries.manifest");
        try
        {
            // Simulate post-swap on-disk binaries with distinct content.
            File.WriteAllText(Path.Combine(installDir, "SuavoAgent.Core.exe"), "core-v2-bytes");
            File.WriteAllText(Path.Combine(installDir, "SuavoAgent.Broker.exe"), "broker-v2-bytes");
            File.WriteAllText(Path.Combine(installDir, "SuavoAgent.Helper.exe"), "helper-v2-bytes");
            File.WriteAllText(Path.Combine(installDir, "SuavoAgent.Watchdog.exe"), "watchdog-v2-bytes");

            SelfUpdater.RegenerateBinariesManifest(installDir, NullLogger.Instance, manifestPath);

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
            // A box without the Watchdog binary — manifest should list only what's present.
            File.WriteAllText(Path.Combine(installDir, "SuavoAgent.Core.exe"), "core");
            File.WriteAllText(Path.Combine(installDir, "SuavoAgent.Broker.exe"), "broker");
            File.WriteAllText(Path.Combine(installDir, "SuavoAgent.Helper.exe"), "helper");

            SelfUpdater.RegenerateBinariesManifest(installDir, NullLogger.Instance, manifestPath);

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

            SelfUpdater.RegenerateBinariesManifest(installDir, NullLogger.Instance, manifestPath);

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
}
