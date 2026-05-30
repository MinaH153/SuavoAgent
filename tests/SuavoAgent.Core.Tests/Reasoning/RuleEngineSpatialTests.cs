using System;
using System.Collections.Generic;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Core.Reasoning;
using Xunit;

namespace SuavoAgent.Core.Tests.Reasoning;

/// <summary>
/// Spatial visual predicate: a rule can require that on-screen text sits NEAR a control of a
/// given role (Euclidean distance between bounding-box centers within a pixel budget). This
/// grounds reasoning on spatial relationships ("the price label is next to an input"), still
/// app-agnostically. All-of semantics; empty = no constraint.
/// </summary>
public class RuleEngineSpatialTests
{
    private static RuleContext Ctx(IReadOnlyList<TextRegion> text, IReadOnlyList<VisualElement> elements) =>
        new() { SkillId = "vision-observe", ScreenText = text, ScreenElements = elements };

    private static TextRegion T(string text, int x, int y) =>
        new() { Text = text, Bounds = new Rect(x, y, 20, 10), Confidence = 1.0 };

    private static VisualElement E(string role, int x, int y) =>
        new() { Role = role, Name = role, Bounds = new Rect(x, y, 20, 10), Confidence = 1.0 };

    [Fact]
    public void TextNearElement_TextCloseToElementOfRole_Matches()
    {
        var p = new RulePredicate { TextNearElement = new[] { new SpatialTextPredicate("Price", "button", 100) } };
        var ctx = Ctx(new[] { T("Price label", 0, 0) }, new[] { E("button", 10, 0) });
        Assert.True(RuleEngine.PredicateMatches(p, ctx));
    }

    [Fact]
    public void TextNearElement_ElementTooFar_DoesNotMatch()
    {
        var p = new RulePredicate { TextNearElement = new[] { new SpatialTextPredicate("Price", "button", 100) } };
        var ctx = Ctx(new[] { T("Price label", 0, 0) }, new[] { E("button", 500, 500) });
        Assert.False(RuleEngine.PredicateMatches(p, ctx));
    }

    [Fact]
    public void TextNearElement_WrongRole_DoesNotMatch()
    {
        var p = new RulePredicate { TextNearElement = new[] { new SpatialTextPredicate("Price", "button", 100) } };
        var ctx = Ctx(new[] { T("Price label", 0, 0) }, new[] { E("input", 10, 0) });
        Assert.False(RuleEngine.PredicateMatches(p, ctx));
    }

    [Fact]
    public void TextNearElement_TextAbsent_DoesNotMatch()
    {
        var p = new RulePredicate { TextNearElement = new[] { new SpatialTextPredicate("Price", "button", 100) } };
        var ctx = Ctx(new[] { T("Quantity", 0, 0) }, new[] { E("button", 10, 0) });
        Assert.False(RuleEngine.PredicateMatches(p, ctx));
    }

    [Fact]
    public void TextNearElement_RoleAndTextCaseInsensitive_Matches()
    {
        var p = new RulePredicate { TextNearElement = new[] { new SpatialTextPredicate("price", "BUTTON", 100) } };
        var ctx = Ctx(new[] { T("Price label", 0, 0) }, new[] { E("button", 10, 0) });
        Assert.True(RuleEngine.PredicateMatches(p, ctx));
    }

    [Fact]
    public void TextNearElement_Empty_NoConstraint()
    {
        var p = new RulePredicate { TextNearElement = Array.Empty<SpatialTextPredicate>() };
        Assert.True(RuleEngine.PredicateMatches(p, Ctx(Array.Empty<TextRegion>(), Array.Empty<VisualElement>())));
    }

    [Fact]
    public void TextNearElement_NegativeDistance_FailsClosed()
    {
        // A negative distance budget is meaningless — must never match, even with co-located items.
        var p = new RulePredicate { TextNearElement = new[] { new SpatialTextPredicate("Price", "button", -1) } };
        Assert.False(RuleEngine.PredicateMatches(p, Ctx(new[] { T("Price", 0, 0) }, new[] { E("button", 0, 0) })));
    }

    [Fact]
    public void TextNearElement_EmptyTextOrRole_FailsClosed()
    {
        var emptyText = new RulePredicate { TextNearElement = new[] { new SpatialTextPredicate("", "button", 100) } };
        var emptyRole = new RulePredicate { TextNearElement = new[] { new SpatialTextPredicate("Price", "", 100) } };
        var ctx = Ctx(new[] { T("Price", 0, 0) }, new[] { E("button", 0, 0) });
        Assert.False(RuleEngine.PredicateMatches(emptyText, ctx));
        Assert.False(RuleEngine.PredicateMatches(emptyRole, ctx));
    }
}
