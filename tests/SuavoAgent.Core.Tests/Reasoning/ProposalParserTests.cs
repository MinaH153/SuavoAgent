using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Reasoning;
using Xunit;

namespace SuavoAgent.Core.Tests.Reasoning;

public class ProposalParserTests
{
    [Fact]
    public void TryParse_WellFormedJson_ReturnsProposal()
    {
        const string json = """
        {
          "action": {
            "type": "Click",
            "parameters": { "name": "Save" }
          },
          "confidence": 0.95,
          "rationaleCode": "target_present"
        }
        """;

        var result = ProposalParser.TryParse(json, "llama-test", latencyMs: 250);

        Assert.NotNull(result);
        Assert.Equal(RuleActionType.Click, result.Action.Type);
        Assert.Equal("Save", result.Action.Parameters["name"]);
        Assert.Equal(0.95, result.Confidence);
        Assert.Equal("llama-test", result.ModelId);
        Assert.Equal(250, result.LatencyMs);
        Assert.Equal(InferenceRationaleCode.TargetPresent, result.RationaleCode);
        Assert.Equal(
            "The requested target is present.",
            result.RationaleCode.ToOperatorMessage());
    }

    [Fact]
    public void TryParse_ParametersEmpty_Allowed()
    {
        const string json = """
        {
          "action": { "type": "Log", "parameters": {} },
          "confidence": 1.0,
          "rationaleCode": "no_safe_action"
        }
        """;

        var result = ProposalParser.TryParse(json, "m", 0);

        Assert.NotNull(result);
        Assert.Empty(result.Action.Parameters);
    }

    [Fact]
    public void TryParse_TrimsWhitespaceAroundJson()
    {
        const string json = "\n  { \"action\": { \"type\": \"Log\", \"parameters\": {} }, \"confidence\": 1.0, \"rationaleCode\": \"no_safe_action\" }\n";

        Assert.NotNull(ProposalParser.TryParse(json, "m", 0));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryParse_EmptyInput_ReturnsNull(string? input) =>
        Assert.Null(ProposalParser.TryParse(input!, "m", 0));

    [Theory]
    [InlineData("{not valid json")]
    [InlineData("[]")]
    public void TryParse_InvalidJsonShape_ReturnsNull(string json) =>
        Assert.Null(ProposalParser.TryParse(json, "m", 0));

    [Fact]
    public void TryParse_MissingAction_ReturnsNull()
    {
        const string json =
            """{ "confidence": 0.9, "rationaleCode": "no_safe_action" }""";
        Assert.Null(ProposalParser.TryParse(json, "m", 0));
    }

    [Fact]
    public void TryParse_MissingActionType_ReturnsNull()
    {
        const string json =
            """{ "action": { "parameters": {} }, "confidence": 0.9, "rationaleCode": "no_safe_action" }""";
        Assert.Null(ProposalParser.TryParse(json, "m", 0));
    }

    [Theory]
    [InlineData("FlyToMars")]
    [InlineData("click")]
    [InlineData("0")]
    public void TryParse_UnknownOrNonCanonicalActionType_ReturnsNull(string action)
    {
        var json = $$"""
        {
          "action": { "type": "{{action}}", "parameters": {} },
          "confidence": 0.9,
          "rationaleCode": "target_present"
        }
        """;

        Assert.Null(ProposalParser.TryParse(json, "m", 0));
    }

    [Theory]
    [InlineData("Click", "{\"name\":\"Save\"}")]
    [InlineData("Type", "{\"source\":\"clipboard\"}")]
    [InlineData("PressKey", "{\"key\":\"Enter\"}")]
    [InlineData("WaitForElement", "{\"controlType\":\"DataGrid\"}")]
    [InlineData("VerifyElement", "{\"containsFromContext\":\"expected_status\"}")]
    [InlineData("Escalate", "{}")]
    [InlineData("AskOperator", "{}")]
    [InlineData("Log", "{}")]
    public void TryParse_ExactPerActionParameterShape_Accepted(
        string action,
        string parameters)
    {
        var json = $$"""
        {
          "action": { "type": "{{action}}", "parameters": {{parameters}} },
          "confidence": 0.9,
          "rationaleCode": "target_present"
        }
        """;

        Assert.NotNull(ProposalParser.TryParse(json, "m", 0));
    }

    [Theory]
    [InlineData("Click", "{}")]
    [InlineData("Click", "{\"controlType\":\"Button\"}")]
    [InlineData("Click", "{\"name\":\"Save\",\"extra\":\"x\"}")]
    [InlineData("Type", "{\"text\":\"  \"}")]
    [InlineData("PressKey", "{\"key\":\"Enter\",\"name\":\"x\"}")]
    [InlineData("WaitForElement", "{\"name\":\"\"}")]
    [InlineData("VerifyElement", "{}")]
    [InlineData("Escalate", "{\"name\":\"x\"}")]
    [InlineData("AskOperator", "{\"reason\":\"x\"}")]
    [InlineData("Log", "{\"message\":\"x\"}")]
    public void TryParse_MissingExtraOrEmptyPerActionParameter_ReturnsNull(
        string action,
        string parameters)
    {
        var json = $$"""
        {
          "action": { "type": "{{action}}", "parameters": {{parameters}} },
          "confidence": 0.9,
          "rationaleCode": "target_present"
        }
        """;

        Assert.Null(ProposalParser.TryParse(json, "m", 0));
    }

    [Fact]
    public void TryParse_MissingConfidence_ReturnsNull()
    {
        const string json = """
        {
          "action": { "type": "Log", "parameters": {} },
          "rationaleCode": "no_safe_action"
        }
        """;
        Assert.Null(ProposalParser.TryParse(json, "m", 0));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.01)]
    [InlineData(-99.0)]
    public void TryParse_ConfidenceOutOfRange_ReturnsNull(double confidence)
    {
        var json = $$"""
        {
          "action": { "type": "Log", "parameters": {} },
          "confidence": {{confidence}},
          "rationaleCode": "no_safe_action"
        }
        """;
        Assert.Null(ProposalParser.TryParse(json, "m", 0));
    }

