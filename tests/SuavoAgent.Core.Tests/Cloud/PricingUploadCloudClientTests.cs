using System.Net;
using System.Text;
using ClosedXML.Excel;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public sealed class PricingUploadCloudClientTests : IDisposable
{
    private const string XlsxMime =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"suavo_pricing_transport_{Guid.NewGuid():N}");

    public PricingUploadCloudClientTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task PollAsync_UsesExactHmacGetAndParsesBoundedOpaqueManifest()
    {
        var id = Guid.NewGuid();
        var sha = new string('a', 64);
        var handler = new RecordingHandler(_ => JsonResponse($$"""
            {"success":true,"capabilityVersion":1,"uploads":[{"id":"{{id:D}}","label":"Pricing workbook","sizeBytes":1024,"sha256":"{{sha}}","contentType":"{{XlsxMime}}","validationVersion":1}]}
            """));
        using var client = new PricingUploadCloudClient(Options(), handler);

        var uploads = await client.PollAsync(CancellationToken.None);

        var upload = Assert.Single(uploads);
        Assert.Equal(id, upload.Id);
        Assert.Equal(sha, upload.Sha256);
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal("/api/agent/pricing-uploads/pending", handler.Path);
        Assert.Equal("2", handler.Headers["x-agent-auth-version"]);
        Assert.Equal("test-secret", handler.Headers["x-agent-api-key"]);
        Assert.Matches("^[a-f0-9]{64}$", handler.Headers["x-agent-signature"]);
    }

    [Fact]
    public async Task DownloadAsync_VerifiesHeadersSizeHashAndNativePolicy()
    {
        var bytes = WorkbookBytes();
        var descriptor = Descriptor(bytes);
        var handler = new RecordingHandler(_ => ContentResponse(descriptor, bytes));
        using var client = new PricingUploadCloudClient(Options(), handler);
        var target = Path.Combine(_root, "opaque.tmp");

        await client.DownloadAsync(descriptor, target, CancellationToken.None);

        Assert.True(File.Exists(target));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(target));
        Assert.Equal(
            $"/api/agent/pricing-uploads/{descriptor.Id:D}/content",
            handler.Path);
    }

    [Fact]
    public async Task DownloadAsync_DeletesPartialFileWhenDigestDoesNotMatch()
    {
        var bytes = WorkbookBytes();
        var descriptor = Descriptor(bytes) with { Sha256 = new string('0', 64) };
        var handler = new RecordingHandler(_ => ContentResponse(descriptor, bytes));
        using var client = new PricingUploadCloudClient(Options(), handler);
        var target = Path.Combine(_root, "bad.tmp");

        var error = await Assert.ThrowsAsync<PricingUploadTransportException>(
            () => client.DownloadAsync(descriptor, target, CancellationToken.None));

        Assert.Equal("pricing_upload_content_integrity_mismatch", error.Code);
        Assert.False(File.Exists(target));
    }

    private static AgentOptions Options() => new()
    {
        ApiKey = "test-secret",
        CloudUrl = "https://example.test",
    };

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage ContentResponse(
        PricingUploadDescriptor descriptor,
        byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        };
        response.Content.Headers.ContentType = new(XlsxMime);
        response.Headers.TryAddWithoutValidation("X-Suavo-Upload-Id", descriptor.Id.ToString("D"));
        response.Headers.TryAddWithoutValidation("X-Suavo-Content-Sha256", descriptor.Sha256);
        response.Headers.TryAddWithoutValidation("X-Suavo-Validation-Version", "1");
        return response;
    }

    private static PricingUploadDescriptor Descriptor(byte[] bytes) => new(
        Guid.NewGuid(),
        "Pricing workbook",
        bytes.LongLength,
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
        XlsxMime,
        1);

    private static byte[] WorkbookBytes()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Pricing");
        sheet.Cell("A1").Value = "NDC";
        sheet.Cell("B1").Value = "Drug Name";
        sheet.Cell("C1").Value = "Price";
        sheet.Cell("A2").Value = "00000000000";
        sheet.Cell("B2").Value = "Example";
        sheet.Cell("C2").Value = 1.25;
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        internal HttpMethod? Method { get; private set; }
        internal string? Path { get; private set; }
        internal Dictionary<string, string> Headers { get; private set; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            Path = request.RequestUri?.PathAndQuery;
            Headers = request.Headers.ToDictionary(
                pair => pair.Key,
                pair => Assert.Single(pair.Value),
                StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(response(request));
        }
    }
}
