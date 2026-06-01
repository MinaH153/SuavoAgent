using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Broker;
using Xunit;

namespace SuavoAgent.Broker.Tests;

// Regression guards for the Broker's H-8 Helper integrity check (SessionWatcher.VerifyHelperIntegrityAt).
//
// #11: a MISSING binaries.manifest used to fail-OPEN silently (return true), which both
//   (a) defeats the tamper guard — delete the manifest and any Helper launches unverified — and
//   (b) hides the failure (the box looks healthy / "green").
// On a MANAGED install the installer always persists bootstrap.ps1 alongside the manifest, so a
// missing manifest there is anomalous → fail-CLOSED (refuse; the resulting helper-down state is the
// degraded signal the cloud sees). Only a genuine pre-install/dev box (no bootstrap.ps1) keeps the
// fail-open so a first boot isn't bricked.
public class HelperIntegrityTests
{
    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "suavo-h8-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Sha256Hex(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    [Fact]
    public void ManagedInstall_MissingManifest_RefusesFailClosed()
    {
        // bootstrap.ps1 present (managed install) but binaries.manifest absent — the integrity
        // root is gone on a box that should have it. Refuse rather than silently launch unverified.
        var installDir = MakeTempDir();
        var programData = MakeTempDir();
        try
        {
            var helper = Path.Combine(installDir, "SuavoAgent.Helper.exe");
            File.WriteAllText(helper, "helper-bytes");
            File.WriteAllText(Path.Combine(programData, "bootstrap.ps1"), "# managed install marker");
            // No binaries.manifest in programData.

            var ok = SessionWatcher.VerifyHelperIntegrityAt(helper, programData, NullLogger.Instance);

            Assert.False(ok);
        }
        finally
        {
            Directory.Delete(installDir, recursive: true);
            Directory.Delete(programData, recursive: true);
        }
    }

    [Fact]
    public void FirstBoot_MissingManifestAndNoMarker_FailsOpen()
    {
        // No bootstrap.ps1 and no manifest = genuine pre-install/dev box. Don't brick the Helper.
        var installDir = MakeTempDir();
        var programData = MakeTempDir();
        try
        {
            var helper = Path.Combine(installDir, "SuavoAgent.Helper.exe");
            File.WriteAllText(helper, "helper-bytes");

            var ok = SessionWatcher.VerifyHelperIntegrityAt(helper, programData, NullLogger.Instance);

            Assert.True(ok);
        }
        finally
        {
            Directory.Delete(installDir, recursive: true);
            Directory.Delete(programData, recursive: true);
        }
    }

    [Fact]
    public void ManifestMatchesHelper_ReturnsTrue()
    {
        var installDir = MakeTempDir();
        var programData = MakeTempDir();
        try
        {
            var helper = Path.Combine(installDir, "SuavoAgent.Helper.exe");
            File.WriteAllText(helper, "the-real-helper");
            File.WriteAllText(
                Path.Combine(programData, "binaries.manifest"),
                $"{{ \"SuavoAgent.Helper.exe\": \"{Sha256Hex(helper)}\" }}");

            var ok = SessionWatcher.VerifyHelperIntegrityAt(helper, programData, NullLogger.Instance);

            Assert.True(ok);
        }
        finally
        {
            Directory.Delete(installDir, recursive: true);
            Directory.Delete(programData, recursive: true);
        }
    }

    [Fact]
    public void ManifestHashMismatch_RefusesFailClosed()
    {
        // The stale-manifest field failure on Mina's box: on-disk Helper hash != manifest hash.
        var installDir = MakeTempDir();
        var programData = MakeTempDir();
        try
        {
            var helper = Path.Combine(installDir, "SuavoAgent.Helper.exe");
            File.WriteAllText(helper, "swapped-new-helper");
            File.WriteAllText(
                Path.Combine(programData, "binaries.manifest"),
                "{ \"SuavoAgent.Helper.exe\": \"159b0110dfddcc4fbde84b76dcf3d90758e55b35ac7f78d64b9feb5347e39f5c\" }");

            var ok = SessionWatcher.VerifyHelperIntegrityAt(helper, programData, NullLogger.Instance);

            Assert.False(ok);
        }
        finally
        {
            Directory.Delete(installDir, recursive: true);
            Directory.Delete(programData, recursive: true);
        }
    }

    [Fact]
    public void ManifestPresentButNoHelperEntry_RefusesFailClosed()
    {
        var installDir = MakeTempDir();
        var programData = MakeTempDir();
        try
        {
            var helper = Path.Combine(installDir, "SuavoAgent.Helper.exe");
            File.WriteAllText(helper, "helper-bytes");
            File.WriteAllText(
                Path.Combine(programData, "binaries.manifest"),
                "{ \"SuavoAgent.Core.exe\": \"abc123\" }");

            var ok = SessionWatcher.VerifyHelperIntegrityAt(helper, programData, NullLogger.Instance);

            Assert.False(ok);
        }
        finally
        {
            Directory.Delete(installDir, recursive: true);
            Directory.Delete(programData, recursive: true);
        }
    }
}
