using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.Reasoning;
using SuavoAgent.Core.Tests.Reasoning;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

public class PricingBrainEvaluatorTests
{
    // --- Context construction -------------------------------------------------

    [Fact]
    public void BuildContext_UsesPricingLookupSkillId()
    {
        var ctx = PricingBrainEvaluator.BuildContext(Row(), Success(), Stats());
        Assert.Equal(PricingSkills.Lookup, ctx.SkillId);
    }

    [Fact]
    public void BuildContext_PopulatesCoreFlags()
    {
        var row = Row(ndc: "00093-7146-56");
        var result = new SupplierPriceResult(
            JobId: "j1", RowIndex: 1, Ndc: row.NdcNormalized,
            Found: true, SupplierName: "McKesson", CostPerUnit: 12.34m, ErrorMessage: null);
        var stats = Stats(total: 100, completed: 20, failed: 3, streak: 0);

        var ctx = PricingBrainEvaluator.BuildContext(row, result, stats);

        Assert.Equal("00093-7146-56", ctx.Flags[PricingBrainFlags.Ndc]);
        Assert.Equal("true", ctx.Flags[PricingBrainFlags.Found]);
        Assert.Equal("McKesson", ctx.Flags[PricingBrainFlags.Supplier]);
        Assert.Equal("true", ctx.Flags[PricingBrainFlags.CostPresent]);
        Assert.Equal("", ctx.Flags[PricingBrainFlags.ErrorClass]);
        Assert.Equal("0", ctx.Flags[PricingBrainFlags.ConsecutiveFailures]);
        Assert.Equal("13", ctx.Flags[PricingBrainFlags.FailureRatePct]); // 3/23 ≈ 13%
        Assert.Equal("100", ctx.Flags[PricingBrainFlags.TotalItems]);
    }

    [Fact]
    public void BuildContext_ClassifiesTimeoutErrors()
    {
        var ctx = PricingBrainEvaluator.BuildContext(
            Row(), FailWith("Lookup timed out after 30s"), Stats());
        Assert.Equal("timeout", ctx.Flags[PricingBrainFlags.ErrorClass]);
    }

    [Fact]
    public void BuildContext_ClassifiesPipeDesync()
    {
        var ctx = PricingBrainEvaluator.BuildContext(
            Row(), FailWith("Response ID mismatch: expected X, got Y"), Stats());
        Assert.Equal("pipe_desync", ctx.Flags[PricingBrainFlags.ErrorClass]);
    }

    [Fact]
    public void BuildContext_ZeroProcessedRows_DoesNotDivideByZero()
    {
        // First row of a fresh job: 0 completed, 0 failed before this one.
        var ctx = PricingBrainEvaluator.BuildContext(
            Row(), Success(), Stats(total: 50, completed: 0, failed: 0, streak: 0));
        Assert.Equal("0", ctx.Flags[PricingBrainFlags.FailureRatePct]);
    }

    // --- Derived threshold flags ---------------------------------------------

    [Theory]
    [InlineData(2, "false")]
    [InlineData(3, "true")]  // threshold crossed
    [InlineData(9, "true")]
    public void BuildContext_StreakWarning_TogglesAtThreshold(int streak, string expected)
    {
        var ctx = PricingBrainEvaluator.BuildContext(Row(), Fail(), Stats(streak: streak));
        Assert.Equal(expected, ctx.Flags[PricingBrainFlags.StreakWarning]);
    }

    [Theory]
    [InlineData(9, "false")]
    [InlineData(10, "true")]  // threshold crossed
    [InlineData(20, "true")]
    public void BuildContext_StreakSevere_TogglesAtThreshold(int streak, string expected)
    {
        var ctx = PricingBrainEvaluator.BuildContext(Row(), Fail(), Stats(streak: streak));
        Assert.Equal(expected, ctx.Flags[PricingBrainFlags.StreakSevere]);
    }

    [Fact]
    public void BuildContext_FailureRateHigh_RequiresMinimumSample()
    {
        // 5 out of 5 is 100% failure but only 5 samples — below min sample.
        var ctx = PricingBrainEvaluator.BuildContext(
            Row(), Fail(), Stats(total: 50, completed: 0, failed: 5, streak: 5));
        Assert.Equal("false", ctx.Flags[PricingBrainFlags.FailureRateHigh]);
    }

    [Fact]
    public void BuildContext_FailureRateHigh_TriggersAt50PctOver10Samples()
    {
        // 5 failed of 10 processed = 50% — at threshold with enough samples.
        var ctx = PricingBrainEvaluator.BuildContext(
            Row(), Fail(), Stats(total: 50, completed: 5, failed: 5, streak: 5));
        Assert.Equal("true", ctx.Flags[PricingBrainFlags.FailureRateHigh]);
    }

    [Fact]
    public void BuildContext_FailureRateHigh_FalseOnHealthyRun()
    {
        // 1 failed of 20 processed = 5% — way below threshold.
        var ctx = PricingBrainEvaluator.BuildContext(
            Row(), Success(), Stats(total: 50, completed: 19, failed: 1, streak: 0));
        Assert.Equal("false", ctx.Flags[PricingBrainFlags.FailureRateHigh]);
    }

