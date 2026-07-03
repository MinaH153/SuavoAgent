using System;
using System.Collections.Generic;
using System.Linq;

namespace SuavoAgent.Core.Agentic;

/// <summary>
/// The natural-language goal a navigate_app run pursues, plus the stable keys
/// that scope autonomy and audit. The <see cref="Goal"/> is the only free-form
/// field; it never reaches the actuation surface directly — it grounds the
/// reasoner, which emits typed <see cref="NextAction"/>s.
/// </summary>
public sealed record AgentObjective(string Goal, string TaskKey, string PharmacyId);

/// <summary>
/// Caller-supplied bounds + safety posture for one navigate_app run. All limits
/// are fail-closed: every default refuses-rather-than-runs when uncertain.
/// </summary>
public sealed record AgenticLoopOptions
{
    /// <summary>Hard cap on perceive→reason→act→verify iterations.</summary>
    public int MaxSteps { get; init; } = 25;

    /// <summary>
    /// Per-run cloud-call sub-budget (reason + verify). Kept well under the
    /// 50-calls/pharmacy/day limit so one run can never starve the pharmacy's
    /// other agent functions. Exhaustion → local-verify-only → BudgetExhausted.
    /// </summary>
    public int MaxCloudCalls { get; init; } = 12;

    /// <summary>K consecutive identical (screenHash, chosenAction) ⇒ NoProgress.</summary>
    public int NoProgressWindow { get; init; } = 3;

    /// <summary>Consecutive null perceptions tolerated before escalating.</summary>
    public int PerceiveNullStrikeLimit { get; init; } = 3;

    /// <summary>Consecutive stable after-screen reads required before verifying.</summary>
    public int SettleStableReads { get; init; } = 2;

    /// <summary>Max settle polls before verifying on the last frame (bounded; never hangs).</summary>
    public int SettleMaxPolls { get; init; } = 5;

    /// <summary>Delay between settle polls (production); tests inject a no-op delay.</summary>
    public TimeSpan SettlePollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Wall-clock budget from run start. null ⇒ no wall-clock cap (step budget still applies).</summary>
    public TimeSpan? Deadline { get; init; }

    /// <summary>Supervised mode: actions dispatch dry-run (audited, not executed), then escalate.</summary>
    public bool DryRun { get; init; }

    /// <summary>Bounded working-memory history length fed back into reasoning.</summary>
    public int MemoryWindow { get; init; } = 8;

    /// <summary>Action-class whitelist handed to the reasoner each step. Defaults to non-destructive only.</summary>
    public IReadOnlySet<string> AllowedActions { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "WaitForElement", "VerifyElement", "Log" };
}

/// <summary>Why a run stopped. Every reason is terminal; the loop never spins silently.</summary>
public enum TerminationReason
{
    /// <summary>Objective satisfied (reasoner returned Done).</summary>
    Done,
    /// <summary>Safety gate denied (preflight or per-action). Deterministic — never an implicit re-reason.</summary>
    GateDenied,
    /// <summary>Cooperative cancellation observed (abort_navigation / kill).</summary>
    Cancelled,
    /// <summary>Step, wall-clock, or cloud-call budget reached.</summary>
    BudgetExhausted,
    /// <summary>K identical (screen, action) cycles — the stuck signal that replaces blind retry.</summary>
    NoProgress,
    /// <summary>Reasoner handed off to a human (Tier-miss, can't decide locally, or multi-action).</summary>
    Escalated,
}

/// <summary>What the reasoner decided to do this step.</summary>
public enum NextActionKind
{
    /// <summary>Execute one grounded UI action.</summary>
    Act,
    /// <summary>Objective met — stop, success.</summary>
    Done,
    /// <summary>Hand off to a human — stop, escalate.</summary>
    Escalate,
    /// <summary>
    /// TERMINAL, NON-EXECUTING assist offer: a high-confidence LEARNED template the agent has observed
    /// enough to propose to the operator. Like Done/Escalate it stops the loop and NEVER reaches the
    /// actuator — the offer is surfaced for human confirmation elsewhere, never auto-run.
    /// </summary>
    Assist,
}

/// <summary>
/// A PHI-safe, NON-EXECUTING assist offer — a high-confidence learned <c>WorkflowTemplate</c> the agent
/// has observed often enough to propose ("watched you do this Nx, C% confident — want me to?"). Carried by
/// a terminal <see cref="NextActionKind.Assist"/> action; the agent NEVER executes it. <see cref="StepsSummary"/>
/// is STRUCTURAL ONLY (step kind + control-type counts) — never element Name/Value text.
/// </summary>
public sealed record LearnedTemplateOffer(
    string TemplateId,
    double Confidence,
    int ObservationCount,
    IReadOnlyList<string> StepsSummary);

