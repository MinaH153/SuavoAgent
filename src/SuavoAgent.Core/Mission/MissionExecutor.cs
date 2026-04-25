using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SuavoAgent.Core.ActionGrammarV1;
using SuavoAgent.Core.Audit;
using SuavoAgent.Core.Config;

namespace SuavoAgent.Core.Mission;

/// <summary>
/// Runs a <see cref="MissionPlan"/> through the <see cref="VerbDispatcher"/>
/// one step at a time, resolving parameter bindings against accumulated
/// output, short-circuiting on the first rejection or failure. Emits
/// <c>mission.*</c> audit events around the plan (the per-verb audit is
/// already handled by the dispatcher).
/// </summary>
public sealed class MissionExecutor
{
    private readonly VerbDispatcher _dispatcher;
    private readonly IReadOnlyDictionary<string, IVerb> _verbsByName;
    private readonly AutoExecutionOptions _autoExecution;

    public MissionExecutor(
        VerbDispatcher dispatcher,
        IEnumerable<IVerb> verbs,
        IOptions<AgentOptions>? agentOptions = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        ArgumentNullException.ThrowIfNull(verbs);
        _verbsByName = verbs.ToDictionary(v => v.Metadata.Name, StringComparer.Ordinal);
        _autoExecution = agentOptions?.Value.AutoExecution
            ?? new AutoExecutionOptions { Enabled = true, RequireConfirmation = false };
    }

    public async Task<MissionResult> RunAsync(
        MissionGoal goal,
        MissionPlan plan,
        MissionCharter charter,
        AuditChain audit,
        IServiceProvider services,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(charter);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(services);

        var missionId = Guid.NewGuid().ToString("D");
        var startedAt = DateTimeOffset.UtcNow;

        audit.Append(
            eventType: "mission.started",
            actor: goal.RequestedBy,
            subjectType: "mission",
            subjectId: missionId,
            metadata: new Dictionary<string, object?>
            {
                ["goal_id"] = goal.GoalId,
                ["goal_type"] = goal.GoalType,
                ["plan_id"] = plan.PlanId,
                ["step_count"] = plan.Steps.Count,
                ["pharmacy_id"] = goal.PharmacyId,
            });

        var stepResults = new List<VerbDispatchResult>(plan.Steps.Count);
        var stepOutputs = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal);

        if (!_autoExecution.Enabled || _autoExecution.RequireConfirmation)
        {
            var reason = !_autoExecution.Enabled
                ? "Agent.AutoExecution.Enabled=false"
                : "Agent.AutoExecution.RequireConfirmation=true";
            audit.Append(
                eventType: "mission.blocked_auto_execution",
                actor: goal.RequestedBy,
                subjectType: "mission",
                subjectId: missionId,
                metadata: new Dictionary<string, object?>
                {
                    ["goal_id"] = goal.GoalId,
                    ["plan_id"] = plan.PlanId,
                    ["reason"] = reason,
                });
            var completed = DateTimeOffset.UtcNow;
            return AuditPlanFailure(
                audit, goal, plan, missionId, startedAt, completed, stepResults,
                reason: reason,
                outcome: MissionOutcome.PlanFailed);
        }

