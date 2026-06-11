using System.Collections.Generic;
using System.Linq;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Agentic;
using SuavoAgent.Core.Agentic.Adapters;
using SuavoAgent.Core.Reasoning;
using Xunit;

namespace SuavoAgent.Core.Tests.Agentic;

/// <summary>
/// Pure mapping between the navigate loop and the TieredBrain — the defect-prone translation,
/// tested without a RuleEngine or LLM.
/// </summary>
public sealed class NavigateReasoningTests
{
    private static readonly AgentObjective Obj = new("price these NDCs", "task.price", "ph.test");

    private static BrainDecision Decision(params RuleActionSpec[] actions) => new()
    {
        Outcome = actions.Length > 0 ? MatchOutcome.Matched : MatchOutcome.NoMatch,
        Tier = DecisionTier.CloudInference,
        Actions = actions,
        Reason = "test",
    };

    private static RuleActionSpec Spec(RuleActionType type, params (string k, string v)[] p) => new()
    {
        Type = type,
        Parameters = p.ToDictionary(x => x.k, x => x.v),
    };

    // --- BuildContext --------------------------------------------------------

    [Fact]
    public void BuildContext_SetsNavigateSkill_ObjectiveWithCompletionInstruction_AndVisibleElements()
    {
        var memory = ContextAccumulator.RecordObservation(
            ContextAccumulator.Start(Obj),
            new PerceivedScreen("h", Scrubbed: true, new[] { "Save", "Cancel" }, "Rx Lookup"));

        var ctx = NavigateReasoning.BuildContext(Obj, memory);

        Assert.Equal(NavigateReasoning.NavigateSkillId, ctx.SkillId);
        Assert.Equal("Rx Lookup", ctx.WindowTitle);
        Assert.Contains("Save", ctx.VisibleElements);
        Assert.NotNull(ctx.UserObjective);
        Assert.Contains("price these NDCs", ctx.UserObjective!);
        Assert.Contains(NavigateReasoning.CompletionInstruction, ctx.UserObjective!);
    }

    [Fact]
    public void BuildContext_IncludesPriorActionsTranscript()
    {
        var m = ContextAccumulator.Start(Obj);
        m = ContextAccumulator.RecordObservation(m, new PerceivedScreen("h", true, new[] { "Save" }, "W"));
        m = ContextAccumulator.RecordStep(m, "h", NextAction.Act("click_by_label", new Dictionary<string, object?> { ["label"] = "Save" }),
            ActStatus.Rejected, PostconditionVerdict.NotMet, memoryWindow: 8);

        var ctx = NavigateReasoning.BuildContext(Obj, m);

        Assert.True(ctx.Flags.ContainsKey("prior_actions"));
        Assert.Contains("click_by_label", ctx.Flags["prior_actions"]);
        Assert.Contains("Rejected", ctx.Flags["prior_actions"]);
    }

    [Fact]
    public void BuildContext_ThreadsKnownSkills_WhenProvided() // Increment 3
    {
        var m = ContextAccumulator.Start(Obj);
        var ctx = NavigateReasoning.BuildContext(Obj, m, knownSkills: "task.calc: click Seven → click Eight");

        Assert.True(ctx.Flags.ContainsKey(SkillRetrieval.KnownSkillsFlag));
        Assert.Contains("click Seven", ctx.Flags[SkillRetrieval.KnownSkillsFlag]);
    }

    [Fact]
    public void BuildContext_OmitsKnownSkills_WhenNullOrEmpty() // Increment 3 — zero behavior change when off
    {
        var m = ContextAccumulator.Start(Obj);
        Assert.False(NavigateReasoning.BuildContext(Obj, m, knownSkills: null).Flags.ContainsKey(SkillRetrieval.KnownSkillsFlag));
        Assert.False(NavigateReasoning.BuildContext(Obj, m, knownSkills: "").Flags.ContainsKey(SkillRetrieval.KnownSkillsFlag));
        // 2-arg overload is identical to "off".
        Assert.False(NavigateReasoning.BuildContext(Obj, m).Flags.ContainsKey(SkillRetrieval.KnownSkillsFlag));
    }

    // --- MapDecision ---------------------------------------------------------

    [Fact]
    public void MapDecision_CompletionSignal_MapsToDone()
    {
        var d = Decision(Spec(RuleActionType.Log, ("status", "complete")));
        Assert.Equal(NextActionKind.Done, NavigateReasoning.MapDecision(d).Kind);
    }

    [Fact]
    public void MapDecision_CompletionSignal_IsCaseInsensitive()
    {
        var d = Decision(Spec(RuleActionType.Log, ("status", "COMPLETE")));
        Assert.Equal(NextActionKind.Done, NavigateReasoning.MapDecision(d).Kind);
    }

    [Theory]
    [InlineData(RuleActionType.Click, "click_by_label")]
    [InlineData(RuleActionType.Type, "type_into_field")]
    [InlineData(RuleActionType.PressKey, "press_keys")]
    public void MapDecision_SingleActuatingAction_MapsToAct_WithVerbAndParams(RuleActionType type, string expectedVerb)
    {
        var d = Decision(Spec(type, ("label", "Save"), ("text", "hi")));
        var action = NavigateReasoning.MapDecision(d);

        Assert.Equal(NextActionKind.Act, action.Kind);
        Assert.Equal(expectedVerb, action.Verb);
        Assert.Equal("Save", action.Parameters!["label"]);
        Assert.Equal("hi", action.Parameters!["text"]);
    }

    [Fact]
    public void MapDecision_MultipleActuatingActions_Escalates_NeverTruncatesToIndex0()
    {
        var d = Decision(
            Spec(RuleActionType.Click, ("label", "A")),
            Spec(RuleActionType.Type, ("text", "x")));

        Assert.Equal(NextActionKind.Escalate, NavigateReasoning.MapDecision(d).Kind);
    }

    [Fact]
    public void MapDecision_OnlyMetaLog_WithoutCompletion_Escalates_NotAct_NotDone()
    {
        var d = Decision(Spec(RuleActionType.Log, ("note", "thinking")));
        Assert.Equal(NextActionKind.Escalate, NavigateReasoning.MapDecision(d).Kind);
    }

    [Fact]
    public void MapDecision_NoActions_TierMiss_EscalatesWithReason()
    {
        var d = new BrainDecision { Outcome = MatchOutcome.NoMatch, Tier = DecisionTier.OperatorRequired, Reason = "no tier could decide" };
        var action = NavigateReasoning.MapDecision(d);

        Assert.Equal(NextActionKind.Escalate, action.Kind);
        Assert.Equal("no tier could decide", action.Rationale);
    }

    [Fact]
    public void MapDecision_CompletionWins_EvenWhenAnActuatingActionAlsoPresent()
    {
        var d = Decision(
            Spec(RuleActionType.Click, ("label", "A")),
            Spec(RuleActionType.Log, ("status", "complete")));

        Assert.Equal(NextActionKind.Done, NavigateReasoning.MapDecision(d).Kind);
    }

    // --- MapAllowedActions ---------------------------------------------------

    [Fact]
    public void MapAllowedActions_ParsesKnownTypes_IgnoresUnknown()
    {
        var set = NavigateReasoning.MapAllowedActions(
            new HashSet<string> { "Click", "type", "PressKey", "not_a_type" });

        Assert.Contains(RuleActionType.Click, set);
        Assert.Contains(RuleActionType.Type, set);
        Assert.Contains(RuleActionType.PressKey, set);
        Assert.Equal(3, set.Count);
    }
}
