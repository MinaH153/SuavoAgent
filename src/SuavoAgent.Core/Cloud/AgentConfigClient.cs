using System.Text.Json;
using SuavoAgent.Contracts.Cloud;
using SuavoAgent.Core.Config;

namespace SuavoAgent.Core.Cloud;

/// <summary>
/// Cloud → agent config-override client. Polls GET /api/agent/config with
/// HMAC auth (same signer as SuavoCloudClient) and returns the current
/// override set. Never throws — errors surface as an empty result so the
/// caller keeps running with whatever overrides were already on disk.
/// </summary>
public interface IAgentConfigClient
{
    /// <summary>
    /// Fetches the current override set from cloud. Returns null on auth
    /// failure, network error, or invalid response shape — caller should
    /// preserve the last good on-disk copy in that case.
    /// </summary>
    Task<ConfigOverrideResponse?> FetchAsync(CancellationToken ct);
}

public sealed class AgentConfigClient : IAgentConfigClient
{
    private const string Endpoint = "/api/agent/config";

    private readonly HttpClient _http;
    private readonly HmacSigner _signer;
    private readonly AgentOptions _options;
    private readonly ILogger<AgentConfigClient> _logger;

    public AgentConfigClient(
        HttpClient http,
        AgentOptions options,
        ILogger<AgentConfigClient> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new InvalidOperationException("AgentConfigClient requires Agent.ApiKey");
        _signer = new HmacSigner(options.ApiKey);
    }

    public async Task<ConfigOverrideResponse?> FetchAsync(CancellationToken ct)
    {
        try
        {
            var timestamp = DateTimeOffset.UtcNow.ToString("o");
            // GET body is empty — signer operates on "timestamp:" with no body.
            var signature = _signer.Sign(timestamp, string.Empty);

            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            request.Headers.Add("x-agent-api-key", _options.ApiKey);
            request.Headers.Add("x-agent-timestamp", timestamp);
            request.Headers.Add("x-agent-signature", signature);

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "AgentConfigClient: non-success {Status} from {Endpoint}",
                    (int)response.StatusCode, Endpoint);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body))
                return null;

            var parsed = JsonSerializer.Deserialize<ConfigOverrideResponse>(
                body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (parsed == null || !parsed.Success)
            {
                _logger.LogWarning("AgentConfigClient: unexpected response shape");
                return null;
            }

            return parsed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AgentConfigClient: fetch failed");
            return null;
        }
    }
}
