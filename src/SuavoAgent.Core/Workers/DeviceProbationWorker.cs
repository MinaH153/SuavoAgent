using Microsoft.Extensions.Options;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Health;

namespace SuavoAgent.Core.Workers;

/// <summary>
/// PHI-free, command-free bootstrap worker used only by a pending device-code
/// credential. A successful exact health receipt is the sole event that writes
/// authority-promotion readiness evidence.
/// </summary>
internal sealed class DeviceProbationWorker : BackgroundService
{
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(3);
    private readonly ILogger<DeviceProbationWorker> _logger;
    private readonly AgentOptions _options;
    private readonly DeviceProbationCloudClient _cloud;
    private readonly IDeviceAuthoritySigner _signer;
    private readonly IPioneerRxProbationSqlCanary _canary;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _retryDelay;

    internal DeviceProbationWorker(
        ILogger<DeviceProbationWorker> logger,
        IOptions<AgentOptions> options,
        DeviceProbationCloudClient cloud,
        IDeviceAuthoritySigner signer,
        IPioneerRxProbationSqlCanary canary,
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? retryDelay = null)
    {
        _logger = logger;
        _options = options.Value;
        _cloud = cloud;
        _signer = signer;
        _canary = canary;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _retryDelay = retryDelay ?? DefaultRetryDelay;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var proof = _signer.SignProvisioningProof(new(
            _options.InstallDeviceCode
                ?? throw new InvalidOperationException("Pending device code is unavailable."),
            _options.InstallProvisioningId
                ?? throw new InvalidOperationException("Pending provisioning identity is unavailable."),
            _options.AgentId
                ?? throw new InvalidOperationException("Pending agent identity is unavailable."),
            _options.PharmacyId
                ?? throw new InvalidOperationException("Pending pharmacy identity is unavailable."),
            _options.MachineFingerprint
                ?? throw new InvalidOperationException("Pending device fingerprint is unavailable."),
            _options.DeviceAttestationKeyId
                ?? throw new InvalidOperationException("Pending device key id is unavailable."),
            _options.InstallDeviceChallenge
                ?? throw new InvalidOperationException("Pending device challenge is unavailable."),
            _options.SqlServerCertificateSha256));

        SignedDeviceProbationHealth? healthProof = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (healthProof is null)
                {
                    var canary = await _canary.ProbeAsync(stoppingToken)
                        .ConfigureAwait(false);
                    if (!canary.SqlConnected || !canary.SchemaCanaryGreen ||
                        canary.Code != "pms_schema_canary")
                    {
                        await Task.Delay(_retryDelay, stoppingToken).ConfigureAwait(false);
                        continue;
                    }
                    healthProof = _signer.SignProbationHealth(new DeviceProbationHealthFields(
                        proof.DeviceCode,
                        proof.ProvisioningId,
                        proof.AgentId,
                        proof.PharmacyId,
                        proof.Fingerprint,
                        _options.Version,
                        proof.KeyId,
                        proof.Challenge,
                        HelperAttached: false,
                        IpcConnected: false,
                        ActuationReady: false,
                        SqlConnected: true,
                        SchemaCanaryGreen: true,
                        PmsCode: canary.Code,
                        SqlServerCertificateSha256: proof.SqlServerCertificateSha256,
                        ObservedAtUtc: _utcNow().UtcDateTime.ToString(
                            "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                            System.Globalization.CultureInfo.InvariantCulture),
                        ChallengeCounter: 1));
                }
                var outcome = await _cloud.SendHealthAsync(
                    healthProof,
                    stoppingToken).ConfigureAwait(false);
                if (outcome == DeviceProbationHealthSendOutcome.RefreshObservation)
                {
                    // The server returns this only after its locked transaction
                    // proves the challenge was never consumed. Unknown outcomes
                    // keep replaying the identical proof so response loss can
                    // never extend freshness.
                    healthProof = null;
                    continue;
                }
                if (outcome == DeviceProbationHealthSendOutcome.CredentialExpired)
                    throw new InvalidOperationException(
                        "Pending device probation credential expired before promotion.");
                if (outcome == DeviceProbationHealthSendOutcome.Accepted)
                {
                    var now = _utcNow();
                    RuntimeHealthEvidence.WriteCloudAuthHealth(
                        RuntimeHealthEvidence.CloudAuthHealthPath(),
                        "ok",
                        now,
                        now,
                        0,
                        null,
                        false,
                        null,
                        false);
                    RuntimeHealthEvidence.WriteActivationReadiness(
                        RuntimeHealthEvidence.ActivationReadinessPath(),
                        _options.Version,
                        _options.AgentId,
                        _options.InstallProvisioningId,
                        now,
                        helperAttached: false,
                        ipcConnected: false,
                        actuationReady: false,
                        sqlConnected: true,
                        schemaCanaryGreen: true,
                        pmsCode: "pms_schema_canary",
                        proof,
                        healthProof);
                    _logger.LogInformation(
                        "Device probation health accepted for provisioning {ProvisioningId}",
                        proof.ProvisioningId);
                    return;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                _logger.LogSafeWarning(ex);
            }

            await Task.Delay(_retryDelay, stoppingToken).ConfigureAwait(false);
        }
    }
}
