using SuavoAgent.Core.Cloud;

namespace SuavoAgent.Core.Workers;

/// <summary>
/// Retries only the PHI-minimized pricing result outbox. The retired workbook
/// upload consumer is deliberately absent: this worker never polls, downloads,
/// stages, or opens a cloud-supplied workbook.
/// </summary>
internal sealed class PricingResultOutboxWorker : ResilientHostedService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private readonly PricingJobCloudUploader _uploader;

    internal PricingResultOutboxWorker(
        PricingJobCloudUploader uploader,
        ILogger<PricingResultOutboxWorker> logger,
        WorkerHealthRegistry? healthRegistry = null)
        : base(logger, healthRegistry)
    {
        _uploader = uploader;
    }

    protected override string WorkerName => "pricing-result-outbox";

    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await _uploader.FlushPendingAsync(stoppingToken).ConfigureAwait(false);
            await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
        }
    }
}
