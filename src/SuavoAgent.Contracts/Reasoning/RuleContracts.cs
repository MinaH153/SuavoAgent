using System.Text.Json.Serialization;
using SuavoAgent.Contracts.Behavioral;

namespace SuavoAgent.Contracts.Reasoning;

// ---------------------------------------------------------------------------
// Tier 1 RuleEngine data contracts.
//
// A Rule = when-predicate + ordered list of actions to execute if matched.
// Rules live in YAML files under ProgramData/SuavoAgent/rules/ and are loaded
// at startup. See docs/superpowers/specs/2026-04-18-tiered-brain-architecture.md
// ---------------------------------------------------------------------------

/// <summary>
/// Outcome of evaluating a RuleContext against the catalog.
/// </summary>
public enum MatchOutcome
{
    /// <summary>A rule matched and its actions are ready to execute.</summary>
    Matched,
    /// <summary>No rule matched — escalate to Tier 2 (LocalInference).</summary>
    NoMatch,
    /// <summary>A rule matched but guards rejected execution (e.g. operator busy).</summary>
    Blocked,
}

/// <summary>
/// Action classes a rule can emit. The executor (Week 2+) interprets these.
/// Keep the set small and auditable — every addition widens the blast radius.
/// </summary>
public enum RuleActionType
{
    /// <summary>Click a UIA element matched by the action's parameters.</summary>
    Click,
    /// <summary>Type text into the currently-focused UIA element.</summary>
    Type,
    /// <summary>Press a named keyboard key (Enter, Escape, Tab...).</summary>
    PressKey,
    /// <summary>Block until a UIA element matching the parameters is visible.</summary>
    WaitForElement,
    /// <summary>Fail the skill if the parameters don't match current UIA state.</summary>
    VerifyElement,
    /// <summary>Give up and pass the decision to LocalInference (Tier 2).</summary>
    Escalate,
    /// <summary>Pause and put the decision in the operator's approval queue.</summary>
    AskOperator,
    /// <summary>Emit a log entry — diagnostic or audit marker.</summary>
    Log,
}

/// <summary>
/// The preconditions that must hold for a rule to fire.
/// Every field is optional; omitted fields don't constrain the match.
/// </summary>
public sealed record RulePredicate
{
    /// <summary>Process name glob (e.g. "PioneerRx*"). Null means any process.</summary>
    public string? ProcessName { get; init; }

    /// <summary>Main window title pattern. Null means any title.</summary>
    public string? WindowTitlePattern { get; init; }

    /// <summary>
    /// UIA element names that must all be present in the current tree.
    /// Used to prove "we are on the right screen" without hardcoding process names.
    /// </summary>
    public IReadOnlyList<string> VisibleElements { get; init; } = Array.Empty<string>();

    /// <summary>Require operator to have been idle for at least this many milliseconds.</summary>
    public int? OperatorIdleMsAtLeast { get; init; }

    /// <summary>
    /// Arbitrary state flags — boolean checks against the caller's RuleContext.Flags.
    /// Example: "main_window_focused": "true"
    /// </summary>
    public IReadOnlyDictionary<string, string> StateFlags { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// Structural UIA fingerprints — the v3.12 cross-installation-safe successor
    /// to <see cref="VisibleElements"/>. Each required signature must be present
    /// (order-insensitive, via <see cref="ElementSignature.MatchesStructurally"/>)
    /// in the context's <see cref="RuleContext.ElementFingerprints"/> for the
    /// predicate to satisfy.
    ///
    /// WHY: A flat name list lets a different screen with the same label pass
    /// the predicate; the triple {ControlType, AutomationId, ClassName} does
    /// not. Auto-generated rules emit fingerprints; legacy bundled rules
    /// default to an empty list and behave exactly as before.
    /// </summary>
    public IReadOnlyList<ElementSignature> ElementFingerprints { get; init; } =
        Array.Empty<ElementSignature>();

    /// <summary>
    /// K-of-M relaxation for <see cref="ElementFingerprints"/>: minimum count of
    /// the required fingerprints that must match in the context. Null = all must
    /// match (legacy all-of semantics). When set, must be 1..ElementFingerprints.Count.
    ///
    /// WHY: cross-installation UIA trees drift (pagination, hover-only nodes,
    /// virtualized panels). A 5-element screen extracted from pharmacy A often
    /// presents 4 at pharmacy B — the template's MinElementsRequired captures
    /// that tolerance and must survive YAML round-trip.
    /// </summary>
    public int? MinRequiredCount { get; init; }
}

/// <summary>
/// A single action the rule emits when matched. Parameters are string-keyed so
/// the YAML format stays flexible without a separate record per action type.
/// The executor (Week 2+) validates the parameter set per action type.
/// </summary>
public sealed record RuleActionSpec
{
    public required RuleActionType Type { get; init; }

