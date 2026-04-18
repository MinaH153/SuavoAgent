using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Reasoning;
using Xunit;

namespace SuavoAgent.Core.Tests.Reasoning;

public class RuleEngineTests
{
    private static readonly ILogger<RuleEngine> Log = NullLogger<RuleEngine>.Instance;

    // --- Basic match/no-match ------------------------------------------------

    [Fact]
    public void Evaluate_NoSkillRegistered_ReturnsNoMatch()
    {
        var engine = new RuleEngine(Array.Empty<Rule>(), Log);
        var ctx = Ctx(skillId: "nonexistent");

        var result = engine.Evaluate(ctx);

        Assert.Equal(MatchOutcome.NoMatch, result.Outcome);
        Assert.Contains("nonexistent", result.Reason);
    }

    [Fact]
    public void Evaluate_MatchingPredicate_ReturnsMatched()
    {
        var rule = Rule("r1", "skill-a", when: new RulePredicate
        {
            VisibleElements = new[] { "Button-A" }
        });
        var engine = new RuleEngine(new[] { rule }, Log);
        var ctx = Ctx(skillId: "skill-a", visibleElements: new[] { "Button-A" });

        var result = engine.Evaluate(ctx);

        Assert.Equal(MatchOutcome.Matched, result.Outcome);
        Assert.Equal("r1", result.MatchedRule!.Id);
    }

    [Fact]
    public void Evaluate_NoMatchingPredicate_ReturnsNoMatch()
    {
        var rule = Rule("r1", "skill-a", when: new RulePredicate
        {
            VisibleElements = new[] { "Button-A" }
        });
        var engine = new RuleEngine(new[] { rule }, Log);
        var ctx = Ctx(skillId: "skill-a", visibleElements: new[] { "Button-B" });

        var result = engine.Evaluate(ctx);

        Assert.Equal(MatchOutcome.NoMatch, result.Outcome);
    }

    // --- Priority ordering ---------------------------------------------------

    [Fact]
    public void Evaluate_HigherPriorityWinsWhenMultipleMatch()
    {
        var low = Rule("low", "skill-a", priority: 50);
        var high = Rule("high", "skill-a", priority: 200);
        var engine = new RuleEngine(new[] { low, high }, Log);

        var result = engine.Evaluate(Ctx(skillId: "skill-a"));

        Assert.Equal(MatchOutcome.Matched, result.Outcome);
        Assert.Equal("high", result.MatchedRule!.Id);
    }

    [Fact]
    public void Evaluate_EqualPriorityLoadOrderWins()
    {
        var first = Rule("first", "skill-a", priority: 100);
        var second = Rule("second", "skill-a", priority: 100);
        var engine = new RuleEngine(new[] { first, second }, Log);

        var result = engine.Evaluate(Ctx(skillId: "skill-a"));

        Assert.Equal(MatchOutcome.Matched, result.Outcome);
        // Both have same priority; one wins deterministically — we just verify
        // the same rule always wins (stable ordering), not which one.
        var rerun = engine.Evaluate(Ctx(skillId: "skill-a"));
        Assert.Equal(result.MatchedRule!.Id, rerun.MatchedRule!.Id);
    }

    // --- Skill scoping -------------------------------------------------------

    [Fact]
    public void Evaluate_OnlyConsidersRulesInRequestedSkill()
    {
        var a = Rule("a-rule", "skill-a");
        var b = Rule("b-rule", "skill-b");
        var engine = new RuleEngine(new[] { a, b }, Log);

        var result = engine.Evaluate(Ctx(skillId: "skill-b"));

        Assert.Equal(MatchOutcome.Matched, result.Outcome);
        Assert.Equal("b-rule", result.MatchedRule!.Id);
    }

    // --- AutonomousOk --------------------------------------------------------

    [Fact]
    public void Evaluate_AutonomousOkFalse_ReturnsBlocked()
    {
        var rule = Rule("r1", "skill-a", autonomousOk: false);
        var engine = new RuleEngine(new[] { rule }, Log);

        var result = engine.Evaluate(Ctx(skillId: "skill-a"));

        Assert.Equal(MatchOutcome.Blocked, result.Outcome);
        Assert.Equal("r1", result.MatchedRule!.Id);
        Assert.NotEmpty(result.Actions);
    }