    // --- Brain wiring ---------------------------------------------------------

    [Fact]
    public async Task EvaluateAsync_NoMatchingRuleAndNoTier2_ReturnsContinue()
    {
        var evaluator = Evaluator();

        var decision = await evaluator.EvaluateAsync(Row(), Success(), Stats(), CancellationToken.None);

        Assert.False(decision.ShouldHalt);
        Assert.Equal(DecisionTier.OperatorRequired, decision.Tier);
    }

    [Fact]
    public async Task EvaluateAsync_Tier1EscalateRule_ReturnsHalt()
    {
        var rule = FlagMatchRule(
            id: "pricing.streak-halt",
            skillId: PricingSkills.Lookup,
            flagKey: PricingBrainFlags.ConsecutiveFailures,
            flagValue: "5",
            action: new RuleActionSpec
            {
                Type = RuleActionType.Escalate,
                Description = "Too many consecutive failures",
            });
        var evaluator = Evaluator(rules: new[] { rule });

        var decision = await evaluator.EvaluateAsync(
            Row(), Fail(), Stats(streak: 5), CancellationToken.None);

        Assert.True(decision.ShouldHalt);
        Assert.Equal(DecisionTier.Rules, decision.Tier);
    }

    [Fact]
    public async Task EvaluateAsync_Tier1LogOnlyAction_Continues()
    {
        var rule = FlagMatchRule(
            id: "pricing.log",
            skillId: PricingSkills.Lookup,
            flagKey: PricingBrainFlags.Ndc,
            flagValue: "00000-0000-00",
            action: new RuleActionSpec { Type = RuleActionType.Log });
        var evaluator = Evaluator(rules: new[] { rule });

        var decision = await evaluator.EvaluateAsync(
            Row(ndc: "00000-0000-00"), Success(), Stats(), CancellationToken.None);

        Assert.False(decision.ShouldHalt);
        Assert.Equal(DecisionTier.Rules, decision.Tier);
    }

    [Fact]
    public async Task EvaluateAsync_Tier3CloudEscalate_ReturnsHalt()
    {
        // Tier-1 no match, Tier-2 disabled, Tier-3 proposes Escalate.
        var cloud = new MockCloudReasoning();
        cloud.EnqueueApproved(RuleActionType.Escalate, 0.95);
        var evaluator = Evaluator(cloud: cloud);

        var decision = await evaluator.EvaluateAsync(
            Row(), Fail(), Stats(streak: 8), CancellationToken.None);

        Assert.True(decision.ShouldHalt);
        Assert.Equal(DecisionTier.CloudInference, decision.Tier);
        Assert.Equal(1, cloud.CallCount);
    }

    [Fact]
    public async Task EvaluateAsync_Tier3OnlyAllowedFlowActions()
    {
        // AllowedActions handed to Tier-2/3 must contain Log/Escalate/AskOperator
        // and must NOT contain destructive UI actions — pricing has no UI.
        var cloud = new MockCloudReasoning();
        cloud.EnqueueApproved(RuleActionType.Log, 0.99);
        var capturingInference = new CapturingLocalInference();
        var evaluator = Evaluator(mock: capturingInference, cloud: cloud);

        await evaluator.EvaluateAsync(Row(), Fail(), Stats(), CancellationToken.None);

        Assert.NotNull(capturingInference.LastRequest);
        var allowed = capturingInference.LastRequest!.AllowedActions;
        Assert.Contains(RuleActionType.Log, allowed);
        Assert.Contains(RuleActionType.Escalate, allowed);
        Assert.Contains(RuleActionType.AskOperator, allowed);
        Assert.DoesNotContain(RuleActionType.Click, allowed);
        Assert.DoesNotContain(RuleActionType.Type, allowed);
        Assert.DoesNotContain(RuleActionType.PressKey, allowed);
    }

