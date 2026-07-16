using SuavoAgent.Contracts.Security;

namespace SuavoAgent.Core.Workers;

public sealed partial class LearningWorker
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var authority = _sp.GetService<ObservationActivationAuthority>();
        if (authority is null)
        {
            try { await RunAuthorizedSessionAsync(stoppingToken).ConfigureAwait(false); }
            finally { await StopObserversAsync().ConfigureAwait(false); }
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            using var activation = authority.TryAcquireExecutionLease(stoppingToken);
            if (activation is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await RunAuthorizedSessionAsync(activation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (activation.Token.IsCancellationRequested)
            {
                // Revocation, expiry, Pause, and normal service shutdown all
                // converge through the same observer teardown below.
            }
            finally
            {
                await StopObserversAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task StopObserversAsync()
    {
        _behavioralReceiver?.SetInteractionCallback(null);
        foreach (var observer in _observers.ToArray())
        {
            try { await observer.StopAsync().ConfigureAwait(false); }
            finally { observer.Dispose(); }
        }
        _observers.Clear();
        _logger.LogInformation("LearningWorker observation runtime stopped");
    }
}
