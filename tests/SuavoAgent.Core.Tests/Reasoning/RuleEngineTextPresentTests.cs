using System;
using System.Linq;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Core.Reasoning;
using Xunit;

namespace SuavoAgent.Core.Tests.Reasoning;

/// <summary>
/// The first visual predicate: a rule can fire on what's literally on screen, app-agnostically.
/// TextPresent = all listed substrings must appear (case-insensitive) in some screen text region
/// (all-of, mirroring VisibleElements). Empty = no constraint (back-compat).
/// </summary>
public class RuleEngineTextPresentTests
{
    private static RuleContext Ctx(params string[] screenTexts) => new()
    {
        SkillId = "vision-observe",
        ScreenText = screenTexts
            .Select(t => new TextRegion { Text = t, Bounds = new Rect(0, 0, 1, 1), Confidence = 1.0 })
            .ToList(),
    };

    [Fact]
    public void TextPresent_SubstringInScreenText_Matches()
    {
        var p = new RulePredicate { TextPresent = new[] { "Pricing" } };
        Assert.True(RuleEngine.PredicateMatches(p, Ctx("Pricing tab", "Home")));
    }

    [Fact]
    public void TextPresent_IsCaseInsensitive()
    {
        var p = new RulePredicate { TextPresent = new[] { "pricing" } };
        Assert.True(RuleEngine.PredicateMatches(p, Ctx("Pricing tab")));
    }

    [Fact]
    public void TextPresent_Missing_DoesNotMatch()
    {
        var p = new RulePredicate { TextPresent = new[] { "Pricing" } };
        Assert.False(RuleEngine.PredicateMatches(p, Ctx("Home", "Patients")));
    }

    [Fact]
    public void TextPresent_AllOf_RequiresEverySubstring()
    {
        var p = new RulePredicate { TextPresent = new[] { "A", "B" } };
        Assert.False(RuleEngine.PredicateMatches(p, Ctx("A only")));
        Assert.True(RuleEngine.PredicateMatches(p, Ctx("A only", "B here")));
    }

    [Fact]
    public void TextPresent_Empty_NoConstraint()
    {
        var p = new RulePredicate { TextPresent = Array.Empty<string>() };
        Assert.True(RuleEngine.PredicateMatches(p, Ctx()));
    }
}
