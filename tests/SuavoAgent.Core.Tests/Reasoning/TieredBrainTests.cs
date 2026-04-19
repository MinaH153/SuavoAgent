using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Reasoning;
using Xunit;

namespace SuavoAgent.Core.Tests.Reasoning;

public class TieredBrainTests
{
    // --- Tier 1 happy path --------------------------------------------------

    [Fact]
    public async Task Decide_RuleMatches_ReturnsTier1()
    {
        var brain = Brain(
            rules: new[] { ClickRule("r1", "s1", name: "Save") },
            mock: new MockLocalInference());

        var decision = await brain.DecideAsync(Ctx("s1", visible: new[] { "Save" }));

        Assert.Equal(MatchOutcome.Matched, decision.Outcome);
        Assert.Equal(DecisionTier.Rules, decision.Tier);
        Assert.Equal("r1", decision.MatchedRule!.Id);
    }

    // --- Tier 1 → Tier 2 escalation -----------------------------------------

    [Fact]
    public async Task Decide_NoRuleMatches_EscalatesToLocalInference()
    {
        // Destructive proposal at 0.99 with AutoExecuteTier2Destructive=true
        // AND explicit Click in allowlist — permissive path to prove the
        // escalation pipeline works.
        var mock = new MockLocalInference();
        mock.EnqueueApproved(RuleActionType.Click, 0.99, ("name", "Save"));

        var brain = Brain(rules: Array.Empty<Rule>(), mock: mock, autoExecuteDestructive: true);

        var decision = await brain.DecideAsync(
            Ctx("s1", visible: new[] { "Save" }),
            allowedTier2Actions: new HashSet<RuleActionType> { RuleActionType.Click });

        Assert.Equal(MatchOutcome.Matched, decision.Outcome);
        Assert.Equal(DecisionTier.LocalInference, decision.Tier);
        Assert.Equal(1, mock.CallCount);
        Assert.Equal(RuleActionType.Click, decision.Actions[0].Type);
    }

    [Fact]
    public async Task Decide_LocalInferenceNotReady_OperatorRequired()
    {
        var mock = new MockLocalInference { IsReady = false };
        var brain = Brain(rules: Array.Empty<Rule>(), mock: mock);

        var decision = await brain.DecideAsync(Ctx("s1"));

        Assert.Equal(DecisionTier.OperatorRequired, decision.Tier);
        Assert.Equal(0, mock.CallCount);
    }

    [Fact]
    public async Task Decide_InferenceReturnsNull_OperatorRequired()
    {
        var mock = new MockLocalInference();
        mock.Responses.Enqueue(null);
        var brain = Brain(rules: Array.Empty<Rule>(), mock: mock);

        var decision = await brain.DecideAsync(Ctx("s1"));

        Assert.Equal(DecisionTier.OperatorRequired, decision.Tier);
        Assert.Contains("no proposal", decision.Reason);
    }

    [Fact]
    public async Task Decide_InferenceThrows_OperatorRequired()
    {
        var mock = new MockLocalInference { ThrowOnPropose = true };
        var brain = Brain(rules: Array.Empty<Rule>(), mock: mock);

        var decision = await brain.DecideAsync(Ctx("s1"));

        Assert.Equal(DecisionTier.OperatorRequired, decision.Tier);
        Assert.Contains("error", decision.Reason.ToLowerInvariant());
    }

    // --- Verifier integration -----------------------------------------------

    [Fact]
    public async Task Decide_ProposalRejectedByVerifier_OperatorRequired()
    {
        // LLM proposes Click on an element that ISN'T visible — should reject.
        var mock = new MockLocalInference();
        mock.EnqueueApproved(RuleActionType.Click, 0.99, ("name", "GhostButton"));
        var brain = Brain(rules: Array.Empty<Rule>(), mock: mock);

        var decision = await brain.DecideAsync(Ctx("s1", visible: new[] { "OtherButton" }));

        Assert.Equal(DecisionTier.OperatorRequired, decision.Tier);
        Assert.NotNull(decision.Verification);
        Assert.Equal(VerificationOutcome.Rejected, decision.Verification!.Outcome);
    }

    [Fact]
    public async Task Decide_ProposalLowConfidence_OperatorApprovalRequired()
    {
        // Read-only action at below-threshold confidence (0.80 < 0.85 spec floor).
        var mock = new MockLocalInference();
        mock.EnqueueApproved(RuleActionType.VerifyElement, 0.80, ("name", "Save"));
        var brain = Brain(rules: Array.Empty<Rule>(), mock: mock);

        var decision = await brain.DecideAsync(Ctx("s1", visible: new[] { "Save" }));

        Assert.Equal(MatchOutcome.Blocked, decision.Outcome);
        Assert.Equal(DecisionTier.OperatorRequired, decision.Tier);
        Assert.Equal(VerificationOutcome.OperatorApprovalRequired, decision.Verification!.Outcome);
    }

