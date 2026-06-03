using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using SuavoAgent.Core.Config;
using SuavoAgent.Diagnostics;

namespace SuavoAgent.Core.Cloud;

/// <summary>
/// Phase 2 OTA ruleset client. Cloud Edge Function <c>agent-ruleset</c>
/// returns the current signed bundle; this client wraps the HTTP +
/// HMAC-auth + ETag-aware fetch into a typed result the worker can
/// branch on without inspecting raw HTTP state.
/// </summary>
/// <remarks>
/// Mirrors <see cref="AgentConfigClient"/>'s HMAC auth (same
/// <see cref="HmacSigner"/> + same 3 headers: <c>x-agent-api-key</c>,
/// <c>x-agent-timestamp</c>, <c>x-agent-signature</c>). The ETag round-trip
/// shaves cloud bandwidth when the ruleset hasn't changed since the last
/// fetch; agents poll every 5min so a 304 is the common case.
/// </remarks>
public interface IRulesetClient
{
    /// <summary>
    /// Normalised failure kind from the last call: <c>null</c> on success
    /// or 304, otherwise <c>http_401_unauthorized</c> / <c>http_503</c> /
    /// <c>network_dns</c> / <c>network_timeout</c> / <c>schema_invalid</c>
    /// / etc. Surfaced via <c>ConfigSyncOptions.LastFailureKind</c> in
    /// health probes.
    /// </summary>
    string? LastFailureKind { get; }

    /// <summary>
    /// Fetch the current ruleset. If <paramref name="currentETag"/> is
    /// non-null, sends <c>If-None-Match</c>; a 304 response yields
    /// <see cref="RulesetFetchOutcome.NotModified"/> with
    /// <see cref="RulesetFetchResult.Bundle"/> == null.
    /// </summary>
    Task<RulesetFetchResult> FetchAsync(string? currentETag, CancellationToken ct);
}

/// <summary>
/// Outcome classification of a single <see cref="IRulesetClient.FetchAsync"/>
/// call. The worker branches on this to decide whether to verify+swap,
/// preserve cache, or alarm.
/// </summary>
public enum RulesetFetchOutcome
{
    /// <summary>200 OK — caller MUST verify signature before swap.</summary>
    Updated,
    /// <summary>304 Not Modified — caller preserves cache, no alarm.</summary>
    NotModified,
    /// <summary>401 / 403 — caller preserves cache + alarms (token invalid).</summary>
    AuthFailed,
    /// <summary>5xx or network unreachable — caller preserves cache, no alarm (transient).</summary>
    Transient,
    /// <summary>Unparseable response body — caller alarms (cloud schema drift).</summary>
    SchemaInvalid,
}

/// <param name="Bundle">Verified-on-receipt bundle (signature NOT yet verified — that's the verifier's job). <c>null</c> for every outcome except <see cref="RulesetFetchOutcome.Updated"/>.</param>
/// <param name="Outcome">Classification — see <see cref="RulesetFetchOutcome"/>.</param>
public sealed record RulesetFetchResult(
    RulesetBundle? Bundle,
    RulesetFetchOutcome Outcome);

public sealed class AgentRulesetClient : IRulesetClient
{
    /// <summary>Default endpoint per the Comp 2 design spec.</summary>
    public const string Endpoint = "/api/agent/ruleset/current";

    private readonly HttpClient _http;
    private readonly HmacSigner _signer;
    private readonly AgentOptions _options;
    private readonly ILogger<AgentRulesetClient>? _logger;

    public string? LastFailureKind { get; private set; }

