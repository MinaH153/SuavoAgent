using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SuavoAgent.Events;

namespace SuavoAgent.Attestation;

/// <summary>
/// BackgroundService that runs an attestation check on startup and every
/// 30 minutes thereafter. Emits attestation.* events via the injected
/// <see cref="IEventPublisher"/>. A failed startup check halts mutation
/// verbs; periodic re-check catches mid-run tampering.
/// </summary>
public sealed class AttestationWorker : BackgroundService
{
    private readonly IAttestationVerifier _verifier;
    private readonly IEventPublisher _eventPublisher;
    private readonly EventBuilder _eventBuilder;
    private readonly ILogger<AttestationWorker> _logger;
    private readonly TimeSpan _recheckInterval;

    public AttestationWorker(
        IAttestationVerifier verifier,
        IEventPublisher eventPublisher,
        EventBuilder eventBuilder,
        ILogger<AttestationWorker> logger,
        TimeSpan? recheckInterval = null)
    {
        _verifier = verifier;
        _eventPublisher = eventPublisher;
        _eventBuilder = eventBuilder;
        _logger = logger;
        _recheckInterval = recheckInterval ?? TimeSpan.FromMinutes(30);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "attestation worker started — recheck interval {Interval}",
            _recheckInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await _verifier.VerifyAsync(stoppingToken).ConfigureAwait(false);
                EmitAttestationEvent(result);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "attestation check threw — swallowing so loop survives");
            }

            try
            {
                await Task.Delay(_recheckInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("attestation worker stopping");
    }

    internal void EmitAttestationEvent(AttestationResult result)
    {
        switch (result.Status)
        {
            case AttestationStatus.Verified:
                _eventPublisher.Publish(_eventBuilder.AttestationVerified(
                    result.ManifestVersion ?? "unknown",
                    fileCount: result.FileCount,
                    verifyDurationMs: result.DurationMs));
                break;

            case AttestationStatus.Mismatch:
                _eventPublisher.Publish(_eventBuilder.AttestationMismatch(
                    result.ManifestVersion ?? "unknown",
                    result.Mismatches?.Select(m => m.FileName).ToList() ?? new List<string>()));
                break;

            case AttestationStatus.SignatureInvalid:
                _eventPublisher.Publish(_eventBuilder.AttestationMismatch(
                    result.ManifestVersion ?? "unknown",
                    new List<string> { "__signature_invalid__" }));
                break;

            case AttestationStatus.NetworkFailure:
                // Network failures are logged but not emitted as mismatch —
                // they're expected behavior during partition.
                _logger.LogInformation("attestation: network failure, not emitting event (retry next interval)");
                break;

            case AttestationStatus.ConfigurationError:
                _logger.LogError("attestation: configuration error: {Reason}", result.Reason);
                break;
        }
    }
}
