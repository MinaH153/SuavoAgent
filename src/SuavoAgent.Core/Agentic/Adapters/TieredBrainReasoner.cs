using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SuavoAgent.Core.Reasoning;

namespace SuavoAgent.Core.Agentic.Adapters;

/// <summary>
/// Production <see cref="IReasoner"/> over the existing tiered brain. Grounds each step in the
/// NL objective + scrubbed screen + prior-actions transcript (via <see cref="NavigateReasoning"/>),
/// runs <see cref="TieredBrain.DecideAsync"/>, and maps the decision back to a single NextAction.
///
/// Cloud sub-budget: when the loop says allowCloud=false (per-run cloud budget spent), the brain
/// is built with cloud disabled so Tier-3 never fires; an undecidable case then escalates rather
/// than calling the cloud. UsedCloud is reported true only when Tier-3 actually decided.
/// </summary>
public sealed class TieredBrainReasoner : IReasoner
{
    private readonly RuleEngine _rules;
    private readonly ILocalInference _localInference;
    private readonly ActionVerifier _verifier;
    private readonly ICloudReasoning _cloudReasoning;
    private readonly ILogger<TieredBrain> _logger;
    private readonly string? _targetProcessName;
    private readonly Func<string?>? _knownSkillsProvider;

    public TieredBrainReasoner(
        RuleEngine rules,
        ILocalInference localInference,
        ActionVerifier verifier,
        ICloudReasoning cloudReasoning,
        ILogger<TieredBrain> logger,
        string? targetProcessName = null,
        Func<string?>? knownSkillsProvider = null)
    {
        _rules = rules;
        _localInference = localInference;
        _verifier = verifier;
        _cloudReasoning = cloudReasoning;
        _logger = logger;
        // The app being navigated. When set, a freeform LLM click intent is grounded to a concrete
        // perceived element + this process so it can actuate (see NavigateReasoning.MapDecision /
        // ActionGrounding). Null preserves the legacy raw-param mapping for non-grounded callers.
        _targetProcessName = targetProcessName;
        // Flywheel Increment 3 (retrieval-as-few-shot): a flag-gated provider that returns the
        // pre-built "known_skills" worked-examples block for THIS run (captures objective+app+db), or
        // null. Null provider ⇒ retrieval off ⇒ identical reasoning to before (default). Computed once
        // and cached per run (the banked skill set is stable across a single navigate run).
        _knownSkillsProvider = knownSkillsProvider;
    }

    // Per-run cache of the retrieval block: the candidate skills don't change within one run, so we
    // pay the DB read + ranking at most once even though ReasonNextAsync runs every step.
    private bool _knownSkillsResolved;
    private string? _knownSkills;

    public async Task<ReasonResult> ReasonNextAsync(
        AgentObjective objective,
        WorkingMemory memory,
        System.Collections.Generic.IReadOnlySet<string> allowedActions,
        bool allowCloud,
        CancellationToken ct)
    {
        var ctx = NavigateReasoning.BuildContext(objective, memory, ResolveKnownSkills());
        var allowed = NavigateReasoning.MapAllowedActions(allowedActions);

        // Gate Tier-3 by the loop's cloud sub-budget: null cloud ⇒ NullCloudReasoning ⇒ no Tier-3.
        var brain = new TieredBrain(_rules, _localInference, _verifier, _logger,
            allowCloud ? _cloudReasoning : null);

        var decision = await brain.DecideAsync(ctx, allowed, shadowMode: false, ct).ConfigureAwait(false);

        var usedCloud = decision.Tier == DecisionTier.CloudInference;
        var action = NavigateReasoning.MapDecision(decision, memory.LatestScreen, _targetProcessName);
        return new ReasonResult(action, usedCloud);
    }

    /// <summary>Resolve (once per run, cached) the retrieval few-shot block. Best-effort: any provider
    /// fault returns null (retrieval is advisory — a failed lookup must never break reasoning).</summary>
    private string? ResolveKnownSkills()
    {
        if (_knownSkillsResolved) return _knownSkills;
        _knownSkillsResolved = true;
        try { _knownSkills = _knownSkillsProvider?.Invoke(); }
        catch { _knownSkills = null; }
        return _knownSkills;
    }

    public Task<VerifyResult> VerifyPostconditionAsync(
        AgentObjective objective,
        NextAction lastAction,
        PerceivedScreen before,
        PerceivedScreen after,
        bool allowCloud,
        CancellationToken ct)
    {
        // Execution-grounded post-state check (v1: screen-change detection) — replaces the previous
        // always-Ambiguous stub so an actuating action that produced no observable effect is caught as
        // NotMet instead of being silently treated as progress. Zero cloud cost. The expected-element /
        // SQL-write-confirmation checks layer on top of this baseline (Phase-1 follow-on).
        var verdict = PostconditionEvaluator.Evaluate(lastAction, before, after);
        return Task.FromResult(new VerifyResult(verdict, UsedCloud: false));
    }
}