    public AgentRulesetClient(
        HttpClient http,
        AgentOptions options,
        ILogger<AgentRulesetClient>? logger = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException("AgentRulesetClient requires Agent.ApiKey");
        }
        _signer = new HmacSigner(options.ApiKey);
    }

    public async Task<RulesetFetchResult> FetchAsync(string? currentETag, CancellationToken ct)
    {
        LastFailureKind = null;
        try
        {
            var timestamp = DateTimeOffset.UtcNow.ToString("o");
            // GET body is empty — signer matches AgentConfigClient.
            var signature = _signer.Sign(timestamp, string.Empty);

            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            request.Headers.Add("x-agent-api-key", _options.ApiKey);
            request.Headers.Add("x-agent-timestamp", timestamp);
            request.Headers.Add("x-agent-signature", signature);
            if (!string.IsNullOrEmpty(currentETag))
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", currentETag);
            }

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return new RulesetFetchResult(null, RulesetFetchOutcome.NotModified);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                LastFailureKind = $"http_{(int)response.StatusCode}_unauthorized";
                _logger?.LogWarning(
                    "AgentRulesetClient: auth failure {Status} from {Endpoint}",
                    (int)response.StatusCode, Endpoint);
                return new RulesetFetchResult(null, RulesetFetchOutcome.AuthFailed);
            }

            if (!response.IsSuccessStatusCode)
            {
                LastFailureKind = $"http_{(int)response.StatusCode}";
                _logger?.LogInformation(
                    "AgentRulesetClient: transient {Status} from {Endpoint}",
                    (int)response.StatusCode, Endpoint);
                return new RulesetFetchResult(null, RulesetFetchOutcome.Transient);
            }

            // 200 — parse the bundle envelope { ruleset: {...}, signature: "<base64>" }.
            var etag = response.Headers.ETag?.Tag ?? string.Empty;
            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var envelope = await JsonSerializer
                .DeserializeAsync<RulesetEnvelope>(body, EnvelopeJsonOptions, ct)
                .ConfigureAwait(false);

            if (envelope?.Ruleset is null || string.IsNullOrEmpty(envelope.SignatureBase64))
            {
                LastFailureKind = "schema_invalid_missing_fields";
                _logger?.LogWarning(
                    "AgentRulesetClient: response missing ruleset or signature fields");
                return new RulesetFetchResult(null, RulesetFetchOutcome.SchemaInvalid);
            }

            byte[] signatureBytes;
            try
            {
                signatureBytes = Convert.FromBase64String(envelope.SignatureBase64);
            }
            catch (FormatException)
            {
                LastFailureKind = "schema_invalid_signature_base64";
                return new RulesetFetchResult(null, RulesetFetchOutcome.SchemaInvalid);
            }

            var bundle = new RulesetBundle(envelope.Ruleset, signatureBytes, etag);
            return new RulesetFetchResult(bundle, RulesetFetchOutcome.Updated);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            LastFailureKind = $"network_{ex.HttpRequestError}".ToLowerInvariant();
            _logger?.LogInformation(ex,
                "AgentRulesetClient: network failure to {Endpoint}", Endpoint);
            return new RulesetFetchResult(null, RulesetFetchOutcome.Transient);
        }
        catch (TaskCanceledException)
        {
            LastFailureKind = "network_timeout";
            return new RulesetFetchResult(null, RulesetFetchOutcome.Transient);
        }
        catch (JsonException ex)
        {
            LastFailureKind = "schema_invalid_json";
            _logger?.LogWarning(ex,
                "AgentRulesetClient: schema-invalid response body");
            return new RulesetFetchResult(null, RulesetFetchOutcome.SchemaInvalid);
        }
    }

    /// <summary>
    /// Wire shape per Comp 2 design §A — the cloud Edge Function returns
    /// the ruleset payload + a detached base64 signature. The ETag is
    /// returned via the HTTP response header, not in this envelope.
    /// </summary>
    internal sealed class RulesetEnvelope
    {
        [JsonPropertyName("ruleset")]
        public RulesetV1? Ruleset { get; set; }

        [JsonPropertyName("signature")]
        public string? SignatureBase64 { get; set; }
    }

    private static readonly JsonSerializerOptions EnvelopeJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
