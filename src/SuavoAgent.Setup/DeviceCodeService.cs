using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SuavoAgent.Contracts.Security;

namespace SuavoAgent.Setup;

public sealed class DeviceAuthorityUnavailableException(string message, Exception inner)
    : InvalidOperationException(message, inner);

public sealed class DeviceCodeTransientException(string message, Exception inner)
    : InvalidOperationException(message, inner);

/// <summary>Result of creating a device code (the screen the operator sees).</summary>
public sealed record DeviceCodeCreateResult(
    string DeviceCode,
    string VerificationUrl,
    int ExpiresInSeconds,
    int PollIntervalSeconds,
    string DeviceSecret = "",
    string DeviceKeyId = "",
    string DeviceKeyName = "",
    string DeviceChallenge = "",
    string MaintenanceKeyId = "")
{
    public override string ToString() =>
        $"DeviceCodeCreateResult {{ DeviceCode = {DeviceCode}, DeviceSecret = [redacted] }}";
}

/// <summary>Polling outcome. Status is one of pending/authorized/expired/denied.</summary>
public sealed record DeviceCodePollResult(
    string Status,
    string? ApiKey = null,
    string? AgentId = null,
    string? PharmacyId = null,
    string? PharmacyName = null,
    AgentReasoningConfig? Reasoning = null,
    string? VerticalConfigRaw = null,
    VerticalConfigDto? VerticalConfig = null,
    string? VerticalConfigSignature = null,
    string? VerticalConfigKeyId = null)
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

/// <summary>HTTP surface for device-code onboarding (abstracted for testing the pairing orchestrator).</summary>
public interface IDeviceCodeService
{
    Task<DeviceCodeCreateResult> CreateAsync(string fingerprint, string version, CancellationToken ct);
    Task<DeviceCodePollResult> PollAsync(
        string deviceCode, string deviceSecret, CancellationToken ct);
    void AbortPendingKey(string fingerprint, string expectedKeyId) { }
}

/// <summary>
/// Client for the v4 device-code onboarding flow. The agent POSTs to create a
/// code, shows it to the operator (who approves it on the dashboard), then
/// polls until the dashboard authorizes — at which point the agent receives a
/// one-time probationary API key. This is the sole native onboarding ingress.
///
/// Mirrors <c>AgentCredentialRecoveryClient</c>'s HTTP conventions (HTTPS-only
/// CloudUrl, BaseAddress, injectable handler for tests).
/// </summary>
public sealed class DeviceCodeService : IDeviceCodeService, IDisposable
{
    private const string CreateEndpoint = "/api/agent/device-code";
    private const string PollEndpoint = "/api/agent/device-token";
    private const int MaxCreateResponseBytes = 64 * 1024;
    private const int MaxPollResponseBytes = 512 * 1024;

    private readonly HttpClient _http;
    private readonly Uri _cloudOrigin;
    private readonly IDeviceAttestationKeyProvider _deviceKeys;
    private readonly IMaintenanceAttestationKeyProvider _maintenanceKeys;

    public DeviceCodeService(string cloudUrl)
        : this(
            cloudUrl,
            new HttpClientHandler { AllowAutoRedirect = false },
            DeviceAttestationKeyProvider.CreateProduction(),
            MaintenanceAttestationKeyProvider.CreateProduction())
    {
    }

    internal DeviceCodeService(
        string cloudUrl,
        HttpMessageHandler handler,
        IDeviceAttestationKeyProvider? deviceKeys = null,
        IMaintenanceAttestationKeyProvider? maintenanceKeys = null)
    {
        if (!Uri.TryCreate(cloudUrl.TrimEnd('/'), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath is not ("" or "/"))
            throw new InvalidOperationException($"CloudUrl must be absolute HTTPS — got: {cloudUrl}");

        if (handler is HttpClientHandler httpHandler)
            httpHandler.AllowAutoRedirect = false;
        _cloudOrigin = new Uri(uri.GetLeftPart(UriPartial.Authority), UriKind.Absolute);
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(_cloudOrigin, "/"),
            Timeout = TimeSpan.FromSeconds(15),
        };
        _deviceKeys = deviceKeys ?? new InMemoryDeviceAttestationKeyProvider();
        _maintenanceKeys = maintenanceKeys ?? new InMemoryMaintenanceAttestationKeyProvider();
    }

