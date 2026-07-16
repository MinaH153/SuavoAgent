using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Core.Cloud;

namespace SuavoAgent.Core.Workers;

/// <summary>
/// Maintains the short-lived control-plane authorization lease. Pairing and a
/// healthy heartbeat never confer observation authority. Every transport,
/// status, parsing, signature, binding, persistence, or replay failure revokes
/// local authority before the next observation operation can proceed.
/// </summary>
internal sealed class ObservationActivationLeaseWorker : BackgroundService
{
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan LocalAuthorityPollInterval = TimeSpan.FromSeconds(1);

    private readonly Func<CancellationToken, Task<ObservationActivationState?>> _requestLease;
    private readonly ObservationActivationAuthority _authority;
    private readonly ILogger<ObservationActivationLeaseWorker> _logger;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _refreshInterval;

    public ObservationActivationLeaseWorker(
        SuavoCloudClient cloud,
        ObservationActivationAuthority authority,
        IObservationActivationRequestSigner requestSigner,
        ILogger<ObservationActivationLeaseWorker> logger)
        : this(
            token => cloud.RequestObservationActivationLeaseAsync(
                requestSigner,
                authority,
                token),
            authority,
            logger,
            TimeProvider.System,
            RefreshInterval)
    { }

    internal ObservationActivationLeaseWorker(
        Func<CancellationToken, Task<ObservationActivationState?>> requestLease,
        ObservationActivationAuthority authority,
        ILogger<ObservationActivationLeaseWorker> logger,
        TimeProvider clock,
        TimeSpan refreshInterval)
    {
        _requestLease = requestLease;
        _authority = authority;
        _logger = logger;
        _clock = clock;
        _refreshInterval = refreshInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RefreshOnceAsync(stoppingToken).ConfigureAwait(false);
            var nextCloudRefresh = _clock.GetUtcNow() + _refreshInterval;
            while (!stoppingToken.IsCancellationRequested)
            {
                var remaining = nextCloudRefresh - _clock.GetUtcNow();
                if (remaining <= TimeSpan.Zero) break;
                _authority.Refresh();
                await Task.Delay(
                        remaining < LocalAuthorityPollInterval
                            ? remaining
                            : LocalAuthorityPollInterval,
                        _clock,
                        stoppingToken)
                    .ConfigureAwait(false);
            }
        }
    }

    internal async Task<bool> RefreshOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var candidate = await _requestLease(cancellationToken).ConfigureAwait(false);
            if (candidate is null)
            {
                _authority.RevokeLocalAuthority();
                _logger.LogWarning("Observation activation response was invalid; authority revoked");
                return false;
            }

            var installed = _authority.TryInstall(candidate);
            if (installed.Succeeded) return true;

            _authority.RevokeLocalAuthority();
            _logger.LogWarning(
                "Observation activation lease was rejected code={Code}; authority revoked",
                installed.Code);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _authority.RevokeLocalAuthority();
            _logger.LogWarning(
                "Observation activation refresh failed type={ExceptionType}; authority revoked",
                exception.GetType().Name);
            return false;
        }
    }
}
