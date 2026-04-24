namespace SuavoAgent.Core.Mission;

/// <summary>
/// The operator-authored goal a mission is trying to achieve. Scoped to a
/// single pharmacy. Parameter shape is verb-level — the planner translates
/// the goal into a concrete sequence of verb invocations.
/// </summary>
public sealed record MissionGoal(
    string GoalId,
    string GoalType,
    string PharmacyId,
    string RequestedBy,
    IReadOnlyDictionary<string, object?> Parameters,
    DateTimeOffset RequestedAt,
    DateTimeOffset DeadlineUtc
);

public static class MissionGoalTypes
{
    public const string LookupPatientTopNdcs = "lookup_patient_top_ndcs";
}