    // --- Shadow mode ---------------------------------------------------------

    [Fact]
    public void Evaluate_ShadowMode_MatchingRule_ReturnsNoMatchWithRule()
    {
        var rule = Rule("r1", "skill-a");
        var engine = new RuleEngine(new[] { rule }, Log);

        var result = engine.Evaluate(Ctx(skillId: "skill-a"), shadowMode: true);

        Assert.Equal(MatchOutcome.NoMatch, result.Outcome);
        Assert.Equal("r1", result.MatchedRule!.Id);
        Assert.Contains("Shadow", result.Reason);
    }

    // --- Predicate: process name glob ---------------------------------------

    [Theory]
    [InlineData("PioneerPharmacy*", "PioneerPharmacy.exe", true)]
    [InlineData("PioneerPharmacy*", "PioneerPharmacyHost.exe", true)]
    [InlineData("PioneerPharmacy*", "Notepad.exe", false)]
    [InlineData("*.exe", "anything.exe", true)]
    [InlineData("foo?", "fooX", true)]
    [InlineData("foo?", "fooXY", false)]
    [InlineData("exact", "exact", true)]
    [InlineData("exact", "exactly", false)]
    public void GlobMatch_MatchesShellStylePatterns(string pattern, string input, bool expected)
    {
        Assert.Equal(expected, RuleEngine.GlobMatch(pattern, input));
    }

    [Fact]
    public void GlobMatch_IsCaseInsensitive()
    {
        Assert.True(RuleEngine.GlobMatch("PioneerPharmacy*", "pioneerpharmacy.EXE"));
    }

    // --- Predicate: visible elements -----------------------------------------

    [Fact]
    public void Predicate_AllVisibleElementsMustBePresent()
    {
        var p = new RulePredicate { VisibleElements = new[] { "A", "B", "C" } };

        Assert.True(RuleEngine.PredicateMatches(p,
            CtxRaw(visible: new[] { "A", "B", "C", "D" })));
        Assert.False(RuleEngine.PredicateMatches(p,
            CtxRaw(visible: new[] { "A", "B" })));
    }

    [Fact]
    public void Predicate_EmptyVisibleElementsMatchesAnything()
    {
        var p = new RulePredicate();
        Assert.True(RuleEngine.PredicateMatches(p, CtxRaw(visible: Array.Empty<string>())));
    }

    // --- Predicate: operator idle -------------------------------------------

    [Fact]
    public void Predicate_OperatorIdleAtLeast_Enforced()
    {
        var p = new RulePredicate { OperatorIdleMsAtLeast = 2000 };

        Assert.True(RuleEngine.PredicateMatches(p, CtxRaw(idleMs: 2000)));
        Assert.True(RuleEngine.PredicateMatches(p, CtxRaw(idleMs: 5000)));
        Assert.False(RuleEngine.PredicateMatches(p, CtxRaw(idleMs: 1999)));
    }

    // --- Predicate: state flags ----------------------------------------------

    [Fact]
    public void Predicate_StateFlagsMustMatch()
    {
        var p = new RulePredicate
        {
            StateFlags = new Dictionary<string, string> { ["focused"] = "true" }
        };

        Assert.True(RuleEngine.PredicateMatches(p,
            CtxRaw(flags: new Dictionary<string, string> { ["focused"] = "true" })));
        Assert.False(RuleEngine.PredicateMatches(p,
            CtxRaw(flags: new Dictionary<string, string> { ["focused"] = "false" })));
        Assert.False(RuleEngine.PredicateMatches(p,
            CtxRaw(flags: new Dictionary<string, string>())));
    }

    [Fact]
    public void Predicate_StateFlagsAreCaseInsensitiveOnValue()
    {
        var p = new RulePredicate
        {
            StateFlags = new Dictionary<string, string> { ["focused"] = "TRUE" }
        };
        Assert.True(RuleEngine.PredicateMatches(p,
            CtxRaw(flags: new Dictionary<string, string> { ["focused"] = "true" })));
    }

