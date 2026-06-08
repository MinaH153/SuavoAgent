using System.Collections.Generic;
using SuavoAgent.Core.Agentic;
using SuavoAgent.Core.Agentic.Adapters;
using Xunit;

namespace SuavoAgent.Core.Tests.Agentic;

/// <summary>
/// The moat's trust line. Before this, VerifyPostconditionAsync always returned Ambiguous — a click
/// that did nothing was never caught. These tests pin the real execution-grounded behavior.
/// </summary>
public class PostconditionEvaluatorTests
{
    private static PerceivedScreen Screen(string hash) => new(hash, true, new[] { $"el:{hash}" });
    private static NextAction Click() =>
        NextAction.Act("click_by_signature", new Dictionary<string, object?> { ["label"] = "Save" });

    [Fact]
    public void ActuatingAction_NoScreenChange_IsNotMet()
    {
        // The silent failure the old always-Ambiguous stub missed: an action that changed nothing.
        Assert.Equal(PostconditionVerdict.NotMet,
            PostconditionEvaluator.Evaluate(Click(), Screen("same"), Screen("same")));
    }

    [Fact]
    public void ActuatingAction_ScreenChanged_IsMet()
    {
        Assert.Equal(PostconditionVerdict.Met,
            PostconditionEvaluator.Evaluate(Click(), Screen("before"), Screen("after")));
    }

    [Fact]
    public void TerminalActions_HaveNothingToVerify_AreAmbiguous()
    {
        Assert.Equal(PostconditionVerdict.Ambiguous,
            PostconditionEvaluator.Evaluate(NextAction.Done, Screen("a"), Screen("a")));
        Assert.Equal(PostconditionVerdict.Ambiguous,
            PostconditionEvaluator.Evaluate(NextAction.Escalate("handoff"), Screen("a"), Screen("b")));
    }
}
