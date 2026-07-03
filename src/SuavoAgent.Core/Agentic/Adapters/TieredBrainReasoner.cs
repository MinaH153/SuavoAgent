using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Reasoning;
using SuavoAgent.Core.State;

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
    /// <summary>Shadow-status offer floor: only a Shadow rule whose shadow match-rate is at least this may
    /// be proposed. Approved rules bypass this (an operator already earned that autonomy tier).</summary>
    private const double ShadowOfferConfidenceFloor = 0.85;

    private readonly RuleEngine _rules;
    private readonly ILocalInference _localInference;
    private readonly ActionVerifier _verifier;
    private readonly ICloudReasoning _cloudReasoning;
    private readonly ILogger<TieredBrain> _logger;
    private readonly string? _targetProcessName;
    private readonly AgentStateDb? _stateDb;

    public TieredBrainReasoner(
        RuleEngine rules,
        ILocalInference localInference,
        ActionVerifier verifier,
        ICloudReasoning cloudReasoning,
        ILogger<TieredBrain> logger,
        string? targetProcessName = null,
        AgentStateDb? stateDb = null)
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
        // Local SQLCipher state for the observe→assist bridge (learned-template offers). Null ⇒ the offer
        // seam is disabled and reasoning is byte-for-byte the legacy path. No egress — purely local reads.
        _stateDb = stateDb;
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

        // OBSERVE→ASSIST SEAM (sits BEFORE the brain): if a high-confidence LEARNED template matches the
        // current screen, surface a TERMINAL, NON-EXECUTING offer for the operator to confirm. It never
        // actuates and never touches a safety gate — it can only produce a terminal Assist. On any miss it
        // returns null and reasoning falls through UNCHANGED. No cloud (UsedCloud=false).
        var offer = TryOfferLearnedTemplate(ctx, memory.LatestScreen);
        if (offer is not null)
            return new ReasonResult(NextAction.Assist(offer, "learned_template_assist"), UsedCloud: false);

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
        // Execution-grounded post-state check (v1: screen-change detection) — replaces the previous
        // always-Ambiguous stub so an actuating action that produced no observable effect is caught as
        // NotMet instead of being silently treated as progress. Zero cloud cost. The expected-element /
        // SQL-write-confirmation checks layer on top of this baseline (Phase-1 follow-on).
        var verdict = PostconditionEvaluator.Evaluate(lastAction, before, after);
        return Task.FromResult(new VerifyResult(verdict, UsedCloud: false));
    }

    /// <summary>
    /// Observe→assist bridge (assist-only). Produces a NON-EXECUTING <see cref="LearnedTemplateOffer"/> when
    /// the current screen matches a high-confidence learned template, else null. This is the ONLY new
    /// selection seam; it can only ever yield a TERMINAL Assist — no verb, no actuation, no gate bypass.
    ///
    /// Hard gates (fail-closed — ANY miss returns null and the loop reasons normally):
    ///   • no state DB wired, or screen null, or no perceived signatures → null.
    ///   • no SINGLE structural entry-screen match (0 or ambiguous &gt;1) → null.
    ///   • approval row missing, or status Pending / Rejected → null.
    ///   • status Shadow: HasWriteback → null (writes need the earned-autonomy path); shadow_runs == 0 →
    ///     null (insufficient evidence); confidence = shadow_matches/shadow_runs, &lt; 0.85 → null.
    ///   • status Approved → offer (confidence = template.AggregateConfidence). Writeback allowed ONLY here.
    /// </summary>
    private LearnedTemplateOffer? TryOfferLearnedTemplate(RuleContext ctx, PerceivedScreen? screen)
    {
        if (_stateDb is null || screen is null) return null;
        var signatures = screen.Signatures;
        if (signatures is null || signatures.Count == 0) return null;

        WorkflowTemplate? template;
        try { template = _stateDb.FindActiveTemplateByScreenSignatures(signatures); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "learned-template assist: lookup failed (offer suppressed)");
            return null;
        }
        if (template is null) return null;

        // Rule id convention (TemplateRuleGenerator): auto.<skillId>.<templateId[:12]>, clamped for short seed ids.
        var idSuffix = template.TemplateId.Length >= 12 ? template.TemplateId[..12] : template.TemplateId;
        var ruleId = $"auto.{template.SkillId}.{idSuffix}";
        var approval = _stateDb.GetAutoRuleApproval(ruleId);
        if (approval is null) return null;

        double confidence;
        switch (approval.Status)
        {
            case AgentStateDb.AutoRuleStatus.Approved:
                confidence = template.AggregateConfidence;
                break;
            case AgentStateDb.AutoRuleStatus.Shadow:
                if (template.HasWriteback) return null;          // writeback requires Approved
                if (approval.ShadowRuns <= 0) return null;       // no shadow evidence yet
                confidence = (double)approval.ShadowMatches / approval.ShadowRuns;
                if (confidence < ShadowOfferConfidenceFloor) return null;
                break;
            default:
                return null;                                     // Pending / Rejected
        }

        _logger.LogInformation(
            "learned-template assist: offering {TemplateId} (skill {SkillId}, status {Status}, conf {Conf:F2}, obs {Obs}) — NON-EXECUTING",
            template.TemplateId, ctx.SkillId, approval.Status, confidence, template.ObservationCount);

        return new LearnedTemplateOffer(
            template.TemplateId, confidence, template.ObservationCount, SummarizeSteps(template.Steps));
    }

    /// <summary>PHI-SAFE step summary — STRUCTURAL ONLY (step kind + target control-type counts). NEVER
    /// element Name/Value text. E.g. ["Click Button x2", "Type Edit x1"].</summary>
    private static IReadOnlyList<string> SummarizeSteps(IReadOnlyList<TemplateStep> steps) =>
        steps
            .GroupBy(s => (s.Kind, s.Target.ControlType))
            .OrderBy(g => g.Key.Kind)
            .ThenBy(g => g.Key.ControlType, StringComparer.Ordinal)
            .Select(g => $"{g.Key.Kind} {g.Key.ControlType} x{g.Count()}")
            .ToList();
}
