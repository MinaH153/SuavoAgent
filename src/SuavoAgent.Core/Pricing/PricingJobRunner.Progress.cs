using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Core.Pricing;

public sealed partial class PricingJobRunner
{
    private void ReportDeliverablePath(
        string path,
        Action<string>? deliverableObserver)
    {
        if (deliverableObserver is null) return;
        try { deliverableObserver(path); }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "core.pricing.deliverable_observer_failed exception_type={ExceptionType}",
                exception.GetType().Name);
        }
    }

    private async ValueTask ReportLocalProgressAsync(
        PricingJobLocalPhase phase,
        int processedItems,
        int totalItems,
        int needsReviewItems,
        Func<PricingJobLocalProgress, CancellationToken, ValueTask>?
            runProgressObserver,
        CancellationToken ct)
    {
        var progress = new PricingJobLocalProgress(
            phase,
            processedItems,
            totalItems,
            needsReviewItems);
        if (_localProgressObserver is not null)
        {
            try
            {
                _localProgressObserver(progress);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    "core.pricing.local_progress_observer_failed exception_type={ExceptionType}",
                    exception.GetType().Name);
            }
        }

        if (runProgressObserver is null) return;
        try
        {
            await runProgressObserver(progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "core.pricing.run_progress_observer_failed exception_type={ExceptionType}",
                exception.GetType().Name);
        }
    }
}
