using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SuavoAgent.Core.ActionGrammarV1.Verbs.Actuation;
using SuavoAgent.Core.Audit;
using SuavoAgent.Core.Mission;

namespace SuavoAgent.Core.ActionGrammarV1.Workflows;

/// <summary>
/// Workflow executor — Phase 5.4. Walks the step list with a control-flow
/// pointer that can advance, jump (goto), retry, or end early. The
/// per-step gate check (kill switch / pause / disabled) still runs before
/// every step so safety semantics are uniform between linear and
/// branching runs.
///
/// Phase-5.4 grammar (deliberately tiny; mirrors WorkflowConditionDto):
///   - <c>condition.kind = "always" | "never"</c>
///   - <c>condition.kind = "previous_outcome"</c> with <c>equals</c>
///   - <c>condition.kind = "step_outcome"</c> with <c>step_id</c> + <c>equals</c>
///   - <c>condition.kind = "step_output"</c> with <c>step_id</c> + <c>output_key</c> + <c>equals</c>
///
/// On-success / on-failure directives:
///   - <c>action = "continue"</c> (default) — fall through
///   - <c>action = "goto"</c> — jump to <c>goto_step_id</c>
///   - <c>action = "end"</c> — terminate run with <c>end_outcome</c> + <c>end_reason</c>
///   - <c>action = "retry"</c> (on-failure only) — retry up to <c>retry_limit</c>
///
/// Cycle guard: each step may execute at most <see cref="MaxStepRevisits"/>
/// times in one run; on the (N+1)th entry the run aborts with
/// <c>cycle_limit_exceeded</c>. This is the CrowdStrike/halt-fast lesson —
/// a misconfigured workflow can't burn a pharmacy's cores.
/// </summary>
public sealed class WorkflowExecutor
{
    public const int MaxStepRevisits = 8;

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

        // Bug 23 guard (SuavoLLC/MKM#478): a zero-step definition cannot
        // legitimately reach the Completed branch — falling through the
        // while loop with no per-step audit rows writes a chain-of-custody
        // hole in agent_actuation_audit. Three prod rows on 2026-05-05/06
        // (38c92afe / 97c53a68 / 1e5bf691) were produced via this path
        // before the guard existed. Treat as a structural abort, not a
        // successful run.
        if (definition.Steps.Count == 0)
        {
            _logger.LogError(
                "WorkflowExecutor: refusing to execute run={RunId} with zero-step definition — workflow={Name}@{Version} (Bug 23 guard)",
                definition.WorkflowRunId,
                definition.WorkflowName,
                definition.WorkflowVersion);
            await _audit.PostRunCompletedAsync(definition.WorkflowRunId, WorkflowRunOutcome.Aborted, "no_steps_in_definition", ct).ConfigureAwait(false);
            return new WorkflowExecutionResult(WorkflowRunOutcome.Aborted, "no_steps_in_definition", 0, 0);
        }

        var stepIdIndex = BuildStepIdIndex(definition.Steps);
        var visitCounter = new int[definition.Steps.Count];
        var perStepHistory = new Dictionary<int, StepHistoryEntry>();
        StepHistoryEntry? previous = null;
        var completedSteps = 0;
        var pointer = 0;

