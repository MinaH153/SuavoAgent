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

    /// <summary>Short human-readable rationale for logs + operator UI.</summary>
    public string? Rationale { get; init; }

    /// <summary>Latency of the local inference call, in milliseconds.</summary>
    public long LatencyMs { get; init; }
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
