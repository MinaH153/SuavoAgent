using Microsoft.Extensions.Options;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Reasoning;
using Xunit;

namespace SuavoAgent.Core.Tests.Reasoning;

public class ActionVerifierTests
{
    /// <summary>
    /// Default verifier policy (AutoExecuteTier2Destructive=false) means
    /// destructive Tier-2 proposals always go to operator. Most tests here
    /// use the "permissive" verifier that allows destructive to execute so
    /// we can exercise the confidence + target checks directly.
    /// </summary>
    private readonly ActionVerifier _permissive = new(autoExecuteDestructive: true);
    private readonly ActionVerifier _safeDefault = new();

    // --- Whitelist -----------------------------------------------------------

    [Fact]
    public void Verify_ActionNotInAllowedSet_Rejected()
    {
        var result = _permissive.Verify(
            Proposal(RuleActionType.Click, 0.99, ("name", "X")),
            Request(allowed: new[] { RuleActionType.Log }, visible: new[] { "X" }));

        Assert.Equal(VerificationOutcome.Rejected, result.Outcome);
        Assert.Contains(result.FailedChecks, c => c.Contains("not allowed"));
    }

    [Fact]
    public void Verify_ActionInAllowedSet_Passes()
    {
        var result = _permissive.Verify(
            Proposal(RuleActionType.Click, 0.99, ("name", "X")),
            Request(allowed: new[] { RuleActionType.Click }, visible: new[] { "X" }));

        Assert.Equal(VerificationOutcome.Approved, result.Outcome);
    }

    // --- Destructive policy (Codex M-4) -------------------------------------

    [Fact]
    public void Verify_Destructive_DefaultPolicy_RequiresOperator()
    {
        // AutoExecuteTier2Destructive=false is the default. Destructive
        // proposals from Tier-2 always go to operator, even at confidence 1.0.
        var result = _safeDefault.Verify(
            Proposal(RuleActionType.Click, 1.0, ("name", "Save")),
            Request(allowed: new[] { RuleActionType.Click }, visible: new[] { "Save" }));

        Assert.Equal(VerificationOutcome.OperatorApprovalRequired, result.Outcome);
        Assert.Contains("AutoExecuteTier2Destructive", result.Reason);
    }

    [Fact]
    public void Verify_ReadOnly_DefaultPolicy_Approved()
    {
        // Read-only actions are allowed through even under default policy.
        var result = _safeDefault.Verify(
            Proposal(RuleActionType.VerifyElement, 0.90, ("name", "Save")),
            Request(visible: new[] { "Save" }));

        Assert.Equal(VerificationOutcome.Approved, result.Outcome);
    }

    [Fact]
    public void Verify_ProductionAutoExecutionDisabled_RoutesToOperator()
    {
        var verifier = new ActionVerifier(Options.Create(new AgentOptions
        {
            AutoExecution = new AutoExecutionOptions
            {
                Enabled = false,
                RequireConfirmation = true,
            },
            Reasoning = new ReasoningOptions
            {
                AutoExecuteTier2Destructive = true,
            },
        }));

        var result = verifier.Verify(
            Proposal(RuleActionType.Click, 0.99, ("name", "Save")),
            Request(allowed: new[] { RuleActionType.Click }, visible: new[] { "Save" }));

        Assert.Equal(VerificationOutcome.OperatorApprovalRequired, result.Outcome);
        Assert.Contains("Agent.AutoExecution.Enabled=false", result.Reason);
    }

    // --- Destructive target-required (Codex C-3) ----------------------------

    [Fact]
    public void Verify_ClickWithoutName_Rejected_EvenWhenPermissive()
    {
        // Destructive actions must have a "name" parameter — controlType-only
        // is never accepted, regardless of the auto-execute flag.
        var result = _permissive.Verify(
            Proposal(RuleActionType.Click, 0.99, ("controlType", "Button")),
            Request(visible: new[] { "Anything" }));

        Assert.Equal(VerificationOutcome.Rejected, result.Outcome);
        Assert.Contains(result.FailedChecks,
            c => c.Contains("Click") && c.Contains("name"));
    }

    [Fact]
    public void Verify_PressKey_NoNameRequired()
    {
        // PressKey targets the focused element — name check doesn't apply.
        var result = _permissive.Verify(
            Proposal(RuleActionType.PressKey, 0.99, ("key", "Escape")),
            Request(visible: Array.Empty<string>()));

        Assert.Equal(VerificationOutcome.Approved, result.Outcome);
    }

    // --- Confidence thresholds (spec floor, Codex M-4) ----------------------

