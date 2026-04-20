using System;
using System.Text.Json;
using SuavoAgent.Contracts.Behavioral;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Behavioral;

/// <summary>
/// ElementSignature is the cross-installation UIA match atom. Must come exclusively
/// from the GREEN tier of UiaPropertyScrubber — never embeds Name or NameHash.
/// Used by both WorkflowTemplate steps and RulePredicate.ElementFingerprints.
/// </summary>
public class ElementSignatureTests
{
    [Fact]
    public void Construction_RequiresNonEmptyAutomationId()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new ElementSignature("Button", "", "WinForms.Button"));
        Assert.Contains("AutomationId", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Construction_RequiresNonEmptyControlType()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new ElementSignature("", "btnOk", "WinForms.Button"));
        Assert.Contains("ControlType", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Construction_NullClassName_Allowed()
    {
        var sig = new ElementSignature("Button", "btnOk", null);
        Assert.Null(sig.ClassName);
    }

    [Fact]
    public void Equality_AutomationIdCaseInsensitive()
    {
        var a = new ElementSignature("Button", "btnOk", "WinForms.Button");
        var b = new ElementSignature("Button", "BTNOK", "WinForms.Button");
        Assert.True(a.MatchesStructurally(b));
    }

    [Fact]
    public void Equality_ControlTypeExactCaseSensitive()
    {
        var a = new ElementSignature("Button", "btnOk", "WinForms.Button");
        var b = new ElementSignature("button", "btnOk", "WinForms.Button");
        Assert.False(a.MatchesStructurally(b));
    }

    [Fact]
    public void Matching_NullClassNameOnEitherSide_IsTolerant()
    {
        // When either side's ClassName is null, we treat ClassName as "unspecified"
        // and allow the match — prevents templates that observed a null-class
        // environment from failing against an enriched one (and vice versa).
        var templateSide = new ElementSignature("Button", "btnOk", null);
        var liveSide = new ElementSignature("Button", "btnOk", "WinForms.Button");
        Assert.True(templateSide.MatchesStructurally(liveSide));
        Assert.True(liveSide.MatchesStructurally(templateSide));
    }

    [Fact]
    public void Matching_ClassNameDifferent_DoesNotMatch()
    {
        // When both sides declare a ClassName, they must agree. This is what
        // prevents two screens with the same AutomationId but different framework
        // ownership from colliding.
        var a = new ElementSignature("Button", "btnOk", "WinForms.Button");
        var b = new ElementSignature("Button", "btnOk", "Wpf.Button");
        Assert.False(a.MatchesStructurally(b));
    }

    [Fact]
    public void CanonicalRepr_Deterministic()
    {
        // Canonical representation is used inside the WorkflowTemplate StepsHash.
        // A tiny change in format cascades into mismatched template IDs and version
        // churn, so lock the format explicitly.
        var sig = new ElementSignature("Button", "btnOk", "WinForms.Button");
        Assert.Equal("Button|btnOk|WinForms.Button", sig.CanonicalRepr);
    }

    [Fact]
    public void CanonicalRepr_NullClassName_EmptyTrailing()
    {
        var sig = new ElementSignature("Button", "btnOk", null);
        Assert.Equal("Button|btnOk|", sig.CanonicalRepr);
    }

    [Fact]
    public void JsonRoundTrip_PreservesAllFields()
    {
        var sig = new ElementSignature("Button", "btnOk", "WinForms.Button");
        var json = JsonSerializer.Serialize(sig);
        var parsed = JsonSerializer.Deserialize<ElementSignature>(json);
        Assert.NotNull(parsed);
        Assert.Equal(sig.ControlType, parsed!.ControlType);
        Assert.Equal(sig.AutomationId, parsed.AutomationId);
        Assert.Equal(sig.ClassName, parsed.ClassName);
    }

    [Fact]
    public void ContainsNoPhiFields_StaticShape()
    {
        // Reflection guard: if anyone adds Name, NameHash, Value, or other YELLOW/RED
        // fields to ElementSignature, this test fails — keeps the GREEN-only invariant
        // from silently drifting.
        var type = typeof(ElementSignature);
        var forbidden = new[] { "Name", "NameHash", "Value", "Text", "Selection", "HelpText", "ItemStatus" };
        foreach (var prop in type.GetProperties())
        {
            Assert.DoesNotContain(prop.Name, forbidden);
        }
    }
}
