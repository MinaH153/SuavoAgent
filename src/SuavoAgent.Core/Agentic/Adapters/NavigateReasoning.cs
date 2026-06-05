using System;
using System.Collections.Generic;
using System.Linq;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Reasoning;

namespace SuavoAgent.Core.Agentic.Adapters;

/// <summary>
/// Pure mapping between the general agentic loop's vocabulary and the existing TieredBrain
/// vocabulary. Extracted from the adapter so the (interesting, defect-prone) translation logic
/// is unit-testable without a RuleEngine or an LLM.
///
/// Design decisions (general-navigate v1, 2026-06-04):
///  - skillId = "navigate": a general objective has no pre-baked rules, so Tier-1 always
///    NoMatch → the decision escalates to the LLM reasoner, which is exactly what we want.
///  - Done is model-driven: the reasoner emits a Log action with parameter status=complete
///    when the objective is achieved. The instruction rides in the objective text itself
///    (threaded via RuleContext.UserObjective), so NO cloud-side prompt change is needed.
/// </summary>
public static class NavigateReasoning
{
    public const string NavigateSkillId = "navigate";
    public const string CompleteStatusValue = "complete";

    /// <summary>Instruction appended to the user's objective so the model can signal completion.</summary>
    public const string CompletionInstruction =
        "When the objective is fully achieved, respond with action Log and parameter status=complete.";

    private static readonly IReadOnlyDictionary<RuleActionType, string> VerbByActionType =
        new Dictionary<RuleActionType, string>
        {
            [RuleActionType.Click] = "click_by_label",
            [RuleActionType.Type] = "type_into_field",
            [RuleActionType.PressKey] = "press_keys",
        };

    /// <summary>The actuating action classes the navigate loop can execute today.</summary>
    public static bool IsActuating(RuleActionType type) => VerbByActionType.ContainsKey(type);

    /// <summary>
    /// Build the RuleContext the brain reasons over: the scrubbed screen + the NL objective
    /// (with the completion instruction) + a compact prior-actions transcript so the model sees
    /// "goal + what I see now + what I already tried and how it went".
    /// </summary>
    public static RuleContext BuildContext(AgentObjective objective, WorkingMemory memory)
    {
        var screen = memory.LatestScreen;
        var visible = screen?.ElementSummary is { } els
            ? new HashSet<string>(els, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>();

        var flags = new Dictionary<string, string>();
        var transcript = BuildPriorActionsTranscript(memory);
        if (transcript.Length > 0)
            flags["prior_actions"] = transcript;

        return new RuleContext
        {
            SkillId = NavigateSkillId,
            WindowTitle = screen?.WindowTitle ?? "",
            VisibleElements = visible,
            Flags = flags,
            UserObjective = $"{objective.Goal}\n{CompletionInstruction}",
        };
    }

    /// <summary>Compact, bounded "tried X → outcome" history for the reasoning prompt.</summary>
    public static string BuildPriorActionsTranscript(WorkingMemory memory)
    {
        if (memory.History.Count == 0)
            return "";
        return string.Join("; ", memory.History.Select(h =>
        {
            var outcome = h.Outcome?.ToString() ?? "pending";
            var verdict = h.Verdict is { } v ? $"/{v}" : "";
            return $"{h.ActionSignature}→{outcome}{verdict}";
        }));
    }

    /// <summary>
    /// Map a brain decision to exactly one NextAction. Completion sentinel → Done; a single
    /// actuating action → Act; more than one actuating action → Escalate (never silently take
    /// index 0); anything else (Tier-miss, operator-required, only meta actions) → Escalate.
    /// </summary>
    public static NextAction MapDecision(BrainDecision decision)
    {
        if (decision.Actions.Any(IsCompletionSignal))
            return NextAction.Done;

        var actuating = decision.Actions.Where(a => IsActuating(a.Type)).ToList();

        if (actuating.Count == 1)
        {
            var a = actuating[0];
            var parameters = a.Parameters.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
            return NextAction.Act(VerbByActionType[a.Type], parameters, a.Description);
        }

        if (actuating.Count > 1)
            return NextAction.Escalate("reasoner returned multiple actuating actions");

        return NextAction.Escalate(string.IsNullOrEmpty(decision.Reason) ? "no actionable decision" : decision.Reason);
    }

    private static bool IsCompletionSignal(RuleActionSpec a)
        => a.Type == RuleActionType.Log
           && a.Parameters.TryGetValue("status", out var status)
           && string.Equals(status, CompleteStatusValue, StringComparison.OrdinalIgnoreCase);

    /// <summary>Parse the loop's string action-whitelist into the brain's RuleActionType set.</summary>
    public static IReadOnlySet<RuleActionType> MapAllowedActions(IReadOnlySet<string> allowed)
    {
        var set = new HashSet<RuleActionType>();
        foreach (var name in allowed)
            if (Enum.TryParse<RuleActionType>(name, ignoreCase: true, out var t))
                set.Add(t);
        return set;
    }
}