    [Fact]
    public async Task Decide_DestructiveProposal_AlwaysGoesToOperator_UnderDefaultPolicy()
    {
        // Default AutoExecuteTier2Destructive=false → any Tier-2 Click goes
        // to operator regardless of confidence.
        var mock = new MockLocalInference();
        mock.EnqueueApproved(RuleActionType.Click, 1.0, ("name", "Save"));
        var brain = Brain(rules: Array.Empty<Rule>(), mock: mock); // default (not permissive)

        var decision = await brain.DecideAsync(
            Ctx("s1", visible: new[] { "Save" }),
            allowedTier2Actions: new HashSet<RuleActionType> { RuleActionType.Click });

        Assert.Equal(MatchOutcome.Blocked, decision.Outcome);
        Assert.Equal(DecisionTier.OperatorRequired, decision.Tier);
        Assert.Contains("AutoExecuteTier2Destructive", decision.Verification!.Reason);
    }

    // --- Precondition short-circuit -----------------------------------------

    [Fact]
    public async Task Decide_PreconditionBlocks_DoesNotCallTier2()
    {
        var gate = new Rule
        {
            Id = "gate",
            SkillId = RuleEngine.PreconditionsSkill,
            Priority = 1000,
            AutonomousOk = false,
            When = new RulePredicate
            {
                StateFlags = new Dictionary<string, string> { ["active_call"] = "true" },
            },
            Then = new[] { new RuleActionSpec { Type = RuleActionType.AskOperator } },
        };
        var mock = new MockLocalInference();

        var brain = Brain(rules: new[] { gate }, mock: mock);

        var decision = await brain.DecideAsync(new RuleContext
        {
            SkillId = "pricing-lookup",
            Flags = new Dictionary<string, string> { ["active_call"] = "true" },
        });

        Assert.Equal(MatchOutcome.Blocked, decision.Outcome);
        Assert.Equal(DecisionTier.Precondition, decision.Tier);
        Assert.Equal(0, mock.CallCount);
    }

    // --- Shadow mode --------------------------------------------------------

    [Fact]
    public async Task Decide_ShadowMode_DoesNotExecuteApprovedTier2()
    {
        var mock = new MockLocalInference();
        mock.EnqueueApproved(RuleActionType.Click, 0.99, ("name", "Save"));
        var brain = Brain(rules: Array.Empty<Rule>(), mock: mock, autoExecuteDestructive: true);

        var decision = await brain.DecideAsync(
            Ctx("s1", visible: new[] { "Save" }),
            allowedTier2Actions: new HashSet<RuleActionType> { RuleActionType.Click },
            shadowMode: true);

        Assert.Equal(MatchOutcome.NoMatch, decision.Outcome);
        Assert.Equal(DecisionTier.LocalInference, decision.Tier);
        Assert.NotNull(decision.Proposal);
        Assert.Contains("Shadow", decision.Reason);
    }

    // --- Allowed-actions narrowing passes through ----------------------------

    [Fact]
    public async Task Decide_AllowedActionsRestrictsVerifier()
    {
        var mock = new MockLocalInference();
        mock.EnqueueApproved(RuleActionType.Click, 0.99, ("name", "Save"));
        var brain = Brain(rules: Array.Empty<Rule>(), mock: mock);

        var decision = await brain.DecideAsync(
            Ctx("s1", visible: new[] { "Save" }),
            allowedTier2Actions: new HashSet<RuleActionType> { RuleActionType.Log });

        Assert.Equal(DecisionTier.OperatorRequired, decision.Tier);
        Assert.Equal(VerificationOutcome.Rejected, decision.Verification!.Outcome);
        Assert.Contains("not allowed", string.Join(" ", decision.Verification.FailedChecks));
    }

    // --- Tier 3 cloud escalation --------------------------------------------

    [Fact]
    public async Task Decide_Tier2Null_EscalatesToTier3_WhenCloudEnabled()
    {
        var mock = new MockLocalInference();
        mock.Responses.Enqueue(null); // Tier-2 fails
        var cloud = new MockCloudReasoning();
        cloud.EnqueueApproved(RuleActionType.VerifyElement, 0.95, ("name", "Save"));

        var brain = Brain(rules: Array.Empty<Rule>(), mock: mock, cloud: cloud);

        var decision = await brain.DecideAsync(Ctx("s1", visible: new[] { "Save" }));

        Assert.Equal(MatchOutcome.Matched, decision.Outcome);
        Assert.Equal(DecisionTier.CloudInference, decision.Tier);
        Assert.Equal(1, cloud.CallCount);
        Assert.Equal("mock-cloud", decision.Proposal!.ModelId);
    }

    [Fact]
    public async Task Decide_Tier2LowConfidence_EscalatesToTier3()
    {
        // Tier-2 returns 0.40 (below 0.5 cloud-escalation threshold).
        var mock = new MockLocalInference();
        mock.EnqueueApproved(RuleActionType.VerifyElement, 0.40, ("name", "Save"));
        var cloud = new MockCloudReasoning();
        cloud.EnqueueApproved(RuleActionType.VerifyElement, 0.92, ("name", "Save"));

        var brain = Brain(rules: Array.Empty<Rule>(), mock: mock, cloud: cloud);

        var decision = await brain.DecideAsync(Ctx("s1", visible: new[] { "Save" }));

        Assert.Equal(DecisionTier.CloudInference, decision.Tier);
        Assert.Equal(1, cloud.CallCount);
        Assert.Equal(0.92, decision.Proposal!.Confidence);
    }

