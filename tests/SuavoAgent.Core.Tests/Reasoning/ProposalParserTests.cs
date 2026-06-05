using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Reasoning;
using Xunit;

namespace SuavoAgent.Core.Tests.Reasoning;

public class ProposalParserTests
{
    // --- Happy path ----------------------------------------------------------

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
          "rationale": "Save button visible and skill expects to save"
        }
        """;

        var result = ProposalParser.TryParse(json, "llama-test", latencyMs: 250);

        Assert.NotNull(result);
        Assert.Equal(RuleActionType.Click, result.Action.Type);
        Assert.Equal("Save", result.Action.Parameters["name"]);
        Assert.Equal(0.95, result.Confidence);
        Assert.Equal("llama-test", result.ModelId);
        Assert.Equal(250, result.LatencyMs);
        Assert.Contains("Save", result.Rationale);
    }

    [Fact]
    public void TryParse_ParametersEmpty_Allowed()
    {
        const string json = """
        {
          "action": { "type": "Log", "parameters": {} },
          "confidence": 1.0,
          "rationale": "marker"
        }
        """;
        var result = ProposalParser.TryParse(json, "m", 0);

        Assert.NotNull(result);
        Assert.Empty(result.Action.Parameters);
    }

    [Fact]
    public void TryParse_TrimsWhitespaceAroundJson()
    {
        const string json = "\n  { \"action\": { \"type\": \"Log\", \"parameters\": {} }, \"confidence\": 1.0, \"rationale\": \"r\" }\n";
        var result = ProposalParser.TryParse(json, "m", 0);
        Assert.NotNull(result);
    }

    // --- Schema violations → null -------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryParse_EmptyInput_ReturnsNull(string? input)
    {
        Assert.Null(ProposalParser.TryParse(input!, "m", 0));
    }

    [Fact]
    public void TryParse_MalformedJson_ReturnsNull()
    {
        Assert.Null(ProposalParser.TryParse("{not valid json", "m", 0));
    }

    [Fact]
    public void TryParse_RootIsArray_ReturnsNull()
    {
        Assert.Null(ProposalParser.TryParse("[]", "m", 0));
    }

    [Fact]
    public void TryParse_MissingAction_ReturnsNull()
    {
        const string json = """{ "confidence": 0.9, "rationale": "r" }""";
        Assert.Null(ProposalParser.TryParse(json, "m", 0));
    }

    [Fact]
    public void TryParse_MissingActionType_ReturnsNull()
    {
        const string json = """{ "action": { "parameters": {} }, "confidence": 0.9, "rationale": "r" }""";
        Assert.Null(ProposalParser.TryParse(json, "m", 0));
    }

    [Fact]
    public void TryParse_UnknownActionType_ReturnsNull()
    {
        const string json = """
        {
          "action": { "type": "FlyToMars", "parameters": {} },
          "confidence": 0.9,
          "rationale": "r"
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
          "rationale": "r"
        }
        """;
        Assert.Null(ProposalParser.TryParse(json, "m", 0));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.01)]
    [InlineData(-99.0)]
    public void TryParse_ConfidenceOutOfRange_ReturnsNull(double conf)
    {
        var json = $$"""
        {
          "action": { "type": "Log", "parameters": {} },
          "confidence": {{conf}},
          "rationale": "r"
        }
        """;
        Assert.Null(ProposalParser.TryParse(json, "m", 0));
    }

    // --- Rationale handling -------------------------------------------------

    [Fact]
    public void TryParse_OptionalRationale_Accepted()
    {
        const string json = """
        {
          "action": { "type": "Log", "parameters": {} },
          "confidence": 1.0
        }
        """;
        var result = ProposalParser.TryParse(json, "m", 0);
        Assert.NotNull(result);
        Assert.Null(result.Rationale);
    }

    [Fact]
    public void TryParse_LongRationale_CappedAt500Chars()
    {
        var longRationale = new string('x', 2000);
        var json = $$"""
        {
          "action": { "type": "Log", "parameters": {} },
          "confidence": 1.0,
          "rationale": "{{longRationale}}"
        }
        """;
        var result = ProposalParser.TryParse(json, "m", 0);
        Assert.NotNull(result);
        Assert.Equal(500, result.Rationale!.Length);
    }

    // --- WS2: parser is the load-bearing JSON guarantee ---------------------
    // The win-x64 llama.cpp GBNF grammar is a no-op (documented 2026-06-05), so
    // the template + this parser are the real schema defense. Instruct models
    // (Phi-3.5 / Qwen2.5) sometimes wrap JSON in a markdown code fence — strip it.

    [Fact]
    public void TryParse_JsonInMarkdownFence_Parses()
    {
        const string json = """
        ```json
        { "action": { "type": "Click", "parameters": { "name": "Save" } }, "confidence": 0.9, "rationale": "r" }
        ```
        """;
        var result = ProposalParser.TryParse(json, "phi-3.5", 0);
        Assert.NotNull(result);
        Assert.Equal(RuleActionType.Click, result.Action.Type);
    }

    [Fact]
    public void TryParse_JsonInBareFence_Parses()
    {
        const string json = "```\n{ \"action\": { \"type\": \"Log\", \"parameters\": {} }, \"confidence\": 1.0 }\n```";
        var result = ProposalParser.TryParse(json, "qwen2.5", 0);
        Assert.NotNull(result);
        Assert.Equal(RuleActionType.Log, result.Action.Type);
    }

    [Fact]
    public void TryParse_ConfidenceAsString_ReturnsNull()
    {
        // A model that emits "confidence":"high" must NOT be trusted — fail closed.
        const string json = """{ "action": { "type": "Log", "parameters": {} }, "confidence": "high" }""";
        Assert.Null(ProposalParser.TryParse(json, "m", 0));
    }

    [Fact]
    public void TryParse_ProseWrappedJson_ReturnsNull()
    {
        // Strict contract: free prose around the object is not silently mined for
        // JSON — it escalates (returns null) rather than guessing the model's intent.
        const string json = "Sure! Here is my decision: { \"action\": { \"type\": \"Log\", \"parameters\": {} }, \"confidence\": 1.0 } Hope that helps!";
        Assert.Null(ProposalParser.TryParse(json, "m", 0));
    }

    // --- Parameters with non-string values ----------------------------------

    [Fact]
    public void TryParse_NonStringParameterValues_Skipped()
    {
        // Grammar constrains parameters to strings, but defense-in-depth.
        const string json = """
        {
          "action": {
            "type": "Click",
            "parameters": {
              "name": "Save",
              "x": 100
            }
          },
          "confidence": 0.95,
          "rationale": "r"
        }
        """;
        var result = ProposalParser.TryParse(json, "m", 0);
        Assert.NotNull(result);
        Assert.Equal("Save", result.Action.Parameters["name"]);
        Assert.DoesNotContain("x", result.Action.Parameters.Keys);
    }
}
