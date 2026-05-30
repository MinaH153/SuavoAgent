using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Learning;

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
        : this(options, CreateHandler(options))
    {
    }

    internal SuavoCloudClient(AgentOptions options, HttpMessageHandler handler)
    {
        _options = options;
        _signer = new HmacSigner(options.ApiKey ?? throw new InvalidOperationException("ApiKey is required"));

        var uri = new Uri(options.CloudUrl);
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"CloudUrl must use HTTPS, got: {uri.Scheme}");

        _http = new HttpClient(handler) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(30) };
    }

    private static HttpMessageHandler CreateHandler(AgentOptions options)
    {
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
        return handler;
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
        OutboundPhiGuard.AssertAllowed(path, body, _options);
        var timestamp = DateTimeOffset.UtcNow.ToString("o");
        var signature = _signer.Sign(timestamp, body);

        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.Add("x-agent-api-key", _options.ApiKey);
        request.Headers.Add("x-agent-timestamp", timestamp);
        request.Headers.Add("x-agent-signature", signature);

        using var response = await _http.SendAsync(request, ct);
        await EnsureCloudSuccessAsync(response, path, ct).ConfigureAwait(false);

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(responseBody))
            return null;

        return JsonSerializer.Deserialize<JsonElement>(responseBody);
    }

    public async Task<JsonElement?> PostSignedVerifiedAsync(string path, object payload, string publicKeyDer, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(payload);
        OutboundPhiGuard.AssertAllowed(path, body, _options);
        var timestamp = DateTimeOffset.UtcNow.ToString("o");
        var signature = _signer.Sign(timestamp, body);

        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.Add("x-agent-api-key", _options.ApiKey);
        request.Headers.Add("x-agent-timestamp", timestamp);
        request.Headers.Add("x-agent-signature", signature);

        using var response = await _http.SendAsync(request, ct);
        await EnsureCloudSuccessAsync(response, path, ct).ConfigureAwait(false);

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

    private static async Task EnsureCloudSuccessAsync(
        HttpResponseMessage response,
        string path,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = response.Content == null
            ? null
            : await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var safeReason = CloudErrorSanitizer.FromBody(body);
        throw new HttpRequestException(
            $"Cloud request {path} failed with {(int)response.StatusCode} ({response.ReasonPhrase}); reason={safeReason}",
            null,
            response.StatusCode);
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

internal static class OutboundPhiGuard
{
    private static readonly HashSet<string> BlockedFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "rxnumber",
        "rx_number",
        "patientfirstname",
        "patientlastname",
        "patientlastinitial",
        "patientname",
        "patientphone",
        "deliveryaddress1",
        "deliveryaddress2",
        "deliverycity",
        "deliverystate",
        "deliveryzip",
        "firstname",
        "lastname",
        "lastinitial",
        "phone",
        "address1",
        "address2",
        "streetaddress",
        "dob",
        "dateofbirth",
        "ssn",
        "mrn",
        "insuranceid",
        "memberid",
        "policy",
        "rxdeliveryqueue",
    };

    public static void AssertAllowed(string path, string body, AgentOptions options)
    {
        if (IsExplicitPhiPath(path, options))
            return;

        // Control-plane telemetry (the heartbeat) is built by the agent from a
        // fixed, PHI-free schema — patient rx data flows on /api/agent/sync, which
        // is still fully scanned. The fuzzy value regex false-positives on the
        // heartbeat's ISO timestamps, install paths, and the operator consent name,
        // which would otherwise block every heartbeat (and with it command delivery
        // + online status). So for control-plane paths we keep the *structural*
        // blocked-field-NAME check (catches an accidental patient field) but skip
        // the fuzzy value scan. Data-sync paths keep both.
        var scanValues = !IsControlPlanePath(path);

        using var doc = JsonDocument.Parse(body);
        if (ContainsPhi(doc.RootElement, scanValues))
        {
            throw new InvalidOperationException(
                $"PHI-classified payload blocked before outbound cloud POST to {path}.");
        }
    }

    private static bool IsExplicitPhiPath(string path, AgentOptions options)
    {
        if (string.Equals(path, "/api/agent/patient-details", StringComparison.Ordinal))
            return true;

        return string.Equals(path, "/api/agent/sync", StringComparison.Ordinal) &&
               options.EnableLegacyPhiDeliveryQueueSync;
    }

    /// <summary>
    /// Agent-built control-plane telemetry that never carries patient rx data by
    /// construction. Exempt from the fuzzy value-PHI scan (not the field-name scan).
    /// </summary>
    private static bool IsControlPlanePath(string path) =>
        string.Equals(path, "/api/agent/heartbeat", StringComparison.Ordinal);

    private static bool ContainsPhi(JsonElement element, bool scanValues, string? propertyName = null)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var normalized = NormalizeFieldName(property.Name);
                    if (BlockedFieldNames.Contains(normalized))
                        return true;
                    if (ContainsPhi(property.Value, scanValues, normalized))
                        return true;
                }

                return false;

            case JsonValueKind.Array:
                return element.EnumerateArray().Any(item => ContainsPhi(item, scanValues, propertyName));

            case JsonValueKind.String:
                // Control-plane paths keep the structural field-name guard above but
                // skip the fuzzy value heuristic (the false-positive source).
                if (!scanValues)
                    return false;
                var value = element.GetString();
                if (string.IsNullOrWhiteSpace(value) || IsOperationalSafeString(propertyName, value))
                    return false;
                return PhiScrubber.ContainsPhi(value);

            default:
                return false;
        }
    }

    private static bool IsOperationalSafeString(string? propertyName, string value)
    {
        if (propertyName is not null &&
            (propertyName.EndsWith("hash", StringComparison.OrdinalIgnoreCase) ||
             propertyName.EndsWith("sha256", StringComparison.OrdinalIgnoreCase) ||
             propertyName.Contains("digest", StringComparison.OrdinalIgnoreCase) ||
             propertyName.Contains("timestamp", StringComparison.OrdinalIgnoreCase) ||
             propertyName.Contains("capturedat", StringComparison.OrdinalIgnoreCase) ||
             propertyName.Contains("syncedat", StringComparison.OrdinalIgnoreCase) ||
             propertyName is "ndc" or "evidenceid" or "scanwindowid" or "schemaversion" or "schemasignature" or
                 "pms" or "pmsversion" or "status" or "outcome" or "severity" or "source" or "sourcedetail" or
                 "classification" or "city" or "state" or "zip5" or "priority" or "temperaturerequirement"))
        {
            return true;
        }

        return value.Length <= 96 &&
               value.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' or ':');
    }

    private static string NormalizeFieldName(string name) =>
        new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
