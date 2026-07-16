using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SuavoAgent.Helper.SystemObservers.BrowserConnector;
using Xunit;

namespace SuavoAgent.Helper.Tests.SystemObservers;

public sealed class BrowserConnectorTrustStoreTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false) },
    };

    [Fact]
    public void MissingDedicatedStore_FailsClosedWithoutFallbackKey()
    {
        var result = BrowserConnectorTrustStore.Load(new MissingSource(), Now);

        Assert.False(result.Valid);
        Assert.Null(result.Authority);
        Assert.Equal(BrowserConnectorReasonCodes.AuthorityInvalid, result.ReasonCode);
    }

    [Fact]
    public void ProtectedStoreDocuments_StillRequireExactValidSignatureAndExpiry()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var unsigned = Document();
        var signature = signer.SignData(
            Encoding.UTF8.GetBytes(BrowserConnectorAuthorityVerifier.BuildCanonical(unsigned)),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var signed = unsigned with
        {
            Signature = BrowserConnectorAuthorityVerifier.Base64UrlEncode(signature),
        };
        var source = new StaticSource(
            JsonSerializer.SerializeToUtf8Bytes(signed, JsonOptions),
            Roots(signer));

        var valid = BrowserConnectorTrustStore.Load(source, Now);
        var expired = BrowserConnectorTrustStore.Load(source, Now.AddDays(2));

        Assert.True(valid.Valid);
        Assert.NotNull(valid.Authority);
        Assert.True(valid.Authority.TryAuthorize(
            "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/",
            out var exact));
        Assert.Equal(BrowserFamily.Chrome, exact.Browser);
        Assert.False(expired.Valid);
    }

    [Fact]
    public void ExtraTrustStoreFields_AreRejectedInsteadOfSilentlyIgnored()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var unsigned = Document();
        var signature = signer.SignData(
            Encoding.UTF8.GetBytes(BrowserConnectorAuthorityVerifier.BuildCanonical(unsigned)),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var signed = unsigned with
        {
            Signature = BrowserConnectorAuthorityVerifier.Base64UrlEncode(signature),
        };
        var authority = JsonSerializer.SerializeToUtf8Bytes(signed, JsonOptions);
        using var parsed = JsonDocument.Parse(authority);
        var tampered = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["schemaVersion"] = parsed.RootElement.GetProperty("schemaVersion").Clone(),
            ["revision"] = parsed.RootElement.GetProperty("revision").Clone(),
            ["issuedAt"] = parsed.RootElement.GetProperty("issuedAt").Clone(),
            ["expiresAt"] = parsed.RootElement.GetProperty("expiresAt").Clone(),
            ["keyId"] = parsed.RootElement.GetProperty("keyId").Clone(),
            ["allowedExtensions"] = parsed.RootElement.GetProperty("allowedExtensions").Clone(),
            ["signature"] = parsed.RootElement.GetProperty("signature").Clone(),
            ["fallbackExtensionId"] = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        });

        var result = BrowserConnectorTrustStore.Load(
            new StaticSource(tampered, Roots(signer)),
            Now);

        Assert.False(result.Valid);
        Assert.Null(result.Authority);
    }

    private static BrowserConnectorAuthorityDocument Document() => new(
        BrowserConnectorAuthorityVerifier.CurrentSchemaVersion,
        17,
        Now.AddMinutes(-1),
        Now.AddDays(1),
        "browser-authority-test",
        [
            new BrowserConnectorAuthorityEntry(
                BrowserFamily.Chrome,
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/",
                BrowserConnectorAuthorityTests.ChromePath),
        ],
        string.Empty);

    private static byte[] Roots(ECDsa signer) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            trustedKeys = new[]
            {
                new
                {
                    keyId = "browser-authority-test",
                    publicKeySpkiBase64 = Convert.ToBase64String(
                        signer.ExportSubjectPublicKeyInfo()),
                },
            },
        });

    private sealed class MissingSource : IBrowserConnectorTrustStoreSource
    {
        public bool TryRead(out byte[] authorityDocument, out byte[] trustedRootsDocument)
        {
            authorityDocument = Array.Empty<byte>();
            trustedRootsDocument = Array.Empty<byte>();
            return false;
        }
    }

    private sealed class StaticSource(byte[] authority, byte[] roots)
        : IBrowserConnectorTrustStoreSource
    {
        public bool TryRead(out byte[] authorityDocument, out byte[] trustedRootsDocument)
        {
            authorityDocument = authority.ToArray();
            trustedRootsDocument = roots.ToArray();
            return true;
        }
    }
}
