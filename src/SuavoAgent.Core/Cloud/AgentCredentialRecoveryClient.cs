using System.Net;
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
    private readonly ILogger<AgentCredentialRecoveryClient> _logger;
    private readonly IDisposable? _transport;

    public AgentCredentialRecoveryClient(
        AgentOptions options,
        IEncryptedCredentialStore credentialStore,
        ILogger<AgentCredentialRecoveryClient> logger)
        : this(options, credentialStore, logger, new HttpClientHandler())
    {
    }

    internal AgentCredentialRecoveryClient(
        AgentOptions options,
        IEncryptedCredentialStore credentialStore,
        ILogger<AgentCredentialRecoveryClient> logger,
        HttpMessageHandler handler)
    {
        _ = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _logger = logger;

        var uri = new Uri(options.CloudUrl);
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"CloudUrl must use HTTPS, got: {uri.Scheme}");

        _transport = handler;
    }

    public async Task<AgentCredentialRecoveryResult> TryRecoverAsync(CancellationToken ct)
    {
        await Task.CompletedTask;
        ct.ThrowIfCancellationRequested();
        _logger.LogWarning(
            "Automatic credential recovery is disabled; pharmacist-approved device re-pairing is required");
        return new(false, "device_repair_required");
    }

    public void Dispose() => _transport?.Dispose();
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
            // Re-pairing is a human-approved permanent gate, not a transient
            // network error. Do not hammer the retired recovery endpoint on
            // every heartbeat; transient failures may retry on the next 401.
            if (!string.Equals(result.Outcome, "device_repair_required", StringComparison.Ordinal))
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
            _logger.LogSafeDebug(healthEx);
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
