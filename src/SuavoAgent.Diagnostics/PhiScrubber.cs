using System.Diagnostics;
using System.Text.RegularExpressions;
using SuavoAgent.Diagnostics.Phi;

namespace SuavoAgent.Diagnostics;

/// SDK-side PHI scrub. Spec §3 + §164.502(b) minimum-necessary contract:
/// only the smallest signal that identifies the crash crosses the wire.
///
/// As of the single-source PHI merge, the rule set is materialized from the shared
/// <see cref="PhiRuleCatalog"/> (the Untrusted subset) rather than defined inline, so this
/// hostile-input path and the trusted <c>PhiTextScrubber</c> path can never drift. The OTA
/// hot-swap contract is unchanged: the instance ctor <c>(RulesetV1, TimeSpan)</c> +
/// <c>Sanitize</c>/<c>IsDefinitelyPhi</c>/<c>RulesetVersion</c> are byte-for-byte identical.
///
/// Every rule uses <c>RegexOptions.NonBacktracking</c> (catastrophic-backtracking immunity,
/// Codex §8.1 RESOLVED v1.0) EXCEPT the XML matching-tag rule, which needs a backreference and
/// therefore falls back to the Standard engine with a per-rule timeout (catalog
/// <see cref="PhiEngineClass.Standard"/>).
public sealed class PhiScrubber
{
    private readonly TimeSpan _timeout;
    private readonly RulesetV1 _ruleset;
    private readonly List<ScrubRule> _rules;

    public PhiScrubber(RulesetV1 ruleset, TimeSpan timeout)
    {
        _ruleset = ruleset;
        _timeout = timeout;
        // Per-rule regex timeout = scrubber timeout (Codex chunk 2b HIGH).
        // No single rule may exceed the overall budget; cumulative budget
        // enforced by the inter-rule check in Sanitize.
        _rules = BuildRules(timeout);
    }

    /// <summary>
    /// Generation tag from the ruleset the scrubber was built against.
    /// Used by snapshot-coherence tests (Codex Comp 2 round-4 MED): every
    /// concurrent reader of <see cref="Wire.CurrentRuntime"/> MUST observe
    /// <c>rt.Ruleset.RulesetVersion == rt.Scrubber.RulesetVersion ==
    /// rt.Fingerprinter.RulesetVersion</c>, never a mixed generation.
    /// </summary>
    internal string RulesetVersion => _ruleset.RulesetVersion;