        while (pointer < definition.Steps.Count)
        {
            if (ct.IsCancellationRequested)
            {
                _logger.LogWarning("WorkflowExecutor: cancellation requested mid-run={RunId} step={Idx}", definition.WorkflowRunId, pointer);
                await _audit.PostRunCompletedAsync(definition.WorkflowRunId, WorkflowRunOutcome.Aborted, "cooperative_cancel", CancellationToken.None);
                return new WorkflowExecutionResult(WorkflowRunOutcome.Aborted, "cooperative_cancel", completedSteps, definition.Steps.Count);
            }

            if (visitCounter[pointer] >= MaxStepRevisits)
            {
                _logger.LogWarning(
                    "WorkflowExecutor: cycle limit hit at step {Idx} (entered {Visits} times)",
                    pointer, visitCounter[pointer]);
                await _audit.PostRunCompletedAsync(definition.WorkflowRunId, WorkflowRunOutcome.Aborted, "cycle_limit_exceeded", ct);
                return new WorkflowExecutionResult(WorkflowRunOutcome.Aborted, "cycle_limit_exceeded", completedSteps, definition.Steps.Count);
            }
            visitCounter[pointer]++;

            var step = definition.Steps[pointer];

            // Gate check between every step.
            var gate = await _gateway.GetStateAsync(ct).ConfigureAwait(false);
            if (gate.KillSwitchTrippedUtc is not null)
            {
                await PostAbortAsync(definition.WorkflowRunId, "kill_switch_tripped", ct);
                return new WorkflowExecutionResult(WorkflowRunOutcome.Aborted, "kill_switch_tripped", completedSteps, definition.Steps.Count);
            }
            if (!gate.Enabled)
            {
                await PostAbortAsync(definition.WorkflowRunId, "gate_disabled", ct);
                return new WorkflowExecutionResult(WorkflowRunOutcome.Aborted, "gate_disabled", completedSteps, definition.Steps.Count);
            }
            if (gate.PausedUntilUtc is { } until && until > DateTimeOffset.UtcNow)
            {
                await PostAbortAsync(definition.WorkflowRunId, $"gate_paused_until:{until:o}", ct);
                return new WorkflowExecutionResult(WorkflowRunOutcome.Aborted, "gate_paused", completedSteps, definition.Steps.Count);
            }

            // Condition check — skip the step (and audit it) when false.
            var conditionPasses = EvaluateCondition(step.Condition, previous, perStepHistory, stepIdIndex);
            if (!conditionPasses)
            {
                _logger.LogInformation("WorkflowExecutor: step {Idx} ({Verb}) skipped by condition", pointer, step.Verb);
                var skipped = await PostSkippedStepAsync(definition, pointer, step, ct);
                perStepHistory[pointer] = skipped;
                previous = skipped;
                pointer++;
                continue;
            }

            var attempt = 0;
            VerbDispatchResult dispatch;
            while (true)
            {
                dispatch = await ExecuteStepAsync(definition, pointer, step, services, auditChain, charter, pharmacyId, actor, ct).ConfigureAwait(false);
                if (dispatch.Outcome == VerbDispatchOutcome.Success) break;

                var retryDirective = step.OnFailure;
                if (retryDirective is { Action: "retry" } && attempt < (retryDirective.RetryLimit ?? 1))
                {
                    attempt++;
                    _logger.LogInformation("WorkflowExecutor: retrying step {Idx} (attempt {Attempt})", pointer, attempt + 1);
                    continue;
                }
                break;
            }

            var history = new StepHistoryEntry(
                StepIndex: pointer,
                StepId: step.StepId,
                Outcome: dispatch.Outcome,
                Output: dispatch.Output);
            perStepHistory[pointer] = history;
            previous = history;
            if (dispatch.Outcome == VerbDispatchOutcome.Success) completedSteps++;

            // Apply control-flow directive.
            var directive = dispatch.Outcome == VerbDispatchOutcome.Success
                ? step.OnSuccess
                : step.OnFailure;

            var (nextPointer, terminal) = ApplyControlFlow(directive, pointer, dispatch.Outcome, dispatch.FailureReason, stepIdIndex, definition.Steps.Count);

            if (terminal is not null)
            {
                _logger.LogInformation(
                    "WorkflowExecutor: terminating run={RunId} early via on_{Branch} directive: outcome={Outcome} reason={Reason}",
                    definition.WorkflowRunId,
                    dispatch.Outcome == VerbDispatchOutcome.Success ? "success" : "failure",
                    terminal.Outcome,
                    terminal.Reason);
                await _audit.PostRunCompletedAsync(definition.WorkflowRunId, terminal.Outcome, terminal.Reason, ct);
                return new WorkflowExecutionResult(terminal.Outcome, terminal.Reason, completedSteps, definition.Steps.Count);
            }

            pointer = nextPointer;
        }

