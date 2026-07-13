using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Helper.SystemObservers.BrowserConnector;
using Xunit;

namespace SuavoAgent.Helper.Tests.SystemObservers;

public sealed class BrowserConnectorAuthorityTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    internal const string ChromePath =
        @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    internal const string EdgePath =
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";

    [Fact]
    public void SignedExactOrigins_AreAuthorizedCaseSensitively()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var validation = SignAndVerify(Document(), signer);

        Assert.True(validation.Valid);
        Assert.NotNull(validation.Authority);
        Assert.True(validation.Authority.TryAuthorize(
            "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/",
            out var chrome));
        Assert.Equal(BrowserFamily.Chrome, chrome.Browser);
        Assert.Equal(ChromePath, chrome.BrowserExecutablePath);
        Assert.False(validation.Authority.TryAuthorize(
            "chrome-extension://AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/",
            out _));
        Assert.False(validation.Authority.TryAuthorize(
            "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            out _));
    }

    [Fact]
    public void WildcardOrMismatchedOrigin_IsRejectedBeforeSignatureTrust()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var document = Document() with
        {
            AllowedExtensions =
            [
                new(
                    BrowserFamily.Chrome,
                    "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    "chrome-extension://*/",
                    ChromePath),
            ],
        };

        var validation = SignAndVerify(document, signer);

        Assert.False(validation.Valid);
        Assert.Equal(BrowserConnectorReasonCodes.AuthorityInvalid, validation.ReasonCode);
    }

    [Fact]
    public void ExpiredOrTamperedAuthority_FailsClosed()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = Sign(Document(), signer);
        var keys = Keys(signer);

        var expired = BrowserConnectorAuthorityVerifier.Verify(signed, keys, Now.AddDays(2));
        var tampered = BrowserConnectorAuthorityVerifier.Verify(
            signed with { Revision = signed.Revision + 1 },
            keys,
            Now);

        Assert.False(expired.Valid);
        Assert.False(tampered.Valid);
        Assert.Null(expired.Authority);
        Assert.Null(tampered.Authority);
    }

    [Fact]
    public void SchemaV1_CannotAuthorizeProduction()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var validation = SignAndVerify(Document() with { SchemaVersion = 1 }, signer);

        Assert.False(validation.Valid);
        Assert.Null(validation.Authority);
    }

    [Fact]
    public void TamperedBrowserExecutablePath_InvalidatesSignature()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = Sign(Document(), signer);
        var changed = signed with
        {
            AllowedExtensions = signed.AllowedExtensions
                .Select(entry => entry.Browser == BrowserFamily.Chrome
                    ? entry with
                    {
                        BrowserExecutablePath =
                            @"C:\Program Files\Google\Chrome Beta\Application\chrome.exe",
                    }
                    : entry)
                .ToArray(),
        };

        var validation = BrowserConnectorAuthorityVerifier.Verify(
            changed,
            Keys(signer),
            Now);

        Assert.False(validation.Valid);
        Assert.Null(validation.Authority);
    }

    [Theory]
    [InlineData(@"\\server\share\chrome.exe")]
    [InlineData(@"chrome.exe")]
    [InlineData(@"%ProgramFiles%\Google\Chrome\Application\chrome.exe")]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\..\chrome.exe")]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\msedge.exe")]
    public void UnsafeOrWrongBrowserPath_IsRejectedBeforeSignatureTrust(string path)
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var document = Document() with
        {
            AllowedExtensions =
            [
                Document().AllowedExtensions[0] with
                {
                    BrowserExecutablePath = path,
                },
            ],
        };

        var validation = SignAndVerify(document, signer);

        Assert.False(validation.Valid);
        Assert.Null(validation.Authority);
    }

    [Fact]
    public void OversizedBrowserPath_IsRejected()
    {
        var path = @"C:\" + new string('a', 500) + @"\chrome.exe";

        Assert.False(BrowserExecutablePathPolicy.IsValidAuthorityPath(
            path,
            BrowserFamily.Chrome));
    }

    [Fact]
    public void PaddedOrNonUrlSignatureEncoding_IsRejected()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = Sign(Document(), signer);
        var keys = Keys(signer);

        var padded = BrowserConnectorAuthorityVerifier.Verify(
            signed with { Signature = signed.Signature + "==" },
            keys,
            Now);
        var standardAlphabet = BrowserConnectorAuthorityVerifier.Verify(
            signed with { Signature = signed.Signature.Replace('-', '+') },
            keys,
            Now);

        Assert.False(padded.Valid);
        if (signed.Signature.Contains('-', StringComparison.Ordinal))
            Assert.False(standardAlphabet.Valid);
    }

    internal static VerifiedBrowserConnectorAuthority VerifiedAuthority(
        string chromePath = ChromePath)
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return Assert.IsType<VerifiedBrowserConnectorAuthority>(
            SignAndVerify(Document(chromePath), signer).Authority);
    }

    private static BrowserConnectorAuthorityDocument Document(
        string chromePath = ChromePath) => new(
        BrowserConnectorAuthorityVerifier.CurrentSchemaVersion,
        7,
        Now.AddMinutes(-1),
        Now.AddDays(1),
        "browser-authority-test",
        [
            new(
                BrowserFamily.Chrome,
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/",
                chromePath),
            new(
                BrowserFamily.Edge,
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "chrome-extension://bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb/",
                EdgePath),
        ],
        string.Empty);

    private static BrowserConnectorAuthorityValidation SignAndVerify(
        BrowserConnectorAuthorityDocument document,
        ECDsa signer) =>
        BrowserConnectorAuthorityVerifier.Verify(Sign(document, signer), Keys(signer), Now);

    private static BrowserConnectorAuthorityDocument Sign(
        BrowserConnectorAuthorityDocument document,
        ECDsa signer)
    {
        var signature = signer.SignData(
            Encoding.UTF8.GetBytes(BrowserConnectorAuthorityVerifier.BuildCanonical(document)),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return document with
        {
            Signature = BrowserConnectorAuthorityVerifier.Base64UrlEncode(signature),
        };
    }

    private static IReadOnlyDictionary<string, string> Keys(ECDsa signer) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["browser-authority-test"] = Convert.ToBase64String(
                signer.ExportSubjectPublicKeyInfo()),
        };
}
