using SuavoAgent.Contracts.Behavioral;

namespace SuavoAgent.Contracts.Reasoning;

/// <summary>
/// The v3.12 structural match helper. Lives in Contracts so RuleEngine (Core)
/// and every consumer (tests, generator, executor) share one implementation.
///
/// Contract:
///   - Empty <see cref="RulePredicate.ElementFingerprints"/> → satisfied.
///   - Non-empty and <see cref="RulePredicate.MinRequiredCount"/> = null →
///     every required signature must match (legacy all-of).
///   - Non-empty and MinRequiredCount = K → at least K of the required
///     signatures must match in context (K-of-M relaxation).
///   Matching uses <see cref="ElementSignature.MatchesStructurally"/>.
/// </summary>
public static class PredicateFingerprintMatcher
{
    public static bool SatisfiedBy(RulePredicate predicate, RuleContext ctx)
    {
        if (predicate.ElementFingerprints.Count == 0)
            return true;
        if (ctx.ElementFingerprints.Count == 0)
            return false;

        var requiredCount = predicate.ElementFingerprints.Count;
        var minRequired = predicate.MinRequiredCount is int k
            ? Math.Clamp(k, 1, requiredCount)
            : requiredCount;

        int matches = 0;
        foreach (var required in predicate.ElementFingerprints)
        {
            foreach (var actual in ctx.ElementFingerprints)
            {
                if (required.MatchesStructurally(actual))
                {
                    matches++;
                    break;
                }
            }
            // Early exit — all-of miss (K==M) stays O(M) worst-case.
            if (matches >= minRequired) return true;
        }
        return matches >= minRequired;
    }
}
