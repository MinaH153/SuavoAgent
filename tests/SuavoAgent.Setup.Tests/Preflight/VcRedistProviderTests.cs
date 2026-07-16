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

    private sealed class UnknownLengthContent(byte[] body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) => stream.WriteAsync(body).AsTask();

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(body, writable: false));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class ContentHandler(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
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

    [Fact]
    public async Task Unknown_length_stream_is_stopped_at_hard_cap_and_partial_is_deleted()
    {
        var body = new byte[] { 1, 2, 3, 4, 5 };
        using var http = new HttpClient(new ContentHandler(new UnknownLengthContent(body)));
        var dest = Path.Combine(Path.GetTempPath(), $"vcr-{Guid.NewGuid():N}.exe");
        var provider = new VcRedistProvider(
            http,
            "https://example/vc_redist.x64.exe",
            Sha256Hex(body),
            maxDownloadBytes: 4);

        await Assert.ThrowsAsync<VcRedistVerificationException>(
            () => provider.EnsureLocalAsync(dest, CancellationToken.None));

        Assert.False(File.Exists(dest));
        Assert.Empty(Directory.GetFiles(
            Path.GetDirectoryName(dest)!,
            Path.GetFileName(dest) + ".download-*"));
    }

    [Fact]
    public async Task Oversized_content_length_is_rejected_without_destination()
    {
        var content = new ByteArrayContent([1]);
        content.Headers.ContentLength = 5;
        using var http = new HttpClient(new ContentHandler(content));
        var dest = Path.Combine(Path.GetTempPath(), $"vcr-{Guid.NewGuid():N}.exe");
        var provider = new VcRedistProvider(
            http,
            "https://example/vc_redist.x64.exe",
            Sha256Hex([1]),
            maxDownloadBytes: 4);

        await Assert.ThrowsAsync<VcRedistVerificationException>(
            () => provider.EnsureLocalAsync(dest, CancellationToken.None));

        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task Existing_destination_is_never_replaced()
    {
        var body = new byte[] { 1, 2, 3, 4 };
        using var http = new HttpClient(new StubHandler(body));
        var dest = Path.Combine(
            Path.GetTempPath(),
            $"vcr-{Guid.NewGuid():N}.exe");
        await File.WriteAllBytesAsync(dest, [9, 9, 9]);
        try
        {
            var provider = new VcRedistProvider(
                http,
                "https://example/vc_redist.x64.exe",
                Sha256Hex(body));

            await Assert.ThrowsAsync<VcRedistVerificationException>(
                () => provider.EnsureLocalAsync(dest, CancellationToken.None));

            Assert.Equal(new byte[] { 9, 9, 9 }, await File.ReadAllBytesAsync(dest));
        }
        finally
        {
            File.Delete(dest);
        }
    }
}
