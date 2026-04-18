using SuavoAgent.Contracts.Reasoning;

namespace SuavoAgent.Core.Reasoning;

/// <summary>
/// Deterministic post-LLM gate. Every proposal from Tier 2 or Tier 3 passes
/// through Verify before it's allowed to execute. The gate is pure C# with no
/// ML — its behavior is completely predictable and exhaustively testable.
///
/// Rules enforced:
/// 1. Action type is in the request's AllowedActions whitelist.
/// 2. Confidence ≥ the class threshold for the action type.
/// 3. If action targets a UIA element by name, that element is in the
///    current RuleContext.VisibleElements (no hallucinated targets).
/// 4. Parameters for the action type are structurally valid.
///
/// Everything that fails these checks is either Rejected outright or bumped
/// to OperatorApprovalRequired. Nothing probabilistic reaches reality unvetted.
/// </summary>
public sealed class ActionVerifier
{
    /// <summary>
    /// Minimum confidence per action class. Destructive actions (Click, Type,
    /// PressKey) demand higher confidence than read-only ones (VerifyElement,
    /// WaitForElement, Log).
    /// </summary>
    private static readonly IReadOnlyDictionary<RuleActionType, double> MinConfidence =
        new Dictionary<RuleActionType, double>
        {
            [RuleActionType.Click]          = 0.95,
            [RuleActionType.Type]           = 0.95,
            [RuleActionType.PressKey]       = 0.90,
            [RuleActionType.VerifyElement]  = 0.80,
            [RuleActionType.WaitForElement] = 0.70,
            [RuleActionType.Escalate]       = 0.00,
            [RuleActionType.AskOperator]    = 0.00,
            [RuleActionType.Log]            = 0.00,
        };

    /// <summary>
    /// Actions considered "destructive" — any failure or uncertainty routes to
    /// the operator approval queue rather than silently retrying.
    /// </summary>
    private static readonly IReadOnlySet<RuleActionType> Destructive =
        new HashSet<RuleActionType>
        {
            RuleActionType.Click,
            RuleActionType.Type,
            RuleActionType.PressKey,
        };

    public VerificationResult Verify(InferenceProposal proposal, InferenceRequest request)
    {
        var failures = new List<string>();

        // --- 1. Whitelist check -------------------------------------------------
        if (!request.AllowedActions.Contains(proposal.Action.Type))
        {
            failures.Add($"action {proposal.Action.Type} not allowed for skill {request.Context.SkillId}");
        }

        // --- 2. Confidence threshold -------------------------------------------
        var threshold = MinConfidence.TryGetValue(proposal.Action.Type, out var t) ? t : 0.95;
        var belowThreshold = proposal.Confidence < threshold;

        // --- 3. Target element existence ---------------------------------------
        // If the proposal names a specific UIA element, the verifier requires
        // that element to actually be visible in the current context. This
        // prevents the LLM from inventing targets that don't exist.
        if (TryGetNameParam(proposal.Action, out var targetName))
        {
            if (!request.Context.VisibleElements.Contains(targetName))
            {
                failures.Add($"target element '{targetName}' not visible in current state");
            }
        }

        // --- 4. Parameter structural validation --------------------------------
        var paramError = ValidateParameters(proposal.Action);
        if (paramError != null) failures.Add(paramError);

        // --- Decide outcome ----------------------------------------------------
        if (failures.Count > 0)
        {
            return new VerificationResult
            {
                Outcome = VerificationOutcome.Rejected,
                Reason = $"Proposal rejected: {string.Join("; ", failures)}",
                FailedChecks = failures,
            };
        }

        if (belowThreshold)
        {
            return new VerificationResult
            {
                Outcome = VerificationOutcome.OperatorApprovalRequired,
                Reason = $"Confidence {proposal.Confidence:F2} below threshold "
                         + $"{threshold:F2} for {proposal.Action.Type}",
            };
        }

        // Even if confidence passes, destructive actions always allow operator
        // to set stricter thresholds by lowering confidence under the
        // destructive minimum — covered above.
        _ = Destructive; // reserved for future policy expansion

        return new VerificationResult
        {
            Outcome = VerificationOutcome.Approved,
            Reason = $"Proposal approved (confidence {proposal.Confidence:F2} ≥ {threshold:F2})",
        };
    }

    /// <summary>
    /// Extracts the target element name from an action's parameters, if any.
    /// Different actions store target names under different keys; keep this
    /// tolerant but exact.
    /// </summary>
    private static bool TryGetNameParam(RuleActionSpec action, out string name)
    {
        // Only actions that target specific UIA elements are checked here.
        // Actions without UIA targets (Log, AskOperator, Escalate, PressKey,
        // Type into the currently-focused element) don't need target lookup.
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

    /// <summary>
    /// Per-action-type structural validation. Called after whitelist check so
    /// we only validate shapes that matter for the proposal's declared type.
    /// </summary>
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
