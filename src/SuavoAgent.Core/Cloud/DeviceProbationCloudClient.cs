using System.Text;
using System.Text.Json;
using SuavoAgent.Core.Config;

namespace SuavoAgent.Core.Cloud;

internal enum DeviceProbationHealthSendOutcome
{
    Accepted,
    RetryExact,
    RefreshObservation,
    CredentialExpired,
}

/// <summary>
/// The only cloud transport available while a newly paired device is in
/// probation. Its API surface cannot fetch commands, config, or PHI and can
/// post only the exact device-health proof required for authority promotion.
/// </summary>
internal sealed class DeviceProbationCloudClient : IDisposable
{
    private const int MaxResponseBytes = 16 * 1024;
    private readonly HttpClient _http;
    private readonly HmacSigner _hmac;

    internal DeviceProbationCloudClient(AgentOptions options)
        : this(options, CreateHandler(options))
    {
    }

    internal DeviceProbationCloudClient(AgentOptions options, HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(options);
        var apiKey = options.ApiKey
            ?? throw new InvalidOperationException("Pending device authentication is unavailable.");
        _hmac = new HmacSigner(apiKey);
        var cloud = new Uri(options.CloudUrl, UriKind.Absolute);
        if (cloud.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Device probation requires an HTTPS cloud endpoint.");
        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = cloud,
            Timeout = TimeSpan.FromSeconds(20),
        };
    }

    internal async Task<DeviceProbationHealthSendOutcome> SendHealthAsync(
        SignedDeviceProbationHealth signed,
        CancellationToken cancellationToken)
    {
        var health = signed.Health;
        var body = JsonSerializer.Serialize(new
        {
            deviceCode = health.DeviceCode,
            provisioningId = health.ProvisioningId,
            agentId = health.AgentId,
            pharmacyId = health.PharmacyId,
            fingerprint = health.Fingerprint,
            version = health.Version,
            keyId = health.KeyId,
            challenge = health.Challenge,
            sqlServerCertificateSha256 = health.SqlServerCertificateSha256,
            observedAtUtc = health.ObservedAtUtc,
            challengeCounter = health.ChallengeCounter,
            signature = signed.Signature,
            helperAttached = health.HelperAttached,
            ipcConnected = health.IpcConnected,
            actuationReady = health.ActuationReady,
            sqlConnected = health.SqlConnected,
            schemaCanaryGreen = health.SchemaCanaryGreen,
            pmsCode = health.PmsCode,
        });
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/agent/device-token/probation-health")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        _hmac.ApplyHeaders(request, body);

        using var response = await _http.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        if (Encoding.UTF8.GetByteCount(responseBody) > MaxResponseBytes)
            return DeviceProbationHealthSendOutcome.RetryExact;
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            if (response.IsSuccessStatusCode &&
                root.ValueKind == JsonValueKind.Object &&
                root.EnumerateObject().Count() == 3 &&
                root.TryGetProperty("success", out var success) &&
                success.ValueKind == JsonValueKind.True &&
                root.TryGetProperty("status", out var status) &&
                status.GetString() == "probation_healthy" &&
                root.TryGetProperty("provisioningId", out var provisioningId) &&
                provisioningId.GetString() == health.ProvisioningId)
                return DeviceProbationHealthSendOutcome.Accepted;

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("success", out success) &&
                success.ValueKind == JsonValueKind.False &&
                root.TryGetProperty("code", out var code) &&
                code.ValueKind == JsonValueKind.String)
            {
                return code.GetString() switch
                {
                    "PROBATION_HEALTH_OBSERVATION_STALE" =>
                        DeviceProbationHealthSendOutcome.RefreshObservation,
                    "PROBATION_HEALTH_EXPIRED" =>
                        DeviceProbationHealthSendOutcome.CredentialExpired,
                    _ => DeviceProbationHealthSendOutcome.RetryExact,
                };
            }
            return DeviceProbationHealthSendOutcome.RetryExact;
        }
        catch (JsonException)
        {
            return DeviceProbationHealthSendOutcome.RetryExact;
        }
    }

    public void Dispose() => _http.Dispose();

    private static HttpMessageHandler CreateHandler(AgentOptions options)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        if (!string.IsNullOrEmpty(options.CloudCertPin))
        {
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
            {
                if (errors != System.Net.Security.SslPolicyErrors.None || cert is null) return false;
                var hash = Convert.ToBase64String(
                    System.Security.Cryptography.SHA256.HashData(cert.GetPublicKey()));
                return options.CloudCertPin!
                    .Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Any(pin => string.Equals(pin, hash, StringComparison.Ordinal));
            };
        }
        return handler;
    }
}
