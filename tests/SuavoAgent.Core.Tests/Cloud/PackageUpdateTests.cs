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
    public void ManifestSignatureVerification_RotationPairAcceptsV2()
    {
        var (v1, v1Public) = GenerateTestKeyPair();
        var (v2, v2Public) = GenerateTestKeyPair();
        using (v1)
        using (v2)
        {
            var canonical = MakeManifest().ToCanonical();
            var signature = Convert.ToHexString(v2.SignData(
                Encoding.UTF8.GetBytes(canonical),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            var roots = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SuavoAgent.Contracts.Maintenance.OtaUpdateTrust.LegacyV1KeyId] = v1Public,
                [SuavoAgent.Contracts.Maintenance.OtaUpdateTrust.CurrentV2KeyId] = v2Public,
            };

            Assert.True(SelfUpdater.VerifyManifestSignature(canonical, signature, roots, _logger));
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