        foreach (var step in plan.Steps)
        {
            if (!_verbsByName.TryGetValue(step.VerbName, out var verb))
            {
                var reason = $"verb '{step.VerbName}' not registered in executor";
                audit.Append(
                    eventType: "mission.step_unknown_verb",
                    actor: goal.RequestedBy,
                    subjectType: "mission",
                    subjectId: missionId,
                    metadata: new Dictionary<string, object?>
                    {
                        ["step_id"] = step.StepId,
                        ["verb"] = step.VerbName,
                    });
                var completed = DateTimeOffset.UtcNow;
                return AuditPlanFailure(
                    audit, goal, plan, missionId, startedAt, completed, stepResults,
                    reason: reason,
                    outcome: MissionOutcome.PlanFailed);
            }

            if (!string.Equals(verb.Metadata.Version, step.VerbVersion, StringComparison.Ordinal))
            {
                var reason = $"verb '{step.VerbName}' version mismatch (plan requested {step.VerbVersion}, registered {verb.Metadata.Version})";
                audit.Append(
                    eventType: "mission.step_version_mismatch",
                    actor: goal.RequestedBy,
                    subjectType: "mission",
                    subjectId: missionId,
                    metadata: new Dictionary<string, object?>
                    {
                        ["step_id"] = step.StepId,
                        ["verb"] = step.VerbName,
                        ["requested_version"] = step.VerbVersion,
                        ["registered_version"] = verb.Metadata.Version,
                    });
                var completed = DateTimeOffset.UtcNow;
                return AuditPlanFailure(
                    audit, goal, plan, missionId, startedAt, completed, stepResults,
                    reason: reason,
                    outcome: MissionOutcome.PlanFailed);
            }

            Dictionary<string, object?> effectiveParams;
            try
            {
                effectiveParams = ResolveParameters(step, stepOutputs);
            }
            catch (MissionExecutionException mex)
            {
                audit.Append(
                    eventType: "mission.step_binding_failed",
                    actor: goal.RequestedBy,
                    subjectType: "mission",
                    subjectId: missionId,
                    metadata: new Dictionary<string, object?>
                    {
                        ["step_id"] = step.StepId,
                        ["error"] = mex.Message,
                    });
                var completed = DateTimeOffset.UtcNow;
                return AuditPlanFailure(
                    audit, goal, plan, missionId, startedAt, completed, stepResults,
                    reason: mex.Message,
                    outcome: MissionOutcome.PlanFailed);
            }

            var invocationId = $"{missionId}:{step.StepId}";
            var ctx = new VerbContext(
                PharmacyId: goal.PharmacyId,
                Charter: charter,
                Audit: audit,
                InvocationId: invocationId,
                Actor: goal.RequestedBy,
                Parameters: effectiveParams,
                Services: services,
                DeadlineUtc: goal.DeadlineUtc);

            var dispatch = await _dispatcher.DispatchAsync(verb, ctx, ct).ConfigureAwait(false);
            stepResults.Add(dispatch);

            if (dispatch.Outcome != VerbDispatchOutcome.Success)
            {
                var completed = DateTimeOffset.UtcNow;
                audit.Append(
                    eventType: "mission.failed",
                    actor: goal.RequestedBy,
                    subjectType: "mission",
                    subjectId: missionId,
                    metadata: new Dictionary<string, object?>
                    {
                        ["goal_id"] = goal.GoalId,
                        ["plan_id"] = plan.PlanId,
                        ["failed_step_id"] = step.StepId,
                        ["outcome"] = dispatch.Outcome.ToString(),
                        ["reason"] = dispatch.FailureReason ?? "unspecified",
                        ["completed_steps"] = stepResults.Count - 1,
                    });
                return new MissionResult(
                    MissionId: missionId,
                    GoalId: goal.GoalId,
                    PlanId: plan.PlanId,
                    Outcome: MissionOutcome.StepFailed,
                    StepResults: stepResults,
                    FinalOutput: new Dictionary<string, object?>(),
                    FailureReason: dispatch.FailureReason,
                    StartedAt: startedAt,
                    CompletedAt: completed);
            }

            stepOutputs[step.StepId] = dispatch.Output;
        }

        var completedAt = DateTimeOffset.UtcNow;
        var finalOutput = stepResults.Count > 0
            ? stepResults[^1].Output
            : (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>();

        audit.Append(
            eventType: "mission.completed",
            actor: goal.RequestedBy,
            subjectType: "mission",
            subjectId: missionId,
            metadata: new Dictionary<string, object?>
            {
                ["goal_id"] = goal.GoalId,
                ["plan_id"] = plan.PlanId,
                ["step_count"] = plan.Steps.Count,
                ["duration_ms"] = (long)(completedAt - startedAt).TotalMilliseconds,
            });

        return new MissionResult(
            MissionId: missionId,
            GoalId: goal.GoalId,
            PlanId: plan.PlanId,
            Outcome: MissionOutcome.Success,
            StepResults: stepResults,
            FinalOutput: finalOutput,
            FailureReason: null,
            StartedAt: startedAt,
            CompletedAt: completedAt);
    }

    private static Dictionary<string, object?> ResolveParameters(
        MissionPlanStep step,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> stepOutputs)
    {
        var effective = new Dictionary<string, object?>(step.Parameters, StringComparer.Ordinal);

        foreach (var (paramName, binding) in step.ParameterBindings)
        {
            if (!stepOutputs.TryGetValue(binding.FromStepId, out var sourceOutput))
            {
                throw new MissionExecutionException(
                    $"parameter binding for '{paramName}' references step '{binding.FromStepId}' which has not produced output yet");
            }

            if (!sourceOutput.TryGetValue(binding.FromOutputKey, out var value))
            {
                throw new MissionExecutionException(
                    $"parameter binding for '{paramName}' references output key '{binding.FromOutputKey}' missing from step '{binding.FromStepId}' output");
            }

            effective[paramName] = value;
        }

        return effective;
    }

    private static MissionResult AuditPlanFailure(
        AuditChain audit,
        MissionGoal goal,
        MissionPlan plan,
        string missionId,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        IReadOnlyList<VerbDispatchResult> stepResults,
        string reason,
        MissionOutcome outcome)
    {
        audit.Append(
            eventType: "mission.failed",
            actor: goal.RequestedBy,
            subjectType: "mission",
            subjectId: missionId,
            metadata: new Dictionary<string, object?>
            {
                ["goal_id"] = goal.GoalId,
                ["plan_id"] = plan.PlanId,
                ["outcome"] = outcome.ToString(),
                ["reason"] = reason,
            });
        return new MissionResult(
            MissionId: missionId,
            GoalId: goal.GoalId,
            PlanId: plan.PlanId,
            Outcome: outcome,
            StepResults: stepResults,
            FinalOutput: new Dictionary<string, object?>(),
            FailureReason: reason,
            StartedAt: startedAt,
            CompletedAt: completedAt);
    }
}

public sealed class MissionExecutionException : Exception
{
    public MissionExecutionException(string message) : base(message) { }
}
