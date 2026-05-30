using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Contracts.Vision;

namespace SuavoAgent.Core.Reasoning;

/// <summary>
/// Observe-only vision-grounded reasoning. Given a captured <see cref="ScreenFrame"/>, it grounds
/// a <see cref="RuleContext"/> on the live screen (via <see cref="VisionContextEnricher"/>) and asks
/// the brain what it WOULD do — then logs that decision and executes NOTHING. This is the consumer
/// that proves vision reaches reasoning end-to-end; autonomous action is a later, pilot-gated chunk.
///
/// Safety: builds its own <see cref="TieredBrain"/> WITHOUT a cloud tier (NullCloudReasoning), so
/// screen-derived context never reaches Tier-3 (HIPAA invariant). "Observe-only" is enforced
/// structurally — this class has no executor and never acts on <see cref="BrainDecision.Actions"/>.
/// </summary>
public sealed class VisionGroundedShadowReasoner
{
    private readonly TieredBrain _shadowBrain;
    private readonly ILogger<VisionGroundedShadowReasoner> _logger;

    public VisionGroundedShadowReasoner(
        RuleEngine rules,
        ILocalInference localInference,
        ActionVerifier verifier,
        ILoggerFactory loggerFactory)
    {
        // Cloud arg omitted => TieredBrain uses NullCloudReasoning => Tier-3 skipped. No screen
        // content can leave the machine through this reasoner regardless of the global cloud config.
        _shadowBrain = new TieredBrain(
            rules, localInference, verifier, loggerFactory.CreateLogger<TieredBrain>());
        _logger = loggerFactory.CreateLogger<VisionGroundedShadowReasoner>();
    }

    /// <summary>
    /// Ground reasoning on <paramref name="frame"/> and return the would-be decision. Executes
    /// nothing. <c>shadowMode:false</c> so we observe the REAL decision (incl. Tier-1 matches);
    /// observe-only is guaranteed by this class never executing the returned actions.
    /// </summary>
    public async Task<BrainDecision> ObserveAsync(ScreenFrame frame, string skillId, CancellationToken ct = default)
    {
        var ctx = VisionContextEnricher.Enrich(new RuleContext { SkillId = skillId }, frame);

        var decision = await _shadowBrain.DecideAsync(
            ctx, allowedTier2Actions: null, shadowMode: false, ct);

        var wouldDo = decision.Actions.Count > 0
            ? decision.Actions[0].Type.ToString()
            : decision.Proposal?.Action.Type.ToString() ?? "none";
        _logger.LogInformation(
            "Vision shadow decision: skill={Skill} tier={Tier} outcome={Outcome} wouldDo={WouldDo} screenText={Text} elements={Elems}",
            skillId, decision.Tier, decision.Outcome, wouldDo,
            frame.TextRegions.Count, frame.Elements.Count);

        return decision;
    }
}
