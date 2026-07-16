using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Contracts.Maintenance;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Maintenance;

public sealed class OtaUpdateTrustTests
{
    [Fact]
    public void Production_registry_trusts_distinct_v1_and_v2_but_still_signs_with_v1()
    {
        Assert.True(OtaUpdateTrust.IsProductionKeyConfigured(OtaUpdateTrust.LegacyV1KeyId));
        Assert.True(OtaUpdateTrust.IsProductionKeyConfigured(OtaUpdateTrust.CurrentV2KeyId));
        Assert.Equal(OtaUpdateTrust.LegacyV1KeyId, OtaUpdateTrust.ProductionSigningKeyId);
        Assert.Equal(2, OtaUpdateTrust.ProductionTrustedPublicKeys.Count);
        Assert.NotEqual(
            OtaUpdateTrust.ProductionTrustedPublicKeys[OtaUpdateTrust.LegacyV1KeyId],
            OtaUpdateTrust.ProductionTrustedPublicKeys[OtaUpdateTrust.CurrentV2KeyId]);
        Assert.DoesNotContain(
            OtaUpdateTrust.PendingV2PublicKeyMarker,
            OtaUpdateTrust.ProductionTrustedPublicKeys.Values);
    }

    [Fact]
    public void Rotation_registry_accepts_either_root_for_both_release_signature_formats()
    {
        using var v1 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var v2 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var roots = Roots(v1, v2);
        const string canonical = "reviewed-ota-manifest";
        var payload = Encoding.UTF8.GetBytes(canonical);

        foreach (var signer in new[] { v1, v2 })
        {
            var p1363 = signer.SignData(
                payload,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            var der = signer.SignData(
                payload,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);

            Assert.True(OtaUpdateTrust.VerifyP1363Hex(
                roots,
                canonical,
                Convert.ToHexString(p1363)));
            Assert.True(OtaUpdateTrust.VerifyDer(roots, payload, der));
        }
    }

    [Fact]
    public void Malformed_or_unknown_extra_root_fails_closed_even_when_v1_signature_is_valid()
    {
        using var v1 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        const string canonical = "reviewed-ota-manifest";
        var signature = Convert.ToHexString(v1.SignData(
            Encoding.UTF8.GetBytes(canonical),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        var roots = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [OtaUpdateTrust.LegacyV1KeyId] = Public(v1),
            ["attacker-root"] = Public(v1),
        };

        Assert.False(OtaUpdateTrust.VerifyP1363Hex(roots, canonical, signature));
    }

    [Fact]
    public void Declared_der_root_must_be_the_root_that_actually_signed()
    {
        using var v1 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var v2 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var roots = Roots(v1, v2);
        var payload = Encoding.UTF8.GetBytes("root-bound-release-receipt");
        var v1Signature = v1.SignData(
            payload,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);

        Assert.True(OtaUpdateTrust.VerifyDerForKeyId(
            roots,
            OtaUpdateTrust.LegacyV1KeyId,
            payload,
            v1Signature));
        Assert.False(OtaUpdateTrust.VerifyDerForKeyId(
            roots,
            OtaUpdateTrust.CurrentV2KeyId,
            payload,
            v1Signature));
        Assert.False(OtaUpdateTrust.VerifyDerForKeyId(
            roots,
            "attacker-root",
            payload,
            v1Signature));
        var aliasedRoots = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [OtaUpdateTrust.LegacyV1KeyId] = Public(v1),
            [OtaUpdateTrust.CurrentV2KeyId] = Public(v1),
        };
        Assert.False(OtaUpdateTrust.VerifyDerForKeyId(
            aliasedRoots,
            OtaUpdateTrust.CurrentV2KeyId,
            payload,
            v1Signature));
    }

    private static IReadOnlyDictionary<string, string> Roots(ECDsa v1, ECDsa v2) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [OtaUpdateTrust.LegacyV1KeyId] = Public(v1),
            [OtaUpdateTrust.CurrentV2KeyId] = Public(v2),
        };

    private static string Public(ECDsa key) =>
        Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
}