    /// <summary>
    /// Returns a PHI-scrubbed copy of the input. Hard 10ms budget per spec
    /// §4 contract; on overrun OR on any per-rule exception, returns the
    /// <c>[SCRUB_TIMEOUT]</c> sentinel and the caller drops the extras
    /// (fail-closed PHI safety per Codex chunk 2b HIGH).
    /// </summary>
    public string Sanitize(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        var sw = Stopwatch.StartNew();
        var result = input;
        foreach (var rule in _rules)
        {
            if (sw.Elapsed > _timeout)
            {
                return "[SCRUB_TIMEOUT]";
            }
            try
            {
                if (rule.PostValidator is { } validator)
                {
                    // Activate PostValidator via match evaluator: only
                    // replace matches that pass the checksum/Luhn check.
                    // Codex chunk 2b MEDIUM: previously dead code, every
                    // shape-match was redacted regardless of validity.
                    result = rule.Regex.Replace(result, m =>
                        validator(m.Value) ? m.Result(rule.Replacement) : m.Value);
                }
                else
                {
                    result = rule.Regex.Replace(result, rule.Replacement);
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return "[SCRUB_TIMEOUT]";
            }
            catch
            {
                // Codex chunk 2b HIGH: fail-closed on ANY rule exception.
                // The previous fail-open behavior (continue to next rule)
                // could leak PHI if a rule that was supposed to catch it
                // threw an unexpected exception.
                return "[SCRUB_TIMEOUT]";
            }
        }

        // Optional ruleset-loaded patient name dictionary. Phase 1: empty.
        if (_ruleset.PatientNamesSeed.Count > 0)
        {
            foreach (var name in _ruleset.PatientNamesSeed)
            {
                if (sw.Elapsed > _timeout) return "[SCRUB_TIMEOUT]";
                if (string.IsNullOrWhiteSpace(name)) continue;
                try
                {
                    result = Regex.Replace(result, $@"\b{Regex.Escape(name)}\b", "[PATIENT]",
                        RegexOptions.IgnoreCase, _timeout);
                }
                catch
                {
                    return "[SCRUB_TIMEOUT]";
                }
            }
        }

        return result;
    }

    /// <summary>
    /// High-confidence PHI-present test. Returns true if the input still
    /// matches a high-severity PHI pattern AFTER scrubbing — meaning the
    /// scrubber failed to defeat it. Caller drops the event entirely and
    /// forwards only the fingerprint per spec §3.
    /// </summary>
    public bool IsDefinitelyPhi(string scrubbed)
    {
        // Codex chunk 2b HIGH: scrubber sentinels (like [PIONEERRX_ID])
        // match some of the high-confidence regexes (e.g., PioneerRx-JSON
        // regex matches "PatientID":"[PIONEERRX_ID]"). Strip sentinels
        // before checking residual PHI to avoid dropping clean events.
        var withoutSentinels = ScrubberSentinelPattern.Replace(scrubbed, string.Empty);
        foreach (var rule in _rules.Where(r => r.HighConfidence))
        {
            try
            {
                if (rule.Regex.IsMatch(withoutSentinels)) return true;
            }
            catch
            {
                // If the residual check itself throws, conservatively treat
                // as "definitely PHI" to fail-closed.
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// All scrubber replacement sentinels emitted by the catalog rules. Used by
    /// <see cref="IsDefinitelyPhi"/> to distinguish "still has PHI" from "was scrubbed
    /// correctly". Catalog-derived single source (blueprint fix #6): the label set lives in
    /// <see cref="PhiRuleCatalog.SentinelLabels"/>. Kept NonBacktracking.
    /// </summary>
    private static readonly Regex ScrubberSentinelPattern = new(
        @"\[(?:" + string.Join("|", PhiRuleCatalog.SentinelLabels) + @")\]",
        RegexOptions.Compiled | RegexOptions.NonBacktracking);

    /// <summary>
    /// Materialize the Untrusted subset of the shared <see cref="PhiRuleCatalog"/> into the
    /// NonBacktracking scrub pipeline. Order, options, replacement, high-confidence flag and
    /// post-validator are all carried by the catalog, so this path and the trusted path cannot
    /// drift. Each rule gets the per-rule <paramref name="timeout"/>.
    /// </summary>
    private static List<ScrubRule> BuildRules(TimeSpan timeout)
    {
        var rules = new List<ScrubRule>(PhiRuleCatalog.Untrusted.Count);
        foreach (var spec in PhiRuleCatalog.Untrusted)
        {
            var options = spec.Options;
            if (spec.Engine == PhiEngineClass.NonBacktracking)
                options |= RegexOptions.NonBacktracking;

            rules.Add(new ScrubRule(
                spec.Name,
                new Regex(spec.Pattern, options, timeout),
                spec.Replacement,
                highConfidence: spec.HighConfidence,
                postValidator: PhiValidators.Resolve(spec.PostValidator)));
        }
        return rules;
    }

    /// <summary>
    /// DEA number checksum: (digit1+digit3+digit5) + 2*(digit2+digit4+digit6)
    /// mod 10 must equal digit7. Retained as a public API (existing callers/tests); delegates
    /// to the shared <see cref="PhiValidators.DeaChecksumValid"/>.
    /// </summary>
    public static bool DeaChecksumValid(string deaCandidate) =>
        PhiValidators.DeaChecksumValid(deaCandidate);

    private sealed class ScrubRule
    {
        public string Name { get; }
        public Regex Regex { get; }
        public string Replacement { get; }
        public bool HighConfidence { get; }
        public Func<string, bool>? PostValidator { get; }

        public ScrubRule(string name, Regex regex, string replacement,
            bool highConfidence = false, Func<string, bool>? postValidator = null)
        {
            Name = name;
            Regex = regex;
            Replacement = replacement;
            HighConfidence = highConfidence;
            PostValidator = postValidator;
        }
    }
}
