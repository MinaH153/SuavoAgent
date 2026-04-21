using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Attestation;
using Xunit;

namespace SuavoAgent.Attestation.Tests;

public class EcdsaManifestVerifierTests
{
    [Fact]
    public void Verify_ValidSignature_ReturnsTrue()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = "{\"version\":\"3.13.7\",\"files\":[]}";
        var signature = ecdsa.SignData(Encoding.UTF8.GetBytes(manifest), HashAlgorithmName.SHA256);
        var publicKeySpki = ecdsa.ExportSubjectPublicKeyInfo();

        var verifier = new EcdsaManifestVerifier(publicKeySpki);
        Assert.True(verifier.Verify(manifest, signature));
    }

    [Fact]
    public void Verify_InvalidSignature_ReturnsFalse()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = "{\"version\":\"3.13.7\"}";
        var signature = ecdsa.SignData(Encoding.UTF8.GetBytes(manifest), HashAlgorithmName.SHA256);
        var publicKeySpki = ecdsa.ExportSubjectPublicKeyInfo();

        var verifier = new EcdsaManifestVerifier(publicKeySpki);
        // Tamper with body — signature no longer valid
        Assert.False(verifier.Verify("{\"version\":\"3.13.8\"}", signature));
    }

    [Fact]
    public void Verify_WrongKey_ReturnsFalse()
    {
        using var signerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = "body";
        var signature = signerKey.SignData(Encoding.UTF8.GetBytes(manifest), HashAlgorithmName.SHA256);

        var verifier = new EcdsaManifestVerifier(wrongKey.ExportSubjectPublicKeyInfo());
        Assert.False(verifier.Verify(manifest, signature));
    }

    [Fact]
    public void Verify_MalformedSignature_ReturnsFalse()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeySpki = ecdsa.ExportSubjectPublicKeyInfo();
        var verifier = new EcdsaManifestVerifier(publicKeySpki);

        Assert.False(verifier.Verify("body", new byte[] { 1, 2, 3 }));
    }
}
