namespace SuavoAgent.Contracts.Reasoning;

// ---------------------------------------------------------------------------
// Tier 2 (LocalInference) data contracts.
//
// When the Tier 1 RuleEngine returns NoMatch, the TieredBrain asks the local
// LLM to propose a RuleActionSpec. Every proposal goes through ActionVerifier
// before it's allowed to execute — the LLM can't act, only suggest.
// ---------------------------------------------------------------------------

/// <summary>
/// A single action proposed by the local LLM for a given RuleContext.
/// Carries confidence so the Verifier can apply class-specific thresholds.
/// </summary>
public sealed record InferenceProposal
{
    public required RuleActionSpec Action { get; init; }

    /// <summary>Model's self-reported confidence, 0.0–1.0.</summary>
    public required double Confidence { get; init; }

    /// <summary>Id of the model that produced this proposal, for audit.</summary>
    public required string ModelId { get; init; }

    /// <summary>
    /// Fixed machine rationale selected by the model. Model-written prose is
    /// never retained, logged, or sent across a process/network boundary.
    /// </summary>
    public required InferenceRationaleCode RationaleCode { get; init; }

    /// <summary>Latency of the local inference call, in milliseconds.</summary>
    public long LatencyMs { get; init; }
}

/// <summary>
/// Closed rationale vocabulary shared by local and cloud inference. Wire values
/// are lower snake case and are parsed ordinally through
/// <see cref="InferenceRationaleCodeCodec"/>.
/// </summary>
public enum InferenceRationaleCode
{
    TargetPresent,
    TargetAbsentWait,
    WorkflowStateAmbiguous,
    OperatorInputRequired,
    VerificationRequired,
    RecoveryStepRequired,
    NoSafeAction,
}

/// <summary>
/// Exact wire codec plus deterministic, local-only operator copy. Keeping the
/// display text here prevents model prose from becoming a PHI/log channel.
/// </summary>
public static class InferenceRationaleCodeCodec
{
    public static bool TryParseWireValue(
        string? value,
        out InferenceRationaleCode code)
    {
        code = value switch
        {
            "target_present" => InferenceRationaleCode.TargetPresent,
            "target_absent_wait" => InferenceRationaleCode.TargetAbsentWait,
            "workflow_state_ambiguous" =>
                InferenceRationaleCode.WorkflowStateAmbiguous,
            "operator_input_required" =>
                InferenceRationaleCode.OperatorInputRequired,
            "verification_required" =>
                InferenceRationaleCode.VerificationRequired,
            "recovery_step_required" =>
                InferenceRationaleCode.RecoveryStepRequired,
            "no_safe_action" => InferenceRationaleCode.NoSafeAction,
            _ => default,
        };
        return value is
            "target_present" or
            "target_absent_wait" or
            "workflow_state_ambiguous" or
            "operator_input_required" or
            "verification_required" or
            "recovery_step_required" or
            "no_safe_action";
    }

    public static string ToWireValue(this InferenceRationaleCode code) =>
        code switch
        {
            InferenceRationaleCode.TargetPresent => "target_present",
            InferenceRationaleCode.TargetAbsentWait => "target_absent_wait",
            InferenceRationaleCode.WorkflowStateAmbiguous =>
                "workflow_state_ambiguous",
            InferenceRationaleCode.OperatorInputRequired =>
                "operator_input_required",
            InferenceRationaleCode.VerificationRequired =>
                "verification_required",
            InferenceRationaleCode.RecoveryStepRequired =>
                "recovery_step_required",
            InferenceRationaleCode.NoSafeAction => "no_safe_action",
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, null),
        };

    public static string ToOperatorMessage(this InferenceRationaleCode code) =>
        code switch
        {
            InferenceRationaleCode.TargetPresent =>
                "The requested target is present.",
            InferenceRationaleCode.TargetAbsentWait =>
                "The requested target is not present yet.",
            InferenceRationaleCode.WorkflowStateAmbiguous =>
                "The current workflow state is ambiguous.",
            InferenceRationaleCode.OperatorInputRequired =>
                "Operator input is required.",
            InferenceRationaleCode.VerificationRequired =>
                "Verification is required before continuing.",
            InferenceRationaleCode.RecoveryStepRequired =>
                "A recovery step is required.",
            InferenceRationaleCode.NoSafeAction =>
                "No safe action is available.",
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, null),
        };
}

