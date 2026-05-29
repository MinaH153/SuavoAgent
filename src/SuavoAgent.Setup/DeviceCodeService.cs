using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SuavoAgent.Setup;

/// <summary>Result of creating a device code (the screen the operator sees).</summary>
public sealed record DeviceCodeCreateResult(
    string DeviceCode,
    string VerificationUrl,
    int ExpiresInSeconds,
    int PollIntervalSeconds);

/// <summary>Polling outcome. Status is one of pending/authorized/expired/denied.</summary>
public sealed record DeviceCodePollResult(
    string Status,
    string? ApiKey = null,
    string? AgentId = null,
    string? PharmacyId = null,
    string? PharmacyName = null)
{
    public bool IsAuthorized => string.Equals(Status, "authorized", StringComparison.Ordinal);
    public bool IsPending => string.Equals(Status, "pending", StringComparison.Ordinal);

    // Only the KNOWN terminal states end polling. An unknown/garbage status
    // (e.g. a transient gateway body) is treated as non-terminal so the poll
    // loop keeps trying until the code's real expiry — never ending pairing
    // on a response we don't recognize. (Codex slice C1 review.)
    public bool IsTerminal =>
        IsAuthorized
        || string.Equals(Status, "expired", StringComparison.Ordinal)
        || string.Equals(Status, "denied", StringComparison.Ordinal);

    // Never leak the key in logs/diagnostics.
    public override string ToString() =>
        $"DeviceCodePollResult {{ Status = {Status}, AgentId = {AgentId}, PharmacyId = {PharmacyId}, ApiKey = [redacted] }}";
}

/// <summary>
/// Client for the v4 device-code onboarding flow. The agent POSTs to create a
/// code, shows it to the operator (who approves it on the dashboard), then
/// polls until the dashboard authorizes — at which point the agent receives a
/// one-time api key. Replaces setup.json-in-zip.
///
/// Mirrors <c>AgentCredentialRecoveryClient</c>'s HTTP conventions (HTTPS-only
/// CloudUrl, BaseAddress, injectable handler for tests).
/// </summary>
public sealed class DeviceCodeService : IDisposable
{
    private const string CreateEndpoint = "/api/agent/device-code";
    private const string PollEndpoint = "/api/agent/device-token";

    private readonly HttpClient _http;

    public DeviceCodeService(string cloudUrl)
        : this(cloudUrl, new HttpClientHandler())
    {
    }

    internal DeviceCodeService(string cloudUrl, HttpMessageHandler handler)
    {
        if (!Uri.TryCreate(cloudUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"CloudUrl must be absolute HTTPS — got: {cloudUrl}");

        _http = new HttpClient(handler) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>Create a device code. Throws on transport/HTTP error or malformed response.</summary>
    public async Task<DeviceCodeCreateResult> CreateAsync(
        string fingerprint, string version, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
            throw new ArgumentException("fingerprint is required", nameof(fingerprint));

        using var resp = await _http.PostAsJsonAsync(
            CreateEndpoint, new { fingerprint, version }, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var node = JsonNode.Parse(json)?.AsObject()
                   ?? throw new InvalidOperationException("Empty device-code response");

        var deviceCode = node["deviceCode"]?.GetValue<string>()
                         ?? throw new InvalidOperationException("device-code response missing deviceCode");
        var verificationUrl = node["verificationUrl"]?.GetValue<string>() ?? "";
        var expiresIn = node["expiresIn"]?.GetValue<int>() ?? 900;
        var pollInterval = node["pollInterval"]?.GetValue<int>() ?? 5;

        return new DeviceCodeCreateResult(deviceCode, verificationUrl, expiresIn, pollInterval);
    }

    /// <summary>Poll once for approval. Returns the current status (no waiting).</summary>
    public async Task<DeviceCodePollResult> PollAsync(string deviceCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceCode))
            throw new ArgumentException("deviceCode is required", nameof(deviceCode));

        using var resp = await _http.PostAsJsonAsync(
            PollEndpoint, new { deviceCode }, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var node = JsonNode.Parse(json)?.AsObject()
                   ?? throw new InvalidOperationException("Empty device-token response");

        var status = node["status"]?.GetValue<string>()
                     ?? throw new InvalidOperationException("device-token response missing status");

        return new DeviceCodePollResult(
            status,
            node["apiKey"]?.GetValue<string>(),
            node["agentId"]?.GetValue<string>(),
            node["pharmacyId"]?.GetValue<string>(),
            node["pharmacyName"]?.GetValue<string>());
    }

    public void Dispose() => _http.Dispose();
}
