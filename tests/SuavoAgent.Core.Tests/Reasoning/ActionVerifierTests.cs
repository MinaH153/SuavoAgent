using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Reasoning;
using Xunit;

namespace SuavoAgent.Core.Tests.Reasoning;

public class ActionVerifierTests
{
    private readonly ActionVerifier _verifier = new();

    // --- Whitelist -----------------------------------------------------------

    [Fact]
    public void Verify_ActionNotInAllowedSet_Rejected()
    {
        var result = _verifier.Verify(
            Proposal(RuleActionType.Click, 0.99, ("name", "X")),
            Request(allowed: new[] { RuleActionType.Log }, visible: new[] { "X" }));

        Assert.Equal(VerificationOutcome.Rejected, result.Outcome);
        Assert.Contains(result.FailedChecks, c => c.Contains("not allowed"));
    }

    [Fact]
    public void Verify_ActionInAllowedSet_Passes()
    {
        var result = _verifier.Verify(
            Proposal(RuleActionType.Click, 0.99, ("name", "X")),
            Request(allowed: new[] { RuleActionType.Click }, visible: new[] { "X" }));

        Assert.Equal(VerificationOutcome.Approved, result.Outcome);
    }

    // --- Confidence thresholds ----------------------------------------------

    [Fact]
    public void Verify_ClickBelowConfidence_RequiresOperator()
    {
        var result = _verifier.Verify(
            Proposal(RuleActionType.Click, 0.80, ("name", "X")),
            Request(visible: new[] { "X" }));

        Assert.Equal(VerificationOutcome.OperatorApprovalRequired, result.Outcome);
    }

    [Fact]
    public void Verify_ClickAtExactlyThreshold_Approved()
    {
        var result = _verifier.Verify(
            Proposal(RuleActionType.Click, 0.95, ("name", "X")),
            Request(visible: new[] { "X" }));

        Assert.Equal(VerificationOutcome.Approved, result.Outcome);
    }

    [Fact]
    public void Verify_LogBelowClickThreshold_StillApproved()
    {
        // Log has no confidence threshold (0.0) — low-confidence Log is fine.
        var result = _verifier.Verify(
            Proposal(RuleActionType.Log, 0.1),
            Request());

        Assert.Equal(VerificationOutcome.Approved, result.Outcome);
    }

    [Theory]
    [InlineData(RuleActionType.Click, 0.94, VerificationOutcome.OperatorApprovalRequired)]
    [InlineData(RuleActionType.Click, 0.95, VerificationOutcome.Approved)]
    [InlineData(RuleActionType.Type, 0.94, VerificationOutcome.OperatorApprovalRequired)]
    [InlineData(RuleActionType.Type, 0.95, VerificationOutcome.Approved)]
    [InlineData(RuleActionType.PressKey, 0.89, VerificationOutcome.OperatorApprovalRequired)]
    [InlineData(RuleActionType.PressKey, 0.90, VerificationOutcome.Approved)]
    [InlineData(RuleActionType.VerifyElement, 0.79, VerificationOutcome.OperatorApprovalRequired)]
    [InlineData(RuleActionType.VerifyElement, 0.80, VerificationOutcome.Approved)]
    public void Verify_ConfidenceThresholdsPerActionType(
        RuleActionType type, double confidence, VerificationOutcome expected)
    {
        var parameters = ParamsFor(type);
        var result = _verifier.Verify(
            Proposal(type, confidence, parameters),
            Request(visible: new[] { "X" }));

        Assert.Equal(expected, result.Outcome);
    }

    // --- Target element existence -------------------------------------------

    [Fact]
    public void Verify_ClickWithMissingTarget_Rejected()
    {
        var result = _verifier.Verify(
            Proposal(RuleActionType.Click, 0.99, ("name", "GhostButton")),
            Request(visible: new[] { "OtherButton" }));

        Assert.Equal(VerificationOutcome.Rejected, result.Outcome);
        Assert.Contains(result.FailedChecks, c => c.Contains("GhostButton"));
    }

    [Fact]
    public void Verify_ClickWithExistingTarget_Approved()
    {
        var result = _verifier.Verify(
            Proposal(RuleActionType.Click, 0.99, ("name", "Save")),
            Request(visible: new[] { "Save", "Cancel" }));

        Assert.Equal(VerificationOutcome.Approved, result.Outcome);
    }

    [Fact]
    public void Verify_ClickByControlTypeOnly_SkipsTargetCheck()
    {
        // If no "name" parameter, no target to verify — controlType-only is ok.
        var result = _verifier.Verify(
            Proposal(RuleActionType.Click, 0.99, ("controlType", "Button")),
            Request(visible: Array.Empty<string>()));

        Assert.Equal(VerificationOutcome.Approved, result.Outcome);
    }

    [Fact]
    public void Verify_PressKey_NeverChecksVisibleElements()
    {
        // PressKey targets the focused element — no name check needed.
        var result = _verifier.Verify(
            Proposal(RuleActionType.PressKey, 0.95, ("key", "Escape")),
            Request(visible: Array.Empty<string>()));

        Assert.Equal(VerificationOutcome.Approved, result.Outcome);
    }

    // --- Parameter structural validation ------------------------------------

    [Fact]
    public void Verify_ClickMissingParameters_Rejected()
    {
        var result = _verifier.Verify(
            Proposal(RuleActionType.Click, 0.99),
            Request());

        Assert.Equal(VerificationOutcome.Rejected, result.Outcome);
        Assert.Contains(result.FailedChecks, c => c.Contains("missing"));
    }

    [Fact]
    public void Verify_PressKeyMissingKey_Rejected()
    {
        var result = _verifier.Verify(
            Proposal(RuleActionType.PressKey, 0.99),
            Request());

        Assert.Equal(VerificationOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public void Verify_TypeWithText_Approved()
    {
        var result = _verifier.Verify(
            Proposal(RuleActionType.Type, 0.99, ("text", "hello")),
            Request());

        Assert.Equal(VerificationOutcome.Approved, result.Outcome);
    }

    [Fact]
    public void Verify_TypeWithSourceOnly_Approved()
    {
        // `source` is an alternative to `text` (dynamic binding in executor)
        var result = _verifier.Verify(
            Proposal(RuleActionType.Type, 0.99, ("source", "context.ndc")),
            Request());

        Assert.Equal(VerificationOutcome.Approved, result.Outcome);
    }

    [Fact]
    public void Verify_EscalateRequiresNoParameters_Approved()
    {
        var result = _verifier.Verify(
            Proposal(RuleActionType.Escalate, 0.0),
            Request());

        Assert.Equal(VerificationOutcome.Approved, result.Outcome);
    }

    // --- Multiple failures accumulate ---------------------------------------

    [Fact]
    public void Verify_MultipleFailures_AllReported()
    {
        // Missing param + ghost target + low confidence — should report multiple.
        var result = _verifier.Verify(
            Proposal(RuleActionType.Click, 0.5, ("name", "GhostButton")),
            Request(visible: new[] { "OtherButton" }));

        Assert.Equal(VerificationOutcome.Rejected, result.Outcome);
        // Target check fails, which is what we care about here.
        Assert.Contains(result.FailedChecks, c => c.Contains("GhostButton"));
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
