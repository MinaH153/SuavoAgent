using System.Text;
using System.Text.Json;
using SuavoAgent.Helper.SystemObservers.BrowserConnector;
using Xunit;

namespace SuavoAgent.Helper.Tests.SystemObservers;

/// <summary>
/// Adversarial shape and root-key validation for the local browser connector
/// trust store. A syntactically parseable document is still denied unless the
/// closed schema and bounded key inventory are exact.
/// </summary>
public sealed class BrowserConnectorTrustStoreBoundaryMatrixTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Fact]
    public void NullSourceIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
            BrowserConnectorTrustStore.Load(null!, Now));
    }

    [Fact]
    public void ProductionLoadFailsClosedOffWindows()
    {
        if (OperatingSystem.IsWindows())
            return;

        var result = BrowserConnectorTrustStore.LoadProduction(Now);

        Assert.False(result.Valid);
        Assert.Equal(BrowserConnectorReasonCodes.AuthorityInvalid, result.ReasonCode);
        Assert.Null(result.Authority);
    }

    [Theory]
    [MemberData(nameof(MalformedDocumentPairs))]
    public void MalformedOrNonExactDocumentsAreDenied(string authority, string roots)
    {
        var result = BrowserConnectorTrustStore.Load(
            new TextSource(authority, roots),
            Now);

        Assert.False(result.Valid);
        Assert.Equal(BrowserConnectorReasonCodes.AuthorityInvalid, result.ReasonCode);
        Assert.Null(result.Authority);
    }

    public static IEnumerable<object[]> MalformedDocumentPairs()
    {
        var exactAuthority = AuthorityJson();
        var exactRoots = RootsJson(new { keyId = "root-1", publicKeySpkiBase64 = "AQ==" });

        yield return ["not-json", exactRoots];
        yield return [exactAuthority, "not-json"];
        yield return ["[]", exactRoots];
        yield return [exactAuthority, "[]"];
        yield return
        [
            exactAuthority.Replace(
                "\"signature\":\"\"",
                "\"signature\":\"\",\"fallbackKey\":\"bad\"",
                StringComparison.Ordinal),
            exactRoots,
        ];
        yield return
        [
            exactAuthority.Replace(
                "\"allowedExtensions\":[]",
                "\"allowedExtensions\":{}",
                StringComparison.Ordinal),
            exactRoots,
        ];
        yield return
        [
            exactAuthority.Replace(
                "\"allowedExtensions\":[]",
                "\"allowedExtensions\":[{\"browser\":\"chrome\",\"extensionId\":\"a\",\"origin\":\"o\"}]",
                StringComparison.Ordinal),
            exactRoots,
        ];
        yield return
        [
            exactAuthority,
            exactRoots.Replace(
                "\"trustedKeys\":[",
                "\"trustedKeys\":[],\"extra\":[",
                StringComparison.Ordinal),
        ];
        yield return
        [
            exactAuthority,
            exactRoots.Replace(
                "\"publicKeySpkiBase64\":\"AQ==\"",
                "\"publicKeySpkiBase64\":\"AQ==\",\"algorithm\":\"none\"",
                StringComparison.Ordinal),
        ];
        yield return
        [
            exactAuthority,
            "{\"schemaVersion\":1,\"trustedKeys\":{}}",
        ];
    }

    [Theory]
    [MemberData(nameof(InvalidRootInventories))]
    public void InvalidRootInventoryIsDenied(object rootsDocument)
    {
        var roots = JsonSerializer.Serialize(rootsDocument);

        var result = BrowserConnectorTrustStore.Load(
            new TextSource(AuthorityJson(), roots),
            Now);

        Assert.False(result.Valid);
        Assert.Equal(BrowserConnectorReasonCodes.AuthorityInvalid, result.ReasonCode);
    }

    public static IEnumerable<object[]> InvalidRootInventories()
    {
        yield return [new { schemaVersion = 2, trustedKeys = new[] { Root("root-1", "AQ==") } }];
        yield return [new { schemaVersion = 1, trustedKeys = Array.Empty<object>() }];
        yield return
        [
            new
            {
                schemaVersion = 1,
                trustedKeys = Enumerable.Range(0, 9)
                    .Select(index => Root($"root-{index}", "AQ=="))
                    .ToArray(),
            },
        ];
        yield return
        [
            new
            {
                schemaVersion = 1,
                trustedKeys = new[] { Root("duplicate", "AQ=="), Root("duplicate", "Ag==") },
            },
        ];
        yield return [new { schemaVersion = 1, trustedKeys = new[] { Root("", "AQ==") } }];
        yield return
        [
            new
            {
                schemaVersion = 1,
                trustedKeys = new[] { Root(new string('k', 65), "AQ==") },
            },
        ];
        yield return [new { schemaVersion = 1, trustedKeys = new[] { Root("root-1", "") } }];
        yield return
        [
            new
            {
                schemaVersion = 1,
                trustedKeys = new[] { Root("root-1", new string('A', 1_025)) },
            },
        ];
    }

    [Fact]
    public void SourceFailureIsDeniedAndOutputBuffersAreStillZeroed()
    {
        var source = new ThrowingSource();

        var result = BrowserConnectorTrustStore.Load(source, Now);

        Assert.False(result.Valid);
        Assert.All(source.Authority, value => Assert.Equal(0, value));
        Assert.All(source.Roots, value => Assert.Equal(0, value));
    }

    [Fact]
    public void ParsedDocumentsAreZeroedAfterSignatureDenial()
    {
        var source = new RetainingSource(
            Encoding.UTF8.GetBytes(AuthorityJson()),
            Encoding.UTF8.GetBytes(RootsJson(
                new { keyId = "root-1", publicKeySpkiBase64 = "AQ==" })));

        var result = BrowserConnectorTrustStore.Load(source, Now);

        Assert.False(result.Valid); // Deliberately unsigned authority.
        Assert.All(source.Authority, value => Assert.Equal(0, value));
        Assert.All(source.Roots, value => Assert.Equal(0, value));
    }

    private static object Root(string keyId, string key) => new
    {
        keyId,
        publicKeySpkiBase64 = key,
    };

    private static string AuthorityJson() => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        revision = 1,
        issuedAt = Now.AddMinutes(-1),
        expiresAt = Now.AddHours(1),
        keyId = "root-1",
        allowedExtensions = Array.Empty<object>(),
        signature = "",
    });

    private static string RootsJson(params object[] roots) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        trustedKeys = roots,
    });

    private sealed class TextSource(string authority, string roots)
        : IBrowserConnectorTrustStoreSource
    {
        public bool TryRead(out byte[] authorityDocument, out byte[] trustedRootsDocument)
        {
            authorityDocument = Encoding.UTF8.GetBytes(authority);
            trustedRootsDocument = Encoding.UTF8.GetBytes(roots);
            return true;
        }
    }

    private sealed class RetainingSource(byte[] authority, byte[] roots)
        : IBrowserConnectorTrustStoreSource
    {
        public byte[] Authority { get; } = authority;
        public byte[] Roots { get; } = roots;

        public bool TryRead(out byte[] authorityDocument, out byte[] trustedRootsDocument)
        {
            authorityDocument = Authority;
            trustedRootsDocument = Roots;
            return true;
        }
    }

    private sealed class ThrowingSource : IBrowserConnectorTrustStoreSource
    {
        public byte[] Authority { get; } = [1, 2, 3];
        public byte[] Roots { get; } = [4, 5, 6];

        public bool TryRead(out byte[] authorityDocument, out byte[] trustedRootsDocument)
        {
            authorityDocument = Authority;
            trustedRootsDocument = Roots;
            throw new InvalidOperationException("trust store read failed");
        }
    }
}
