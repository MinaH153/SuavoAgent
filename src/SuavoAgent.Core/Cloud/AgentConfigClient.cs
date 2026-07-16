using System.Text.Json;
using System.Net;
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
    string? LastFailureKind { get; }

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
    private readonly CloudAuthRecoveryCoordinator? _cloudAuthRecovery;

    public string? LastFailureKind { get; private set; }

    public AgentConfigClient(
        HttpClient http,
        AgentOptions options,
        ILogger<AgentConfigClient> logger,
        CloudAuthRecoveryCoordinator? cloudAuthRecovery = null)
    {
        _http = http;
        _options = options;
        _logger = logger;
        _cloudAuthRecovery = cloudAuthRecovery;
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new InvalidOperationException("AgentConfigClient requires Agent.ApiKey");
        _signer = new HmacSigner(options.ApiKey);
    }

    public async Task<ConfigOverrideResponse?> FetchAsync(CancellationToken ct)
    {
        try
        {
            LastFailureKind = null;
            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            _signer.ApplyHeaders(request, string.Empty);

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = response.Content == null
                    ? null
                    : await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var reason = CloudErrorSanitizer.FromBody(errorBody);
                LastFailureKind = $"http_{(int)response.StatusCode}_{NormalizeReasonCode(reason)}";
                _logger.LogWarning(
                    "AgentConfigClient: non-success {Status} from {Endpoint} reason={Reason}",
                    (int)response.StatusCode,
                    Endpoint,
                    reason);
                await TryRecoverAfterAgentNotFoundAsync(response.StatusCode, errorBody, ct)
                    .ConfigureAwait(false);
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
                LastFailureKind = "unexpected_response_shape";
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
            LastFailureKind = ex.GetType().Name;
            _logger.LogSafeWarning(ex);
            return null;
        }
    }

    private static string NormalizeReasonCode(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "unknown";

        var chars = reason
            .Trim()
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' or ':' ? ch : '_')
            .ToArray();
        var normalized = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(normalized) ? "unknown" : normalized;
    }

    private async Task TryRecoverAfterAgentNotFoundAsync(
        HttpStatusCode statusCode,
        string? errorBody,
        CancellationToken ct)
    {
        if (_cloudAuthRecovery is null || statusCode != HttpStatusCode.Unauthorized)
            return;

        var reason = CloudErrorSanitizer.FromBody(errorBody);
        if (!reason.Equals("Agent not found", StringComparison.OrdinalIgnoreCase))
            return;

        var ex = new HttpRequestException(
            $"Cloud request {Endpoint} failed with 401 (Unauthorized); reason={reason}",
            null,
            statusCode);
        await _cloudAuthRecovery.TryRecoverAfterAuthFailureAsync(ex, ct).ConfigureAwait(false);
    }
}
