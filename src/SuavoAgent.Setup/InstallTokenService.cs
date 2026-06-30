using System.Text.Json;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace SuavoAgent.Setup;

/// <summary>Pharmacy credentials returned by exchanging a filename install token.</summary>
public sealed record InstallTokenExchangeResult(
    string ApiKey, string AgentId, string PharmacyId, string? PharmacyName,
    AgentReasoningConfig? Reasoning = null)
{
    // Never leak the key in logs/diagnostics.
    public override string ToString() =>
        $"InstallTokenExchangeResult {{ AgentId = {AgentId}, PharmacyId = {PharmacyId}, ApiKey = [redacted] }}";
}

/// <summary>HTTP surface for the zero-touch filename-token exchange (testable).</summary>
public interface IInstallTokenService
{
    Task<InstallTokenExchangeResult> ExchangeAsync(
        string token, string machineName, string fingerprint, string version, CancellationToken ct);
}

/// <summary>
/// Exchanges the single-use install token baked into the EXE filename for
/// pharmacy credentials at the existing <c>POST /api/agent/register</c> — the
/// same endpoint bootstrap.ps1 uses. Mirrors <see cref="DeviceCodeService"/>'s
/// HTTP conventions (HTTPS-only CloudUrl, BaseAddress, injectable handler for
/// tests). Throws on any transport/HTTP/parse failure — the caller treats every
/// failure as "fall back to device-code pairing".
/// </summary>
public sealed class InstallTokenService : IInstallTokenService, IDisposable
{
    private const string RegisterEndpoint = "/api/agent/register";
    private readonly HttpClient _http;

    public InstallTokenService(string cloudUrl) : this(cloudUrl, new HttpClientHandler()) { }

    internal InstallTokenService(string cloudUrl, HttpMessageHandler handler)
    {
        if (!Uri.TryCreate(cloudUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"CloudUrl must be absolute HTTPS — got: {cloudUrl}");
        _http = new HttpClient(handler) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<InstallTokenExchangeResult> ExchangeAsync(
        string token, string machineName, string fingerprint, string version, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("token is required", nameof(token));

        using var resp = await _http.PostAsJsonAsync(RegisterEndpoint, new
        {
            licenseKey = "0000000000",   // token flow ignores NPI (matches bootstrap.ps1)
            installToken = token,
            machineName,
            machineFingerprint = fingerprint,
            agentVersion = version,
        }, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var root = JsonNode.Parse(json)?.AsObject()
                   ?? throw new InvalidOperationException("Empty register response");
        if (root["success"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("register response was not successful");

        var data = root["data"]?.AsObject()
                   ?? throw new InvalidOperationException("register response missing data");

        // Optional on-device brain config (fail-soft: a malformed block is
        // ignored → the agent installs rules-only). Mirrors DeviceCodeService.
        AgentReasoningConfig? reasoning = null;
        if (data["reasoning"] is JsonObject reasoningNode)
        {
            try { reasoning = reasoningNode.Deserialize<AgentReasoningConfig>(); }
            catch (JsonException) { reasoning = null; }
        }

        return new InstallTokenExchangeResult(
            data["apiKey"]?.GetValue<string>() ?? throw new InvalidOperationException("register response missing apiKey"),
            data["agentId"]?.GetValue<string>() ?? throw new InvalidOperationException("register response missing agentId"),
            data["pharmacyId"]?.GetValue<string>() ?? throw new InvalidOperationException("register response missing pharmacyId"),
            data["pharmacyName"]?.GetValue<string>(),
            reasoning);
    }

    public void Dispose() => _http.Dispose();
}
