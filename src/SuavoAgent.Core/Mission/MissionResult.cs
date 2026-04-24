using SuavoAgent.Core.ActionGrammarV1;

namespace SuavoAgent.Core.Mission;

public enum MissionOutcome
{
    PlanFailed = 0,
    StepFailed = 1,
    EvaluationFailed = 2,
    Success = 3,
}

public sealed record MissionResult(
    string MissionId,
    string GoalId,
    string PlanId,
    MissionOutcome Outcome,
    IReadOnlyList<VerbDispatchResult> StepResults,
    IReadOnlyDictionary<string, object?> FinalOutput,
    string? FailureReason,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt
);
