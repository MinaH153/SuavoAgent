using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Core.Cloud;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public class PackageUpdateTests
{
    private readonly ILogger _logger = NullLogger.Instance;

    private static (ECDsa Key, string PublicKeyDer) GenerateTestKeyPair()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pubBytes = key.ExportSubjectPublicKeyInfo();
        return (key, Convert.ToBase64String(pubBytes));
    }

    private static UpdateManifest MakeManifest(string version = "2.1.0") =>
        new(
            CoreUrl: "https://github.com/MinaH153/SuavoAgent/releases/download/v2.1.0/SuavoAgent.Core.exe",
            CoreSha256: "abc123",
            BrokerUrl: "https://github.com/MinaH153/SuavoAgent/releases/download/v2.1.0/SuavoAgent.Broker.exe",
            BrokerSha256: "def456",
            HelperUrl: "https://github.com/MinaH153/SuavoAgent/releases/download/v2.1.0/SuavoAgent.Helper.exe",
            HelperSha256: "789012",
            Version: version,
            Runtime: "net8.0",
            Arch: "win-x64");

    [Fact]
    public void ManifestSignatureVerification_ValidSignature_Passes()
    {
        var (key, _) = GenerateTestKeyPair();
        var manifest = MakeManifest();
        var canonical = manifest.ToCanonical();
        var sigBytes = key.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256);
        var sigHex = Convert.ToHexString(sigBytes).ToLowerInvariant();

        // Temporarily swap public key — verify via the internal method
        var result = SelfUpdater.VerifyManifestSignature(canonical, sigHex, _logger);
        // This will fail because it uses the embedded key, not our test key.
        // But we're testing the code path works, not the key match.
        // The real integration test needs the actual signing key.
        Assert.False(result); // Expected: test key != embedded key
    }

    [Fact]
    public void ManifestSignatureVerification_RoundTrip_AcceptsValid_RejectsTamperAndWrongKey()
    {
        // QA wave2.5: the real ACCEPTANCE path (generate key → sign → verify) the old test couldn't
        // reach, via the key-injectable overload. Guards against a key-rotation / P1363 encoding bug
        // that would make the OTA verify accept nothing and brick every agent on next heartbeat.
        var (key, pubDer) = GenerateTestKeyPair();
        using (key)
        {
            var canonical = MakeManifest().ToCanonical();
            var sigHex = Convert.ToHexString(
                key.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256)).ToLowerInvariant();

            // ACCEPTANCE: a signature from the matching key verifies.
            Assert.True(SelfUpdater.VerifyManifestSignature(canonical, sigHex, pubDer, _logger));

            // TAMPER: the same signature over altered canonical bytes is rejected.
            Assert.False(SelfUpdater.VerifyManifestSignature(canonical + " ", sigHex, pubDer, _logger));

            // WRONG KEY: a valid signature checked against a different public key is rejected.
            var (other, otherPubDer) = GenerateTestKeyPair();
            using (other)
                Assert.False(SelfUpdater.VerifyManifestSignature(canonical, sigHex, otherPubDer, _logger));
        }
    }

    [Fact]
    public void ManifestSignatureVerification_NullSignature_Rejects()
    {
        var manifest = MakeManifest();
        var result = SelfUpdater.VerifyManifestSignature(manifest.ToCanonical(), null, _logger);
        Assert.False(result);
    }

    [Fact]
    public void ManifestSignatureVerification_EmptySignature_Rejects()
    {
        var manifest = MakeManifest();
        var result = SelfUpdater.VerifyManifestSignature(manifest.ToCanonical(), "", _logger);
        Assert.False(result);
    }

    [Fact]
    public void ManifestSignatureVerification_GarbageSignature_Rejects()
    {
        var manifest = MakeManifest();
        var result = SelfUpdater.VerifyManifestSignature(manifest.ToCanonical(), "deadbeef", _logger);
        Assert.False(result);
    }

    [Fact]
    public void SwapBinaries_NoNewFiles_ReturnsFalse()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "suavo-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            // Create existing binaries but no .new files
            File.WriteAllText(Path.Combine(tempDir, "SuavoAgent.Core.exe"), "old-core");
            File.WriteAllText(Path.Combine(tempDir, "SuavoAgent.Broker.exe"), "old-broker");

            var result = SelfUpdater.SwapBinaries(tempDir, _logger);
            Assert.False(result);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SwapBinaries_AllNewFiles_SwapsAndReturnsTrue()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "suavo-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            // Create old and new files
            File.WriteAllText(Path.Combine(tempDir, "SuavoAgent.Core.exe"), "old-core");
            File.WriteAllText(Path.Combine(tempDir, "SuavoAgent.Core.exe.new"), "new-core");
            File.WriteAllText(Path.Combine(tempDir, "SuavoAgent.Broker.exe"), "old-broker");
            File.WriteAllText(Path.Combine(tempDir, "SuavoAgent.Broker.exe.new"), "new-broker");
            File.WriteAllText(Path.Combine(tempDir, "SuavoAgent.Helper.exe"), "old-helper");
            File.WriteAllText(Path.Combine(tempDir, "SuavoAgent.Helper.exe.new"), "new-helper");

            var result = SelfUpdater.SwapBinaries(tempDir, _logger);

            Assert.True(result);
            Assert.Equal("new-core", File.ReadAllText(Path.Combine(tempDir, "SuavoAgent.Core.exe")));
            Assert.Equal("new-broker", File.ReadAllText(Path.Combine(tempDir, "SuavoAgent.Broker.exe")));
            Assert.Equal("new-helper", File.ReadAllText(Path.Combine(tempDir, "SuavoAgent.Helper.exe")));
            Assert.Equal("old-core", File.ReadAllText(Path.Combine(tempDir, "SuavoAgent.Core.exe.old")));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SwapBinaries_PartialNewFiles_RefusesAndSwapsNothing()
    {
        // #10 (regression): a PARTIAL staged set — e.g. only Core.exe.new with no Broker/Helper
        // .new — must NOT be swapped. Applying a subset leaves version skew (new Core running
        // against an old Broker/Helper), the exact silent-corruption class this guard prevents.
        // SwapBinaries refuses and swaps NOTHING.
        var tempDir = Path.Combine(Path.GetTempPath(), "suavo-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "SuavoAgent.Core.exe"), "old-core");
            File.WriteAllText(Path.Combine(tempDir, "SuavoAgent.Core.exe.new"), "new-core");
            File.WriteAllText(Path.Combine(tempDir, "SuavoAgent.Broker.exe"), "old-broker");
            File.WriteAllText(Path.Combine(tempDir, "SuavoAgent.Helper.exe"), "old-helper");
            // No Broker.exe.new / Helper.exe.new → partial set

            var result = SelfUpdater.SwapBinaries(tempDir, _logger);

            Assert.False(result);
            // Nothing swapped: Core is still the old binary, no .old was created, and the
            // staged Core.exe.new is left in place for the caller to clean up on abort.
            Assert.Equal("old-core", File.ReadAllText(Path.Combine(tempDir, "SuavoAgent.Core.exe")));
            Assert.False(File.Exists(Path.Combine(tempDir, "SuavoAgent.Core.exe.old")));
            Assert.True(File.Exists(Path.Combine(tempDir, "SuavoAgent.Core.exe.new")));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void VerifyStagedBinaries_MissingDeclaredBinary_ReturnsFalse()
    {
        // #10 (regression): the pre-swap gate must REQUIRE the full declared set. If the manifest
        // declares Core/Broker/Helper but only some .new files are staged, a missing one is a
        // partial/corrupted stage — abort the update instead of silently skipping it (which let
        // SwapBinaries apply a subset → skew). Present .new files carry manifest-matching hashes
        // so the only reason to fail is the MISSING Helper.exe.new, not a hash mismatch.
        var tempDir = Path.Combine(Path.GetTempPath(), "suavo-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var coreNew = Path.Combine(tempDir, "SuavoAgent.Core.exe.new");
            var brokerNew = Path.Combine(tempDir, "SuavoAgent.Broker.exe.new");
            File.WriteAllText(coreNew, "new-core-bytes");
            File.WriteAllText(brokerNew, "new-broker-bytes");
            // SuavoAgent.Helper.exe.new deliberately absent.

            var manifest = new UpdateManifest(
                CoreUrl: "https://example/c", CoreSha256: Sha256Hex(coreNew),
                BrokerUrl: "https://example/b", BrokerSha256: Sha256Hex(brokerNew),
                HelperUrl: "https://example/h", HelperSha256: "deadbeef",
                Version: "2.1.0", Runtime: "net8.0", Arch: "win-x64");

            var result = SelfUpdater.VerifyStagedBinaries(tempDir, manifest, _logger);

            Assert.False(result);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    private static string Sha256Hex(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    [Fact]
    public void CheckPendingUpdate_NoSentinel_ReturnsFalse()
    {
        // CheckPendingUpdate uses Environment.ProcessPath which we can't mock easily,
        // but we can verify it returns false when no sentinel exists (current state).
        var result = SelfUpdater.CheckPendingUpdate(_logger);
        Assert.False(result);
    }

    [Fact]
    public void Manifest_UntrustedUrl_RejectedByIsAllowedUrl()
    {
        var manifest = new UpdateManifest(
            CoreUrl: "https://evil.com/core.exe", CoreSha256: "abc",
            BrokerUrl: "https://github.com/broker.exe", BrokerSha256: "def",
            HelperUrl: "https://github.com/helper.exe", HelperSha256: "ghi",
            Version: "2.1.0", Runtime: "net8.0", Arch: "win-x64");

        Assert.False(SelfUpdater.IsAllowedUrl(manifest.CoreUrl));
        Assert.True(SelfUpdater.IsAllowedUrl(manifest.BrokerUrl));
    }

    [Theory]
    [InlineData("net8.0", "win-x64", true)]
    [InlineData("net8.0", "linux-x64", false)]
    [InlineData("net9.0", "win-x64", false)]
    public void Manifest_RuntimeCheck(string runtime, string arch, bool expected)
    {
        var manifest = MakeManifest() with { Runtime = runtime, Arch = arch };
        Assert.Equal(expected, manifest.MatchesRuntime("net8.0", "win-x64"));
    }
}
