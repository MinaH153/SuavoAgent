using SuavoAgent.Core.ActionGrammarV1.Verbs.LookupPatient;
using SuavoAgent.Core.ActionGrammarV1.Verbs.QueryTopNdcs;

namespace SuavoAgent.Core.Mission;

/// <summary>
/// Deterministic goal → plan mapping. Handles the Phase-1 goal catalogue:
///
/// <list type="bullet">
///   <item><c>MissionGoalTypes.LookupPatientTopNdcs</c> — two-step:
///     <see cref="LookupPatientVerb"/> then <see cref="QueryTopNdcsForPatientVerb"/>
///     with output→input binding on <c>patient_id</c>.</item>
/// </list>
///
/// LLM-backed planners will replace this class; the interface contract is
/// stable so the executor + audit pipeline don't care which planner produced
/// the plan.
/// </summary>
public sealed class RuleBasedMissionPlanner : IMissionPlanner
{
    public Task<MissionPlan> PlanAsync(MissionGoal goal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(goal);

        return goal.GoalType switch
        {
            MissionGoalTypes.LookupPatientTopNdcs => Task.FromResult(PlanLookupPatientTopNdcs(goal)),
            _ => throw new MissionPlanningException(
                goal.GoalType,
                $"no plan template registered for goal type '{goal.GoalType}'")
        };
    }

    private static MissionPlan PlanLookupPatientTopNdcs(MissionGoal goal)
    {
        if (!goal.Parameters.TryGetValue("patient_identifier", out var idRaw) ||
            idRaw is not string identifier ||
            string.IsNullOrWhiteSpace(identifier))
        {
            throw new MissionPlanningException(
                goal.GoalType,
                "goal parameter 'patient_identifier' is required and must be a non-empty string");
        }

        if (!goal.Parameters.TryGetValue("top_n", out var nRaw) || nRaw is not int topN)
        {
            throw new MissionPlanningException(
                goal.GoalType,
                "goal parameter 'top_n' is required and must be an integer");
        }

        if (topN <= 0 || topN > 50)
        {
            throw new MissionPlanningException(
                goal.GoalType,
                $"goal parameter 'top_n' out of range (1..50), got {topN}");
        }

        var lookupStep = new MissionPlanStep(
            StepId: "step-1-lookup",
            VerbName: LookupPatientVerb.VerbName,
            VerbVersion: LookupPatientVerb.VerbVersion,
            Parameters: new Dictionary<string, object?>
            {
                ["patient_identifier"] = identifier,
            },
            ParameterBindings: new Dictionary<string, ParameterBinding>());

        var queryStep = new MissionPlanStep(
            StepId: "step-2-top-ndcs",
            VerbName: QueryTopNdcsForPatientVerb.VerbName,
            VerbVersion: QueryTopNdcsForPatientVerb.VerbVersion,
            Parameters: new Dictionary<string, object?>
            {
                ["top_n"] = topN,
            },
            ParameterBindings: new Dictionary<string, ParameterBinding>
            {
                ["patient_id"] = new ParameterBinding(FromStepId: "step-1-lookup", FromOutputKey: "patient_id"),
            });

        return new MissionPlan(
            PlanId: Guid.NewGuid().ToString("D"),
            GoalId: goal.GoalId,
            Steps: new List<MissionPlanStep> { lookupStep, queryStep });
    }
}
