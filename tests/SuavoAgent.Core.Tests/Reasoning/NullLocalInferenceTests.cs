using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Reasoning;
using Xunit;

namespace SuavoAgent.Core.Tests.Reasoning;

public class NullLocalInferenceTests
{
    [Fact]
    public async Task ProposeAsync_AlwaysReturnsNull()
    {
        var inf = new NullLocalInference();
        var req = new InferenceRequest
        {
            Context = new RuleContext { SkillId = "s" },
            EscalationReason = "test",
        };

        var result = await inf.ProposeAsync(req, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public void IsReady_IsFalse()
    {
        Assert.False(new NullLocalInference().IsReady);
    }

    [Fact]
    public void ModelId_IsNone()
    {
        Assert.Equal("none", new NullLocalInference().ModelId);
    }

    [Fact]
    public async Task IntegratesWith_TieredBrain_AsRulesOnlyFallback()
    {
        // When Tier-2 is Null, any Tier-1 NoMatch should cleanly surface
        // as operator-required, not crash or hang.
        using var _ = new System.Threading.CancellationTokenSource();

        var engine = new RuleEngine(
            Array.Empty<Rule>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RuleEngine>.Instance);
        var verifier = new ActionVerifier();
        var brain = new TieredBrain(
            engine, new NullLocalInference(), verifier,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TieredBrain>.Instance);

        var decision = await brain.DecideAsync(
            new RuleContext { SkillId = "anything" });

        Assert.Equal(DecisionTier.OperatorRequired, decision.Tier);
    }
}
