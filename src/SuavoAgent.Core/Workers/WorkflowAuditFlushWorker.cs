using SuavoAgent.Core.ActionGrammarV1.Workflows;
using SuavoAgent.Core.Cloud;

namespace SuavoAgent.Core.Workers;

/// <summary>
/// Drains already-staged workflow audit facts. This worker owns network retry
/// only; it never executes or revisits a workflow step.
/// </summary>
internal sealed class WorkflowAuditFlushWorker : ResilientHostedService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);
    private readonly WorkflowAuditCloudClient? _auditClient;

    internal WorkflowAuditFlushWorker(
        IWorkflowAuditClient auditClient,
        ILogger<WorkflowAuditFlushWorker> logger,
        WorkerHealthRegistry? healthRegistry = null)
        : base(logger, healthRegistry)
    {
        ArgumentNullException.ThrowIfNull(auditClient);
        _auditClient = auditClient as WorkflowAuditCloudClient;
    }

    protected override string WorkerName => "workflow-audit-flush";

    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        if (_auditClient is null)
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            await _auditClient.FlushPendingAsync(stoppingToken)
                .ConfigureAwait(false);
            await Task.Delay(FlushInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    internal Task RunOnceAsync(CancellationToken cancellationToken) =>
        _auditClient?.FlushPendingAsync(cancellationToken) ?? Task.CompletedTask;
}
