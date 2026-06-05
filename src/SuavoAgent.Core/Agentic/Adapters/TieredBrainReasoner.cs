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

    public TieredBrainReasoner(
        RuleEngine rules,
        ILocalInference localInference,
        ActionVerifier verifier,
        ICloudReasoning cloudReasoning,
        ILogger<TieredBrain> logger,
        string? targetProcessName = null)
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
    }

    public async Task<ReasonResult> ReasonNextAsync(
        AgentObjective objective,
        WorkingMemory memory,
        System.Collections.Generic.IReadOnlySet<string> allowedActions,
        bool allowCloud,
        CancellationToken ct)
    {
        var ctx = NavigateReasoning.BuildContext(objective, memory);
        var allowed = NavigateReasoning.MapAllowedActions(allowedActions);

        // Gate Tier-3 by the loop's cloud sub-budget: null cloud ⇒ NullCloudReasoning ⇒ no Tier-3.
        var brain = new TieredBrain(_rules, _localInference, _verifier, _logger,
            allowCloud ? _cloudReasoning : null);

        var decision = await brain.DecideAsync(ctx, allowed, shadowMode: false, ct).ConfigureAwait(false);

        var usedCloud = decision.Tier == DecisionTier.CloudInference;
        var action = NavigateReasoning.MapDecision(decision, memory.LatestScreen, _targetProcessName);
        return new ReasonResult(action, usedCloud);
    }

    public Task<VerifyResult> VerifyPostconditionAsync(
        AgentObjective objective,
        NextAction lastAction,
        PerceivedScreen before,
        PerceivedScreen after,
        bool allowCloud,
        CancellationToken ct)
    {
        // v1: completion is model-driven — the reasoner emits Done when the objective is achieved.
        // The verify step's job here is only to let the loop settle + re-perceive (already done by
        // the time we're called). Return Ambiguous (loop treats as NotMet ⇒ re-reason on the fresh
        // screen) with zero cloud cost. A local-predicate / cloud postcondition check is a follow-up.
        return Task.FromResult(new VerifyResult(PostconditionVerdict.Ambiguous, UsedCloud: false));
    }
}
