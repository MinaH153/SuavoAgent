using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Reasoning;
using Xunit;

namespace SuavoAgent.Core.Tests.Reasoning;

public class ActionGrammarTests
{
    [Fact]
    public void BuildProposalGrammar_WithSingleAction_ProducesValidGrammar()
    {
        var grammar = ActionGrammar.BuildProposalGrammar(
            new HashSet<RuleActionType> { RuleActionType.Click });

        Assert.True(ActionGrammar.LooksWellFormed(grammar));
        Assert.Contains("root ::= proposal", grammar);
        Assert.Contains("\"\\\"Click\\\"\"", grammar);
    }

    [Fact]
    public void BuildProposalGrammar_WithMultipleActions_ProducesAlternation()
    {
        var grammar = ActionGrammar.BuildProposalGrammar(
            new HashSet<RuleActionType> { RuleActionType.Click, RuleActionType.PressKey });

        Assert.True(ActionGrammar.LooksWellFormed(grammar));
        Assert.Contains("\"\\\"Click\\\"\"", grammar);
        Assert.Contains("\"\\\"PressKey\\\"\"", grammar);
        Assert.Contains(" | ", grammar);
    }

    [Fact]
    public void BuildProposalGrammar_EmptySet_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            ActionGrammar.BuildProposalGrammar(new HashSet<RuleActionType>()));
    }

    [Fact]
    public void BuildProposalGrammar_AllActions_WellFormed()
    {
        var allTypes = new HashSet<RuleActionType>(Enum.GetValues<RuleActionType>());
        var grammar = ActionGrammar.BuildProposalGrammar(allTypes);

        Assert.True(ActionGrammar.LooksWellFormed(grammar));
        foreach (var t in allTypes)
            Assert.Contains($"\"\\\"{t}\\\"\"", grammar);
    }

    [Fact]
    public void BuildProposalGrammar_ActionOrderingDeterministic()
    {
        var types = new HashSet<RuleActionType> { RuleActionType.Log, RuleActionType.Click };

        var a = ActionGrammar.BuildProposalGrammar(types);
        var b = ActionGrammar.BuildProposalGrammar(types);

        Assert.Equal(a, b); // stable ordering so llama.cpp can cache compiled grammar
    }

    [Fact]
    public void LooksWellFormed_RejectsEmpty()
    {
        Assert.False(ActionGrammar.LooksWellFormed(""));
        Assert.False(ActionGrammar.LooksWellFormed("   "));
    }

    [Fact]
    public void LooksWellFormed_RejectsGrammarWithoutRoot()
    {
        Assert.False(ActionGrammar.LooksWellFormed("proposal ::= \"foo\""));
    }

    [Fact]
    public void LooksWellFormed_RejectsUnbalancedQuotes()
    {
        Assert.False(ActionGrammar.LooksWellFormed("root ::= \"unclosed"));
    }

    [Fact]
    public void LooksWellFormed_RejectsEmptyRuleBody()
    {
        Assert.False(ActionGrammar.LooksWellFormed("root ::= "));
    }
}