    [Theory]
    [InlineData(RuleActionType.Click, 0.97, VerificationOutcome.OperatorApprovalRequired)]
    [InlineData(RuleActionType.Click, 0.98, VerificationOutcome.Approved)]
    [InlineData(RuleActionType.Type, 0.97, VerificationOutcome.OperatorApprovalRequired)]
    [InlineData(RuleActionType.Type, 0.98, VerificationOutcome.Approved)]
    [InlineData(RuleActionType.PressKey, 0.97, VerificationOutcome.OperatorApprovalRequired)]
    [InlineData(RuleActionType.PressKey, 0.98, VerificationOutcome.Approved)]
    [InlineData(RuleActionType.VerifyElement, 0.84, VerificationOutcome.OperatorApprovalRequired)]
    [InlineData(RuleActionType.VerifyElement, 0.85, VerificationOutcome.Approved)]
    [InlineData(RuleActionType.WaitForElement, 0.84, VerificationOutcome.OperatorApprovalRequired)]
    [InlineData(RuleActionType.WaitForElement, 0.85, VerificationOutcome.Approved)]
    public void Verify_SpecFloorConfidenceThresholds(
        RuleActionType type, double confidence, VerificationOutcome expected)
    {
        var parameters = ParamsFor(type);
        var result = _permissive.Verify(
            Proposal(type, confidence, parameters),
            Request(visible: new[] { "X" }));

        Assert.Equal(expected, result.Outcome);
    }

    [Fact]
    public void Verify_LogAtLowConfidence_StillApproved()
    {
        var result = _permissive.Verify(
            Proposal(RuleActionType.Log, 0.01),
            Request());

        Assert.Equal(VerificationOutcome.Approved, result.Outcome);
    }

    // --- Target element existence -------------------------------------------

    [Fact]
    public void Verify_ClickWithMissingTarget_Rejected()
    {
        var result = _permissive.Verify(
            Proposal(RuleActionType.Click, 0.99, ("name", "GhostButton")),
            Request(visible: new[] { "OtherButton" }));

        Assert.Equal(VerificationOutcome.Rejected, result.Outcome);
        Assert.Contains(result.FailedChecks, c => c.Contains("GhostButton"));
    }

    [Fact]
    public void Verify_ClickWithExistingTarget_Approved()
    {
        var result = _permissive.Verify(
            Proposal(RuleActionType.Click, 0.99, ("name", "Save")),
            Request(visible: new[] { "Save", "Cancel" }));

        Assert.Equal(VerificationOutcome.Approved, result.Outcome);
    }

    // --- Parameter structural validation ------------------------------------

    [Fact]
    public void Verify_ClickMissingParameters_Rejected()
    {
        var result = _permissive.Verify(
            Proposal(RuleActionType.Click, 0.99),
            Request());

        Assert.Equal(VerificationOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public void Verify_PressKeyMissingKey_Rejected()
    {
        var result = _permissive.Verify(
            Proposal(RuleActionType.PressKey, 0.99),
            Request());

        Assert.Equal(VerificationOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public void Verify_TypeWithText_Approved()
    {
        var result = _permissive.Verify(
            Proposal(RuleActionType.Type, 0.99, ("text", "hello")),
            Request());

        Assert.Equal(VerificationOutcome.Approved, result.Outcome);
    }

    [Fact]
    public void Verify_TypeWithSourceOnly_Approved()
    {
        var result = _permissive.Verify(
            Proposal(RuleActionType.Type, 0.99, ("source", "context.ndc")),
            Request());

        Assert.Equal(VerificationOutcome.Approved, result.Outcome);
    }

    [Fact]
    public void Verify_EscalateRequiresNoParameters_Approved()
    {
        var result = _permissive.Verify(
            Proposal(RuleActionType.Escalate, 0.0),
            Request(allowed: new[] { RuleActionType.Escalate }));

        Assert.Equal(VerificationOutcome.Approved, result.Outcome);
    }

    // --- Helpers -------------------------------------------------------------

    private static InferenceProposal Proposal(
        RuleActionType type, double confidence,
        params (string Key, string Value)[] parameters) =>
        new()
        {
            Action = new RuleActionSpec
            {
                Type = type,
                Parameters = parameters.ToDictionary(p => p.Key, p => p.Value),
            },
            Confidence = confidence,
            ModelId = "test",
        };

    private static InferenceRequest Request(
        IEnumerable<RuleActionType>? allowed = null,
        IEnumerable<string>? visible = null) =>
        new()
        {
            Context = new RuleContext
            {
                SkillId = "test",
                VisibleElements = new HashSet<string>(visible ?? Array.Empty<string>()),
            },
            EscalationReason = "test",
            AllowedActions = new HashSet<RuleActionType>(
                allowed ?? Enum.GetValues<RuleActionType>()),
        };

    private static (string Key, string Value)[] ParamsFor(RuleActionType type) =>
        type switch
        {
            RuleActionType.Click           => new[] { ("name", "X") },
            RuleActionType.Type            => new[] { ("text", "hi") },
            RuleActionType.PressKey        => new[] { ("key", "Enter") },
            RuleActionType.VerifyElement   => new[] { ("name", "X") },
            RuleActionType.WaitForElement  => new[] { ("name", "X") },
            _                              => Array.Empty<(string, string)>(),
        };
}