/// <summary>
/// Exact action-parameter vocabulary shared by local proposal parsing and the
/// signed cloud-receipt boundary. This mirrors the server/SQL preflight gate;
/// model output cannot widen it with extra keys.
/// </summary>
public static class InferenceActionParameterContract
{
    public static bool IsExact(
        RuleActionType action,
        IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var keys = parameters.Keys;
        bool Only(params string[] allowed) =>
            keys.All(key => allowed.Contains(key, StringComparer.Ordinal));
        bool Present(string key) =>
            parameters.TryGetValue(key, out var value) &&
            !string.IsNullOrWhiteSpace(value);

        return action switch
        {
            RuleActionType.Click =>
                parameters.Count is >= 1 and <= 2 &&
                Only("name", "controlType") && Present("name"),
            RuleActionType.Type =>
                parameters.Count is >= 1 and <= 2 &&
                Only("text", "source") &&
                (Present("text") || Present("source")),
            RuleActionType.PressKey =>
                parameters.Count == 1 && Only("key") && Present("key"),
            RuleActionType.WaitForElement =>
                parameters.Count is >= 1 and <= 2 &&
                Only("controlType", "name") &&
                (Present("controlType") || Present("name")),
            RuleActionType.VerifyElement =>
                parameters.Count is >= 1 and <= 3 &&
                Only("name", "controlType", "containsFromContext") &&
                (Present("name") || Present("controlType") ||
                    Present("containsFromContext")),
            RuleActionType.Escalate or RuleActionType.AskOperator or
                RuleActionType.Log => parameters.Count == 0,
            _ => false,
        };
    }
}

/// <summary>
/// Input to ILocalInference — a context plus the reason the caller is
/// escalating from Tier 1. Callers include the failure reason so the model
/// can tailor its proposal (e.g. "no rule matched — suggest next action").
/// </summary>
public sealed record InferenceRequest
{
    public required RuleContext Context { get; init; }

    /// <summary>Why Tier 1 couldn't decide. Populated from EvaluationResult.Reason.</summary>
    public required string EscalationReason { get; init; }

    /// <summary>
    /// Restrict allowed actions. Default is SAFE actions only (read-only,
    /// escalation, log, ask-operator). Destructive actions (Click, Type,
    /// PressKey) must be explicitly opted into per skill (Codex C-3). A caller
    /// that forgets to narrow should NOT be able to authorize a destructive
    /// proposal by accident.
    /// </summary>
    public IReadOnlySet<RuleActionType> AllowedActions { get; init; } = SafeDefault;

    /// <summary>The built-in safe default — no destructive actions.</summary>
    public static readonly IReadOnlySet<RuleActionType> SafeDefault =
        new HashSet<RuleActionType>
        {
            RuleActionType.VerifyElement,
            RuleActionType.WaitForElement,
            RuleActionType.Escalate,
            RuleActionType.AskOperator,
            RuleActionType.Log,
        };

    /// <summary>Max wall-clock time the caller will wait for this proposal.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// Outcome of running ActionVerifier on a proposal.
/// </summary>
public enum VerificationOutcome
{
    /// <summary>Proposal passed all checks — safe to execute.</summary>
    Approved,
    /// <summary>Proposal failed a check — must not execute, escalate to operator.</summary>
    Rejected,
    /// <summary>Proposal below confidence threshold — operator approval required.</summary>
    OperatorApprovalRequired,
}

/// <summary>
/// Verifier output. On rejection, Reason explains which check failed.
/// </summary>
public sealed record VerificationResult
{
    public required VerificationOutcome Outcome { get; init; }
    public required string Reason { get; init; }
    public IReadOnlyList<string> FailedChecks { get; init; } = Array.Empty<string>();
}