    [Fact]
    public void TryParse_MissingRationaleCode_ReturnsNull()
    {
        const string json = """
        {
          "action": { "type": "Log", "parameters": {} },
          "confidence": 1.0
        }
        """;
        Assert.Null(ProposalParser.TryParse(json, "m", 0));
    }

    [Theory]
    [InlineData("target_present", InferenceRationaleCode.TargetPresent)]
    [InlineData("target_absent_wait", InferenceRationaleCode.TargetAbsentWait)]
    [InlineData("workflow_state_ambiguous", InferenceRationaleCode.WorkflowStateAmbiguous)]
    [InlineData("operator_input_required", InferenceRationaleCode.OperatorInputRequired)]
    [InlineData("verification_required", InferenceRationaleCode.VerificationRequired)]
    [InlineData("recovery_step_required", InferenceRationaleCode.RecoveryStepRequired)]
    [InlineData("no_safe_action", InferenceRationaleCode.NoSafeAction)]
    public void TryParse_EachExactRationaleCode_Accepted(
        string wireValue,
        InferenceRationaleCode expected)
    {
        var json = $$"""
        {
          "action": { "type": "Log", "parameters": {} },
          "confidence": 1.0,
          "rationaleCode": "{{wireValue}}"
        }
        """;

        var result = ProposalParser.TryParse(json, "m", 0);

        Assert.NotNull(result);
        Assert.Equal(expected, result.RationaleCode);
        Assert.Equal(wireValue, result.RationaleCode.ToWireValue());
        Assert.NotEmpty(result.RationaleCode.ToOperatorMessage());
    }

    [Theory]
    [InlineData("Target_Present")]
    [InlineData("target present")]
    [InlineData("Save button is visible")]
    [InlineData("Patient: John Smith needs Rx 998877")]
    [InlineData("")]
    public void TryParse_UnknownOrFreeTextRationaleCode_ReturnsNull(string code)
    {
        var json = $$"""
        {
          "action": { "type": "Log", "parameters": {} },
          "confidence": 1.0,
          "rationaleCode": "{{code}}"
        }
        """;

        Assert.Null(ProposalParser.TryParse(json, "m", 0));
    }

