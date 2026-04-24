using SuavoAgent.Core.Adapters;
using SuavoAgent.Core.Audit;

namespace SuavoAgent.Core.Mission;

/// <summary>
/// Post-hoc evaluator. Runs after <see cref="MissionExecutor"/> to attach a
/// structured evaluation to the audit chain so operators have a one-line
/// summary and the learning pipeline has a consistent schema to aggregate.
///
/// Today it emits one <c>mission.evaluated</c> audit entry summarising the
/// outcome, duration, and a goal-specific shape check. Later this is where
/// LLM-as-judge evaluations attach (Phase B).
/// </summary>
public sealed class MissionEvaluator
{
    public MissionEvaluation Evaluate(MissionResult result, AuditChain audit)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(audit);

        var isHealthy = result.Outcome == MissionOutcome.Success;
        var shapeIssues = isHealthy ? DetectShapeIssues(result) : Array.Empty<string>();
        var healthy = isHealthy && shapeIssues.Count == 0;

        audit.Append(
            eventType: "mission.evaluated",
            actor: "mission-evaluator",
            subjectType: "mission",
            subjectId: result.MissionId,
            metadata: new Dictionary<string, object?>
            {
                ["goal_id"] = result.GoalId,
                ["plan_id"] = result.PlanId,
                ["outcome"] = result.Outcome.ToString(),
                ["healthy"] = healthy,
                ["duration_ms"] = (long)(result.CompletedAt - result.StartedAt).TotalMilliseconds,
                ["step_count"] = result.StepResults.Count,
                ["shape_issues"] = shapeIssues,
                ["failure_reason"] = result.FailureReason,
            });

        return new MissionEvaluation(
            MissionId: result.MissionId,
            Healthy: healthy,
            ShapeIssues: shapeIssues);
    }

    private static IReadOnlyList<string> DetectShapeIssues(MissionResult result)
    {
        var issues = new List<string>();

        if (result.FinalOutput.Count == 0)
        {
            issues.Add("mission succeeded but final output is empty");
        }

        if (result.FinalOutput.TryGetValue("ndcs", out var ndcsRaw) &&
            ndcsRaw is IReadOnlyList<RxHistoryRecord> ndcs)
        {
            if (ndcs.Count == 0)
            {
                issues.Add("ndcs list empty — no Rx history found for patient");
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in ndcs)
            {
                if (!seen.Add(row.Ndc))
                {
                    issues.Add($"duplicate NDC '{row.Ndc}' in result — adapter should deduplicate");
                }
            }
        }

        return issues;
    }
}

public sealed record MissionEvaluation(
    string MissionId,
    bool Healthy,
    IReadOnlyList<string> ShapeIssues
);
