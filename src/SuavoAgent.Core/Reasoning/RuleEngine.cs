using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Contracts.Vision;

namespace SuavoAgent.Core.Reasoning;

/// <summary>
/// Tier 1 decision engine. Matches a RuleContext against the loaded rule
/// catalog and returns the winning rule's actions, or signals NoMatch so the
/// caller escalates to Tier 2 (LocalInference).
///
/// Design notes:
/// - Stateless and thread-safe after construction.
/// - Rules indexed by skillId at load — Evaluate is O(rules-in-skill), not O(catalog).
/// - Shadow mode available per-context — matches without signalling Matched,
///   so new skills can run in observe-only mode on real traffic for 24 h.
/// - Every evaluation is logged so the pattern miner (Week 4) can learn.
/// </summary>
public sealed class RuleEngine
{
    /// <summary>
    /// The well-known skill id reserved for universal safety gates. Every call
    /// to Evaluate for a non-preconditions skill runs preconditions first, and
    /// a blocking match there short-circuits the normal skill evaluation.
    /// </summary>
    public const string PreconditionsSkill = "preconditions";

    private readonly ILogger<RuleEngine> _logger;
    private readonly System.Collections.Frozen.FrozenDictionary<string, System.Collections.Immutable.ImmutableArray<Rule>> _bySkill;
    private readonly IActiveLearnedRuleRegistry? _activeLearnedRules;
    private readonly int _totalRules;

    /// <summary>Number of rules loaded — useful for DI logging and /health.</summary>
    public int RuleCount => _totalRules + (_activeLearnedRules?.Count ?? 0);

    /// <summary>Set of skill ids the engine knows about.</summary>
    public IReadOnlyCollection<string> KnownSkills => (IReadOnlyCollection<string>)_bySkill.Keys;

    public RuleEngine(
        IEnumerable<Rule> rules,
        ILogger<RuleEngine> logger,
        IActiveLearnedRuleRegistry? activeLearnedRules = null)
    {
        _logger = logger;
        _activeLearnedRules = activeLearnedRules;

        var dict = rules
            .GroupBy(r => r.SkillId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(r => r.Priority).ToImmutableArray());

        _bySkill = dict.ToFrozenDictionary();
        _totalRules = dict.Values.Sum(v => v.Length);
    }

    /// <summary>
    /// Matches the context against the catalog.
    ///
    /// When shadowMode=true, a match is logged but the outcome returned is
    /// NoMatch so the caller escalates. Useful for rolling out new rules on
    /// real traffic without committing to their actions.
    /// </summary>
    public EvaluationResult Evaluate(RuleContext ctx, bool shadowMode = false)
    {
        // [M-4] Universal safety gates run first for any non-preconditions skill.
        // If a precondition Blocks (e.g. active phone call → AskOperator), we
        // short-circuit — the skill-specific rules never get a chance.
        if (ctx.SkillId != PreconditionsSkill &&
            _bySkill.TryGetValue(PreconditionsSkill, out var gates))
        {
            foreach (var gate in gates)
            {
                if (!PredicateMatches(gate.When, ctx)) continue;

                if (!gate.AutonomousOk)
                {
                    _logger.LogInformation("core.rule_engine.precondition_blocked");
                    return new EvaluationResult
                    {
                        Outcome = MatchOutcome.Blocked,
                        MatchedRule = gate,
                        Actions = gate.Then,
                        Reason = "precondition_blocked",
                    };
                }
                // Autonomous-ok preconditions just mean "safety clear, keep going".
                // They never terminate evaluation — they gate it.
            }
        }

        // Immutable built-in/hand-authored rules always run first. A live learned rule is consulted
        // only when none of those rules match, so runtime admission can never replace a shipped guard.
        if (_bySkill.TryGetValue(ctx.SkillId, out var candidates))
        {
            var staticMatch = EvaluateCandidates(candidates, ctx, shadowMode);
            if (staticMatch is not null) return staticMatch;
        }

        var learnedCandidates = _activeLearnedRules?.GetRulesForSkill(ctx.SkillId)
            ?? Array.Empty<Rule>();
        var learnedMatch = EvaluateCandidates(learnedCandidates, ctx, shadowMode);
        if (learnedMatch is not null) return learnedMatch;

        return new EvaluationResult
        {
            Outcome = MatchOutcome.NoMatch,
            Reason = "rule_no_match",
        };
    }

    private EvaluationResult? EvaluateCandidates(
        IEnumerable<Rule> candidates,
        RuleContext ctx,
        bool shadowMode)
    {
        foreach (var rule in candidates)
        {
            if (!PredicateMatches(rule.When, ctx)) continue;

            if (!rule.AutonomousOk)
            {
                _logger.LogInformation("core.rule_engine.operator_approval_required");
                return new EvaluationResult
                {
                    Outcome = MatchOutcome.Blocked,
                    MatchedRule = rule,
                    Actions = rule.Then,
                    Reason = "rule_operator_approval_required",
                };
            }

            if (shadowMode)
            {
                _logger.LogInformation(
                    "core.rule_engine.shadow_match");
                return new EvaluationResult
                {
                    Outcome = MatchOutcome.NoMatch,
                    MatchedRule = rule,
                    Reason = "rule_shadow_match",
                };
            }

            _logger.LogDebug("core.rule_engine.rule_matched");
            return new EvaluationResult
            {
                Outcome = MatchOutcome.Matched,
                MatchedRule = rule,
                Actions = rule.Then,
                Reason = "rule_matched",
            };
        }
        return null;
    }

