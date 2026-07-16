using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Core.Cloud;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public sealed class ManifestSigningRootResolutionTests
{
    [Fact]
    public void ExactV2Signature_ResolvesOnlyV2_AndTamperFailsClosed()
    {
        using var v1 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var v2 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var roots = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [OtaUpdateTrust.LegacyV1KeyId] = Convert.ToBase64String(
                v1.ExportSubjectPublicKeyInfo()),
            [OtaUpdateTrust.CurrentV2KeyId] = Convert.ToBase64String(
                v2.ExportSubjectPublicKeyInfo()),
        };
        const string canonical = "exact-manifest-canonical";
        var signature = Convert.ToHexString(v2.SignData(
            Encoding.UTF8.GetBytes(canonical),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation)).ToLowerInvariant();

        Assert.Equal(
            OtaUpdateTrust.CurrentV2KeyId,
            SelfUpdater.ResolveManifestSigningKeyId(
                canonical,
                signature,
                roots,
                NullLogger.Instance));
        Assert.Null(SelfUpdater.ResolveManifestSigningKeyId(
            canonical + "-tampered",
            signature,
            roots,
            NullLogger.Instance));
    }

    [Fact]
    public void DuplicateRootRegistry_IsRejectedBeforeIndividualRootResolution()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo());
        var roots = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [OtaUpdateTrust.LegacyV1KeyId] = publicKey,
            [OtaUpdateTrust.CurrentV2KeyId] = publicKey,
        };
        const string canonical = "exact-manifest-canonical";
        var signature = Convert.ToHexString(signer.SignData(
            Encoding.UTF8.GetBytes(canonical),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation)).ToLowerInvariant();

        Assert.Null(SelfUpdater.ResolveManifestSigningKeyId(
            canonical,
            signature,
            roots,
            NullLogger.Instance));
    }
}