    [Fact]
    public void TryParse_LegacyFreeTextRationale_ReturnsNull()
    {
        const string json = """
        {
          "action": { "type": "Log", "parameters": {} },
          "confidence": 1.0,
          "rationale": "Patient: John Smith needs Rx 998877"
        }
        """;

        Assert.Null(ProposalParser.TryParse(json, "m", 0));
    }

    [Fact]
    public void TryParse_UnknownTopLevelField_ReturnsNull()
    {
        const string json = """
        {
          "action": { "type": "Log", "parameters": {} },
          "confidence": 1.0,
          "rationaleCode": "no_safe_action",
          "rationale": "dynamic prose"
        }
        """;

        Assert.Null(ProposalParser.TryParse(json, "m", 0));
    }

    [Fact]
    public void TryParse_JsonInMarkdownFence_Parses()
    {
        const string json = """
        ```json
        { "action": { "type": "Click", "parameters": { "name": "Save" } }, "confidence": 0.9, "rationaleCode": "target_present" }
        ```
        """;

        var result = ProposalParser.TryParse(json, "phi-3.5", 0);

        Assert.NotNull(result);
        Assert.Equal(RuleActionType.Click, result.Action.Type);
    }

    [Fact]
    public void TryParse_JsonInBareFence_Parses()
    {
        const string json = "```\n{ \"action\": { \"type\": \"Log\", \"parameters\": {} }, \"confidence\": 1.0, \"rationaleCode\": \"no_safe_action\" }\n```";

        var result = ProposalParser.TryParse(json, "qwen2.5", 0);

        Assert.NotNull(result);
        Assert.Equal(RuleActionType.Log, result.Action.Type);
    }

    [Fact]
    public void TryParse_LeadingQwenThinkBlock_ParsesAndDiscardsProse()
    {
        const string json = """
        <think>
        Patient John Smith's Save button is visible.
        </think>
        { "action": { "type": "Click", "parameters": { "name": "Save" } }, "confidence": 0.9, "rationaleCode": "target_present" }
        """;

        var result = ProposalParser.TryParse(json, "qwen3-1.7b", 0);

        Assert.NotNull(result);
        Assert.Equal(InferenceRationaleCode.TargetPresent, result.RationaleCode);
        Assert.DoesNotContain(
            "John Smith",
            result.RationaleCode.ToOperatorMessage(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_UnclosedThinkBlock_ReturnsNull()
    {
        const string json = """
        <think>
        unfinished reasoning
        { "action": { "type": "Log", "parameters": {} }, "confidence": 1.0, "rationaleCode": "no_safe_action" }
        """;

        Assert.Null(ProposalParser.TryParse(json, "qwen3-1.7b", 0));
    }

    [Fact]
    public void TryParse_ConfidenceAsString_ReturnsNull()
    {
        const string json = """{ "action": { "type": "Log", "parameters": {} }, "confidence": "high", "rationaleCode": "no_safe_action" }""";
        Assert.Null(ProposalParser.TryParse(json, "m", 0));
    }

    [Fact]
    public void TryParse_ProseWrappedJson_ReturnsNull()
    {
        const string json = "Sure! { \"action\": { \"type\": \"Log\", \"parameters\": {} }, \"confidence\": 1.0, \"rationaleCode\": \"no_safe_action\" } Done!";
        Assert.Null(ProposalParser.TryParse(json, "m", 0));
    }

    [Fact]
    public void TryParse_NonStringParameterValue_ReturnsNull()
    {
        const string json = """
        {
          "action": {
            "type": "Click",
            "parameters": { "name": "Save", "x": 100 }
          },
          "confidence": 0.95,
          "rationaleCode": "target_present"
        }
        """;

        Assert.Null(ProposalParser.TryParse(json, "m", 0));
    }
}