    /// <summary>
    /// Verifies a predicate against a RuleContext. Public so post-action
    /// VerifyAfter assertions share the same logic.
    /// </summary>
    public static bool PredicateMatches(RulePredicate p, RuleContext ctx)
    {
        if (p.ProcessName != null && !GlobMatch(p.ProcessName, ctx.ProcessName))
            return false;

        if (p.WindowTitlePattern != null && !RegexMatch(p.WindowTitlePattern, ctx.WindowTitle))
            return false;

        if (p.VisibleElements.Count > 0)
        {
            foreach (var required in p.VisibleElements)
            {
                if (!ctx.VisibleElements.Contains(required))
                    return false;
            }
        }

        // v3.12 Codex BLOCK fix: structural fingerprint gate lives alongside the
        // legacy name list, not in place of it. Empty fingerprint list = no
        // structural constraint (legacy behaviour); non-empty = every required
        // triple must be matched by at least one triple in the context.
        if (!PredicateFingerprintMatcher.SatisfiedBy(p, ctx))
            return false;

        if (p.OperatorIdleMsAtLeast.HasValue &&
            ctx.OperatorIdleMs < p.OperatorIdleMsAtLeast.Value)
        {
            return false;
        }

        if (p.StateFlags.Count > 0)
        {
            foreach (var (k, required) in p.StateFlags)
            {
                if (!ctx.Flags.TryGetValue(k, out var actual) ||
                    !string.Equals(actual, required, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        // W4b visual predicate: every required substring must appear (case-insensitive)
        // in some on-screen text region. Empty = no constraint (legacy behaviour).
        if (p.TextPresent.Count > 0)
        {
            foreach (var required in p.TextPresent)
            {
                var found = false;
                foreach (var region in ctx.ScreenText)
                {
                    if (region.Text.Contains(required, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                    return false;
            }
        }

        // W4b+ spatial predicate: each entry requires on-screen text near a control of a role.
        if (p.TextNearElement.Count > 0)
        {
            foreach (var near in p.TextNearElement)
            {
                if (!SatisfiesTextNearElement(near, ctx))
                    return false;
            }
        }

        return true;
    }

    private static bool SatisfiesTextNearElement(SpatialTextPredicate p, RuleContext ctx)
    {
        // Fail closed on meaningless predicates: a negative distance budget or an empty text/role
        // can never be a legitimate spatial constraint (and empty text would otherwise match all).
        if (p.MaxDistancePx < 0 || string.IsNullOrEmpty(p.Text) || string.IsNullOrEmpty(p.ElementRole))
            return false;

        // "*" is the deliberate role-agnostic sentinel: the rule asserts the text sits near SOME
        // rendered control (a liveness signal — the screen isn't a static image), without pinning the
        // exact UIA role. Used when the real control's role can't be confirmed yet (e.g. a PioneerRx
        // grid cell whose role needs a live-box UIA dump). Empty role still fails closed above (typo
        // protection); "*" is an explicit, authored choice.
        var anyRole = p.ElementRole == "*";
        long maxSq = (long)p.MaxDistancePx * p.MaxDistancePx;
        foreach (var t in ctx.ScreenText)
        {
            if (!t.Text.Contains(p.Text, StringComparison.OrdinalIgnoreCase))
                continue;
            var (tx, ty) = Center(t.Bounds);
            foreach (var e in ctx.ScreenElements)
            {
                if (!anyRole && !string.Equals(e.Role, p.ElementRole, StringComparison.OrdinalIgnoreCase))
                    continue;
                var (ex, ey) = Center(e.Bounds);
                long dx = tx - ex, dy = ty - ey;
                if (dx * dx + dy * dy <= maxSq)
                    return true;
            }
        }
        return false;
    }

    private static (long X, long Y) Center(Rect r) => ((long)r.X + r.Width / 2, (long)r.Y + r.Height / 2);

    /// <summary>
    /// Basic shell-style glob matching (only '*' and '?'). Case-insensitive.
    /// Small, dependency-free, safe against ReDoS because the translated
    /// regex has bounded backtracking.
    /// </summary>
    internal static bool GlobMatch(string pattern, string input)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        try
        {
            // Escape regex metacharacters, then reintroduce * and ?
            var escaped = Regex.Escape(pattern)
                .Replace(@"\*", ".*")
                .Replace(@"\?", ".");
            return Regex.IsMatch(input, "^" + escaped + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        }
        catch (RegexMatchTimeoutException)
        {
            return false; // fail-closed on ReDoS
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static bool RegexMatch(string pattern, string input)
    {
        try
        {
            return Regex.IsMatch(input, pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        }
        catch (RegexMatchTimeoutException)
        {
            return false; // fail-closed on ReDoS
        }
        catch (ArgumentException)
        {
            return false; // malformed pattern = no match, already logged at load
        }
    }

    /// <summary>
    /// Compile-time validation for a predicate's pattern fields. Called by
    /// YamlRuleLoader during load so malformed patterns fail the catalog at
    /// startup rather than running code in production.
    /// </summary>
    public static void ValidatePredicate(RulePredicate p, string ruleId)
    {
        if (p.ProcessName != null)
        {
            if (string.IsNullOrWhiteSpace(p.ProcessName))
                throw new InvalidOperationException($"Rule '{ruleId}' has empty processName");

            // Compile test — throws ArgumentException on malformed pattern,
            // catches timeout budget overrun via default .NET Regex matcher.
            _ = new Regex(
                "^" + Regex.Escape(p.ProcessName).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        }
        if (p.WindowTitlePattern != null)
        {
            if (string.IsNullOrWhiteSpace(p.WindowTitlePattern))
                throw new InvalidOperationException($"Rule '{ruleId}' has empty windowTitlePattern");

            try
            {
                _ = new Regex(p.WindowTitlePattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException(
                    $"rule_window_pattern_invalid:{ex.GetType().Name}", ex);
            }
        }
    }
}
