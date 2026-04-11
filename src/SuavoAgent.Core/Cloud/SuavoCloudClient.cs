using System.Text;
using System.Text.Json;
using SuavoAgent.Core.Config;

namespace SuavoAgent.Core.Cloud;

public sealed class SuavoCloudClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly HmacSigner _signer;
    private readonly AgentOptions _options;

    public SuavoCloudClient(AgentOptions options)
    {
        _options = options;
        _signer = new HmacSigner(options.ApiKey ?? throw new InvalidOperationException("ApiKey is required"));

        var uri = new Uri(options.CloudUrl);
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"CloudUrl must use HTTPS, got: {uri.Scheme}");

        _http = new HttpClient
        {
            BaseAddress = uri,
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public async Task<JsonElement?> HeartbeatAsync(object payload, CancellationToken ct)
    {
        return await PostSignedAsync("/api/agent/heartbeat", payload, ct);
    }

    public async Task<JsonElement?> SyncRxAsync(object payload, CancellationToken ct)
    {
        return await PostSignedAsync("/api/agent/sync", payload, ct);
    }

    public async Task SendPatientDetailsAsync(string rxNumber, object details, string commandId, CancellationToken ct)
    {
        await PostSignedAsync("/api/agent/patient-details", new { rxNumber, details, commandId }, ct);
    }

    private async Task<JsonElement?> PostSignedAsync(string path, object payload, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(payload);
        var timestamp = DateTimeOffset.UtcNow.ToString("o");
        var signature = _signer.Sign(timestamp, body);

        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.Add("x-agent-api-key", _options.ApiKey);
        request.Headers.Add("x-agent-timestamp", timestamp);
        request.Headers.Add("x-agent-signature", signature);

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(responseBody))
            return null;

        return JsonSerializer.Deserialize<JsonElement>(responseBody);
    }

    public record AuditArchiveAck(string ArchiveId, string ArchiveDigest, string Timestamp);

    public async Task<AuditArchiveAck?> UploadAuditArchiveAsync(string archiveJson, string digest, CancellationToken ct)
    {
        var response = await PostSignedAsync("/api/agent/audit-archive",
            new { archive = archiveJson, archiveDigest = digest }, ct);
        if (response == null) return null;
        try
        {
            return JsonSerializer.Deserialize<AuditArchiveAck>(response.Value.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { return null; }
    }

    public void Dispose() => _http.Dispose();
}