        await _audit.PostRunCompletedAsync(definition.WorkflowRunId, WorkflowRunOutcome.Completed, abortReason: null, ct).ConfigureAwait(false);
        return new WorkflowExecutionResult(WorkflowRunOutcome.Completed, null, completedSteps, definition.Steps.Count);
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
            DeadlineUtc: deadline,
            DryRun: definition.DryRun);

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
            AfterState: dispatch.Output is { Count: > 0 } ? dispatch.Output : null,
            EffectiveDryRun: ExtractEffectiveDryRun(dispatch.Output)), ct).ConfigureAwait(false);

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

    private async Task<StepHistoryEntry> PostSkippedStepAsync(
        WorkflowDefinitionDto definition,
        int stepIndex,
        WorkflowStepDto step,
        CancellationToken ct)
    {
        await _audit.PostStepAuditAsync(new WorkflowStepAuditEntry(
            WorkflowRunId: definition.WorkflowRunId,
            StepIndex: stepIndex,
            VerbName: step.Verb,
            VerbVersion: step.VerbVersion ?? "?",
            DryRun: definition.DryRun,
            Outcome: "skipped",
            ExecDurationMs: 0,
            ErrorKind: "condition_not_met",
            ErrorDetail: $"condition.kind={step.Condition?.Kind} did not pass",
            Params: ParseStepParameters(step.Params, new VerbParameterSchema(Array.Empty<VerbParameterSpec>())),
            BeforeState: null,
            AfterState: null), ct).ConfigureAwait(false);
        return new StepHistoryEntry(stepIndex, step.StepId, VerbDispatchOutcome.Rejected, new Dictionary<string, object?>())
        { Outcome = SkippedOutcome };
    }

    private async Task PostAbortAsync(string runId, string reason, CancellationToken ct)
    {
        _logger.LogWarning("WorkflowExecutor: aborting run={RunId} reason={Reason}", runId, reason);
        await _audit.PostRunCompletedAsync(runId, WorkflowRunOutcome.Aborted, reason, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sentinel outcome value used to differentiate "step skipped" from
    /// "step rejected" in <see cref="StepHistoryEntry"/> evaluations. Anything
    /// outside <see cref="VerbDispatchOutcome"/>'s 0/1/2 range works — we
    /// pick a sentinel that won't collide with future enum additions.
    /// </summary>
    private const VerbDispatchOutcome SkippedOutcome = (VerbDispatchOutcome)100;

    private static IReadOnlyDictionary<string, int> BuildStepIdIndex(IReadOnlyList<WorkflowStepDto> steps)
    {
        var dict = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < steps.Count; i++)
        {
            var id = steps[i].StepId;
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (!dict.ContainsKey(id)) dict[id] = i;
        }
        return dict;
    }

    private static bool EvaluateCondition(
        WorkflowConditionDto? condition,
        StepHistoryEntry? previous,
        IReadOnlyDictionary<int, StepHistoryEntry> history,
        IReadOnlyDictionary<string, int> stepIdIndex)
    {
        if (condition is null) return true;
        return condition.Kind switch
        {
            "always" => true,
            "never" => false,
            "previous_outcome" => previous is not null
                && string.Equals(OutcomeToString(previous.Outcome), condition.EqualsValue, StringComparison.OrdinalIgnoreCase),
            "step_outcome" => TryFindStep(condition.StepId, stepIdIndex, history, out var step1)
                && string.Equals(OutcomeToString(step1!.Outcome), condition.EqualsValue, StringComparison.OrdinalIgnoreCase),
            "step_output" => TryFindStep(condition.StepId, stepIdIndex, history, out var step2)
                && step2!.Output.TryGetValue(condition.OutputKey ?? "", out var v)
                && string.Equals(v?.ToString(), condition.EqualsValue, StringComparison.OrdinalIgnoreCase),
            // Unknown kinds fail closed — the agent never executes a step it cannot prove safe.
            _ => false,
        };
    }

    private static bool TryFindStep(
        string? stepId,
        IReadOnlyDictionary<string, int> stepIdIndex,
        IReadOnlyDictionary<int, StepHistoryEntry> history,
        out StepHistoryEntry? entry)
    {
        entry = null;
        if (string.IsNullOrEmpty(stepId)) return false;
        if (!stepIdIndex.TryGetValue(stepId, out var idx)) return false;
        if (!history.TryGetValue(idx, out var found)) return false;
        entry = found;
        return true;
    }

    private static string OutcomeToString(VerbDispatchOutcome outcome) =>
        outcome switch
        {
            VerbDispatchOutcome.Success => "success",
            VerbDispatchOutcome.Rejected => "rejected",
            VerbDispatchOutcome.Failed => "failed",
            SkippedOutcome => "skipped",
            _ => "unknown",
        };

    private static (int Next, TerminalDirective? Terminal) ApplyControlFlow(
        WorkflowControlFlowDto? directive,
        int currentPointer,
        VerbDispatchOutcome outcome,
        string? failureReason,
        IReadOnlyDictionary<string, int> stepIdIndex,
        int totalSteps)
    {
        if (directive is null)
        {
            // No directive — failure terminates the run, success advances.
            if (outcome != VerbDispatchOutcome.Success)
            {
                return (currentPointer, new TerminalDirective(WorkflowRunOutcome.Failed, failureReason));
            }
            return (currentPointer + 1, null);
        }

        switch (directive.Action)
        {
            case "continue":
                if (outcome != VerbDispatchOutcome.Success)
                {
                    return (currentPointer, new TerminalDirective(WorkflowRunOutcome.Failed, failureReason));
                }
                return (currentPointer + 1, null);

            case "goto":
                if (string.IsNullOrEmpty(directive.GotoStepId)
                    || !stepIdIndex.TryGetValue(directive.GotoStepId, out var target)
                    || target < 0
                    || target >= totalSteps)
                {
                    return (currentPointer, new TerminalDirective(
                        WorkflowRunOutcome.Failed,
                        $"goto_target_unresolved:{directive.GotoStepId}"));
                }
                return (target, null);

            case "end":
                var endOutcome = directive.EndOutcome switch
                {
                    "completed" => WorkflowRunOutcome.Completed,
                    "failed" => WorkflowRunOutcome.Failed,
                    "aborted" => WorkflowRunOutcome.Aborted,
                    _ => outcome == VerbDispatchOutcome.Success ? WorkflowRunOutcome.Completed : WorkflowRunOutcome.Failed,
                };
                return (currentPointer, new TerminalDirective(endOutcome, directive.EndReason ?? failureReason));

            case "retry":
                // Retry is honoured inside ExecuteAsync's per-step loop — by the
                // time we reach ApplyControlFlow with action=retry, all retries
                // have been exhausted, so the run fails.
                return (currentPointer, new TerminalDirective(
                    WorkflowRunOutcome.Failed,
                    $"retry_exhausted:{failureReason}"));

            default:
                return (currentPointer, new TerminalDirective(
                    WorkflowRunOutcome.Failed,
                    $"unknown_control_flow:{directive.Action}"));
        }
    }

    private record StepHistoryEntry(
        int StepIndex,
        string? StepId,
        VerbDispatchOutcome Outcome,
        IReadOnlyDictionary<string, object?> Output)
    {
        public VerbDispatchOutcome Outcome { get; init; } = Outcome;
    }

    private sealed record TerminalDirective(WorkflowRunOutcome Outcome, string? Reason);

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

    // Bug 21: actuation verbs (LaunchSandboxApp, PressKeys, TypeIntoField,
    // ClickByLabel) emit "dry_run" in their output dictionary from
    // ActuationResult.DryRun. WorkflowExecutor surfaces it as the audit
    // row's EffectiveDryRun. Returns null when the verb produced no
    // output map (Fail) or no "dry_run" key (read-only verbs, future
    // additions) — null is the audit-chain signal that "effective" is
    // not meaningful for this row.
    private static bool? ExtractEffectiveDryRun(IReadOnlyDictionary<string, object?> output)
    {
        if (output is null || output.Count == 0) return null;
        if (!output.TryGetValue("dry_run", out var raw)) return null;
        return raw switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var parsed) => parsed,
            _ => null,
        };
    }
}