    /// <summary>Create a device code. Throws on transport/HTTP error or malformed response.</summary>
    public async Task<DeviceCodeCreateResult> CreateAsync(
        string fingerprint, string version, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
            throw new ArgumentException("fingerprint is required", nameof(fingerprint));

        IDeviceAttestationKey key;
        MaintenanceKeyRegistration maintenance;
        try
        {
            key = _deviceKeys.OpenOrCreate(fingerprint);
            maintenance = _maintenanceKeys.OpenOrCreate(fingerprint);
        }
        catch (Exception ex) when (ex is CryptographicException or
                                   UnauthorizedAccessException or
                                   InvalidOperationException)
        {
            throw new DeviceAuthorityUnavailableException(
                "This PC could not create its secure device key. Enable TPM 2.0 in firmware, resolve any TPM warning in Windows Security, then restart Setup.",
                ex);
        }
        using (key)
        {
        using var resp = await _http.PostAsJsonAsync(
            CreateEndpoint,
            new
            {
                fingerprint,
                version,
                deviceKey = new
                {
                    algorithm = key.Enrollment.Algorithm,
                    keyId = key.Enrollment.KeyId,
                    publicKeySpki = key.Enrollment.PublicKeySpki,
                },
                maintenanceKey = new
                {
                    algorithm = maintenance.Enrollment.Algorithm,
                    keyId = maintenance.Enrollment.KeyId,
                    publicKeySpki = maintenance.Enrollment.PublicKeySpki,
                    proof = maintenance.PossessionProof,
                },
            },
            ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        try
        {
            var node = await BoundedJsonResponse.ReadObjectAsync(
                resp.Content,
                MaxCreateResponseBytes,
                ct).ConfigureAwait(false);

            var deviceCode = node["deviceCode"]?.GetValue<string>()
                             ?? throw new InvalidOperationException("device-code response missing deviceCode");
            var verificationUrl = node["verificationUrl"]?.GetValue<string>() ?? "";
            if (!IsValidDeviceCode(deviceCode) ||
                !IsExactVerificationUrl(verificationUrl, deviceCode))
                throw new InvalidOperationException(
                    "device-code response contains an untrusted verification URL");
            var expiresIn = node["expiresIn"]?.GetValue<int>() ?? 900;
            var pollInterval = node["pollInterval"]?.GetValue<int>() ?? 5;
            var deviceSecret = node["deviceSecret"]?.GetValue<string>()
                               ?? throw new InvalidOperationException(
                                   "device-code response missing deviceSecret");
            var deviceChallenge = node["deviceChallenge"]?.GetValue<string>()
                                  ?? throw new InvalidOperationException(
                                      "device-code response missing deviceChallenge");
            if (deviceChallenge.Length != 43 ||
                deviceChallenge.Any(character =>
                    character is not (>= 'A' and <= 'Z') and
                    not (>= 'a' and <= 'z') and
                    not (>= '0' and <= '9') and
                    not '-' and not '_'))
                throw new InvalidOperationException(
                    "device-code response contains an invalid deviceChallenge");

            return new DeviceCodeCreateResult(
                deviceCode,
                verificationUrl,
                expiresIn,
                pollInterval,
                deviceSecret,
                key.Enrollment.KeyId,
                key.LocalKeyName,
                deviceChallenge,
                maintenance.Enrollment.KeyId);
        }
        catch (Exception ex) when (ex is JsonException or
                                   InvalidOperationException or
                                   FormatException or InvalidDataException or
                                   DecoderFallbackException)
        {
            throw new DeviceCodeTransientException(
                "The pairing service returned an unreadable response.",
                ex);
        }
        }
    }

    /// <summary>Poll once for approval. Returns the current status (no waiting).</summary>
    public async Task<DeviceCodePollResult> PollAsync(
        string deviceCode, string deviceSecret, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceCode))
            throw new ArgumentException("deviceCode is required", nameof(deviceCode));
        if (string.IsNullOrWhiteSpace(deviceSecret))
            throw new ArgumentException("deviceSecret is required", nameof(deviceSecret));

        using var resp = await _http.PostAsJsonAsync(
            PollEndpoint, new { deviceCode, deviceSecret }, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        try
        {
        var node = await BoundedJsonResponse.ReadObjectAsync(
            resp.Content,
            MaxPollResponseBytes,
            ct).ConfigureAwait(false);

        var status = node["status"]?.GetValue<string>()
                     ?? throw new InvalidOperationException("device-token response missing status");

        // Optional on-device brain config. Presence is fail-closed: silently
        // ignoring a malformed publisher envelope would make an authorized
        // install appear successful while dropping its required Brain cohort.
        AgentReasoningConfig? reasoning = null;
        if (node["reasoning"] is JsonObject reasoningNode)
        {
            try
            {
                reasoning = reasoningNode.Deserialize<AgentReasoningConfig>();
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException(
                    "device-token response contains a malformed reasoning manifest",
                    exception);
            }
        }

        var vc = VerticalConfigPayloadParser.Parse(node);
        if (string.Equals(status, "authorized", StringComparison.Ordinal) &&
            (vc.Raw is null || vc.Dto is null ||
             string.IsNullOrWhiteSpace(vc.Signature) ||
             string.IsNullOrWhiteSpace(vc.KeyId)))
            throw new InvalidOperationException(
                "authorized device-token response is missing its signed workstation profile");

        return new DeviceCodePollResult(
            status,
            node["apiKey"]?.GetValue<string>(),
            node["agentId"]?.GetValue<string>(),
            node["pharmacyId"]?.GetValue<string>(),
            node["pharmacyName"]?.GetValue<string>(),
            reasoning,
            VerticalConfigRaw: vc.Raw,
            VerticalConfig: vc.Dto,
            VerticalConfigSignature: vc.Signature,
            VerticalConfigKeyId: vc.KeyId);
        }
        catch (Exception ex) when (ex is JsonException or
                                   InvalidOperationException or
                                   FormatException or InvalidDataException or
                                   DecoderFallbackException)
        {
            throw new DeviceCodeTransientException(
                "The pairing service returned an unreadable response.",
                ex);
        }
    }

    public void Dispose() => _http.Dispose();

    public void AbortPendingKey(string fingerprint, string expectedKeyId) =>
        _deviceKeys.AbortPending(fingerprint, expectedKeyId);

    private bool IsExactVerificationUrl(string value, string deviceCode)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate) ||
            candidate.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(candidate.UserInfo) ||
            !string.IsNullOrEmpty(candidate.Fragment) ||
            candidate.AbsolutePath != "/pharmacy/agent/pair" ||
            candidate.Query != "?code=" + Uri.EscapeDataString(deviceCode))
            return false;
        return string.Equals(
            candidate.GetLeftPart(UriPartial.Authority),
            _cloudOrigin.GetLeftPart(UriPartial.Authority),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidDeviceCode(string value) =>
        value is { Length: >= 4 and <= 64 } && value.All(character =>
            character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '-');
}
