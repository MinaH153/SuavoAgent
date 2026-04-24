namespace SuavoAgent.Core.Mission;

/// <summary>
/// Produces a <see cref="MissionPlan"/> for a given goal. Phase-1 ships a
/// rule-based planner (deterministic mapping from goal-type → verb sequence);
/// LLM-backed planners slot into this interface without caller changes.
///
/// Planners MUST NOT execute — plan construction is pure. Execution is the
/// <see cref="MissionExecutor"/>'s job, which gives the operator approval UI
/// a place to review the plan before anything runs.
/// </summary>
public interface IMissionPlanner
{
    Task<MissionPlan> PlanAsync(MissionGoal goal, CancellationToken ct);
}

public sealed class MissionPlanningException : Exception
{
    public string GoalType { get; }

    public MissionPlanningException(string goalType, string message)
        : base(message)
    {
        GoalType = goalType;
    }
}
