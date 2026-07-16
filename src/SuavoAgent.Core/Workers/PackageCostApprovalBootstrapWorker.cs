using Microsoft.Extensions.Logging;
using SuavoAgent.Core.Pricing;

namespace SuavoAgent.Core.Workers;

/// <summary>
/// Proactively stages the PHI-free package-cost PIC proposal after the live
/// PioneerRx UIA surface has been verified. Heartbeat then transports the
/// durable proposal through its existing pricingApprovalProposals field.
/// </summary>
internal sealed class PackageCostApprovalBootstrapWorker : ResilientHostedService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly PackageCostApprovalBootstrapper _bootstrapper;
    private readonly ILogger<PackageCostApprovalBootstrapWorker> _logger;
    private string? _lastCode;

    internal PackageCostApprovalBootstrapWorker(
        PackageCostApprovalBootstrapper bootstrapper,
        ILogger<PackageCostApprovalBootstrapWorker> logger,
        WorkerHealthRegistry? healthRegistry = null)
        : base(logger, healthRegistry)
    {
        _bootstrapper = bootstrapper ?? throw new ArgumentNullException(nameof(bootstrapper));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override string WorkerName => "package-cost-approval-bootstrap";

    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var result = await _bootstrapper.TryStageAsync(
                    DateTimeOffset.UtcNow,
                    stoppingToken)
                .ConfigureAwait(false);
            if (!string.Equals(_lastCode, result.Code, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "core.pricing.package_approval_bootstrap_state code={Code}",
                    result.Code);
                _lastCode = result.Code;
            }

            await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
        }
    }
}
