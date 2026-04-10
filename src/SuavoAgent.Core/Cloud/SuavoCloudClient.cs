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
        _http = new HttpClient
        {
            BaseAddress = new Uri(options.CloudUrl),
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

    private async Task<JsonElement?> PostSignedAsync(string path, object payload, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(payload);
        var timestamp = DateTimeOffset.UtcNow.ToString("o");
        var signature = _signer.Sign(timestamp, body);

        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.Add("x-agent-id", _options.AgentId);
        request.Headers.Add("x-timestamp", timestamp);
        request.Headers.Add("x-signature", signature);

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(responseBody))
            return null;

        return JsonSerializer.Deserialize<JsonElement>(responseBody);
    }

    public void Dispose() => _http.Dispose();
}
