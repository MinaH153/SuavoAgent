using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SuavoAgent.Diagnostics;

/// SDK-side PHI scrub. Spec §3 + §164.502(b) minimum-necessary contract:
/// only the smallest signal that identifies the crash crosses the wire.
/// All regexes use <c>RegexOptions.NonBacktracking</c> to defeat
/// catastrophic-backtracking attacks (Codex §8.1 RESOLVED v1.0).
///
/// Patterns ordered by selectivity — most-specific first so JSON/XML/SQL
/// field-context patterns catch identifiers before the bare-number patterns
/// (NPI Luhn, NDC unhyphenated) trigger on neighboring digits.
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
    /// All scrubber replacement sentinels emitted by the rules in
    /// <see cref="BuildRules"/>. Used by <see cref="IsDefinitelyPhi"/> to
    /// distinguish "still has PHI" from "was scrubbed correctly".
    /// </summary>
    private static readonly Regex ScrubberSentinelPattern = new(
        @"\[(?:PIONEERRX_ID|MEMBER_ID|NPI|NDC|DEA|SSN|RX_NUM|USER|DOB|PATIENT|SCRUB_TIMEOUT|SCRUB_FAILED)\]",
        RegexOptions.Compiled | RegexOptions.NonBacktracking);

    private static List<ScrubRule> BuildRules(TimeSpan timeout)
    {
        var opts = RegexOptions.NonBacktracking | RegexOptions.IgnoreCase;
        // NonBacktracking does not support backreferences (Codex chunk 2b
        // LOW confirmed via .NET 8 docs; lazy quantifiers ARE supported).
        // Only the XML matching-tag rule uses \1, so it alone falls back
        // to the standard engine with a per-rule timeout.
        var optsWithBackref = RegexOptions.IgnoreCase;

        return new List<ScrubRule>
        {
            // ── PioneerRx field-context (highest selectivity, run first) ──
            // Codex chunk 2b CRITICAL: previous value class [^",}\s]+
            // stopped at first space, leaking the rest of quoted values
            // like "John Doe". Now matches either a full quoted string
            // (with spaces / commas inside) OR an unquoted compact value.
            new("PioneerRx-JSON",
                new Regex(@"""(RxNumber|PatientID|PrescriberID|PharmacyChainID)""\s*:\s*(?:""[^""]*""|[^,}\s]+)",
                    opts, timeout),
                "\"$1\":\"[PIONEERRX_ID]\"",
                highConfidence: true),
            new("PioneerRx-XML",
                new Regex(@"<(RxNumber|PatientID|PrescriberID|PharmacyChainID)>[^<]+</\1>",
                    optsWithBackref, timeout),
                "<$1>[PIONEERRX_ID]</$1>",
                highConfidence: true),
            new("PioneerRx-SQL",
                new Regex(@"\b(RxNumber|PatientID|PrescriberID|PharmacyChainID)\s*=\s*'[^']+'",
                    opts, timeout),
                "$1='[PIONEERRX_ID]'",
                highConfidence: true),

            // ── Insurance member IDs (field-context required) ──
            // Lazy .{0,24}? skips filler words between context keyword and
            // the member-ID-shaped token. Codex chunk 2b LOW corrected:
            // .NET 8 NonBacktracking DOES support lazy quantifiers (only
            // backreferences + lookarounds are unsupported), so these
            // rules now use the NonBacktracking engine for catastrophic-
            // backtracking immunity.
            new("BCBS-Member",
                new Regex(@"\b(bcbs|blue\s*cross|member_?id)\b.{0,24}?\b[A-Z]{3}\d{6,14}\b",
                    opts, timeout),
                "$1 [MEMBER_ID]"),
            new("Aetna-Member",
                new Regex(@"\b(aetna|member_?id)\b.{0,24}?\bW\d{8,12}\b",
                    opts, timeout),
                "$1 [MEMBER_ID]"),
            new("Cigna-Member",
                new Regex(@"\b(cigna|member_?id)\b.{0,24}?\bU?\d{9,12}\b",
                    opts, timeout),
                "$1 [MEMBER_ID]"),

            // ── Prescriber NPI (field-context required, more reliable than naked-Luhn) ──
            // Permissive separator handles JSON ("npi":"..."), YAML
            // (npi: ...), env (NPI=...), and plain text.
            new("Prescriber-NPI-field",
                new Regex(@"\b(prescriber_?npi|provider_?npi|npi)\s*""?\s*[:=]\s*""?\d{10}""?",
                    opts, timeout),
                "$1=[NPI]",
                highConfidence: true),

            // ── NDC variants (5 hyphenation formats) ──
            new("NDC-4-4-2",
                new Regex(@"\b\d{4}-\d{4}-\d{2}\b", opts, timeout),
                "[NDC]"),
            new("NDC-5-4-2",
                new Regex(@"\b\d{5}-\d{4}-\d{2}\b", opts, timeout),
                "[NDC]"),
            new("NDC-5-3-2",
                new Regex(@"\b\d{5}-\d{3}-\d{2}\b", opts, timeout),
                "[NDC]"),
            new("NDC-5-4-1",
                new Regex(@"\b\d{5}-\d{4}-\d\b", opts, timeout),
                "[NDC]"),
            new("NDC-unhyphenated-with-label",
                new Regex(@"\b(ndc|national[_\s-]?drug[_\s-]?code)\s*""?\s*[:=]\s*""?\d{10,11}""?",
                    opts, timeout),
                "$1=[NDC]"),

            // ── Rx-number (PioneerRx pattern). MUST run BEFORE DEA shape
            //     because "RX1234567" is 9 chars matching both patterns
            //     and we want it labeled RX_NUM not DEA. ──
            new("Rx-number-PioneerRx",
                new Regex(@"\bRX\d{6,12}\b", opts, timeout),
                "[RX_NUM]",
                highConfidence: true),

            // ── DEA number (2 letters + 7 digits + Luhn-like checksum) ──
            // Pattern matches shape; checksum validation happens in a
            // second pass via DeaChecksumValid below to keep the regex
            // NonBacktracking-compatible. NonBacktracking doesn't support
            // lookarounds, so DEA can't (?!RX) negatively-anchor; rule
            // ordering above handles the RX/DEA collision instead.
            new("DEA-number-shape",
                new Regex(@"\b[A-Z]{2}\d{7}\b", opts, timeout),
                "[DEA]",
                highConfidence: true,
                postValidator: DeaChecksumValid),

            // ── Standard PHI shapes ──
            new("SSN",
                new Regex(@"\b\d{3}-\d{2}-\d{4}\b", opts, timeout),
                "[SSN]",
                highConfidence: true),
            new("DOB-shape",
                new Regex(@"\b(0?[1-9]|1[0-2])[-/](0?[1-9]|[12]\d|3[01])[-/](19|20)\d{2}\b",
                    opts, timeout),
                "[DOB]"),

            // ── File path scrub (PII via username in path) ──
            new("Windows-user-path",
                new Regex(@"[\\/]Users[\\/][^\\/\s""'<>:|?*]+", opts, timeout),
                "/Users/[USER]"),
        };
    }

    /// <summary>
    /// DEA number checksum: (digit1+digit3+digit5) + 2*(digit2+digit4+digit6)
    /// mod 10 must equal digit7. Used as a post-regex validator since
    /// NonBacktracking can't express the checksum constraint.
    /// </summary>
    public static bool DeaChecksumValid(string deaCandidate)
    {
        if (deaCandidate.Length != 9) return false;
        var digits = deaCandidate.AsSpan(2);
        if (digits.Length != 7) return false;
        for (int i = 0; i < 7; i++)
        {
            if (!char.IsDigit(digits[i])) return false;
        }
        var n1 = digits[0] - '0';
        var n2 = digits[1] - '0';
        var n3 = digits[2] - '0';
        var n4 = digits[3] - '0';
        var n5 = digits[4] - '0';
        var n6 = digits[5] - '0';
        var n7 = digits[6] - '0';
        var checksum = (n1 + n3 + n5) + 2 * (n2 + n4 + n6);
        return checksum % 10 == n7;
    }

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
