using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Health;

namespace SuavoAgent.Core.Cloud;

public interface IAgentCredentialRecoveryClient
{
    Task<AgentCredentialRecoveryResult> TryRecoverAsync(CancellationToken ct);
}

public sealed record AgentCredentialRecoveryResult(
    bool Success,
    string Outcome,
    bool RestartRequired = false)
{
    public override string ToString() =>
        $"AgentCredentialRecoveryResult {{ Success = {Success}, Outcome = {Outcome}, RestartRequired = {RestartRequired}, ApiKey = [redacted] }}";
}

public sealed class AgentCredentialRecoveryClient : IAgentCredentialRecoveryClient, IDisposable
{
    private const string Endpoint = "/api/agent/recover-key";

    private readonly AgentOptions _options;
    private readonly string _appSettingsPath;
    private readonly ILogger<AgentCredentialRecoveryClient> _logger;
    private readonly HttpClient _http;

    public AgentCredentialRecoveryClient(
        AgentOptions options,
        string appSettingsPath,
        ILogger<AgentCredentialRecoveryClient> logger)
        : this(options, appSettingsPath, logger, new HttpClientHandler())
    {
    }

    internal AgentCredentialRecoveryClient(
        AgentOptions options,
        string appSettingsPath,
        ILogger<AgentCredentialRecoveryClient> logger,
        HttpMessageHandler handler)
    {
        _options = options;
        _appSettingsPath = appSettingsPath;
        _logger = logger;

        var uri = new Uri(options.CloudUrl);
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"CloudUrl must use HTTPS, got: {uri.Scheme}");

        _http = new HttpClient(handler) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<AgentCredentialRecoveryResult> TryRecoverAsync(CancellationToken ct)
    {
        if (!Guid.TryParse(_options.AgentId, out _))
            return new(false, "agent_id_not_recoverable");

        if (string.IsNullOrWhiteSpace(_options.MachineFingerprint))
            return new(false, "missing_machine_fingerprint");

        var body = JsonSerializer.Serialize(new
        {
            agentId = _options.AgentId,
            machineFingerprint = _options.MachineFingerprint,
        });

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = response.Content == null
                    ? null
                    : await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var reason = CloudErrorSanitizer.FromBody(errorBody);
                return new(false, $"http_{(int)response.StatusCode}_{reason}");
            }

            var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var apiKey = ExtractApiKey(responseBody);
            if (string.IsNullOrWhiteSpace(apiKey))
                return new(false, "invalid_recovery_response");

            try
            {
                AgentCredentialStore.ReplaceApiKey(_appSettingsPath, apiKey);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Cloud credential recovery could not update appsettings.json; check LocalService Modify ACL");
                return new(false, "appsettings_write_failed");
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Cloud credential recovery could not update appsettings.json");
                return new(false, "appsettings_write_failed");
            }

            return new(true, "key_rotated", RestartRequired: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cloud credential recovery failed before key rotation");
            return new(false, "recovery_exception");
        }
    }

    private static string? ExtractApiKey(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("success", out var success) || !success.GetBoolean())
                return null;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return null;
            if (!data.TryGetProperty("apiKey", out var apiKey) || apiKey.ValueKind != JsonValueKind.String)
                return null;

            var value = apiKey.GetString();
            return IsPlausibleAgentKey(value) ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsPlausibleAgentKey(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.StartsWith("sagent_", StringComparison.Ordinal)
        && value.Length <= 128;

    public void Dispose() => _http.Dispose();
}

internal static class AgentCredentialStore
{
    public static void ReplaceApiKey(string appSettingsPath, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(appSettingsPath))
            throw new ArgumentException("appsettings path is required", nameof(appSettingsPath));

        var json = File.Exists(appSettingsPath)
            ? File.ReadAllText(appSettingsPath)
            : "{}";
        var root = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
        var agent = root["Agent"] as JsonObject;
        if (agent is null)
        {
            agent = new JsonObject();
            root["Agent"] = agent;
        }

        agent["ApiKey"] = OperatingSystem.IsWindows()
            ? CredentialProtector.Protect(apiKey)
            : apiKey;

