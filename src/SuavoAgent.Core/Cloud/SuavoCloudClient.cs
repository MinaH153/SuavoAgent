using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Core.Config;

namespace SuavoAgent.Core.Cloud;

public interface IPostSigner
{
    Task<JsonElement?> PostSignedAsync(string path, object payload, CancellationToken ct);

    /// <summary>
    /// Like PostSignedAsync but also verifies the response body's ECDSA signature (H-11).
    /// Returns null if the response is unsigned or signature verification fails.
    /// </summary>
    Task<JsonElement?> PostSignedVerifiedAsync(string path, object payload, string publicKeyDer, CancellationToken ct);
}

public sealed class SuavoCloudClient : IPostSigner, IDisposable
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

        var handler = new HttpClientHandler();
        if (!string.IsNullOrEmpty(options.CloudCertPin))
        {
            handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
            {
                if (errors != System.Net.Security.SslPolicyErrors.None) return false;
                if (cert == null) return false;
                var certHash = Convert.ToBase64String(
                    System.Security.Cryptography.SHA256.HashData(cert.GetPublicKey()));
                var pins = options.CloudCertPin!.Split(';', StringSplitOptions.RemoveEmptyEntries);
                return pins.Any(pin => pin.Equals(certHash, StringComparison.Ordinal));
            };
        }
        _http = new HttpClient(handler) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<JsonElement?> HeartbeatAsync(object payload, CancellationToken ct)
    {
        return await PostSignedAsync("/api/agent/heartbeat", payload, ct);
    }

    public async Task<JsonElement?> SyncRxAsync(object payload, CancellationToken ct)
    {
        return await PostSignedAsync("/api/agent/sync", payload, ct);
    }

    /// <summary>
    /// Ships PHI to /api/agent/patient-details — driver-needed delivery
    /// fields only. The <see cref="SuavoAgent.Contracts.Models.PatientDetailsPayload"/>
    /// type is the deliberate compile-time contract: any new PHI field that
    /// reaches cloud has to land in that record first, which makes the diff
    /// impossible to miss in code review (Codex 2026-04-26 hardening).
    ///
    /// The Rx number itself is NEVER sent in cleartext alongside the hash;
    /// only <c>rxNumberHash</c> ships, and the payload record deliberately
    /// omits a RxNumber field.
    /// </summary>
    public async Task SendPatientDetailsAsync(
        string rxNumber,
        SuavoAgent.Contracts.Models.PatientDetailsPayload details,
        string commandId,
        CancellationToken ct)
    {
        var rxNumberHash = Learning.PhiScrubber.HmacHash(rxNumber, _options.HmacSalt ?? "[no-hmac-salt]");
        await PostSignedAsync("/api/agent/patient-details", new { rxNumberHash, details, commandId }, ct);
    }

    public async Task<JsonElement?> PostSignedAsync(string path, object payload, CancellationToken ct)
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

    public async Task<JsonElement?> PostSignedVerifiedAsync(string path, object payload, string publicKeyDer, CancellationToken ct)
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

        // H-11: Reject seed responses with missing or invalid ECDSA signature.
        if (!response.Headers.TryGetValues("X-Response-Signature", out var sigValues)
            || !VerifyEcdsaSignature(responseBody, sigValues.FirstOrDefault() ?? "", publicKeyDer))
        {
            Serilog.Log.Warning("Seed response ECDSA signature missing or invalid — rejecting (H-11)");
            return null;
        }

        return JsonSerializer.Deserialize<JsonElement>(responseBody);
    }

    private static bool VerifyEcdsaSignature(string body, string signatureBase64, string publicKeyDer)
    {
        if (string.IsNullOrEmpty(signatureBase64)) return false;
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyDer), out _);
            var sigBytes = Convert.FromBase64String(signatureBase64);
            return ecdsa.VerifyData(Encoding.UTF8.GetBytes(body), sigBytes, HashAlgorithmName.SHA256);
        }
        catch { return false; }
    }

    /// <summary>
    /// Acknowledges execution of a signed cloud command. Updates agent_commands row
    /// with status=executed or failed, plus optional result/error.
    /// </summary>
    public async Task AckCommandAsync(string commandId, bool success, object? result, string? error, CancellationToken ct)
    {
        try
        {
            await PostSignedAsync(
                $"/api/agent/commands/{commandId}/ack",
                new
                {
                    status = success ? "executed" : "failed",
                    result,
                    error,
                },
                ct);
        }
        catch (Exception ex)
        {
            // Best-effort ack — don't crash the agent if cloud is unreachable.
            Serilog.Log.Warning(ex, "AckCommand failed for {CommandId}", commandId);
        }
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

    public async Task<string?> UploadPomAsync(string pomJson, string digest, CancellationToken ct)
    {
        var response = await PostSignedAsync("/api/agent/pom", new { pom = pomJson, digest }, ct);
        if (response == null) return null;

        try
        {
            if (response.Value.TryGetProperty("pomId", out var id))
                return id.GetString();
        }
        catch { /* malformed response */ }

        return null;
    }

    public void Dispose() => _http.Dispose();
}
