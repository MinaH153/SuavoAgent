using SuavoAgent.Core.ActionGrammarV1;

namespace SuavoAgent.Core.Mission;

/// <summary>
/// A plan is an ordered list of verb invocations. Each step can feed output
/// into the next step's parameters via <c>ParameterBindings</c>, which the
/// executor resolves at step-entry time — this keeps verbs decoupled from
/// plan composition.
/// </summary>
public sealed record MissionPlan(
    string PlanId,
    string GoalId,
    IReadOnlyList<MissionPlanStep> Steps
);

/// <summary>
/// One step in a mission plan. <c>Parameters</c> is the literal parameter
/// map; <c>ParameterBindings</c> is the dynamic binding map that pulls
/// values from prior-step outputs. Dynamic bindings are resolved against the
/// plan's accumulated output map at execution time.
/// </summary>
public sealed record MissionPlanStep(
    string StepId,
    string VerbName,
    string VerbVersion,
    IReadOnlyDictionary<string, object?> Parameters,
    IReadOnlyDictionary<string, ParameterBinding> ParameterBindings
);

public sealed record ParameterBinding(string FromStepId, string FromOutputKey);
