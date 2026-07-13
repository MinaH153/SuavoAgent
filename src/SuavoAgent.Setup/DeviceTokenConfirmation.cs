using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Setup.Maintenance;
using SuavoAgent.Setup.Verify;

namespace SuavoAgent.Setup;

internal static class DeviceTokenConfirmation
{
    private const int MaxResponseBytes = 16 * 1024;

    internal static async Task<AuthorityPromotionOutcome> ConfirmAsync(
        SetupConfig config,
        string provisioningId,
        CancellationToken cancellationToken,
        HttpMessageHandler? handler = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        string? readinessJson = null,
        string? sqlServerCertificateSha256 = null)
    {
        if (string.IsNullOrWhiteSpace(config.DeviceCode) ||
            string.IsNullOrWhiteSpace(config.DeviceKeyId) ||
            string.IsNullOrWhiteSpace(config.DeviceFingerprint) ||
            string.IsNullOrWhiteSpace(config.DeviceChallenge) ||
            string.IsNullOrWhiteSpace(config.AgentId) ||
            string.IsNullOrWhiteSpace(config.PharmacyId) ||
            string.IsNullOrWhiteSpace(config.ApiKey))
            return AuthorityPromotionOutcome.Rejected;
        if (!Uri.TryCreate(config.CloudUrl.TrimEnd('/'), UriKind.Absolute, out var cloud) ||
            cloud.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(cloud.UserInfo) ||
            !string.IsNullOrEmpty(cloud.Query) ||
            !string.IsNullOrEmpty(cloud.Fragment) ||
            cloud.AbsolutePath is not ("" or "/"))
            return AuthorityPromotionOutcome.Rejected;
        if (!Guid.TryParseExact(provisioningId, "D", out var parsedProvisioningId) ||
            !string.Equals(
                parsedProvisioningId.ToString("D"),
                provisioningId,
                StringComparison.Ordinal))
            return AuthorityPromotionOutcome.Rejected;
        if (sqlServerCertificateSha256 is not null &&
            (sqlServerCertificateSha256.Length != 64 ||
             sqlServerCertificateSha256.Any(ch =>
                 ch is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))))
            return AuthorityPromotionOutcome.Rejected;

        var expectation = new DeviceProvisioningExpectation(
            config.DeviceCode,
            provisioningId,
            config.AgentId,
            config.PharmacyId,
            config.DeviceFingerprint,
            config.DeviceKeyId,
            config.DeviceChallenge,
            sqlServerCertificateSha256);
        if (!TryReadProof(readinessJson, expectation, out var proof))
            return AuthorityPromotionOutcome.Rejected;

        var confirmation = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["deviceCode"] = config.DeviceCode,
            ["provisioningId"] = provisioningId,
            ["fingerprint"] = config.DeviceFingerprint,
            ["keyId"] = config.DeviceKeyId,
            ["challenge"] = config.DeviceChallenge,
            ["sqlServerCertificateSha256"] = sqlServerCertificateSha256,
            ["signature"] = proof!.Signature,
        };
        var body = JsonSerializer.Serialize(confirmation);
        handler ??= new HttpClientHandler { AllowAutoRedirect = false };
        if (handler is HttpClientHandler httpHandler)
            httpHandler.AllowAutoRedirect = false;
        using var http = new HttpClient(
            handler,
            disposeHandler: true)
        {
            BaseAddress = new Uri(cloud.GetLeftPart(UriPartial.Authority) + "/"),
            Timeout = TimeSpan.FromSeconds(20),
        };
        delay ??= Task.Delay;
        var signer = new AgentRequestSigner(config.ApiKey);

        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    "/api/agent/device-token/confirm")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                signer.ApplyHeaders(request, body);
                using var response = await http.SendAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                JsonObject? responseBody = null;
                try
                {
                    responseBody = await BoundedJsonResponse.ReadObjectAsync(
                        response.Content,
                        MaxResponseBytes,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is
                    JsonException or InvalidDataException or DecoderFallbackException)
                {
                    if (!ShouldRetryUnknown(response.StatusCode) || attempt == maxAttempts)
                        return AuthorityPromotionOutcome.Unknown;
                }
                if (responseBody is not null && response.IsSuccessStatusCode)
                    return IsConfirmed(responseBody, provisioningId)
                        ? AuthorityPromotionOutcome.Promoted
                        : AuthorityPromotionOutcome.Unknown;
                if (responseBody is not null &&
                    IsDeterministicRejection(response.StatusCode, responseBody))
                    return AuthorityPromotionOutcome.Rejected;
                if (!ShouldRetryUnknown(response.StatusCode) || attempt == maxAttempts)
                    return AuthorityPromotionOutcome.Unknown;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (attempt == maxAttempts)
                    return AuthorityPromotionOutcome.Unknown;
            }

            await delay(TimeSpan.FromSeconds(attempt), cancellationToken)
                .ConfigureAwait(false);
        }

        return AuthorityPromotionOutcome.Unknown;
    }

    private static bool TryReadProof(
        string? readinessJson,
        DeviceProvisioningExpectation expected,
        out LocalDeviceProvisioningProof? proof)
    {
        proof = null;
        try
        {
            var json = readinessJson;
            if (json is null)
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "SuavoAgent",
                    "activation-readiness.json");
                if (!File.Exists(path)) return false;
                json = File.ReadAllText(path);
            }
            if (Encoding.UTF8.GetByteCount(json) > 64 * 1024) return false;
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   DeviceProvisioningProofReader.TryRead(
                       document.RootElement,
                       expected,
                       out proof);
        }
        catch (Exception ex) when (ex is IOException or
                                   UnauthorizedAccessException or
                                   JsonException or
                                   InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsConfirmed(JsonObject root, string provisioningId)
    {
        try
        {
            return root.Count == 3 &&
                   root["success"]?.GetValue<bool>() == true &&
                   root["status"]?.GetValue<string>() == "confirmed" &&
                   root["provisioningId"]?.GetValue<string>() == provisioningId;
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private static bool IsDeterministicRejection(HttpStatusCode status, JsonObject root)
    {
        var expectedCode = status switch
        {
            HttpStatusCode.BadRequest => "PAIRING_CONFIRMATION_INVALID",
            HttpStatusCode.Unauthorized => "PAIRING_CONFIRMATION_AUTH_INVALID",
            HttpStatusCode.NotFound => "PAIRING_CONFIRMATION_NOT_FOUND",
            HttpStatusCode.Gone => "PAIRING_CONFIRMATION_EXPIRED",
            HttpStatusCode.PreconditionFailed => "PAIRING_CONFIRMATION_BAA_REQUIRED",
            HttpStatusCode.UnprocessableEntity => "PAIRING_CONFIRMATION_PROOF_INVALID",
            _ => null,
        };
        if (expectedCode is null) return false;
        try
        {
            return root.Count == 4 &&
                   root["success"]?.GetValue<bool>() == false &&
                   root["status"]?.GetValue<string>() == "rejected" &&
                   root["code"]?.GetValue<string>() == expectedCode &&
                   root["error"] is JsonValue error &&
                   error.TryGetValue<string>(out _);
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private static bool ShouldRetryUnknown(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)status >= 500;
}