        var directory = Path.GetDirectoryName(appSettingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        // LocalService is granted Modify on appsettings.json, not broad write
        // access to the install directory. Rewrite the existing file directly
        // instead of creating a sibling .tmp file that Program Files may block.
        File.WriteAllText(appSettingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed class CloudAuthRecoveryCoordinator
{
    private readonly IAgentCredentialRecoveryClient? _client;
    private readonly IHostApplicationLifetime? _lifetime;
    private readonly ILogger<CloudAuthRecoveryCoordinator> _logger;
    private readonly string _healthPath;
    // 0 = no attempt in flight, 1 = one claimed. Interlocked, not a plain bool: ConfigSync and
    // Heartbeat share this singleton and can hit the same 401 concurrently — the CAS lets exactly
    // one proceed (no double key-rotation / double restart). Reset to 0 on TRANSIENT failure so a
    // later 401 can retry; a SUCCESSFUL recovery restarts the process, so the latch value is moot.
    private int _attempted;

    public CloudAuthRecoveryCoordinator(
        IAgentCredentialRecoveryClient? client,
        IHostApplicationLifetime? lifetime,
        ILogger<CloudAuthRecoveryCoordinator> logger,
        string? healthPath = null)
    {
        _client = client;
        _lifetime = lifetime;
        _logger = logger;
        _healthPath = healthPath ?? RuntimeHealthEvidence.CloudAuthHealthPath();
    }

    public async Task<bool> TryRecoverAfterAuthFailureAsync(Exception ex, CancellationToken ct)
    {
        if (!IsRecoverableAgentNotFound(ex))
            return false;

        // Atomically claim the single in-flight recovery. A loser (the other worker on the same
        // 401) returns without double-rotating the key.
        if (Interlocked.CompareExchange(ref _attempted, 1, 0) != 0)
            return false;

        if (_client is null)
        {
            WriteHealth(
                ex,
                status: "failed",
                recoveryAttempted: false,
                recoveryOutcome: "recovery_unavailable",
                restartRequested: false);
            _logger.LogWarning("Cloud credential recovery unavailable after agent-not-found auth failure");
            return false;
        }

        var result = await _client.TryRecoverAsync(ct).ConfigureAwait(false);
        if (!result.Success)
        {
            WriteHealth(
                ex,
                status: "failed",
                recoveryAttempted: true,
                recoveryOutcome: result.Outcome,
                restartRequested: false);
            _logger.LogWarning("Cloud credential recovery did not complete: {Outcome}", result.Outcome);
            // Transient failure (5xx / network throw): release the latch so the next 401 retries
            // instead of being short-circuited for the whole process lifetime.
            Interlocked.Exchange(ref _attempted, 0);
            return false;
        }

        WriteHealth(
            ex,
            status: "recovered",
            recoveryAttempted: true,
            recoveryOutcome: result.Outcome,
            restartRequested: result.RestartRequired);
        _logger.LogWarning(
            "Cloud credential recovery rotated the local API key; restarting Core so HMAC clients use the new key");
        if (result.RestartRequired)
            _lifetime?.StopApplication();
        return true;
    }

    private static bool IsRecoverableAgentNotFound(Exception ex) =>
        ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized }
        && ex.Message.Contains("reason=Agent not found", StringComparison.OrdinalIgnoreCase);

    private void WriteHealth(
        Exception ex,
        string status,
        bool recoveryAttempted,
        string? recoveryOutcome,
        bool restartRequested)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            RuntimeHealthEvidence.WriteCloudAuthHealth(
                _healthPath,
                status,
                now,
                status == "recovered" ? now : null,
                status == "recovered" ? 0 : 1,
                // Don't leave the triggering 401 as lastErrorKind on a SUCCESSFUL recovery — the
                // health probe tests errKind for "401" BEFORE status, so a stale kind would pin the
                // gate to Fail permanently even though auth is now healthy.
                status == "recovered" ? null : AuthFailureKind(ex),
                recoveryAttempted,
                recoveryOutcome,
                restartRequested);
        }
        catch (Exception healthEx)
        {
            _logger.LogDebug(healthEx, "Cloud credential recovery health write failed");
        }
    }

    private static string AuthFailureKind(Exception ex)
    {
        if (ex is not HttpRequestException { StatusCode: { } status })
            return ex.GetType().Name;

        var reason = ExtractReason(ex.Message);
        return $"http_{(int)status}_{NormalizeReason(reason)}";
    }

    private static string ExtractReason(string message)
    {
        const string marker = "reason=";
        var index = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return "unknown";

        return message[(index + marker.Length)..].Trim().TrimEnd('.', ';');
    }

    private static string NormalizeReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "unknown";

        Span<char> buffer = stackalloc char[Math.Min(reason.Length, 96)];
        var count = 0;
        var previousUnderscore = false;
        foreach (var ch in reason)
        {
            if (count >= buffer.Length)
                break;

            if (char.IsLetterOrDigit(ch) || ch is '.' or ':' or '-')
            {
                buffer[count++] = ch;
                previousUnderscore = false;
            }
            else if (!previousUnderscore)
            {
                buffer[count++] = '_';
                previousUnderscore = true;
            }
        }

        if (count == 0)
            return "unknown";

        var normalized = new string(buffer[..count]).Trim('_');
        return normalized.Length == 0 ? "unknown" : normalized;
    }
}
