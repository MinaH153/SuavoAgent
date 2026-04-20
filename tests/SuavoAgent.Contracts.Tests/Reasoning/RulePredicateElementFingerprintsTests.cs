using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Reasoning;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Reasoning;

/// <summary>
/// The fix for Codex Area 2 (BLOCK): RulePredicate.VisibleElements was a flat
/// string list, so post-action verification could not distinguish a wrong
/// screen that happened to reuse an element name. v3.12 adds
/// ElementFingerprints — structural {ControlType, AutomationId, ClassName}
/// triples — that must all be present in the RuleContext's fingerprint list
/// for the predicate to satisfy.
///
/// Behavior to lock in:
///   1. Empty fingerprint list = legacy predicate behaviour (always satisfied).
///   2. Non-empty fingerprint list = every required signature must match at
///      least one signature in the context (order-insensitive).
///   3. Signatures compare under ElementSignature.MatchesStructurally.
/// </summary>
public class RulePredicateElementFingerprintsTests
{
    private static RuleContext CtxWith(params ElementSignature[] sigs) => new()
    {
        SkillId = "test",
        ProcessName = "Test.exe",
        ElementFingerprints = sigs,
    };

    [Fact]
    public void RulePredicate_DefaultsToEmptyFingerprints()
    {
        var p = new RulePredicate();
        Assert.NotNull(p.ElementFingerprints);
        Assert.Empty(p.ElementFingerprints);
    }

    [Fact]
    public void RuleContext_DefaultsToEmptyFingerprints()
    {
        var c = new RuleContext { SkillId = "s" };
        Assert.NotNull(c.ElementFingerprints);
        Assert.Empty(c.ElementFingerprints);
    }

    [Fact]
    public void EmptyFingerprints_AlwaysSatisfy()
    {
        var p = new RulePredicate();
        var c = CtxWith();
        Assert.True(SuavoAgent.Contracts.Reasoning.PredicateFingerprintMatcher.SatisfiedBy(p, c));
    }

    [Fact]
    public void AllRequired_Present_Matches()
    {
        var a = new ElementSignature("Button", "btnOk", "WinForms.Button");
        var b = new ElementSignature("Edit", "txtSearch", null);

        var p = new RulePredicate { ElementFingerprints = new[] { a, b } };
        var c = CtxWith(a, b,
            new ElementSignature("MenuItem", "miFile", "WinForms.MenuItem"));

        Assert.True(PredicateFingerprintMatcher.SatisfiedBy(p, c));
    }

    [Fact]
    public void MissingRequired_DoesNotMatch()
    {
        var a = new ElementSignature("Button", "btnOk", "WinForms.Button");
        var b = new ElementSignature("Edit", "txtSearch", null);

        var p = new RulePredicate { ElementFingerprints = new[] { a, b } };
        var c = CtxWith(a);

        Assert.False(PredicateFingerprintMatcher.SatisfiedBy(p, c));
    }

    [Fact]
    public void OrderInsensitive()
    {
        var a = new ElementSignature("Button", "btnOk", "WinForms.Button");
        var b = new ElementSignature("Edit", "txtSearch", null);

        var p = new RulePredicate { ElementFingerprints = new[] { a, b } };
        var c = CtxWith(b, a);
        Assert.True(PredicateFingerprintMatcher.SatisfiedBy(p, c));
    }

    [Fact]
    public void AutomationIdCaseInsensitive()
    {
        var required = new ElementSignature("Button", "btnOk", "WinForms.Button");
        var actual = new ElementSignature("Button", "BTNOK", "WinForms.Button");

        var p = new RulePredicate { ElementFingerprints = new[] { required } };
        var c = CtxWith(actual);
        Assert.True(PredicateFingerprintMatcher.SatisfiedBy(p, c));
    }

    [Fact]
    public void ClassNameMismatch_DoesNotMatch()
    {
        var required = new ElementSignature("Button", "btnOk", "WinForms.Button");
        var actual = new ElementSignature("Button", "btnOk", "Wpf.Button");

        var p = new RulePredicate { ElementFingerprints = new[] { required } };
        var c = CtxWith(actual);
        Assert.False(PredicateFingerprintMatcher.SatisfiedBy(p, c));
    }

    [Fact]
    public void NullClassNameOnContextSide_MatchesRequired()
    {
        // Null-tolerant: context signature without ClassName still matches a
        // required signature that specifies one. Defends against environments
        // that scrub ClassName differently.
        var required = new ElementSignature("Button", "btnOk", "WinForms.Button");
        var actual = new ElementSignature("Button", "btnOk", null);

        var p = new RulePredicate { ElementFingerprints = new[] { required } };
        var c = CtxWith(actual);
        Assert.True(PredicateFingerprintMatcher.SatisfiedBy(p, c));
    }

    [Fact]
    public void ControlTypeMismatch_DoesNotMatch()
    {
        // This is the exact scenario Codex flagged — same AutomationId on a
        // different control type must not confuse the predicate.
        var required = new ElementSignature("Button", "Submit", null);
        var actual = new ElementSignature("MenuItem", "Submit", null);

        var p = new RulePredicate { ElementFingerprints = new[] { required } };
        var c = CtxWith(actual);
        Assert.False(PredicateFingerprintMatcher.SatisfiedBy(p, c));
    }

    // ─────────── K-of-M relaxation (spec §2.3 MatchRatio) ───────────

    [Fact]
    public void MinRequiredCount_KofM_PartialMatchSatisfies()
    {
        // Template extracted K=4 on a 5-element screen. Runtime presents 4 of 5.
        var a = new ElementSignature("Button", "a", null);
        var b = new ElementSignature("Button", "b", null);
        var c = new ElementSignature("Button", "c", null);
        var d = new ElementSignature("Button", "d", null);
        var e = new ElementSignature("Button", "e", null);

        var p = new RulePredicate
        {
            ElementFingerprints = new[] { a, b, c, d, e },
            MinRequiredCount = 4,
        };
        var ctx = CtxWith(a, b, c, d); // only 4 of 5 visible
        Assert.True(PredicateFingerprintMatcher.SatisfiedBy(p, ctx));
    }

    [Fact]
    public void MinRequiredCount_KofM_BelowThresholdFails()
    {
        var a = new ElementSignature("Button", "a", null);
        var b = new ElementSignature("Button", "b", null);
        var c = new ElementSignature("Button", "c", null);
        var d = new ElementSignature("Button", "d", null);
        var e = new ElementSignature("Button", "e", null);

        var p = new RulePredicate
        {
            ElementFingerprints = new[] { a, b, c, d, e },
            MinRequiredCount = 4,
        };
        var ctx = CtxWith(a, b, c); // 3 < 4 required
        Assert.False(PredicateFingerprintMatcher.SatisfiedBy(p, ctx));
    }

    [Fact]
    public void MinRequiredCount_Null_PreservesAllOfSemantics()
    {
        var a = new ElementSignature("Button", "a", null);
        var b = new ElementSignature("Button", "b", null);

        var p = new RulePredicate
        {
            ElementFingerprints = new[] { a, b },
            MinRequiredCount = null,
        };
        Assert.False(PredicateFingerprintMatcher.SatisfiedBy(p, CtxWith(a)));
        Assert.True(PredicateFingerprintMatcher.SatisfiedBy(p, CtxWith(a, b)));
    }
}
