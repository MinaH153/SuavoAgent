using System.Net;
using System.Text.Json;

namespace SuavoAgent.Setup;

/// <summary>Progress reported to the pairing UI as the flow advances.</summary>
public sealed record PairingProgress(
    string DeviceCode,
    string VerificationUrl,
    int SecondsRemaining,
    string Status);

/// <summary>Terminal outcome of a pairing run.</summary>
public sealed record PairingResult(bool Authorized, string Reason, SetupConfig? Config = null)
{
    public static PairingResult Success(SetupConfig config) => new(true, "authorized", config);
    public static PairingResult Failure(string reason) => new(false, reason);
}

/// <summary>
/// Orchestrates the agent side of device-code onboarding: create a code, then
/// poll (with backoff, bounded by the code's expiry and a 15-minute hard cap)
/// until the dashboard authorizes. On success it returns a <see cref="SetupConfig"/>
/// built from the dashboard-issued credentials. The install flow writes the
/// cloud key to the machine-DPAPI ProgramData credential store and stages an
/// appsettings file with identity/config only. SQL passwords, when present,
/// are DPAPI-sealed by Setup before the immutable file is staged.
///
/// All time + IO is injectable (delay + service) so the loop is fully
/// unit-testable without real waits or network.
/// </summary>
public sealed class DeviceCodePairing
{
    private const int HardCapSeconds = 15 * 60;
    private const int BackoffAfterSeconds = 60;
    private const int SlowPollSeconds = 10;
    private const int MaxCreateAttempts = 3;
    private const int MaxConsecutivePollFailures = 5;

    private readonly IDeviceCodeService _service;
    private readonly string _cloudUrl;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public DeviceCodePairing(
        IDeviceCodeService service,
        string cloudUrl,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _service = service;
        _cloudUrl = cloudUrl;
        // Injectable so tests pass a no-op delay (no real waits). `waited`
        // already tracks elapsed poll seconds for the backoff threshold.
        _delay = delay ?? Task.Delay;
    }

