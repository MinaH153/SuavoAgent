using SuavoAgent.Contracts.Behavioral;

namespace SuavoAgent.Contracts.Reasoning;

/// <summary>
/// The v3.12 structural match helper. Lives in Contracts so RuleEngine (Core)
/// and every consumer (tests, generator, executor) share one implementation.
///
/// Contract:
///   - Empty <see cref="RulePredicate.ElementFingerprints"/> → satisfied.
///   - Non-empty → every required signature must match at least one signature
///     in <see cref="RuleContext.ElementFingerprints"/> via
///     <see cref="ElementSignature.MatchesStructurally"/>.
/// </summary>
public static class PredicateFingerprintMatcher
{
    public static bool SatisfiedBy(RulePredicate predicate, RuleContext ctx)
    {
        if (predicate.ElementFingerprints.Count == 0)
            return true;
        if (ctx.ElementFingerprints.Count == 0)
            return false;

        foreach (var required in predicate.ElementFingerprints)
        {
            var matched = false;
            foreach (var actual in ctx.ElementFingerprints)
            {
                if (required.MatchesStructurally(actual))
                {
                    matched = true;
                    break;
                }
            }
            if (!matched) return false;
        }
        return true;
    }
}