    [Fact]
    public async Task Decide_Tier2HighConfidence_DoesNotCallTier3()
    {
        var mock = new MockLocalInference();
        mock.EnqueueApproved(RuleActionType.VerifyElement, 0.95, ("name", "Save"));
        var cloud = new MockCloudReasoning();

        var brain = Brain(rules: Array.Empty<Rule>(), mock: mock, cloud: cloud);

        var decision = await brain.DecideAsync(Ctx("s1", visible: new[] { "Save" }));

        Assert.Equal(DecisionTier.LocalInference, decision.Tier);
        Assert.Equal(0, cloud.CallCount);
    }

    [Fact]
    public async Task Decide_Tier3DeclinesAfterTier2Null_OperatorRequired()
    {
        var mock = new MockLocalInference();
        mock.Responses.Enqueue(null);
        var cloud = new MockCloudReasoning();
        cloud.Responses.Enqueue(null); // cloud also bails

        var brain = Brain(rules: Array.Empty<Rule>(), mock: mock, cloud: cloud);

        var decision = await brain.DecideAsync(Ctx("s1"));

        Assert.Equal(DecisionTier.OperatorRequired, decision.Tier);
        Assert.Equal(1, cloud.CallCount);
    }

    [Fact]
    public async Task Decide_CloudDisabled_SkipsTier3()
    {
        var mock = new MockLocalInference();
        mock.Responses.Enqueue(null);
        var cloud = new MockCloudReasoning { IsEnabled = false };

        var brain = Brain(rules: Array.Empty<Rule>(), mock: mock, cloud: cloud);

        var decision = await brain.DecideAsync(Ctx("s1"));

        Assert.Equal(DecisionTier.OperatorRequired, decision.Tier);
        Assert.Equal(0, cloud.CallCount);
    }

    [Fact]
    public async Task Decide_Tier2NotReady_StillTriesTier3()
    {
        var mock = new MockLocalInference { IsReady = false };
        var cloud = new MockCloudReasoning();
        cloud.EnqueueApproved(RuleActionType.VerifyElement, 0.95, ("name", "Save"));

        var brain = Brain(rules: Array.Empty<Rule>(), mock: mock, cloud: cloud);

        var decision = await brain.DecideAsync(Ctx("s1", visible: new[] { "Save" }));

        Assert.Equal(DecisionTier.CloudInference, decision.Tier);
        Assert.Equal(1, cloud.CallCount);
        Assert.Equal(0, mock.CallCount);
    }

    [Fact]
    public async Task Decide_Tier3Throws_FallsBackToTier2Proposal()
    {
        // Tier-2 gives a low-confidence proposal; Tier-3 throws; we should
        // still verify/route the Tier-2 proposal instead of losing it.
        var mock = new MockLocalInference();
        mock.EnqueueApproved(RuleActionType.VerifyElement, 0.40, ("name", "Save"));
        var cloud = new MockCloudReasoning { ThrowOnPropose = true };

        var brain = Brain(rules: Array.Empty<Rule>(), mock: mock, cloud: cloud);

        var decision = await brain.DecideAsync(Ctx("s1", visible: new[] { "Save" }));

        // Tier-2's 0.40 is below the verifier's 0.85 spec floor → operator.
        Assert.Equal(DecisionTier.OperatorRequired, decision.Tier);
        Assert.NotNull(decision.Proposal);
        Assert.Equal("mock", decision.Proposal!.ModelId);
    }

    // --- helpers ------------------------------------------------------------

    private static TieredBrain Brain(
        IEnumerable<Rule> rules,
        MockLocalInference mock,
        bool autoExecuteDestructive = false,
        ICloudReasoning? cloud = null)
    {
        var engine = new RuleEngine(rules, NullLogger<RuleEngine>.Instance);
        var verifier = new ActionVerifier(autoExecuteDestructive);
        return new TieredBrain(engine, mock, verifier, NullLogger<TieredBrain>.Instance, cloud);
    }

    private static Rule ClickRule(string id, string skillId, string name) =>
        new()
        {
            Id = id,
            SkillId = skillId,
            When = new RulePredicate { VisibleElements = new[] { name } },
            Then = new[]
            {
                new RuleActionSpec
                {
                    Type = RuleActionType.Click,
                    Parameters = new Dictionary<string, string> { ["name"] = name },
                },
            },
        };

    private static RuleContext Ctx(string skillId, IEnumerable<string>? visible = null) =>
        new()
        {
            SkillId = skillId,
            VisibleElements = new HashSet<string>(visible ?? Array.Empty<string>()),
        };
}
