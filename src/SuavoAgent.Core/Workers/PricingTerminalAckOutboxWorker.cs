using SuavoAgent.Core.Pricing;

namespace SuavoAgent.Core.Workers;

/// <summary>
/// Retry-only worker for PHI-free terminal pricing ACKs. This process has no
/// execution dependency, which makes restart recovery incapable of reopening
/// PioneerRx or a workbook.
/// </summary>
internal sealed class PricingTerminalAckOutboxWorker : ResilientHostedService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private readonly PricingTerminalAckOutbox _outbox;

    internal PricingTerminalAckOutboxWorker(
        PricingTerminalAckOutbox outbox,
        ILogger<PricingTerminalAckOutboxWorker> logger,
        WorkerHealthRegistry? healthRegistry = null)
        : base(logger, healthRegistry)
    {
        _outbox = outbox;
    }

    protected override string WorkerName => "pricing-terminal-ack-outbox";

    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await _outbox.RetryPendingAsync(stoppingToken).ConfigureAwait(false);
            await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
        }
    }
}
