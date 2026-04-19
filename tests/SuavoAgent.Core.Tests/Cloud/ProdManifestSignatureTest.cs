using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Core.Cloud;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

/// <summary>
/// Smoke test for the v3.11.1 signed update manifest. Locks in that the
/// signature we generated locally (via ~/.suavo/signing-key.pem + openssl
/// + DER→P1363 conversion) actually passes SelfUpdater.VerifyManifestSignature.
///
/// If this test fails, a manifest we try to push to deployed agents will be
/// rejected — and nobody discovers that until an agent comes back online.
/// </summary>
public class ProdManifestSignatureTest
{
    private const string Manifest =
        "https://github.com/MinaH153/SuavoAgent/releases/download/v3.11.1/SuavoAgent.Core.exe"
      + "|f0a53cd49e8932313ceae2c80df3383835c186c71718aec69bb3e3f8a8aa022a"
      + "|https://github.com/MinaH153/SuavoAgent/releases/download/v3.11.1/SuavoAgent.Broker.exe"
      + "|8158e13b5a05a4e4406a2129cb18a0a5bad375b4411737c087fc753da97ef054"
      + "|https://github.com/MinaH153/SuavoAgent/releases/download/v3.11.1/SuavoAgent.Helper.exe"
      + "|2b89974ffe08525a338226d84e1e6a46e6fd5748204357346237cb96b6ae5bcd"
      + "|3.11.1|net8.0|win-x64";

    // P1363-formatted (r || s, 64 bytes) ECDSA-P256 signature of the manifest.
    private const string SignatureHex =
        "92969561a2c0930b6e34f77b8222eaed58bf95dc10836e4fc2e2ca7ef7390f33"
      + "9c373dc4fd0a808dfe5b985a725f7c45db62f8453a3e179085bef46b1d9c7f81";

    [Fact]
    public void ProdManifest_Signature_VerifiesAgainstEmbeddedPublicKey()
    {
        var ok = SelfUpdater.VerifyManifestSignature(
            Manifest, SignatureHex, NullLogger.Instance);
        Assert.True(ok, "v3.11.1 manifest signature must verify against SelfUpdater.UpdatePublicKeyDer");
    }

    [Fact]
    public void ProdManifest_ParsesIntoWellFormedRecord()
    {
        var m = UpdateManifest.Parse(Manifest);
        Assert.NotNull(m);
        Assert.Equal("3.11.1", m!.Version);
        Assert.True(m.MatchesRuntime("net8.0", "win-x64"));
        Assert.EndsWith("SuavoAgent.Core.exe", m.CoreUrl);
        Assert.EndsWith("SuavoAgent.Helper.exe", m.HelperUrl);
        Assert.EndsWith("SuavoAgent.Broker.exe", m.BrokerUrl);
    }
}
