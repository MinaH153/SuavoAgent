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
/// built from the dashboard-issued credentials; the EXISTING install flow
/// (InstallOrchestrator) then persists it to appsettings.json and DPAPI-seals
/// it via CredentialProtector — the proven path. (We deliberately do NOT couple
/// this lean installer project to Core's credential store; making the encrypted
/// credential store Core's boot source-of-truth is a separate persistence-model
/// change — see PR notes.)
///
/// All time + IO is injectable (delay + service) so the loop is fully
/// unit-testable without real waits or network.
/// </summary>
public sealed class DeviceCodePairing
{
    private const int HardCapSeconds = 15 * 60;
    private const int BackoffAfterSeconds = 60;
    private const int SlowPollSeconds = 10;

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
        var created = await _service.CreateAsync(fingerprint, version, ct).ConfigureAwait(false);

        var deadlineSeconds = Math.Min(
            created.ExpiresInSeconds > 0 ? created.ExpiresInSeconds : HardCapSeconds,
            HardCapSeconds);
        var fastInterval = created.PollIntervalSeconds > 0 ? created.PollIntervalSeconds : 5;

        var waited = 0;
        progress?.Report(new PairingProgress(
            created.DeviceCode, created.VerificationUrl, deadlineSeconds, "pending"));

        while (waited < deadlineSeconds)
        {
            ct.ThrowIfCancellationRequested();

            DeviceCodePollResult poll;
            try
            {
                poll = await _service.PollAsync(created.DeviceCode, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                // Transient (network blip or 429). Don't abort pairing — back
                // off and try again until the deadline.
                poll = new DeviceCodePollResult("pending");
            }

            if (poll.IsAuthorized)
            {
                var config = new SetupConfig(
                    PharmacyId: poll.PharmacyId ?? "",
                    ApiKey: poll.ApiKey ?? "",
                    CloudUrl: _cloudUrl,
                    ReleaseTag: "",
                    LearningMode: false,
                    AgentId: poll.AgentId ?? "");
                progress?.Report(new PairingProgress(
                    created.DeviceCode, created.VerificationUrl,
                    Math.Max(0, deadlineSeconds - waited), "authorized"));
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
}
