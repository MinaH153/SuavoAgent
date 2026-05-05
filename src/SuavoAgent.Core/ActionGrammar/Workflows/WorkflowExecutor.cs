using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SuavoAgent.Core.ActionGrammarV1.Verbs.Actuation;
using SuavoAgent.Core.Audit;
using SuavoAgent.Core.Mission;

namespace SuavoAgent.Core.ActionGrammarV1.Workflows;

/// <summary>
/// Linear workflow executor. Iterates the definition's step list,
/// dispatching each step through <see cref="VerbDispatcher"/>, posting
/// per-step audit rows to <see cref="IWorkflowAuditClient"/>, honouring
/// <see cref="ActuationGateState"/> between steps (kill switch / pause /
/// disabled flags abort the run mid-flight).
///
/// Phase-5.2 scope: linear only (no branches, no loops, no conditionals).
/// Branching workflows ship in Phase 5.4; explicitly out of contract here
/// per the plan.
/// </summary>
public sealed class WorkflowExecutor
{
    private readonly VerbRegistry _registry;
    private readonly VerbDispatcher _dispatcher;
    private readonly IActuationGateway _gateway;
    private readonly IWorkflowAuditClient _audit;
    private readonly ILogger<WorkflowExecutor> _logger;

    public WorkflowExecutor(
        VerbRegistry registry,
        VerbDispatcher dispatcher,
        IActuationGateway gateway,
        IWorkflowAuditClient audit,
        ILogger<WorkflowExecutor> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public sealed record WorkflowExecutionResult(
        WorkflowRunOutcome Outcome,
        string? AbortReason,
        int StepsCompleted,
        int TotalSteps);

    public async Task<WorkflowExecutionResult> ExecuteAsync(
        WorkflowDefinitionDto definition,
        IServiceProvider services,
        AuditChain auditChain,
        MissionCharter charter,
        string pharmacyId,
        string actor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(auditChain);

        _logger.LogInformation(
            "WorkflowExecutor: starting run={RunId} workflow={Name}@{Version} steps={Count} dry_run={DryRun}",
            definition.WorkflowRunId,
            definition.WorkflowName,
            definition.WorkflowVersion,
            definition.Steps.Count,
            definition.DryRun);

        for (var stepIndex = 0; stepIndex < definition.Steps.Count; stepIndex++)
        {
            ct.ThrowIfCancellationRequested();

            // Gate check between every step. Kill switch / pause / disabled
            // each stop the run cleanly with a typed audit row.
            var gate = await _gateway.GetStateAsync(ct).ConfigureAwait(false);
            if (gate.KillSwitchTrippedUtc is not null)
            {
                await PostAbortAsync(definition.WorkflowRunId, "kill_switch_tripped", definition.Steps.Count, stepIndex, ct);
                return new WorkflowExecutionResult(WorkflowRunOutcome.Aborted, "kill_switch_tripped", stepIndex, definition.Steps.Count);
            }
            if (!gate.Enabled)
            {
                await PostAbortAsync(definition.WorkflowRunId, "gate_disabled", definition.Steps.Count, stepIndex, ct);
                return new WorkflowExecutionResult(WorkflowRunOutcome.Aborted, "gate_disabled", stepIndex, definition.Steps.Count);
            }
            if (gate.PausedUntilUtc is { } until && until > DateTimeOffset.UtcNow)
            {
                await PostAbortAsync(definition.WorkflowRunId, $"gate_paused_until:{until:o}", definition.Steps.Count, stepIndex, ct);
                return new WorkflowExecutionResult(WorkflowRunOutcome.Aborted, "gate_paused", stepIndex, definition.Steps.Count);
            }

            var step = definition.Steps[stepIndex];
            var stepResult = await ExecuteStepAsync(
                definition,
                stepIndex,
                step,
                services,
                auditChain,
                charter,
                pharmacyId,
                actor,
                ct).ConfigureAwait(false);

            if (stepResult.Outcome != VerbDispatchOutcome.Success)
            {
                _logger.LogWarning(
                    "WorkflowExecutor: step {Index} ({Verb}) outcome={Outcome}: {Reason}",
                    stepIndex,
                    step.Verb,
                    stepResult.Outcome,
                    stepResult.FailureReason);

                await _audit.PostRunCompletedAsync(definition.WorkflowRunId, WorkflowRunOutcome.Failed, stepResult.FailureReason, ct);
                return new WorkflowExecutionResult(WorkflowRunOutcome.Failed, stepResult.FailureReason, stepIndex, definition.Steps.Count);
            }
        }

        await _audit.PostRunCompletedAsync(definition.WorkflowRunId, WorkflowRunOutcome.Completed, abortReason: null, ct).ConfigureAwait(false);
        return new WorkflowExecutionResult(WorkflowRunOutcome.Completed, null, definition.Steps.Count, definition.Steps.Count);
    }

    private async Task<VerbDispatchResult> ExecuteStepAsync(
        WorkflowDefinitionDto definition,
        int stepIndex,
        WorkflowStepDto step,
        IServiceProvider services,
        AuditChain auditChain,
        MissionCharter charter,
        string pharmacyId,
        string actor,
        CancellationToken ct)
    {
        var entry = _registry.Resolve(step.Verb, step.ManifestHash, out var failureReason);
        if (entry is null)
        {
            await PostStepFailureAsync(definition.WorkflowRunId, stepIndex, step, "manifest_resolution_failed", failureReason, ct);
            return new VerbDispatchResult(
                InvocationId: $"{definition.WorkflowRunId}:{stepIndex}",
                Verb: step.Verb,
                Version: step.VerbVersion ?? "?",
                Outcome: VerbDispatchOutcome.Rejected,
                Authz: null,
                RollbackEnvelope: VerbRollbackEnvelope.None($"{definition.WorkflowRunId}:{stepIndex}"),
                Output: new Dictionary<string, object?>(),
                FailureReason: failureReason);
        }

        var parameters = ParseStepParameters(step.Params, entry.Metadata.Params);
        var verb = _registry.Instantiate(entry, services);
        var invocationId = $"{definition.WorkflowRunId}:{stepIndex}";
        var deadline = DateTimeOffset.UtcNow + entry.Metadata.MaxExecutionTime;
        var verbCtx = new VerbContext(
            PharmacyId: pharmacyId,
            Charter: charter,
            Audit: auditChain,
            InvocationId: invocationId,
            Actor: actor,
            Parameters: parameters,
            Services: services,
            DeadlineUtc: deadline);

        var sw = Stopwatch.StartNew();
        var dispatch = await _dispatcher.DispatchAsync(verb, verbCtx, ct).ConfigureAwait(false);
        sw.Stop();

        var outcome = dispatch.Outcome switch
        {
            VerbDispatchOutcome.Success => "success",
            VerbDispatchOutcome.Rejected => "rejected",
            VerbDispatchOutcome.Failed => "failed",
            _ => "unknown",
        };
        var (errKind, errDetail) = dispatch.FailureReason is { } fr
            ? SplitErrorReason(fr)
            : (null, null);

        await _audit.PostStepAuditAsync(new WorkflowStepAuditEntry(
            WorkflowRunId: definition.WorkflowRunId,
            StepIndex: stepIndex,
            VerbName: step.Verb,
            VerbVersion: entry.Version,
            DryRun: definition.DryRun,
            Outcome: outcome,
            ExecDurationMs: sw.ElapsedMilliseconds,
            ErrorKind: errKind,
            ErrorDetail: errDetail,
            Params: parameters,
            BeforeState: null,
            AfterState: dispatch.Output is { Count: > 0 } ? dispatch.Output : null), ct).ConfigureAwait(false);

        return dispatch;
    }

    private async Task PostStepFailureAsync(
        string runId,
        int stepIndex,
        WorkflowStepDto step,
        string errKind,
        string? errDetail,
        CancellationToken ct)
    {
        await _audit.PostStepAuditAsync(new WorkflowStepAuditEntry(
            WorkflowRunId: runId,
            StepIndex: stepIndex,
            VerbName: step.Verb,
            VerbVersion: step.VerbVersion ?? "?",
            DryRun: false,
            Outcome: "rejected",
            ExecDurationMs: null,
            ErrorKind: errKind,
            ErrorDetail: errDetail,
            Params: ParseStepParameters(step.Params, new VerbParameterSchema(Array.Empty<VerbParameterSpec>())),
            BeforeState: null,
            AfterState: null), ct).ConfigureAwait(false);
    }

    private async Task PostAbortAsync(string runId, string reason, int totalSteps, int currentIndex, CancellationToken ct)
    {
        _logger.LogWarning("WorkflowExecutor: aborting run={RunId} at step {Idx}/{Total} reason={Reason}", runId, currentIndex, totalSteps, reason);
        await _audit.PostRunCompletedAsync(runId, WorkflowRunOutcome.Aborted, reason, ct).ConfigureAwait(false);
    }

    private static IReadOnlyDictionary<string, object?> ParseStepParameters(JsonElement raw, VerbParameterSchema schema)
    {
        if (raw.ValueKind != JsonValueKind.Object) return new Dictionary<string, object?>();
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in raw.EnumerateObject())
        {
            var spec = schema.Parameters.FirstOrDefault(p => p.Name == prop.Name);
            object? value = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when prop.Value.TryGetInt32(out var i) => i,
                JsonValueKind.Number when prop.Value.TryGetInt64(out var l) => l,
                JsonValueKind.Number => prop.Value.GetDouble(),
                JsonValueKind.Null => null,
                JsonValueKind.Array => prop.Value.EnumerateArray()
                    .Select(e => e.ValueKind == JsonValueKind.String ? (object?)e.GetString() : e.GetRawText())
                    .ToList(),
                _ => prop.Value.GetRawText(),
            };
            // Coerce ints when the schema expects bool/int (avoid type-mismatch rejections from the dispatcher).
            if (spec is not null && spec.ClrType == typeof(bool) && value is string sb)
            {
                value = bool.TryParse(sb, out var b) && b;
            }
            dict[prop.Name] = value;
        }
        return dict;
    }

    private static (string Kind, string Detail) SplitErrorReason(string reason)
    {
        var idx = reason.IndexOf(':');
        return idx > 0
            ? (reason[..idx].Trim(), reason[(idx + 1)..].Trim())
            : ("error", reason);
    }
}
