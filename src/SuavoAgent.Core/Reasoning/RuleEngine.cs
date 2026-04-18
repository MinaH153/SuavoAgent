using System.Text.RegularExpressions;
using SuavoAgent.Contracts.Reasoning;

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
    private readonly ILogger<RuleEngine> _logger;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<Rule>> _bySkill;
    private readonly int _totalRules;

    /// <summary>Number of rules loaded — useful for DI logging and /health.</summary>
    public int RuleCount => _totalRules;

    /// <summary>Set of skill ids the engine knows about.</summary>
    public IReadOnlyCollection<string> KnownSkills => (IReadOnlyCollection<string>)_bySkill.Keys;

    public RuleEngine(IEnumerable<Rule> rules, ILogger<RuleEngine> logger)
    {
        _logger = logger;

        var dict = rules
            .GroupBy(r => r.SkillId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<Rule>)g.OrderByDescending(r => r.Priority).ToList());

        _bySkill = dict;
        _totalRules = dict.Values.Sum(v => v.Count);
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
        if (!_bySkill.TryGetValue(ctx.SkillId, out var candidates))
        {
            return new EvaluationResult
            {
                Outcome = MatchOutcome.NoMatch,
                Reason = $"No rules registered for skill '{ctx.SkillId}'",
            };
        }

        foreach (var rule in candidates)
        {
            if (!PredicateMatches(rule.When, ctx))
                continue;

            // Guard: autonomousOk=false always goes to operator approval.
            if (!rule.AutonomousOk)
            {
                _logger.LogInformation(
                    "RuleEngine: rule {RuleId} matched but requires operator approval (autonomousOk=false)",
                    rule.Id);
                return new EvaluationResult
                {
                    Outcome = MatchOutcome.Blocked,
                    MatchedRule = rule,
                    Actions = rule.Then,
                    Reason = "Rule requires operator approval",
                };
            }

            if (shadowMode)
            {
                _logger.LogInformation(
                    "RuleEngine: [SHADOW] rule {RuleId} would match — returning NoMatch",
                    rule.Id);
                return new EvaluationResult
                {
                    Outcome = MatchOutcome.NoMatch,
                    MatchedRule = rule,
                    Reason = $"Shadow mode — rule '{rule.Id}' would have matched",
                };
            }

            _logger.LogDebug("RuleEngine: rule {RuleId} matched for skill {Skill}", rule.Id, ctx.SkillId);
            return new EvaluationResult
            {
                Outcome = MatchOutcome.Matched,
                MatchedRule = rule,
                Actions = rule.Then,
                Reason = $"Matched rule '{rule.Id}'",
            };
        }

        return new EvaluationResult
        {
            Outcome = MatchOutcome.NoMatch,
            Reason = $"No rule in skill '{ctx.SkillId}' matched context "
                     + $"(process={ctx.ProcessName}, elements={ctx.VisibleElements.Count})",
        };
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

        return true;
    }

    /// <summary>
    /// Basic shell-style glob matching (only '*' and '?'). Case-insensitive.
    /// Small, dependency-free, safe against ReDoS because the translated
    /// regex has bounded backtracking.
    /// </summary>
    internal static bool GlobMatch(string pattern, string input)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        // Escape regex metacharacters, then reintroduce * and ?
        var escaped = Regex.Escape(pattern)
            .Replace(@"\*", ".*")
            .Replace(@"\?", ".");
        return Regex.IsMatch(input, "^" + escaped + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
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
}
