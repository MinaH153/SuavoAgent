using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Core.Reasoning;
using Xunit;

namespace SuavoAgent.Core.Tests.Reasoning;

/// <summary>
/// The end-to-end proof that vision-grounded reasoning is real (not scaffolding): a captured
/// ScreenFrame is enriched into a RuleContext and run through the brain, so a rule keyed on
/// on-screen text actually fires. The reasoner executes nothing (observe-only) and uses a
/// cloud-disabled brain (no screen content to Tier-3).
/// </summary>
public class VisionGroundedShadowReasonerTests
{
    private static ScreenFrame Frame(string text) => new()
    {
        Id = "f",
        CapturedAt = DateTimeOffset.UnixEpoch,
        Width = 800,
        Height = 600,
        ExtractorId = "test",
        TextRegions = new[] { new TextRegion { Text = text, Bounds = new Rect(0, 0, 80, 12), Confidence = 0.95 } },
    };

    private static VisionGroundedShadowReasoner Build(params Rule[] rules) =>
        new(new RuleEngine(rules, NullLogger<RuleEngine>.Instance),
            new NullLocalInference(),
            new ActionVerifier(),
            NullLoggerFactory.Instance);

    private static Rule TextRule(string skill, string text) => new()
    {
        Id = $"{skill}.on-{text}",
        SkillId = skill,
        When = new RulePredicate { TextPresent = new[] { text } },
        Then = new[] { new RuleActionSpec { Type = RuleActionType.Log } },
    };

    [Fact]
    public async Task ObserveAsync_ScreenTextMatchesRule_BrainDecidesViaTier1()
    {
        var reasoner = Build(TextRule("vision-observe", "Pricing"));

        var decision = await reasoner.ObserveAsync(Frame("Pricing tab"), "vision-observe");

        Assert.Equal(MatchOutcome.Matched, decision.Outcome);
        Assert.Equal(DecisionTier.Rules, decision.Tier);
    }

    [Fact]
    public async Task ObserveAsync_ScreenTextDoesNotMatch_NoTier1Action()
    {
        var reasoner = Build(TextRule("vision-observe", "Pricing"));

        var decision = await reasoner.ObserveAsync(Frame("Home screen"), "vision-observe");

        Assert.NotEqual(DecisionTier.Rules, decision.Tier);
    }
}
