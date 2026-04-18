using Microsoft.Extensions.Options;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Config;

namespace SuavoAgent.Core.Reasoning;

/// <summary>
/// Deterministic post-LLM gate. Every proposal from Tier 2 or Tier 3 passes
/// through Verify before it's allowed to execute. The gate is pure C# with no
/// ML — its behavior is completely predictable and exhaustively testable.
///
/// Rules enforced (in order):
/// 1. Action type is in the request's AllowedActions whitelist.
/// 2. Destructive actions (Click/Type/PressKey) require a "name" parameter
///    AND that name must resolve to a visible UIA element in the current
///    state. No controlType-only clicks. (Codex C-3).
/// 3. Parameters for the action type are structurally valid.
/// 4. Confidence ≥ the class threshold (spec floor: 0.98 destructive, 0.85
///    read-only). Model-reported confidence alone doesn't authorize — see #5.
/// 5. If the proposal is destructive AND ReasoningOptions.AutoExecuteTier2Destructive
///    is false (default), the outcome is OperatorApprovalRequired regardless
///    of confidence. Model self-reported confidence is not trusted as a
///    deterministic signal (Codex M-4).
/// </summary>
public sealed class ActionVerifier
{
    /// <summary>
    /// Per-class confidence thresholds from the tiered-brain spec. Destructive
    /// (Click/Type/PressKey) = 0.98. Read-only (VerifyElement/WaitForElement)
    /// = 0.85. Non-action types (Escalate/AskOperator/Log) require nothing.
    /// </summary>
    private static readonly IReadOnlyDictionary<RuleActionType, double> MinConfidence =
        new Dictionary<RuleActionType, double>
        {
            [RuleActionType.Click]          = 0.98,
            [RuleActionType.Type]           = 0.98,
            [RuleActionType.PressKey]       = 0.98,
            [RuleActionType.VerifyElement]  = 0.85,
            [RuleActionType.WaitForElement] = 0.85,
            [RuleActionType.Escalate]       = 0.00,
            [RuleActionType.AskOperator]    = 0.00,
            [RuleActionType.Log]            = 0.00,
        };

    /// <summary>
    /// Actions considered "destructive" — these modify UI state, keystrokes, or
    /// click targets. Default policy: never auto-execute from Tier-2 without
    /// operator review (Codex M-4).
    /// </summary>
    public static readonly IReadOnlySet<RuleActionType> Destructive =
        new HashSet<RuleActionType>
        {
            RuleActionType.Click,
            RuleActionType.Type,
            RuleActionType.PressKey,
        };

    private readonly bool _autoExecuteDestructive;

    public ActionVerifier() : this(autoExecuteDestructive: false) { }

    public ActionVerifier(bool autoExecuteDestructive)
    {
        _autoExecuteDestructive = autoExecuteDestructive;
    }

    public ActionVerifier(IOptions<AgentOptions> agentOptions)
    {
        _autoExecuteDestructive = agentOptions.Value.Reasoning.AutoExecuteTier2Destructive;
    }

    public VerificationResult Verify(InferenceProposal proposal, InferenceRequest request)
    {
        var failures = new List<string>();
        var isDestructive = Destructive.Contains(proposal.Action.Type);

        // --- 1. Whitelist check -------------------------------------------------
        if (!request.AllowedActions.Contains(proposal.Action.Type))
        {
            failures.Add($"action {proposal.Action.Type} not allowed for skill {request.Context.SkillId}");
        }

        // --- 2. Click must name a unique visible target (Codex C-3) ------------
        // Click is the one destructive action that targets a specific UIA element.
        // Type targets whatever is focused; PressKey targets focus. Those destructives
        // don't need a "name" but do get confidence + policy checks below.
        if (proposal.Action.Type == RuleActionType.Click)
        {
            if (!TryGetNameParam(proposal.Action, out var targetName))
            {
                failures.Add("Click requires a 'name' parameter — " +
                             "controlType-only clicks are never autonomous");
            }
            else if (!request.Context.VisibleElements.Contains(targetName))
            {
                failures.Add($"target element '{targetName}' not visible in current state");
            }
        }
        else if (TryGetNameParam(proposal.Action, out var targetName))
        {
            // Other actions (VerifyElement, WaitForElement) may name a target.
            if (!request.Context.VisibleElements.Contains(targetName))
                failures.Add($"target element '{targetName}' not visible in current state");
        }

        // --- 3. Parameter structural validation --------------------------------
        var paramError = ValidateParameters(proposal.Action);
        if (paramError != null) failures.Add(paramError);

        if (failures.Count > 0)
        {
            return new VerificationResult
            {
                Outcome = VerificationOutcome.Rejected,
                Reason = $"Proposal rejected: {string.Join("; ", failures)}",
                FailedChecks = failures,
            };
        }

        // --- 4. Confidence threshold -------------------------------------------
        var threshold = MinConfidence.TryGetValue(proposal.Action.Type, out var t) ? t : 0.98;
        if (proposal.Confidence < threshold)
        {
            return new VerificationResult
            {
                Outcome = VerificationOutcome.OperatorApprovalRequired,
                Reason = $"Confidence {proposal.Confidence:F2} below threshold "
                         + $"{threshold:F2} for {proposal.Action.Type}",
            };
        }

        // --- 5. Destructive policy — no auto-execute by default (Codex M-4) ----
        // Model-reported confidence is not a trust signal. Until we have
        // deterministic calibration (pattern miner + observed-outcome feedback),
        // destructive Tier-2 proposals go to the operator queue.
        if (isDestructive && !_autoExecuteDestructive)
        {
            return new VerificationResult
            {
                Outcome = VerificationOutcome.OperatorApprovalRequired,
                Reason = $"Destructive actions from Tier-2 always require operator approval " +
                         $"(AutoExecuteTier2Destructive=false)",
            };
        }

        return new VerificationResult
        {
            Outcome = VerificationOutcome.Approved,
            Reason = $"Proposal approved (confidence {proposal.Confidence:F2} ≥ {threshold:F2})",
        };
    }

    /// <summary>
    /// Extracts the target element name from an action's parameters, if any.
    /// </summary>
    private static bool TryGetNameParam(RuleActionSpec action, out string name)
    {
        switch (action.Type)
        {
            case RuleActionType.Click:
            case RuleActionType.VerifyElement:
            case RuleActionType.WaitForElement:
                if (action.Parameters.TryGetValue("name", out var n) && !string.IsNullOrEmpty(n))
                {
                    name = n;
                    return true;
                }
                break;
        }

        name = "";
        return false;
    }

    /// <summary>Per-action-type structural validation.</summary>
    private static string? ValidateParameters(RuleActionSpec action)
    {
        return action.Type switch
        {
            RuleActionType.Click           => RequireAny(action, "name", "controlType"),
            RuleActionType.Type            => RequireAny(action, "text", "source"),
            RuleActionType.PressKey        => RequireAny(action, "key"),
            RuleActionType.WaitForElement  => RequireAny(action, "controlType", "name"),
            RuleActionType.VerifyElement   => RequireAny(action, "name", "controlType", "containsFromContext"),
            RuleActionType.Escalate        => null,
            RuleActionType.AskOperator     => null,
            RuleActionType.Log             => null,
            _                              => $"unknown action type {action.Type}",
        };
    }

    private static string? RequireAny(RuleActionSpec action, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (action.Parameters.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v))
                return null;
        }
        return $"{action.Type} missing required parameter (one of: {string.Join(", ", keys)})";
    }
}
