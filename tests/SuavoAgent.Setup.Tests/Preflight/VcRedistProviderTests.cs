// tests/SuavoAgent.Setup.Tests/Preflight/VcRedistProviderTests.cs
using System;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Setup.Preflight;
using Xunit;

namespace SuavoAgent.Setup.Tests.Preflight;

public class VcRedistProviderTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        public StubHandler(byte[] body) => _body = body;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_body) });
    }

    private static string Sha256Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    [Fact]
    public async Task Downloads_and_returns_path_when_sha_matches()
    {
        var body = new byte[] { 1, 2, 3, 4 };
        var http = new HttpClient(new StubHandler(body));
        var dest = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vcr-{Guid.NewGuid():N}.exe");
        var provider = new VcRedistProvider(http, "https://example/vc_redist.x64.exe", Sha256Hex(body));

        var path = await provider.EnsureLocalAsync(dest, CancellationToken.None);

        Assert.Equal(dest, path);
        Assert.True(System.IO.File.Exists(dest));
        System.IO.File.Delete(dest);
    }

    [Fact]
    public async Task Throws_when_sha_mismatches()
    {
        var http = new HttpClient(new StubHandler(new byte[] { 9, 9 }));
        var dest = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vcr-{Guid.NewGuid():N}.exe");
        var provider = new VcRedistProvider(http, "https://example/vc_redist.x64.exe", "deadbeef");

        await Assert.ThrowsAsync<VcRedistVerificationException>(
            () => provider.EnsureLocalAsync(dest, CancellationToken.None));
        Assert.False(System.IO.File.Exists(dest)); // partial/bad download must not be left behind
    }
}