    /// <summary>Free-form key/value args. Executor validates per Type.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// Assertion to verify AFTER this action completes. If the assertion fails,
    /// the skill aborts and rollback runs. Optional — null means no post-verify.
    /// </summary>
    public RulePredicate? VerifyAfter { get; init; }

    /// <summary>Human-readable label for logs / operator UI.</summary>
    public string? Description { get; init; }
}

/// <summary>
/// Complete rule definition. Loaded from YAML, immutable once loaded.
/// </summary>
public sealed record Rule
{
    /// <summary>Globally unique rule id (e.g. "pricing-lookup.open-rx-item").</summary>
    public required string Id { get; init; }

    /// <summary>
    /// Skill this rule belongs to (e.g. "pricing-lookup"). Rules are indexed by
    /// skill at load time so lookups are O(1) per skill.
    /// </summary>
    public required string SkillId { get; init; }

    /// <summary>
    /// Evaluation order within a skill. Higher priority wins when multiple
    /// rules match. Default 100.
    /// </summary>
    public int Priority { get; init; } = 100;

    /// <summary>The preconditions.</summary>
    public required RulePredicate When { get; init; }

    /// <summary>The actions to emit, in order.</summary>
    public required IReadOnlyList<RuleActionSpec> Then { get; init; }

    /// <summary>Actions to execute if the skill fails mid-way. Empty = no rollback.</summary>
    public IReadOnlyList<RuleActionSpec> Rollback { get; init; } = Array.Empty<RuleActionSpec>();

    /// <summary>
    /// Minimum confidence required for downstream Tier 2/3 output to satisfy a
    /// rule that escalates. Pure Tier 1 rules ignore this (0.0 default).
    /// </summary>
    public double MinConfidence { get; init; }

    /// <summary>
    /// Semver-style version. Bumped when behavior changes so downstream
    /// caches invalidate. Defaults to "1.0.0".
    /// </summary>
    public string Version { get; init; } = "1.0.0";

    /// <summary>
    /// Whether this rule is allowed to execute without operator oversight.
    /// False = every match goes to the approval queue. Default true.
    /// </summary>
    public bool AutonomousOk { get; init; } = true;

    /// <summary>Short human summary for audit logs / dashboard.</summary>
    public string? Description { get; init; }
}

/// <summary>
/// The observed state at the moment a skill asks the engine "what should I do?"
/// Populated by the caller (Helper or Core) before Evaluate is called.
/// </summary>
public sealed record RuleContext
{
    public required string SkillId { get; init; }
    public string ProcessName { get; init; } = "";
    public string WindowTitle { get; init; } = "";

    /// <summary>UIA element names visible in the current tree. Set, not list, for O(1) lookups.</summary>
    public IReadOnlySet<string> VisibleElements { get; init; } = new HashSet<string>();

    /// <summary>Milliseconds since the operator last pressed a key or moved the mouse.</summary>
    public int OperatorIdleMs { get; init; }

    /// <summary>Arbitrary string-keyed state flags (e.g. "main_window_focused": "true").</summary>
    public IReadOnlyDictionary<string, string> Flags { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// Structural UIA fingerprints captured alongside <see cref="VisibleElements"/>.
    /// Populated by the caller from live UIA before Evaluate is called; used by
    /// the rule engine's fingerprint gate (see
    /// <see cref="PredicateFingerprintMatcher.SatisfiedBy"/>).
    /// </summary>
    public IReadOnlyList<ElementSignature> ElementFingerprints { get; init; } =
        Array.Empty<ElementSignature>();

    /// <summary>Timestamp of context capture. Used for audit trails.</summary>
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Result of evaluating a context against the rule catalog.
/// </summary>
public sealed record EvaluationResult
{
    public required MatchOutcome Outcome { get; init; }

    /// <summary>The rule that matched, if any. Null when Outcome != Matched.</summary>
    public Rule? MatchedRule { get; init; }

    /// <summary>Actions to execute. Null/empty when Outcome != Matched.</summary>
    public IReadOnlyList<RuleActionSpec> Actions { get; init; } = Array.Empty<RuleActionSpec>();

    /// <summary>Human-readable explanation for logs / operator UI.</summary>
    public required string Reason { get; init; }

    /// <summary>When the evaluator captured this result.</summary>
    public DateTimeOffset EvaluatedAt { get; init; } = DateTimeOffset.UtcNow;
}
