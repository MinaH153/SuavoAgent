using Microsoft.Extensions.Logging;
using SuavoAgent.Core.ActionGrammarV1.Workflows;

namespace SuavoAgent.Core.Cloud;

/// <summary>
/// Concrete <see cref="IWorkflowAuditClient"/>. Wraps
/// <see cref="SuavoCloudClient.PostSignedAsync"/> for the two POST
/// endpoints the agent uses during a workflow run:
///
///   - <c>POST /api/agent/workflows/{run_id}/audit</c> per step
///   - <c>POST /api/agent/workflows/{run_id}/complete</c> at run end
///
/// Both endpoints are HMAC-authed against the agent's machine_fingerprint
/// (the same channel that <c>/api/agent/sync</c> uses).
/// </summary>
public sealed class WorkflowAuditCloudClient : IWorkflowAuditClient
{
    private readonly SuavoCloudClient _cloudClient;
    private readonly ILogger<WorkflowAuditCloudClient> _logger;

    public WorkflowAuditCloudClient(SuavoCloudClient cloudClient, ILogger<WorkflowAuditCloudClient> logger)
    {
        _cloudClient = cloudClient ?? throw new ArgumentNullException(nameof(cloudClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task PostStepAuditAsync(WorkflowStepAuditEntry entry, CancellationToken ct)
    {
        try
        {
            // Bug 21 (MinaH153/SuavoAgent#63): `dry_run` is the REQUESTED
            // value from the workflow definition (alias `requested_dry_run`
            // for the cloud schema once the supabase migration lands). The
            // new `effective_dry_run` is what the Helper actually enforced
            // — the OR of request.DryRun and the local ActuationGate. Null
            // when the verb did not emit a dry-run output (e.g. dispatch
            // failures before any actuation call).
            var payload = new
            {
                step_index = entry.StepIndex,
                verb_name = entry.VerbName,
                verb_version = entry.VerbVersion,
                dry_run = entry.DryRun,
                requested_dry_run = entry.DryRun,
                effective_dry_run = entry.EffectiveDryRun,
                outcome = entry.Outcome,
                exec_duration_ms = entry.ExecDurationMs,
                error_kind = entry.ErrorKind,
                error_detail = entry.ErrorDetail,
                @params = entry.Params,
                before_state = entry.BeforeState,
                after_state = entry.AfterState,
            };
            await _cloudClient
                .PostSignedAsync($"/api/agent/workflows/{entry.WorkflowRunId}/audit", payload, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "WorkflowAudit: step {StepIndex} POST failed for run {RunId}",
                entry.StepIndex,
                entry.WorkflowRunId);
        }
    }

    public async Task PostRunCompletedAsync(string workflowRunId, WorkflowRunOutcome outcome, string? abortReason, CancellationToken ct)
    {
        try
        {
            var payload = new
            {
                outcome = outcome.ToString().ToLowerInvariant(),
                abort_reason = abortReason,
            };
            await _cloudClient
                .PostSignedAsync($"/api/agent/workflows/{workflowRunId}/complete", payload, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "WorkflowAudit: complete POST failed for run {RunId} outcome={Outcome}",
                workflowRunId,
                outcome);
        }
    }
}
