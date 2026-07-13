using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using SuavoAgent.Setup;
using Xunit;

namespace SuavoAgent.Setup.Tests;

public sealed class BinaryDownloaderFailurePathTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-binary-downloader-matrix-" + Guid.NewGuid().ToString("N"));

    public BinaryDownloaderFailurePathTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("../v3.80.0")]
    public async Task FullDownload_RejectsUnpinnedReleaseBeforeCreatingDestination(string release)
    {
        var destination = Path.Combine(_root, Guid.NewGuid().ToString("N"));

        Assert.False(await BinaryDownloader.DownloadAndVerifyAsync(release, destination));
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task SignedMetadataDownload_VerifiesAndPersistsAuthenticatedManifest()
    {
        var checksums = Fixture("ChecksumsB64");
        var signature = Fixture("SignatureB64");
        using var http = new HttpClient(new RouteHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith(".sig", StringComparison.Ordinal)
                ? Bytes(signature)
                : Bytes(checksums)));

        var result = await InvokeChecksumDownload(http, "https://release.example/v3.15.2", _root);

        Assert.NotNull(result);
        Assert.Contains("SuavoAgent.Core.exe", result.Keys);
        Assert.Contains("field-release-receipt.json", result.Keys);
        Assert.Equal(checksums, File.ReadAllBytes(Path.Combine(_root, "checksums.sha256")));
        Assert.Equal(signature, File.ReadAllBytes(Path.Combine(_root, "checksums.sha256.sig")));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp-*"));
    }

    [Fact]
    public async Task SignedMetadataDownload_DeletesUnauthenticatedArtifacts()
    {
        var checksums = Fixture("ChecksumsB64");
        var signature = Fixture("SignatureB64");
        signature[^1] ^= 0x01;
        File.WriteAllText(Path.Combine(_root, "checksums.sha256"), "stale");
        File.WriteAllText(Path.Combine(_root, "checksums.sha256.sig"), "stale");
        using var http = new HttpClient(new RouteHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith(".sig", StringComparison.Ordinal)
                ? Bytes(signature)
                : Bytes(checksums)));

        var result = await InvokeChecksumDownload(http, "https://release.example/v3.15.2", _root);

        Assert.Null(result);
        Assert.False(File.Exists(Path.Combine(_root, "checksums.sha256")));
        Assert.False(File.Exists(Path.Combine(_root, "checksums.sha256.sig")));
    }

    [Fact]
    public async Task SignedMetadataDownload_ContainsNonTransientHttpFailureAndCleansState()
    {
        using var http = new HttpClient(new RouteHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound)));
        File.WriteAllText(Path.Combine(_root, "checksums.sha256"), "stale");

        var result = await InvokeChecksumDownload(http, "https://release.example/missing", _root);

        Assert.Null(result);
        Assert.False(File.Exists(Path.Combine(_root, "checksums.sha256")));
    }

    [Fact]
    public async Task BinaryDownload_WritesNonEmptyPayloadAndReportsSuccess()
    {
        var payload = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        using var http = new HttpClient(new RouteHandler(_ => Bytes(payload)));
        var destination = Path.Combine(_root, "binary.exe");

        var downloaded = await InvokeFileDownload(http, destination, maxBytes: payload.Length);

        Assert.True(downloaded);
        Assert.Equal(payload, File.ReadAllBytes(destination));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public async Task BinaryDownload_RejectsInvalidDeclaredLengthAndRemovesPartialFile(long length)
    {
        var response = Bytes([1]);
        response.Content.Headers.ContentLength = length;
        using var http = new HttpClient(new RouteHandler(_ => response));
        var destination = Path.Combine(_root, "invalid-length.exe");

        var downloaded = await InvokeFileDownload(http, destination, maxBytes: 64);

        Assert.False(downloaded);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task BinaryDownload_EnforcesStreamingLimitWhenLengthIsUnknown()
    {
        using var http = new HttpClient(new RouteHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new UnknownLengthContent(new byte[65]),
            }));
        var destination = Path.Combine(_root, "stream-overflow.exe");

        var downloaded = await InvokeFileDownload(http, destination, maxBytes: 64);

        Assert.False(downloaded);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task BinaryDownload_RejectsEmptyUnknownLengthBody()
    {
        using var http = new HttpClient(new RouteHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new UnknownLengthContent([]),
            }));
        var destination = Path.Combine(_root, "empty.exe");

        Assert.False(await InvokeFileDownload(http, destination, maxBytes: 64));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task BinaryDownload_PropagatesOnlyTransientHttpFailureForRetry()
    {
        using var transient = new HttpClient(new RouteHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var permanent = new HttpClient(new RouteHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound)));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            InvokeFileDownload(
                transient,
                Path.Combine(_root, "transient.exe"),
                maxBytes: 64));
        Assert.False(await InvokeFileDownload(
            permanent,
            Path.Combine(_root, "permanent.exe"),
            maxBytes: 64));
    }

    [Fact]
    public async Task RetryWrapper_RetriesOneTransientFailureThenReturnsExactResult()
    {
        var calls = 0;
        Func<Task<int>> operation = () =>
        {
            calls++;
            return calls == 1
                ? Task.FromException<int>(new HttpRequestException(
                    "temporary",
                    null,
                    HttpStatusCode.BadGateway))
                : Task.FromResult(42);
        };

        var result = await InvokeRetry(operation);

        Assert.Equal(42, result);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.HttpVersionNotSupported, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    public void TransientClassifier_IsLimitedToNetworkAnd5xx(
        HttpStatusCode? status,
        bool expected)
    {
        var exception = new HttpRequestException("test", null, status);
        Assert.Equal(expected, InvokePrivate<bool>("IsTransientHttpFailure", exception));
    }

    [Theory]
    [InlineData(typeof(HttpRequestException), "SETUP-DOWNLOAD-NETWORK")]
    [InlineData(typeof(InvalidDataException), "SETUP-DOWNLOAD-INVALID")]
    [InlineData(typeof(UnauthorizedAccessException), "SETUP-DOWNLOAD-ACCESS")]
    [InlineData(typeof(IOException), "SETUP-DOWNLOAD-IO")]
    [InlineData(typeof(CryptographicException), "SETUP-DOWNLOAD-TRUST")]
    [InlineData(typeof(InvalidOperationException), "SETUP-DOWNLOAD-FAILED")]
    public void FailureCode_IsStableAndNeverReflectsExceptionText(Type type, string expected)
    {
        var exception = (Exception)Activator.CreateInstance(type, "sensitive")!;
        Assert.Equal(expected, InvokePrivate<string>("DownloadFailureCode", exception));
    }

    [Fact]
    public void CohortCleanup_DeletesOnlyOwnedReleaseArtifacts()
    {
        foreach (var binary in BinaryDownloader.RequiredBinaries)
            File.WriteAllText(Path.Combine(_root, binary), "partial");
        File.WriteAllText(Path.Combine(_root, "field-release-receipt.json"), "partial");
        File.WriteAllText(Path.Combine(_root, "operator-evidence.txt"), "preserve");

        _ = InvokePrivate<object?>("CleanupBinaries", _root);

        Assert.All(BinaryDownloader.RequiredBinaries, binary =>
            Assert.False(File.Exists(Path.Combine(_root, binary))));
        Assert.False(File.Exists(Path.Combine(_root, "field-release-receipt.json")));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(_root, "operator-evidence.txt")));
    }

    private static async Task<Dictionary<string, string>?> InvokeChecksumDownload(
        HttpClient http,
        string baseUrl,
        string directory)
    {
        var task = (Task<Dictionary<string, string>?>)Method(
                "DownloadAndVerifyChecksumsAsync")
            .Invoke(null, [http, baseUrl, directory])!;
        return await task;
    }

    private static async Task<bool> InvokeFileDownload(
        HttpClient http,
        string destination,
        long maxBytes)
    {
        var task = (Task<bool>)Method("DownloadFileAsync").Invoke(
            null,
            [http, "https://release.example/binary.exe", destination, "test binary", maxBytes])!;
        return await task;
    }

    private static async Task<int> InvokeRetry(Func<Task<int>> operation)
    {
        var method = Method("RetryTransientAsync").MakeGenericMethod(typeof(int));
        var task = (Task<int>)method.Invoke(null, [operation, "test operation"])!;
        return await task;
    }

    private static T InvokePrivate<T>(string name, params object?[] args) =>
        (T)Method(name).Invoke(null, args)!;

    private static MethodInfo Method(string name)
    {
        var method = typeof(BinaryDownloader).GetMethod(
            name,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method;
    }

    private static byte[] Fixture(string fieldName)
    {
        var field = typeof(BinaryDownloaderTests).GetField(
            fieldName,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return Convert.FromBase64String((string)field.GetRawConstantValue()!);
    }

    private static HttpResponseMessage Bytes(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private sealed class RouteHandler(
        Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response(request));
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