    [Fact]
    public async Task EvaluateAsync_CancellationPropagates()
    {
        // Use a Tier-2 mock that awaits the token so cancellation is observed —
        // the all-synchronous path (Tier-2 not ready, no cloud) would never
        // reach an await and never see the pre-cancelled token.
        var mock = new MockLocalInference
        {
            IsReady = true,
            ArtificialLatency = TimeSpan.FromSeconds(1),
        };
        var evaluator = Evaluator(mock: mock);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            evaluator.EvaluateAsync(Row(), Success(), Stats(), cts.Token));
    }

    [Fact]
    public async Task EvaluateAsync_Tier2ThrowsInternally_FailsSafeAsContinue()
    {
        // TieredBrain's contract is non-throwing — it catches Tier-2 errors
        // internally and returns OperatorRequired. The evaluator should
        // translate that to Continue so the pricing loop keeps running.
        var mock = new MockLocalInference { ThrowOnPropose = true };
        var evaluator = Evaluator(mock: mock);

        var decision = await evaluator.EvaluateAsync(
            Row(), Fail(), Stats(), CancellationToken.None);

        Assert.False(decision.ShouldHalt);
    }

    // --- Bundled YAML rules end-to-end ---------------------------------------

    [Fact]
    public async Task BundledRules_HaltOnPipeDesync()
    {
        var evaluator = EvaluatorWithBundledRules();

        var decision = await evaluator.EvaluateAsync(
            Row(),
            FailWith("Response ID mismatch: expected X, got Y"),
            Stats(streak: 1),
            CancellationToken.None);

        Assert.True(decision.ShouldHalt);
        Assert.Equal(DecisionTier.Rules, decision.Tier);
        Assert.Contains("pipe-desync", decision.Reason);
    }

    [Fact]
    public async Task BundledRules_HaltOnSevereStreak()
    {
        var evaluator = EvaluatorWithBundledRules();

        var decision = await evaluator.EvaluateAsync(
            Row(), Fail(),
            Stats(total: 100, completed: 5, failed: 10, streak: 10),
            CancellationToken.None);

        Assert.True(decision.ShouldHalt);
        Assert.Equal(DecisionTier.Rules, decision.Tier);
        Assert.Contains("severe-streak", decision.Reason);
    }

    [Fact]
    public async Task BundledRules_ContinueOnHealthyRun()
    {
        var evaluator = EvaluatorWithBundledRules();

        var decision = await evaluator.EvaluateAsync(
            Row(), Success(),
            Stats(total: 100, completed: 50, failed: 2, streak: 0),
            CancellationToken.None);

        Assert.False(decision.ShouldHalt);
    }

    private static PricingBrainEvaluator EvaluatorWithBundledRules()
    {
        // Walk up from test bin to repo root. If the path shape changes in CI,
        // this assertion is the signal to update.
        var rulesDir = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "SuavoAgent.Core", "Reasoning", "Rules");
        rulesDir = Path.GetFullPath(rulesDir);
        Assert.True(Directory.Exists(rulesDir), $"Bundled rules dir missing: {rulesDir}");

        var loader = new YamlRuleLoader(NullLogger<YamlRuleLoader>.Instance);
        var rules = loader.LoadFromDirectory(rulesDir, required: true);
        return Evaluator(rules: rules);
    }

    // --- Helpers --------------------------------------------------------------

    private static NdcRow Row(int rowIndex = 1, string ndc = "00093-7146-56") =>
        new(rowIndex, ndc, ndc);

    private static SupplierPriceResult Success(string jobId = "j1", int rowIndex = 1) =>
        new(jobId, rowIndex, "00093-7146-56", Found: true,
            SupplierName: "McKesson", CostPerUnit: 12.34m, ErrorMessage: null);

    private static SupplierPriceResult Fail(string jobId = "j1", int rowIndex = 1) =>
        new(jobId, rowIndex, "00093-7146-56", Found: false,
            SupplierName: null, CostPerUnit: null, ErrorMessage: "No response from Helper");

    private static SupplierPriceResult FailWith(string error, string jobId = "j1", int rowIndex = 1) =>
        new(jobId, rowIndex, "00093-7146-56", Found: false,
            SupplierName: null, CostPerUnit: null, ErrorMessage: error);

    private static PricingRunStats Stats(
        int total = 100, int completed = 10, int failed = 2, int streak = 0) =>
        new()
        {
            TotalItems = total,
            CompletedItems = completed,
            FailedItems = failed,
            ConsecutiveFailures = streak,
        };

    private static PricingBrainEvaluator Evaluator(
        IEnumerable<Rule>? rules = null,
        ILocalInference? mock = null,
        ICloudReasoning? cloud = null)
    {
        var engine = new RuleEngine(rules ?? Array.Empty<Rule>(), NullLogger<RuleEngine>.Instance);
        var inference = mock ?? new MockLocalInference { IsReady = false };
        var verifier = new ActionVerifier(autoExecuteDestructive: false);
        var brain = new TieredBrain(
            engine, inference, verifier, NullLogger<TieredBrain>.Instance, cloud);
        return new PricingBrainEvaluator(brain, NullLogger<PricingBrainEvaluator>.Instance);
    }

    private static Rule FlagMatchRule(
        string id, string skillId, string flagKey, string flagValue, RuleActionSpec action) =>
        new()
        {
            Id = id,
            SkillId = skillId,
            When = new RulePredicate
            {
                StateFlags = new Dictionary<string, string> { [flagKey] = flagValue },
            },
            Then = new[] { action },
        };

    /// <summary>Captures the last InferenceRequest so tests can assert on its shape.</summary>
    private sealed class CapturingLocalInference : ILocalInference
    {
        public string ModelId => "capturing";
        public bool IsReady => true;
        public InferenceRequest? LastRequest { get; private set; }

        public Task<InferenceProposal?> ProposeAsync(InferenceRequest request, CancellationToken ct)
        {
            LastRequest = request;
            // Return null so the brain tries Tier-3 (MockCloudReasoning in the test).
            return Task.FromResult<InferenceProposal?>(null);
        }
    }
}