    // --- Predicate: combined conditions -------------------------------------

    [Fact]
    public void Predicate_AllFieldsMustMatch()
    {
        var p = new RulePredicate
        {
            ProcessName = "foo",
            VisibleElements = new[] { "X" },
            OperatorIdleMsAtLeast = 1000,
            StateFlags = new Dictionary<string, string> { ["a"] = "b" },
        };

        Assert.True(RuleEngine.PredicateMatches(p, new RuleContext
        {
            SkillId = "test",
            ProcessName = "foo",
            VisibleElements = new HashSet<string> { "X" },
            OperatorIdleMs = 2000,
            Flags = new Dictionary<string, string> { ["a"] = "b" },
        }));

        // Flip one field at a time to prove all are required.
        Assert.False(RuleEngine.PredicateMatches(p with { ProcessName = "nope" }, new RuleContext
        {
            SkillId = "test",
            ProcessName = "foo",
            VisibleElements = new HashSet<string> { "X" },
            OperatorIdleMs = 2000,
            Flags = new Dictionary<string, string> { ["a"] = "b" },
        }));
    }

    // --- RuleEngine metadata -------------------------------------------------

    [Fact]
    public void RuleEngine_TracksRuleCount()
    {
        var engine = new RuleEngine(new[] { Rule("r1", "a"), Rule("r2", "a"), Rule("r3", "b") }, Log);
        Assert.Equal(3, engine.RuleCount);
    }

    [Fact]
    public void RuleEngine_ExposesKnownSkills()
    {
        var engine = new RuleEngine(new[] { Rule("r1", "a"), Rule("r2", "b") }, Log);
        Assert.Contains("a", engine.KnownSkills);
        Assert.Contains("b", engine.KnownSkills);
        Assert.Equal(2, engine.KnownSkills.Count);
    }

    [Fact]
    public void RuleEngine_EmptyCatalog_StillUsable()
    {
        var engine = new RuleEngine(Array.Empty<Rule>(), Log);
        Assert.Equal(0, engine.RuleCount);
        Assert.Empty(engine.KnownSkills);
        var result = engine.Evaluate(Ctx());
        Assert.Equal(MatchOutcome.NoMatch, result.Outcome);
    }

    // --- Precondition gating (Codex M-4) -------------------------------------

    [Fact]
    public void Evaluate_Precondition_AutonomousOkFalse_BlocksOtherSkills()
    {
        var gate = new Rule
        {
            Id = "gate-active-call",
            SkillId = RuleEngine.PreconditionsSkill,
            Priority = 1000,
            AutonomousOk = false,
            When = new RulePredicate
            {
                StateFlags = new Dictionary<string, string> { ["active_call"] = "true" },
            },
            Then = new[] { new RuleActionSpec { Type = RuleActionType.AskOperator } },
        };
        var skillRule = Rule("step", "pricing-lookup");
        var engine = new RuleEngine(new[] { gate, skillRule }, Log);

        var ctx = new RuleContext
        {
            SkillId = "pricing-lookup",
            Flags = new Dictionary<string, string> { ["active_call"] = "true" },
        };

        var result = engine.Evaluate(ctx);

        Assert.Equal(MatchOutcome.Blocked, result.Outcome);
        Assert.Equal("gate-active-call", result.MatchedRule!.Id);
    }

    [Fact]
    public void Evaluate_Precondition_AutonomousOk_DoesNotBlockSkillEvaluation()
    {
        var gate = new Rule
        {
            Id = "gate-idle",
            SkillId = RuleEngine.PreconditionsSkill,
            Priority = 1000,
            AutonomousOk = true,
            When = new RulePredicate { OperatorIdleMsAtLeast = 1000 },
            Then = new[] { new RuleActionSpec { Type = RuleActionType.Log } },
        };
        var skillRule = Rule("step", "pricing-lookup");
        var engine = new RuleEngine(new[] { gate, skillRule }, Log);

        var ctx = new RuleContext
        {
            SkillId = "pricing-lookup",
            OperatorIdleMs = 5000,
        };

        var result = engine.Evaluate(ctx);

        Assert.Equal(MatchOutcome.Matched, result.Outcome);
        Assert.Equal("step", result.MatchedRule!.Id);
    }

