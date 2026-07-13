using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Pricing;

namespace SuavoAgent.Core.Cloud;

internal sealed record PricingUploadDescriptor(
    Guid Id,
    string Label,
    long SizeBytes,
    string Sha256,
    string ContentType,
    int ValidationVersion);

internal sealed class PricingUploadTransportException(string code) : IOException(code)
{
    public string Code { get; } = code;
}

internal interface IPricingUploadCloudClient
{
    Task<IReadOnlyList<PricingUploadDescriptor>> PollAsync(CancellationToken ct);
    Task DownloadAsync(PricingUploadDescriptor descriptor, string temporaryPath, CancellationToken ct);
    Task AckFetchedAsync(PricingUploadDescriptor descriptor, CancellationToken ct);
    Task AckLifecycleAsync(Guid id, bool consumed, string? reasonCode, CancellationToken ct);
}

/// <summary>Exact HMAC transport for the opaque pricing intake lifecycle.</summary>
internal sealed class PricingUploadCloudClient : IPricingUploadCloudClient, IDisposable
{
    private const int MaxMetadataBytes = 64 * 1024;
    private const string XlsxMime =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private readonly HttpClient _http;
    private readonly HmacSigner _signer;

    internal PricingUploadCloudClient(AgentOptions options)
        : this(options, CreateHandler(options)) { }