/// <summary>
/// Exactly one grounded action the orchestrator may execute this step (or a
/// terminal Done/Escalate). The production adapter maps a single-actuating-action
/// BrainDecision into this; a multi-actuating-action decision maps to Escalate
/// (never silently truncated to index 0).
/// </summary>
public sealed record NextAction(
    NextActionKind Kind,
    string? Verb = null,
    IReadOnlyDictionary<string, object?>? Parameters = null,
    string? Rationale = null,
    LearnedTemplateOffer? Offer = null)
{
    public static readonly NextAction Done = new(NextActionKind.Done);

    public static NextAction Escalate(string rationale) => new(NextActionKind.Escalate, Rationale: rationale);

    public static NextAction Act(string verb, IReadOnlyDictionary<string, object?>? parameters = null, string? rationale = null)
        => new(NextActionKind.Act, verb, parameters, rationale);

    /// <summary>A terminal, NON-EXECUTING assist offer (see <see cref="NextActionKind.Assist"/>). Carries no
    /// Verb/Parameters, so the loop's act path is never reached for it.</summary>
    public static NextAction Assist(LearnedTemplateOffer offer, string? rationale = null)
        => new(NextActionKind.Assist, Rationale: rationale, Offer: offer);

    /// <summary>
    /// Stable identity used for no-progress detection: verb + ordered params.
    /// Two actions with the same signature on the same screen are "the same move".
    /// </summary>
    public string Signature()
    {
        if (Kind != NextActionKind.Act)
            return Kind.ToString();
        var paramPart = Parameters is null || Parameters.Count == 0
            ? string.Empty
            : string.Join(",", Parameters.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                                          .Select(kv => $"{kv.Key}={kv.Value}"));
        return $"{Verb}({paramPart})";
    }
}

/// <summary>A scrubbed, app-agnostic snapshot of the current screen.</summary>
/// <param name="ElementSummary">Flat "role:name" / "text:..." lines for the NL reasoner to ground on.</param>
/// <param name="Signatures">
/// GREEN-tier structural signatures ("controlType|automationId") for signature-based matching during
/// template replay. Empty until the perception pipeline surfaces AutomationId (follow-up); the NL loop
/// doesn't need it, so it defaults empty and is backward-compatible.
/// </param>
public sealed record PerceivedScreen(
    string ContentHash,
    bool Scrubbed,
    IReadOnlyList<string> ElementSummary,
    string? WindowTitle = null,
    IReadOnlyList<string>? Signatures = null);

/// <summary>Result of executing one action through the actuation chain.</summary>
public enum ActStatus { Success, Rejected, Failed }

public sealed record ActOutcome(
    ActStatus Status,
    string? RejectionCode = null,
    string? EvidenceHash = null,
    bool DryRun = false);

/// <summary>Did the last action achieve its local postcondition? Ambiguous is treated as NotMet (fail-closed).</summary>
public enum PostconditionVerdict { Met, NotMet, Ambiguous }

/// <summary>Safety decision for preflight / per-action gating. No implicit re-reason on deny.</summary>
public enum SafetyDecision
{
    /// <summary>Execute for real.</summary>
    Allow,
    /// <summary>Supervised: dispatch dry-run (audited, not executed), then escalate for operator approval.</summary>
    AllowDryRun,
    /// <summary>Refuse — terminate GateDenied.</summary>
    Deny,
}

public sealed record SafetyVerdict(SafetyDecision Decision, string? Reason = null)
{
    public static readonly SafetyVerdict Allow = new(SafetyDecision.Allow);
    public static SafetyVerdict Dry(string reason) => new(SafetyDecision.AllowDryRun, reason);
    public static SafetyVerdict Denied(string reason) => new(SafetyDecision.Deny, reason);
}

/// <summary>Envelope handed to the actuator for one action.</summary>
public sealed record ActuationContext(string PharmacyId, string TaskKey, bool DryRun);

/// <summary>One completed step of working memory. Immutable; bounded to the memory window.</summary>
/// <param name="ActionVerb">The action's verb (e.g. "click_by_label"), captured structurally so a banked
/// skill can reconstruct the EXACT action without parsing the (unescaped) <see cref="ActionSignature"/>.
/// Null for legacy/terminal steps. Optional — existing readers use only the fields above.</param>
/// <param name="ActionParams">The action's parameters as scrubbed structural strings (e.g.
/// label→"Seven", process_name→"calc.exe"). Pairs with <see cref="ActionVerb"/> for airtight replay.
/// NOTE: being a dictionary, it participates in the record's synthesized equality by REFERENCE (Codex Q1);
/// no consumer compares StepRecords by value (readers use individual fields), so this is inert today.</param>
public sealed record StepRecord(
    string DecisionScreenHash,
    string ActionSignature,
    ActStatus? Outcome,
    PostconditionVerdict? Verdict,
    string? ActionVerb = null,
    IReadOnlyDictionary<string, string>? ActionParams = null);

/// <summary>
/// Bounded, immutable working memory threaded through the loop and fed back into
/// each reason step. Holds only scrubbed text + content hashes — never raw pixels.
/// </summary>
public sealed record WorkingMemory(
    AgentObjective Objective,
    PerceivedScreen? LatestScreen,
    IReadOnlyList<StepRecord> History,
    int ConsecutiveNoProgress,
    int PerceiveNullStrikes);

/// <summary>Final outcome of a navigate_app run.</summary>
/// <param name="AssistedTemplate">Set ONLY when the run terminated on a terminal <see cref="NextActionKind.Assist"/>
/// offer — the NON-EXECUTING learned-template proposal surfaced to the operator. Null on every other path.</param>
public sealed record AgenticLoopResult(
    TerminationReason Termination,
    int StepCount,
    int CloudCallsUsed,
    bool EscalationEmitted,
    string? Detail,
    WorkingMemory FinalMemory,
    LearnedTemplateOffer? AssistedTemplate = null);