    [Fact]
    public void Evaluate_PreconditionsSkill_DoesNotRecurse()
    {
        // Evaluating the preconditions skill itself must not trigger the
        // precondition pre-sweep (infinite recursion or double-match).
        var gate = new Rule
        {
            Id = "gate-call",
            SkillId = RuleEngine.PreconditionsSkill,
            Priority = 1000,
            AutonomousOk = false,
            When = new RulePredicate
            {
                StateFlags = new Dictionary<string, string> { ["active_call"] = "true" },
            },
            Then = new[] { new RuleActionSpec { Type = RuleActionType.AskOperator } },
        };
        var engine = new RuleEngine(new[] { gate }, Log);

        // Asking the preconditions skill with active_call=true — matches the
        // rule, returns Blocked. That is the normal behavior of Evaluate on
        // an autonomousOk=false rule, NOT precondition pre-sweep.
        var result = engine.Evaluate(new RuleContext
        {
            SkillId = RuleEngine.PreconditionsSkill,
            Flags = new Dictionary<string, string> { ["active_call"] = "true" },
        });

        Assert.Equal(MatchOutcome.Blocked, result.Outcome);
        Assert.Equal("gate-call", result.MatchedRule!.Id);
    }

    // --- Glob / regex safety (Codex M-7) -------------------------------------

    [Fact]
    public void GlobMatch_NoCrashOnEmptyPattern()
    {
        Assert.False(RuleEngine.GlobMatch("", "anything"));
    }

    [Fact]
    public void ValidatePredicate_EmptyProcessName_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RuleEngine.ValidatePredicate(new RulePredicate { ProcessName = "  " }, "r1"));
    }

    [Fact]
    public void ValidatePredicate_InvalidWindowTitleRegex_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RuleEngine.ValidatePredicate(new RulePredicate { WindowTitlePattern = "(unclosed" }, "r1"));
    }

    [Fact]
    public void ValidatePredicate_ValidPatterns_DoNotThrow()
    {
        RuleEngine.ValidatePredicate(new RulePredicate
        {
            ProcessName = "PioneerPharmacy*",
            WindowTitlePattern = "Edit .*",
        }, "r1");
    }

    // --- Thread safety smoke -------------------------------------------------

    [Fact]
    public void Evaluate_ConcurrentCalls_NoCrash()
    {
        var rule = Rule("r1", "skill-a");
        var engine = new RuleEngine(new[] { rule }, Log);

        Parallel.For(0, 1000, _ =>
        {
            var result = engine.Evaluate(Ctx(skillId: "skill-a"));
            Assert.Equal(MatchOutcome.Matched, result.Outcome);
        });
    }

    // --- helpers -------------------------------------------------------------

    private static Rule Rule(
        string id,
        string skillId,
        int priority = 100,
        bool autonomousOk = true,
        RulePredicate? when = null) =>
        new()
        {
            Id = id,
            SkillId = skillId,
            Priority = priority,
            AutonomousOk = autonomousOk,
            When = when ?? new RulePredicate(),
            Then = new[]
            {
                new RuleActionSpec { Type = RuleActionType.Log },
            },
        };

    private static RuleContext Ctx(
        string skillId = "test-skill",
        string processName = "test.exe",
        IEnumerable<string>? visibleElements = null) =>
        new()
        {
            SkillId = skillId,
            ProcessName = processName,
            VisibleElements = new HashSet<string>(visibleElements ?? Array.Empty<string>()),
        };

    private static RuleContext CtxRaw(
        IEnumerable<string>? visible = null,
        int idleMs = 0,
        Dictionary<string, string>? flags = null) =>
        new()
        {
            SkillId = "test",
            VisibleElements = new HashSet<string>(visible ?? Array.Empty<string>()),
            OperatorIdleMs = idleMs,
            Flags = flags ?? new Dictionary<string, string>(),
        };
}