    internal PricingUploadCloudClient(AgentOptions options, HttpMessageHandler handler)
    {
        _signer = new HmacSigner(options.ApiKey ??
            throw new InvalidOperationException("ApiKey is required"));
        var cloud = new Uri(options.CloudUrl);
        if (cloud.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("CloudUrl must use HTTPS");
        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = cloud,
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public async Task<IReadOnlyList<PricingUploadDescriptor>> PollAsync(CancellationToken ct)
    {
        const string path = "/api/agent/pricing-uploads/pending";
        using var request = Signed(HttpMethod.Get, path, null);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccess(response).ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(response.Content, MaxMetadataBytes, ct).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                MaxDepth = 8,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            var root = document.RootElement;
            if (!Exact(root, "success", "capabilityVersion", "uploads") ||
                !root.GetProperty("success").GetBoolean() ||
                root.GetProperty("capabilityVersion").GetInt32() != 1 ||
                root.GetProperty("uploads").ValueKind != JsonValueKind.Array)
                throw new PricingUploadTransportException("pricing_upload_manifest_invalid");
            var uploads = new List<PricingUploadDescriptor>();
            foreach (var item in root.GetProperty("uploads").EnumerateArray())
            {
                if (!Exact(item, "id", "label", "sizeBytes", "sha256", "contentType", "validationVersion") ||
                    !Guid.TryParseExact(item.GetProperty("id").GetString(), "D", out var id) ||
                    item.GetProperty("label").GetString() != "Pricing workbook" ||
                    !item.GetProperty("sizeBytes").TryGetInt64(out var size) ||
                    size is <= 0 or > PricingWorkbookContentPolicy.MaxArchiveBytes ||
                    !IsDigest(item.GetProperty("sha256").GetString()) ||
                    item.GetProperty("contentType").GetString() != XlsxMime ||
                    item.GetProperty("validationVersion").GetInt32() != 1)
                    throw new PricingUploadTransportException("pricing_upload_manifest_invalid");
                uploads.Add(new(
                    id,
                    "Pricing workbook",
                    size,
                    item.GetProperty("sha256").GetString()!,
                    XlsxMime,
                    1));
            }
            return uploads;
        }
        catch (JsonException)
        {
            throw new PricingUploadTransportException("pricing_upload_manifest_invalid");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public async Task DownloadAsync(
        PricingUploadDescriptor descriptor,
        string temporaryPath,
        CancellationToken ct)
    {
        var path = $"/api/agent/pricing-uploads/{descriptor.Id:D}/content";
        using var request = Signed(HttpMethod.Get, path, null);
        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureSuccess(response).ConfigureAwait(false);
        if (response.Content.Headers.ContentType?.MediaType != XlsxMime ||
            response.Content.Headers.ContentLength != descriptor.SizeBytes ||
            Header(response, "X-Suavo-Upload-Id") != descriptor.Id.ToString("D") ||
            Header(response, "X-Suavo-Content-Sha256") != descriptor.Sha256 ||
            Header(response, "X-Suavo-Validation-Version") != "1")
            throw new PricingUploadTransportException("pricing_upload_content_headers_invalid");

        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long written = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0) break;
                written += read;
                if (written > descriptor.SizeBytes || written > PricingWorkbookContentPolicy.MaxArchiveBytes)
                    throw new PricingUploadTransportException("pricing_upload_content_oversize");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
            await output.FlushAsync(ct).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            var digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (written != descriptor.SizeBytes || digest != descriptor.Sha256)
                throw new PricingUploadTransportException("pricing_upload_content_integrity_mismatch");
        }
        catch
        {
            DeleteTemporaryOrThrow(temporaryPath);
            throw;
        }

        PricingWorkbookValidation validation;
        try
        {
            validation = PricingWorkbookContentPolicy.Validate(temporaryPath);
        }
        catch
        {
            DeleteTemporaryOrThrow(temporaryPath);
            throw;
        }
        if (validation.SizeBytes != descriptor.SizeBytes || validation.Sha256 != descriptor.Sha256)
        {
            DeleteTemporaryOrThrow(temporaryPath);
            throw new PricingUploadTransportException("pricing_upload_native_validation_mismatch");
        }
    }

    public Task AckFetchedAsync(PricingUploadDescriptor descriptor, CancellationToken ct) =>
        PostReceiptAsync(
            $"/api/agent/pricing-uploads/{descriptor.Id:D}/fetched",
            new { receiptVersion = 1, sha256 = descriptor.Sha256, sizeBytes = descriptor.SizeBytes },
            "fetched",
            ct);

    public Task AckLifecycleAsync(
        Guid id,
        bool consumed,
        string? reasonCode,
        CancellationToken ct) =>
        PostReceiptAsync(
            $"/api/agent/pricing-uploads/{id:D}/lifecycle",
            new
            {
                receiptVersion = 1,
                status = consumed ? "consumed" : "failed",
                reasonCode = consumed ? null : reasonCode,
            },
            consumed ? "consumed" : "failed",
            ct);

    private async Task PostReceiptAsync(string path, object payload, string expectedState, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(payload);
        using var request = Signed(HttpMethod.Post, path, body);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccess(response).ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(response.Content, MaxMetadataBytes, ct).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (!Exact(root, "success", "state") ||
                !root.GetProperty("success").GetBoolean() ||
                root.GetProperty("state").GetString() != expectedState)
                throw new PricingUploadTransportException("pricing_upload_receipt_invalid");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private HttpRequestMessage Signed(HttpMethod method, string path, string? body)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        _signer.ApplyHeaders(request, body ?? "");
        return request;
    }

    private static async Task EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        await Task.CompletedTask;
        throw new PricingUploadTransportException(
            response.StatusCode == HttpStatusCode.UpgradeRequired
                ? "pricing_upload_upgrade_required"
                : "pricing_upload_cloud_unavailable");
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content, int maxBytes, CancellationToken ct)
    {
        if (content.Headers.ContentLength is > 0 && content.Headers.ContentLength > maxBytes)
            throw new PricingUploadTransportException("pricing_upload_response_oversize");
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maxBytes)
                throw new PricingUploadTransportException("pricing_upload_response_oversize");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) && values.ToArray() is [var value]
            ? value
            : null;

    private static bool Exact(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var found = element.EnumerateObject().Select(property => property.Name).ToArray();
        return found.Length == names.Length && found.ToHashSet(StringComparer.Ordinal).SetEquals(names);
    }

    private static bool IsDigest(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static HttpMessageHandler CreateHandler(AgentOptions options)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        if (!string.IsNullOrEmpty(options.CloudCertPin))
        {
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
            {
                if (errors != System.Net.Security.SslPolicyErrors.None || cert is null) return false;
                var digest = Convert.ToBase64String(SHA256.HashData(cert.GetPublicKey()));
                return options.CloudCertPin.Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Contains(digest, StringComparer.Ordinal);
            };
        }
        return handler;
    }

    private static void DeleteTemporaryOrThrow(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            throw new PricingUploadTransportException("pricing_upload_temp_cleanup_pending");
        }
        catch (UnauthorizedAccessException)
        {
            throw new PricingUploadTransportException("pricing_upload_temp_cleanup_pending");
        }
        if (File.Exists(path))
            throw new PricingUploadTransportException("pricing_upload_temp_cleanup_pending");
    }

    public void Dispose() => _http.Dispose();
}