    public async Task<PairingResult> RunAsync(
        string fingerprint,
        string version,
        IProgress<PairingProgress>? progress,
        CancellationToken ct)
    {
        var created = await CreateWithRetryAsync(fingerprint, version, ct)
            .ConfigureAwait(false);
        var retainForInstallCutover = false;
        try
        {

        var deadlineSeconds = Math.Min(
            created.ExpiresInSeconds > 0 ? created.ExpiresInSeconds : HardCapSeconds,
            HardCapSeconds);
        var fastInterval = created.PollIntervalSeconds > 0 ? created.PollIntervalSeconds : 5;

        var waited = 0;
        var consecutiveFailures = 0;
        progress?.Report(new PairingProgress(
            created.DeviceCode, created.VerificationUrl, deadlineSeconds, "pending"));

        while (waited < deadlineSeconds)
        {
            ct.ThrowIfCancellationRequested();

            DeviceCodePollResult poll;
            var pollSucceeded = false;
            try
            {
                poll = await _service.PollAsync(
                    created.DeviceCode,
                    created.DeviceSecret,
                    ct).ConfigureAwait(false);
                pollSucceeded = true;
            }
            catch (Exception ex) when (IsTransientPollFailure(ex, ct))
            {
                consecutiveFailures++;
                if (consecutiveFailures >= MaxConsecutivePollFailures)
                {
                    progress?.Report(new PairingProgress(
                        created.DeviceCode,
                        created.VerificationUrl,
                        Math.Max(0, deadlineSeconds - waited),
                        "transient_retry_exhausted"));
                    return PairingResult.Failure("transient_retry_exhausted");
                }
                poll = new DeviceCodePollResult("pending");
            }

            if (pollSucceeded)
                consecutiveFailures = 0;

            if (poll.IsAuthorized)
            {
                // Fail LOUD if the server authorized us but delivered no API key.
                // device-token is a single-read key: if a prior poll consumed it,
                // a re-poll returns authorized WITHOUT the key. Building a config
                // with ApiKey="" would install a DEAD agent (Program.cs never
                // creates the cloud client → never heartbeats → offline). The
                // operator retries pairing rather than getting a silent dead box.
                if (string.IsNullOrWhiteSpace(poll.ApiKey))
                {
                    progress?.Report(new PairingProgress(
                        created.DeviceCode, created.VerificationUrl, 0, "no_credentials"));
                    return PairingResult.Failure("no_credentials");
                }
                if (poll.VerticalConfigRaw is null || poll.VerticalConfig is null ||
                    string.IsNullOrWhiteSpace(poll.VerticalConfigSignature) ||
                    string.IsNullOrWhiteSpace(poll.VerticalConfigKeyId))
                {
                    progress?.Report(new PairingProgress(
                        created.DeviceCode,
                        created.VerificationUrl,
                        0,
                        "signed_profile_unavailable"));
                    return PairingResult.Failure("signed_profile_unavailable");
                }

                var config = new SetupConfig(
                    PharmacyId: poll.PharmacyId ?? "",
                    ApiKey: poll.ApiKey,
                    CloudUrl: _cloudUrl,
                    ReleaseTag: "",
                    // The operator has not installed anything yet; the native
                    // consent screen still gates the write. Once they agree,
                    // every connected install starts in local observe/learn
                    // capture mode (no autonomous actuation).
                    LearningMode: true,
                    AgentId: poll.AgentId ?? "",
                    Reasoning: poll.Reasoning,
                    VerticalConfigRaw: poll.VerticalConfigRaw,
                    VerticalConfig: poll.VerticalConfig,
                    VerticalConfigSignature: poll.VerticalConfigSignature,
                    VerticalConfigKeyId: poll.VerticalConfigKeyId,
                    DeviceCode: created.DeviceCode,
                    DeviceKeyId: created.DeviceKeyId,
                    DeviceKeyName: created.DeviceKeyName,
                    MaintenanceKeyId: created.MaintenanceKeyId,
                    DeviceFingerprint: fingerprint,
                    DeviceChallenge: created.DeviceChallenge);
                progress?.Report(new PairingProgress(
                    created.DeviceCode, created.VerificationUrl,
                    Math.Max(0, deadlineSeconds - waited), "authorized"));
                retainForInstallCutover = true;
                return PairingResult.Success(config);
            }

            if (poll.IsTerminal)
            {
                progress?.Report(new PairingProgress(
                    created.DeviceCode, created.VerificationUrl, 0, poll.Status));
                return PairingResult.Failure(poll.Status);
            }

            var interval = waited >= BackoffAfterSeconds ? SlowPollSeconds : fastInterval;
            progress?.Report(new PairingProgress(
                created.DeviceCode, created.VerificationUrl,
                Math.Max(0, deadlineSeconds - waited), "pending"));

            await _delay(TimeSpan.FromSeconds(interval), ct).ConfigureAwait(false);
            waited += interval;
        }

        return PairingResult.Failure("expired");
        }
        finally
        {
            if (!retainForInstallCutover && !string.IsNullOrWhiteSpace(created.DeviceKeyId))
                _service.AbortPendingKey(fingerprint, created.DeviceKeyId);
        }
    }

    private async Task<DeviceCodeCreateResult> CreateWithRetryAsync(
        string fingerprint,
        string version,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxCreateAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await _service.CreateAsync(fingerprint, version, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (
                attempt < MaxCreateAttempts && IsTransientCreateFailure(ex, ct))
            {
                await _delay(TimeSpan.FromSeconds(attempt), ct).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("Pairing code retry budget was exhausted.");
    }

    private static bool IsTransientCreateFailure(Exception ex, CancellationToken ct) =>
        ex is DeviceCodeTransientException or JsonException ||
        ex is TaskCanceledException && !ct.IsCancellationRequested ||
        ex is HttpRequestException http && IsTransientHttp(http);

    private static bool IsTransientPollFailure(Exception ex, CancellationToken ct) =>
        ex is DeviceCodeTransientException or JsonException ||
        ex is InvalidOperationException && ex is not DeviceAuthorityUnavailableException ||
        ex is TaskCanceledException && !ct.IsCancellationRequested ||
        ex is HttpRequestException http && IsTransientHttp(http);

    private static bool IsTransientHttp(HttpRequestException exception) =>
        exception.StatusCode is null or
            HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests ||
        (int?)exception.StatusCode >= 500;
}
