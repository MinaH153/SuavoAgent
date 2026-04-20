using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Reasoning;
using Xunit;

namespace SuavoAgent.Core.Tests.Reasoning;

/// <summary>
/// Integration tests for the Codex Area 2 BLOCK fix: structural fingerprint
/// gating now rides inside <see cref="RuleEngine.PredicateMatches"/>.
/// Legacy rules with empty fingerprint lists must behave exactly as before;
/// new rules that declare fingerprints must reject context drift.
/// </summary>
public class RuleEngineFingerprintTests
{
    private static Rule MakeRule(string id, string skill, RulePredicate when) => new()
    {
        Id = id,
        SkillId = skill,
        Priority = 100,
        When = when,
        Then = new[]
        {
            new RuleActionSpec { Type = RuleActionType.Log },
        },
    };

    [Fact]
    public void LegacyRule_NoFingerprints_StillMatches()
    {
        var when = new RulePredicate
        {
            ProcessName = "PioneerPharmacy*",
            VisibleElements = new[] { "Item" },
        };
        var rule = MakeRule("legacy.any", "pricing-lookup", when);
        var engine = new RuleEngine(new[] { rule }, NullLogger<RuleEngine>.Instance);

        var ctx = new RuleContext
        {
            SkillId = "pricing-lookup",
            ProcessName = "PioneerPharmacy.exe",
            VisibleElements = new HashSet<string> { "Item" },
        };
        Assert.Equal(MatchOutcome.Matched, engine.Evaluate(ctx).Outcome);
    }

    [Fact]
    public void FingerprintRule_AllFingerprintsPresent_Matches()
    {
        var when = new RulePredicate
        {
            ProcessName = "PioneerPharmacy*",
            ElementFingerprints = new[]
            {
                new ElementSignature("Button", "btnApprove", "WinForms.Button"),
                new ElementSignature("Edit", "txtRxNumber", null),
            },
        };
        var rule = MakeRule("fp.all", "pricing-lookup", when);
        var engine = new RuleEngine(new[] { rule }, NullLogger<RuleEngine>.Instance);

        var ctx = new RuleContext
        {
            SkillId = "pricing-lookup",
            ProcessName = "PioneerPharmacy.exe",
            ElementFingerprints = new[]
            {
                new ElementSignature("Button", "btnApprove", "WinForms.Button"),
                new ElementSignature("Edit", "txtRxNumber", "WinForms.TextBox"),
                new ElementSignature("Button", "btnCancel", "WinForms.Button"),
            },
        };
        Assert.Equal(MatchOutcome.Matched, engine.Evaluate(ctx).Outcome);
    }

    [Fact]
    public void FingerprintRule_MissingFingerprint_DoesNotMatch()
    {
        var when = new RulePredicate
        {
            ProcessName = "PioneerPharmacy*",
            ElementFingerprints = new[]
            {
                new ElementSignature("Button", "btnApprove", null),
                new ElementSignature("Edit", "txtRxNumber", null),
            },
        };
        var rule = MakeRule("fp.missing", "pricing-lookup", when);
        var engine = new RuleEngine(new[] { rule }, NullLogger<RuleEngine>.Instance);

        var ctx = new RuleContext
        {
            SkillId = "pricing-lookup",
            ProcessName = "PioneerPharmacy.exe",
            ElementFingerprints = new[]
            {
                new ElementSignature("Button", "btnApprove", null),
                // No txtRxNumber — predicate should reject
            },
        };
        Assert.Equal(MatchOutcome.NoMatch, engine.Evaluate(ctx).Outcome);
    }

    [Fact]
    public void FingerprintRule_WrongControlType_DoesNotMatch()
    {
        // The exact Codex scenario: same AutomationId on a different control
        // must not be confused with the intended target.
        var when = new RulePredicate
        {
            ElementFingerprints = new[]
            {
                new ElementSignature("Button", "Submit", null),
            },
        };
        var rule = MakeRule("fp.wrongtype", "pricing-lookup", when);
        var engine = new RuleEngine(new[] { rule }, NullLogger<RuleEngine>.Instance);

        var ctx = new RuleContext
        {
            SkillId = "pricing-lookup",
            ElementFingerprints = new[]
            {
                new ElementSignature("MenuItem", "Submit", null),
            },
        };
        Assert.Equal(MatchOutcome.NoMatch, engine.Evaluate(ctx).Outcome);
    }

    [Fact]
    public void FingerprintRule_ContextWithoutFingerprints_DoesNotMatch()
    {
        // Safety property: a rule that demands structural fingerprints must
        // not accept a context that carries none (e.g. a caller that hasn't
        // migrated yet).
        var when = new RulePredicate
        {
            ElementFingerprints = new[]
            {
                new ElementSignature("Button", "btnApprove", null),
            },
        };
        var rule = MakeRule("fp.nocontext", "pricing-lookup", when);
        var engine = new RuleEngine(new[] { rule }, NullLogger<RuleEngine>.Instance);

        var ctx = new RuleContext
        {
            SkillId = "pricing-lookup",
            // No ElementFingerprints
        };
        Assert.Equal(MatchOutcome.NoMatch, engine.Evaluate(ctx).Outcome);
    }

    [Fact]
    public void BothGatesMustPass_NameListSatisfiedButFingerprintFails()
    {
        // Even if the legacy name-list gate is satisfied, the fingerprint
        // gate still runs. This catches templates that share labels but not
        // structure.
        var when = new RulePredicate
        {
            VisibleElements = new[] { "Approve" },
            ElementFingerprints = new[]
            {
                new ElementSignature("Button", "btnApprove", "WinForms.Button"),
            },
        };
        var rule = MakeRule("fp.both", "pricing-lookup", when);
        var engine = new RuleEngine(new[] { rule }, NullLogger<RuleEngine>.Instance);

        var ctx = new RuleContext
        {
            SkillId = "pricing-lookup",
            VisibleElements = new HashSet<string> { "Approve" },
            ElementFingerprints = new[]
            {
                // Same AutomationId but rendered as a MenuItem on the wrong screen
                new ElementSignature("MenuItem", "btnApprove", "WinForms.MenuItem"),
            },
        };
        Assert.Equal(MatchOutcome.NoMatch, engine.Evaluate(ctx).Outcome);
    }
}
